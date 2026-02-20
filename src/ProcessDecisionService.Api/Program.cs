using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<DecisionEngine>();
builder.Services.AddScoped<IIntegrationAdapter, MockRestAdapter>();
builder.Services.AddScoped<IIntegrationAdapter, MockSoapAdapter>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
}

// ---------------- API ----------------

app.MapPost("/api/jobs/evaluate", async (EvaluateJobDto dto, AppDb db, DecisionEngine engine) =>
{
    var job = new Job
    {
        Id = Guid.NewGuid(),
        OrderNo = dto.OrderNo.Trim(),
        BatchNo = dto.BatchNo.Trim(),
        AttrCode = dto.AttrCode.Trim().ToUpperInvariant(),
        Filename = dto.Filename?.Trim(),
        PiecesTotal = dto.PiecesTotal,
        Priority = dto.Priority.Trim().ToUpperInvariant(),
        Status = JobStatus.Evaluated,
        CreatedAtUtc = DateTime.UtcNow
    };

    var plan = engine.Evaluate(job);
    job.Decision = plan.Decision;
    job.PlannedActionsJson = System.Text.Json.JsonSerializer.Serialize(plan.Actions);

    db.Jobs.Add(job);
    db.Audit.Add(AuditEvent.For(job.Id, "EVALUATED", $"Decision={job.Decision} Actions={plan.Actions.Count}"));
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        jobId = job.Id,
        job.Status,
        job.Decision,
        actions = plan.Actions
    });
});

app.MapPost("/api/jobs/{id:guid}/execute", async (Guid id, AppDb db, IEnumerable<IIntegrationAdapter> adapters) =>
{
    var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id);
    if (job is null) return Results.NotFound();

    if (job.Status == JobStatus.Completed)
        return Results.BadRequest(new { message = "Job already completed." });

    var actions = string.IsNullOrWhiteSpace(job.PlannedActionsJson)
        ? new List<ActionPlanItem>()
        : System.Text.Json.JsonSerializer.Deserialize<List<ActionPlanItem>>(job.PlannedActionsJson!) ?? new();

    job.Status = JobStatus.Executing;
    db.Audit.Add(AuditEvent.For(job.Id, "EXECUTING", $"Actions={actions.Count}"));
    await db.SaveChangesAsync();

    foreach (var action in actions)
    {
        var adapter = adapters.FirstOrDefault(a => a.Name == action.Adapter);
        if (adapter is null)
        {
            db.Audit.Add(AuditEvent.For(job.Id, "ADAPTER_MISSING", action.Adapter));
            job.Status = JobStatus.Failed;
            await db.SaveChangesAsync();
            return Results.Ok(new { jobId = job.Id, job.Status, error = "Adapter missing: " + action.Adapter });
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var call = new IntegrationCall
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            Adapter = adapter.Name,
            CorrelationId = correlationId,
            StartedAtUtc = DateTime.UtcNow,
            Attempt = 1,
            Status = "STARTED"
        };
        db.IntegrationCalls.Add(call);
        await db.SaveChangesAsync();

        var result = await adapter.ExecuteAsync(job, action, correlationId);

        call.EndedAtUtc = DateTime.UtcNow;
        call.Status = result.Success ? "OK" : "ERROR";
        call.ExternalId = result.ExternalId;
        call.Error = result.Error;

        db.Audit.Add(AuditEvent.For(
            job.Id,
            result.Success ? "INTEGRATION_OK" : "INTEGRATION_ERROR",
            $"{adapter.Name} corr={correlationId} externalId={result.ExternalId} error={result.Error}"
        ));

        if (!result.Success)
        {
            job.Status = JobStatus.Failed;
            await db.SaveChangesAsync();
            return Results.Ok(new { jobId = job.Id, job.Status, failedAdapter = adapter.Name, error = result.Error });
        }

        await db.SaveChangesAsync();
    }

    job.Status = JobStatus.Completed;
    db.Audit.Add(AuditEvent.For(job.Id, "COMPLETED", "All actions executed."));
    await db.SaveChangesAsync();

    return Results.Ok(new { jobId = job.Id, job.Status });
});

app.MapGet("/api/jobs/{id:guid}", async (Guid id, AppDb db) =>
{
    var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (job is null) return Results.NotFound();

    var audit = await db.Audit.AsNoTracking()
        .Where(x => x.JobId == id)
        .OrderBy(x => x.AtUtc)
        .ToListAsync();

    var calls = await db.IntegrationCalls.AsNoTracking()
        .Where(x => x.JobId == id)
        .OrderBy(x => x.StartedAtUtc)
        .ToListAsync();

    return Results.Ok(new { job, audit, calls });
});

