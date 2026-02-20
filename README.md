# process-decision-service

Lightweight **decision-processing backend service** built with **.NET 8** and **ASP.NET Minimal API**.

The service is designed to model, evaluate, and execute deterministic decision logic via a simple HTTP interface, following a container-first deployment approach.

---

## Overview

`process-decision-service` provides a minimal, extensible backend skeleton focused on predictable execution and clean separation of concerns.

Instead of embedding business logic directly into client applications, decision flows can be centralized and executed by the service.

The project emphasizes:

- Deterministic processing behavior
- Stateless service design
- Minimal dependency footprint
- Container-native deployment model
- Clean architecture baseline for further extensions

---

## Core Responsibilities

The service acts as a decision / rule execution layer:

- Accepts HTTP requests
- Validates and normalizes input payloads
- Executes decision logic / rules / flows
- Returns structured responses

Designed to serve as:

- Decision engine prototype
- Backend microservice skeleton
- Rule/flow evaluation layer
- Internal automation building block

---

## Architectural Model

High-level flow:

Client → HTTP API → Decision Engine → Response

Key characteristics:

- **Stateless processing**
- **Deterministic outputs**
- **Clear separation between API and logic**
- **Container-friendly runtime**

Primary components:

- **API Layer**  
  Handles request processing, validation, and response shaping.

- **Decision Engine**  
  Encapsulates rule execution / decision logic.

- **Configuration Layer**  
  Provides runtime behavior tuning via `appsettings.json` / environment variables.

- **Container Layer**  
  Docker-based deployment model.

---

## Technology Stack

- **.NET 8**
- **ASP.NET Minimal API**
- **C#**
- **Docker / Docker Compose**

Design goals:

- Lightweight runtime
- Fast startup characteristics
- Low operational complexity
- Easy horizontal scaling

---

## Running the Service

### Docker (recommended)

```bash
docker compose up -d --build
