# Monitor

A self-hostable operations and observability control plane for autonomous software: AI agents, MCP servers, bots, workflows, scheduled jobs, scrapers, and background services.

Monitor starts with one deliberately small model:

```text
MonitoredComponent
  └─ AgentRun
       └─ TraceSpan
```

The goal is to make every autonomous component answer the same questions: Is it alive? What is it doing? What did it do? How long did it take? What tools/models did it call? How many tokens did it use? What did it cost? What failed?

## Current vertical slice

- Component registry with environment, version, type, enabled state, and heartbeat.
- Run ingestion with input/output, model, token usage, cost, failure state, and timing.
- Nested trace spans for agent/model/tool/http/internal work.
- Dark Razor dashboard with system health and recent activity.
- Components, runs, and run-detail screens.
- SQLite development persistence with EF Core.
- GitHub Actions build.

This first API is intentionally simple HTTP. Native OpenTelemetry/OTLP ingestion is the next transport; the domain model is kept independent from the ingestion protocol.

## Run locally

Requirements: .NET 10 SDK.

```bash
dotnet restore Monitor.sln
dotnet run --project src/Monitor.Web
```

The application creates `monitor.db` on first startup.

## Register a component

```bash
curl -X POST http://localhost:5000/api/components/register \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Website Auditor",
    "slug": "website-auditor",
    "type": "Agent",
    "environment": "development",
    "version": "0.1.0"
  }'
```

Then send a heartbeat using the returned component id:

```bash
curl -X POST http://localhost:5000/api/components/{componentId}/heartbeat
```

## Start a run

```bash
curl -X POST http://localhost:5000/api/runs \
  -H "Content-Type: application/json" \
  -d '{
    "componentId": "{componentId}",
    "name": "Audit website",
    "trigger": "Manual",
    "model": "gpt-5.6"
  }'
```

Add spans while the run is executing:

```bash
curl -X POST http://localhost:5000/api/runs/{runId}/spans \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Fetch homepage",
    "kind": "Http",
    "status": "Success",
    "startedAt": "2026-08-07T20:00:00Z",
    "completedAt": "2026-08-07T20:00:00.400Z"
  }'
```

Complete it:

```bash
curl -X POST http://localhost:5000/api/runs/{runId}/complete \
  -H "Content-Type: application/json" \
  -d '{
    "status": "Success",
    "inputTokens": 8421,
    "outputTokens": 2119,
    "costUsd": 0.041,
    "outputJson": "{\"result\":\"ok\"}"
  }'
```

## Structure

```text
src/
  Monitor.Domain/          protocol-agnostic monitoring model
  Monitor.Infrastructure/  EF Core persistence
  Monitor.Web/             HTTP ingestion API + Razor control plane

docs/
  architecture.md
```

## Next

1. Replace `EnsureCreated` with versioned EF migrations.
2. OTLP receiver / OpenTelemetry semantic-convention mapping.
3. Live activity stream using SignalR or SSE.
4. API-key authentication for components.
5. Retention, aggregation, and cost/model dashboards.
6. Control-plane commands (pause, disable, kill run, configuration).
7. A tiny .NET client package and sample worker to dogfood the ingestion API.