app.MapGet("/api/metrics", async (AppDb db) =>
{
    var total = await db.Jobs.CountAsync();
    var byStatus = await db.Jobs
        .GroupBy(x => x.Status)
        .Select(g => new { status = g.Key.ToString(), count = g.Count() })
        .ToListAsync();

    return Results.Ok(new { total, byStatus });
});

// ---------------- TESTS (Swagger-run) ----------------

app.MapGet("/api/tests/smoke", async (AppDb db) =>
{
    var checks = new List<TestCheck>();

    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        checks.Add(canConnect
            ? TestCheck.Pass("db.canConnect")
            : TestCheck.Fail("db.canConnect", "Database.CanConnectAsync=false"));

        if (!canConnect)
            return Results.Ok(TestReport.From(checks));
    }
    catch (Exception ex)
    {
        checks.Add(TestCheck.Fail("db.canConnect", ex.GetType().Name + ": " + ex.Message));
        return Results.Ok(TestReport.From(checks));
    }

    try
    {
        var total = await db.Jobs.CountAsync();
        checks.Add(TestCheck.Pass("metrics.total.exists", $"total={total}"));

        var byStatus = await db.Jobs.GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        checks.Add(TestCheck.Pass("metrics.byStatus.exists", $"groups={byStatus.Count}"));
    }
    catch (Exception ex)
    {
        checks.Add(TestCheck.Fail("metrics.query", ex.GetType().Name + ": " + ex.Message));
    }

    return Results.Ok(TestReport.From(checks));
});

