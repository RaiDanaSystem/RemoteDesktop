# Anbarino project skills

Selected from [alirezarezvani/claude-skills](https://github.com/alirezarezvani/claude-skills) for this WMS stack:

- Backend: .NET 8 Clean Architecture + CQRS/MediatR + EF Core
- FrontEnd: React + Vite + TypeScript + Tailwind + design system
- Ops: Docker, PrintAgent, Integration engine, Playwright, secrets

Project Cursor **rules** (`.cursor/rules/`) still win when they conflict with a skill (UI design system, versioning, ponytail, Persian RTL chat, menu/permissions sync).

## When to use what

| Need | Skill |
|------|--------|
| Architecture / module boundaries | `senior-architect` |
| API, auth, handlers, domain logic | `senior-backend` |
| React pages, forms, tables | `senior-frontend`, `ui-design-system` |
| End-to-end feature across API+UI | `senior-fullstack` |
| Tests / TDD | `senior-qa`, `tdd-guide`, `playwright-pro` (+ `playwright-*`) |
| Review PR / diff | `code-reviewer`, `pr-review-expert` |
| EF Core schema / SQL / migrations | `database-designer`, `database-schema-designer`, `migration-architect`, `sql-database-assistant` |
| REST contract quality | `api-design-reviewer`, `api-test-suite-builder` |
| Secrets / appsettings | `env-secrets-manager`, `security-guidance`, `senior-security` |
| Docker / CI | `docker-development`, `senior-devops`, `ci-cd-pipeline-builder` |
| Perf / observability | `performance-profiler`, `observability-designer` |
| Spec → implementation | `spec-driven-workflow`, `spec-to-repo`, `zero-hallucination-coder`, `ship-gate` |
| Release notes / debt | `changelog-generator`, `tech-debt-tracker`, `dependency-auditor`, `runbook-generator` |
| a11y / RTL-related UI checks | `a11y-audit` |

## Intentionally not imported

Marketing, C-level advisory, academic research, medical-device RA (MDR/FDA), cloud-only (AWS/GCP/K8s/Helm/Terraform), RAG/agent-designer, Snowflake, Stripe — not day-to-day for Anbarino coding.

## Source

Upstream: `alirezarezvani/claude-skills` (MIT). Re-sync by re-copying skill folders into `.cursor/skills/<name>/`.
