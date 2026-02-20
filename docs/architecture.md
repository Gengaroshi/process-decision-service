## Architecture (anonimizowane)

ProcessDecisionService działa jako niezależny komponent BPM/Decision Engine:

Wejście:
- dane operacyjne (Order / Batch / atrybuty)

Przetwarzanie:
- walidacja
- reguły biznesowe (routing/decision)
- zapis stanu + audyt
- wykonanie akcji integracyjnych (adaptery: REST/SOAP‑like, tu: mock)

Wyjście:
- zaplanowane akcje
- wynik wykonania (status, externalId)
- audit trail i metryki