app.MapPost("/api/tests/flow", async (FlowTestRequest req, AppDb db, DecisionEngine engine, IEnumerable<IIntegrationAdapter> adapters) =>
{
    var checks = new List<TestCheck>();

    // --- evaluate (inline, ¿eby nie robiæ HTTP do siebie)
    var job = new Job
    {
        Id = Guid.NewGuid(),
        OrderNo = (req.OrderNo ?? "TEST-ORDER").Trim(),
        BatchNo = (req.BatchNo ?? "TEST-BATCH").Trim(),
        AttrCode = (req.AttrCode ?? "Y").Trim().ToUpperInvariant(),
        Filename = req.Filename?.Trim(),
        PiecesTotal = req.PiecesTotal,
        Priority = (req.Priority ?? "N").Trim().ToUpperInvariant(),
        Status = JobStatus.Evaluated,
        CreatedAtUtc = DateTime.UtcNow
    };

    EvaluationPlan plan;
    try
    {
        plan = engine.Evaluate(job);
        job.Decision = plan.Decision;
        job.PlannedActionsJson = System.Text.Json.JsonSerializer.Serialize(plan.Actions);

        db.Jobs.Add(job);
        db.Audit.Add(AuditEvent.For(job.Id, "EVALUATED", $"Decision={job.Decision} Actions={plan.Actions.Count}"));
        await db.SaveChangesAsync();

        checks.Add(TestCheck.Pass("evaluate.returned.jobId", $"jobId={job.Id}"));
        checks.Add(TestCheck.Pass("evaluate.plannedActions", $"actions={plan.Actions.Count}"));
    }
    catch (Exception ex)
    {
        checks.Add(TestCheck.Fail("evaluate", ex.GetType().Name + ": " + ex.Message));
        return Results.Ok(TestReport.From(checks, new { jobId = job.Id }));
    }

    if (req.ExpectedActionsCount is not null)
    {
        checks.Add(plan.Actions.Count == req.ExpectedActionsCount
            ? TestCheck.Pass("plannedActions.count", $"expected={req.ExpectedActionsCount} actual={plan.Actions.Count}")
            : TestCheck.Fail("plannedActions.count", $"expected={req.ExpectedActionsCount} actual={plan.Actions.Count}"));
    }

    // --- execute (inline)
    try
    {
        job.Status = JobStatus.Executing;
        db.Audit.Add(AuditEvent.For(job.Id, "EXECUTING", $"Actions={plan.Actions.Count}"));
        await db.SaveChangesAsync();

        foreach (var action in plan.Actions)
        {
            var adapter = adapters.FirstOrDefault(a => a.Name == action.Adapter);
            if (adapter is null)
            {
                db.Audit.Add(AuditEvent.For(job.Id, "ADAPTER_MISSING", action.Adapter));
                job.Status = JobStatus.Failed;
                await db.SaveChangesAsync();

                checks.Add(TestCheck.Fail("execute.adapter.exists", "Adapter missing: " + action.Adapter));
                checks.Add(TestCheck.Pass("execute.finished", "Job finished (Completed or Failed)"));
                checks.Add(TestCheck.Pass("execute.finalStatus", "Failed"));
                return Results.Ok(TestReport.From(checks, new { jobId = job.Id, status = job.Status.ToString() }));
            }

            var correlationId = Guid.NewGuid().ToString("N");
            var call = new IntegrationCall
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                Adapter = adapter.Name,
                CorrelationId = correlationId,
                StartedAtUtc = DateTime.UtcNow,
                Attempt = 1,
                Status = "STARTED"
            };
            db.IntegrationCalls.Add(call);
            await db.SaveChangesAsync();

            var result = await adapter.ExecuteAsync(job, action, correlationId);

            call.EndedAtUtc = DateTime.UtcNow;
            call.Status = result.Success ? "OK" : "ERROR";
            call.ExternalId = result.ExternalId;
            call.Error = result.Error;

            db.Audit.Add(AuditEvent.For(
                job.Id,
                result.Success ? "INTEGRATION_OK" : "INTEGRATION_ERROR",
                $"{adapter.Name} corr={correlationId} externalId={result.ExternalId} error={result.Error}"
            ));

            if (!result.Success)
            {
                job.Status = JobStatus.Failed;
                await db.SaveChangesAsync();

                checks.Add(TestCheck.Pass("execute.finished", "Job finished (Completed or Failed)"));
                checks.Add(TestCheck.Pass("execute.finalStatus", "Failed"));
                return Results.Ok(TestReport.From(checks, new { jobId = job.Id, status = job.Status.ToString() }));
            }

            await db.SaveChangesAsync();
        }

        job.Status = JobStatus.Completed;
        db.Audit.Add(AuditEvent.For(job.Id, "COMPLETED", "All actions executed."));
        await db.SaveChangesAsync();

        checks.Add(TestCheck.Pass("execute.finished", "Job finished (Completed or Failed)"));
        checks.Add(TestCheck.Pass("execute.finalStatus", "Completed"));
    }
    catch (Exception ex)
    {
        checks.Add(TestCheck.Fail("execute", ex.GetType().Name + ": " + ex.Message));
        return Results.Ok(TestReport.From(checks, new { jobId = job.Id, status = job.Status.ToString() }));
    }

    // --- verify
    try
    {
        var jobExists = await db.Jobs.AsNoTracking().AnyAsync(x => x.Id == job.Id);
        checks.Add(jobExists ? TestCheck.Pass("job.exists") : TestCheck.Fail("job.exists"));

        var audit = await db.Audit.AsNoTracking().Where(x => x.JobId == job.Id).ToListAsync();
        checks.Add(audit.Count > 0 ? TestCheck.Pass("audit.exists", $"count={audit.Count}") : TestCheck.Fail("audit.exists"));

        var calls = await db.IntegrationCalls.AsNoTracking().Where(x => x.JobId == job.Id).ToListAsync();
        checks.Add(calls.Count > 0 ? TestCheck.Pass("calls.exists", $"count={calls.Count}") : TestCheck.Fail("calls.exists"));

        var hasEvaluated = audit.Any(a => a.EventType == "EVALUATED");
        checks.Add(hasEvaluated ? TestCheck.Pass("audit.has.EVALUATED") : TestCheck.Fail("audit.has.EVALUATED"));

        if (req.ExpectedActionsCount is not null)
        {
            checks.Add(calls.Count == req.ExpectedActionsCount
                ? TestCheck.Pass("calls.count", $"expected={req.ExpectedActionsCount} actual={calls.Count}")
                : TestCheck.Fail("calls.count", $"expected={req.ExpectedActionsCount} actual={calls.Count}"));
        }
    }
    catch (Exception ex)
    {
        checks.Add(TestCheck.Fail("verify", ex.GetType().Name + ": " + ex.Message));
    }

    return Results.Ok(TestReport.From(checks, new { jobId = job.Id, status = job.Status.ToString() }));
});

app.Run();

// ---------------- Domain / DTOs ----------------

public enum JobStatus
{
    Evaluated,
    Executing,
    Completed,
    Failed
}

public record EvaluateJobDto(
    [Required] string OrderNo,
    [Required] string BatchNo,
    [Required] string AttrCode,
    string? Filename,
    int PiecesTotal,
    [Required] string Priority
);

public class Job
{
    public Guid Id { get; set; }
    public string OrderNo { get; set; } = "";
    public string BatchNo { get; set; } = "";
    public string AttrCode { get; set; } = "";
    public string? Filename { get; set; }
    public int PiecesTotal { get; set; }
    public string Priority { get; set; } = "";

    public JobStatus Status { get; set; } = JobStatus.Evaluated;
    public string Decision { get; set; } = "";
    public string? PlannedActionsJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public class AuditEvent
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public DateTime AtUtc { get; set; }
    public string EventType { get; set; } = "";
    public string Details { get; set; } = "";

    public static AuditEvent For(Guid jobId, string type, string details) =>
        new() { Id = Guid.NewGuid(), JobId = jobId, AtUtc = DateTime.UtcNow, EventType = type, Details = details };
}

