# Architecture

## Principle

Monitor is an observability system first and a control plane second. Components should not need to run inside Monitor and should not need to use the same language or agent framework.

The domain therefore does not depend on OpenAI, Anthropic, MCP, LangChain, Semantic Kernel, OpenTelemetry, or any specific runtime.

## Core model

### MonitoredComponent

A deployable or independently observable unit. Examples: agent, MCP server, Discord bot, workflow, scheduled job, scraper, background service.

Identity is `(Slug, Environment)`. A deployment can register repeatedly; registration is idempotent and updates mutable metadata.

### AgentRun

One execution or unit of work. This is the main operational object, rather than a log line. It owns timing, terminal status, model information, token usage, cost, input/output payloads, and error information.

### TraceSpan

One nested operation inside a run. Spans can represent agent reasoning steps, model calls, tool calls, HTTP calls, or ordinary internal work. `ParentSpanId` permits a trace tree without coupling the domain to a telemetry vendor.

## Transport

The initial HTTP API exists to make the first end-to-end slice usable immediately. It is not intended to become a custom observability standard.

The planned primary ingestion path is OpenTelemetry/OTLP. Incoming OTLP traces and GenAI semantic-convention attributes will be mapped into the same component/run/span domain.

## Persistence

SQLite is the development default because `git clone && dotnet run` should work without infrastructure. `MonitorDbContext` is isolated in Infrastructure so a production provider such as PostgreSQL or SQL Server can replace it without changing the domain or UI contract.

## Control plane

Commands are intentionally absent from the first slice. Observability must be trustworthy before Monitor is allowed to alter remote workloads. Future commands should be auditable entities with requested/accepted/completed states rather than fire-and-forget HTTP actions.