public class IntegrationCall
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string Adapter { get; set; } = "";
    public int Attempt { get; set; }
    public string CorrelationId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ExternalId { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}

public class ActionPlanItem
{
    public string Name { get; set; } = "";
    public string Adapter { get; set; } = "";   // MOCK_REST / MOCK_SOAP
    public string Payload { get; set; } = "";   // JSON or XML-like string (demo)
}

public class EvaluationPlan
{
    public string Decision { get; set; } = "";
    public List<ActionPlanItem> Actions { get; set; } = new();
}

// ---------------- Decision Engine ----------------

public class DecisionEngine
{
    public EvaluationPlan Evaluate(Job job)
    {
        var plan = new EvaluationPlan();

        switch (job.AttrCode)
        {
            case "Y":
                plan.Decision = "LABELS_AND_TICKET";
                plan.Actions.Add(new ActionPlanItem
                {
                    Name = "PRINT_LABELS",
                    Adapter = "MOCK_REST",
                    Payload = $$"""{"orderNo":"{{job.OrderNo}}","qty":12,"priority":"{{job.Priority}}"}"""
                });
                plan.Actions.Add(new ActionPlanItem
                {
                    Name = "CREATE_TICKET",
                    Adapter = "MOCK_SOAP",
                    Payload = $$"""<ticket><orderNo>{{job.OrderNo}}</orderNo><batchNo>{{job.BatchNo}}</batchNo></ticket>"""
                });
                break;

            case "Z":
                plan.Decision = "JOBSHEET_AND_TICKET";
                plan.Actions.Add(new ActionPlanItem
                {
                    Name = "PRINT_JOBSHEET",
                    Adapter = "MOCK_REST",
                    Payload = $$"""{"orderNo":"{{job.OrderNo}}","language":"PL"}"""
                });
                plan.Actions.Add(new ActionPlanItem
                {
                    Name = "CREATE_TICKET",
                    Adapter = "MOCK_SOAP",
                    Payload = $$"""<ticket><orderNo>{{job.OrderNo}}</orderNo><type>jobsheet</type></ticket>"""
                });
                break;

            default:
                plan.Decision = "MANUAL_REVIEW";
                break;
        }

        return plan;
    }
}

// ---------------- Integrations (Mock Adapters) ----------------

public record IntegrationResult(bool Success, string? ExternalId, string? Error);

public interface IIntegrationAdapter
{
    string Name { get; }
    Task<IntegrationResult> ExecuteAsync(Job job, ActionPlanItem action, string correlationId);
}

public class MockRestAdapter : IIntegrationAdapter
{
    public string Name => "MOCK_REST";
    private static readonly Random Rng = new();

    public Task<IntegrationResult> ExecuteAsync(Job job, ActionPlanItem action, string correlationId)
    {
        var fail = Rng.NextDouble() < 0.15;
        if (fail) return Task.FromResult(new IntegrationResult(false, null, $"REST 500 simulated (corr={correlationId})"));
        var externalId = $"REST-{DateTime.UtcNow:yyyyMMddHHmmss}-{Rng.Next(1000, 9999)}";
        return Task.FromResult(new IntegrationResult(true, externalId, null));
    }
}

public class MockSoapAdapter : IIntegrationAdapter
{
    public string Name => "MOCK_SOAP";
    private static readonly Random Rng = new();

    public Task<IntegrationResult> ExecuteAsync(Job job, ActionPlanItem action, string correlationId)
    {
        var fail = Rng.NextDouble() < 0.30;
        if (fail) return Task.FromResult(new IntegrationResult(false, null, $"SOAP fault simulated (corr={correlationId})"));
        var externalId = $"SOAP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Rng.Next(1000, 9999)}";
        return Task.FromResult(new IntegrationResult(true, externalId, null));
    }
}

// ---------------- Persistence ----------------

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AuditEvent> Audit => Set<AuditEvent>();
    public DbSet<IntegrationCall> IntegrationCalls => Set<IntegrationCall>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>().Property(x => x.Status).HasConversion<string>();
    }
}

// ---------------- Test DTOs ----------------

public record FlowTestRequest(
    string? AttrCode,
    string? OrderNo,
    string? BatchNo,
    string? Filename,
    int PiecesTotal,
    string? Priority,
    int? ExpectedActionsCount
);

public record TestCheck(string Name, bool Passed, string? Details)
{
    public static TestCheck Pass(string name, string? details = null) => new(name, true, details);
    public static TestCheck Fail(string name, string? details = null) => new(name, false, details);
}


public record TestReport(int Passed, int Failed, List<TestCheck> Checks, object? Context)
{
    public static TestReport From(List<TestCheck> checks, object? context = null)
    {
        var passed = checks.Count(c => c.Passed);
        var failed = checks.Count - passed;
        return new TestReport(passed, failed, checks, context);
    }
}

