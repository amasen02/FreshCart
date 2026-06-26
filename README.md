# FreshCart &mdash; Online Supermarket Reference Architecture

> **Start here:** open **[`index.html`](index.html)** in any browser. That single page is the
> animated architecture guide, the demo-credentials sheet, the operations runbook and the
> interview tour, all in one self-contained document.

[![.NET](https://img.shields.io/badge/.NET-10-blueviolet)](https://dotnet.microsoft.com/)
[![Frontend: Angular 20](https://img.shields.io/badge/frontend-Angular%2020-red)](https://github.com/amasen02/freshcart-web)
[![Aspire](https://img.shields.io/badge/Aspire-13-blue)](https://learn.microsoft.com/en-us/dotnet/aspire/)
[![Azure](https://img.shields.io/badge/Azure-AKS-0078d4)](https://azure.microsoft.com/en-us/products/kubernetes-service)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![PRs welcome](https://img.shields.io/badge/PRs-welcome-brightgreen)](CONTRIBUTING.md)
[![Contributor Covenant](https://img.shields.io/badge/Contributor%20Covenant-2.1-blue)](CODE_OF_CONDUCT.md)

The backend platform for FreshCart: twelve bounded contexts, each in a deliberately different
architectural style, behind a YARP gateway, hosted on Azure Kubernetes Service through Azure
DevOps or GitHub Actions multi-stage pipelines. The Angular 20 signal-based storefront lives in
a companion repository &mdash; **[amasen02/freshcart-web](https://github.com/amasen02/freshcart-web)**.

---

## Try it now &mdash; demo URLs + seeded credentials

The platform ships with three pre-seeded accounts so you can sign in and walk through the
customer and back-office flows in under a minute. The
[`IdentityDataSeeder`](src/Services/Identity/FreshCart.Identity.Infrastructure/Persistence/IdentityDataSeeder.cs)
creates them automatically on first boot when `ASPNETCORE_ENVIRONMENT=Development`. It refuses
to run in any other environment.

### Boot the platform

```bash
# 0. Clone
git clone https://github.com/amasen02/freshcart-backend.git && cd freshcart-backend

# 1. Backing services (SQL Server, Postgres, MySQL, MongoDB, Redis, RabbitMQ, Seq, Grafana, Prometheus)
docker compose -f deploy/docker/docker-compose.yaml up -d

# 2. Whole .NET stack via Aspire — boots every microservice + the gateway + seeds demo accounts
dotnet run --project src/AspireAppHost/FreshCart.AppHost

# 3. Customer storefront (separate repo) — ng serve proxies /api and /hubs to the gateway on 7100
git clone https://github.com/amasen02/freshcart-web.git
cd freshcart-web && npm install && npm start
```

### Open in the browser

| Surface | URL | What it shows |
|---|---|---|
| **Customer storefront** | http://localhost:4200 | Catalog, basket, checkout, orders, support chat, real-time notifications |
| **Aspire dashboard** | http://localhost:15888 | Every service + live traces, metrics, logs |
| **YARP API Gateway** | https://localhost:7100 | Single public edge; cookie-to-JWT BFF exchange |
| **Identity API** (OpenAPI) | https://localhost:7101 | Sign-up, sign-in, refresh, MFA enrollment |
| **Reporting API** (OpenAPI) | https://localhost:7110 | Dashboards, invoices, Excel exports |
| **Seq** (logs) | http://localhost:5341 | Structured logs across every service |
| **Grafana** (metrics) | http://localhost:3000 | RED + USE dashboards (`admin / freshcart_local_dev`) |
| **Prometheus** | http://localhost:9090 | Raw metric store |
| **RabbitMQ management** | http://localhost:15672 | Queues + exchanges (`freshcart / freshcart_local_dev`) |

### Sign in with one of the seeded accounts

| Role | Email | Password | What this account can do |
|---|---|---|---|
| **Customer** | `demo@freshcart.test` | `Demo-P@ssw0rd-2026` | Browse catalog, add to basket, check out, view orders, chat to support |
| **SupportAgent** | `support@freshcart.test` | `Support-P@ssw0rd-2026` | Answer support chats; query the Reporting API for order context |
| **Administrator** | `admin@freshcart.test` | `Admin-P@ssw0rd-2026` | Reporting API: KPI dashboards, invoices, inventory health, exports |

> **Production safety.** The seeder is guarded by `IHostEnvironment.IsDevelopment()` and the
> sign-up validator rejects any `*.test` email &mdash; these accounts cannot exist in
> Staging or Production.

> The admin SPA is a planned phase (see [`CHANGELOG.md`](CHANGELOG.md)). Until it ships, the
> `SupportAgent` and `Administrator` surfaces are reached through the Reporting API and the
> support hub directly.

### 90-second guided tour after sign-in

1. Open <http://localhost:4200> &rarr; sign in as `demo@freshcart.test`.
2. Browse the catalog (30 seeded products across 8 categories), add three items to the basket.
3. Check out &mdash; accept the default address. The Ordering saga drives stock reservation,
   payment capture, and delivery booking.
4. A toast notification appears within ~1 second &mdash; that is the `NotificationHub`
   SignalR push through the Redis backplane.
5. Open the support panel and start a chat; a `SupportAgent` session picks it up over the
   WebSocket hub.
6. Hit the Reporting API at <https://localhost:7110> with the `admin@freshcart.test` token &rarr;
   *Sales overview* reflects the test order in the GMV and order-count tiles.
7. `POST /invoices` for the order &rarr; a QuestPDF-rendered PDF opens via a 15-minute Azure Blob
   SAS URL.
8. `GET /exports/sales-transactions.xlsx` &mdash; ClosedXML Excel with frozen header and
   auto-width columns.

---

## Why this repo exists

The candidate's CV lists eleven years of work across .NET, Angular, Azure, AWS, IdentityServer4,
SignalR, RabbitMQ, microservices, DDD, CQRS, Event Sourcing, Saga, polyglot persistence,
EF Core + Dapper hybrid, SonarQube, Prometheus + Grafana, AKS, EKS, Azure DevOps CI/CD.

A long list of skills is unfalsifiable. This repository is the **falsifiable evidence** for the
backend &mdash; every claim anchored to running code, an ADR, and a walkthrough card. The Angular
front-end evidence lives in the companion repo
[freshcart-web](https://github.com/amasen02/freshcart-web).

---

## Domain &mdash; twelve bounded contexts

| # | Context | Architecture | Persistence | Status |
|---|---|---|---|---|
| 1 | **Identity** | Clean Architecture + ASP.NET Identity | Azure SQL | Built |
| 2 | **Catalog** | Vertical Slice + Carter + MediatR | PostgreSQL + Marten | Built |
| 3 | **Pricing** | gRPC service | SQLite (in-container) | Built |
| 4 | **Basket** | Vertical Slice + Outbox + Decorator | PostgreSQL + Redis HybridCache | Built |
| 5 | **Ordering** | Clean / Onion + DDD + Saga | SQL Server (EF Core writes, Dapper reads) | Built |
| 6 | **Inventory** | Layered (3-tier) + Dapper | SQL Server | Built |
| 7 | **Payment** | Clean + Event Sourcing | MongoDB events + SQL Server projection | Built |
| 8 | **Delivery** | Hexagonal (Ports & Adapters) | MongoDB (geo indexes) | Built |
| 9 | **Notification** | Pipes-and-filters + SignalR | MongoDB + Redis backplane | Built |
| 10 | **CustomerSupport** | Stateful WebSocket | MongoDB transcripts + Redis presence | Built |
| 11 | **Reviews** | Vertical Slice + Document DB | MongoDB | Built |
| 12 | **Reporting** | CQRS read model + QuestPDF | MySQL warehouse + Blob Storage | Built |

Edge and clients:

| Surface | Technology | Status |
|---|---|---|
| **YARP API Gateway** | .NET 10 + YARP; cookie-to-JWT BFF exchange | Built |
| **AdminBackoffice** | Modular monolith (`IModule` pattern) | Planned |
| **Customer SPA** | Angular 20 standalone + signals + Bootstrap 5 | Built &mdash; separate repo: [freshcart-web](https://github.com/amasen02/freshcart-web) |
| **Admin SPA** | Angular 20 standalone + signals + Bootstrap 5 | Planned &mdash; will live in [freshcart-web](https://github.com/amasen02/freshcart-web) |

The browser clients live in the companion frontend repository
[**amasen02/freshcart-web**](https://github.com/amasen02/freshcart-web); this repository is the
backend platform they reach through the gateway.

`BuildingBlocks` carries the cross-cutting libraries: `BuildingBlocks` (CQRS, behaviors,
exception handling, pagination, security), `BuildingBlocks.Messaging` (integration event
contracts, outbox, MassTransit wiring), `BuildingBlocks.Observability`, and `ServiceDefaults`.

---

## Reporting suite

The Reporting service is a first-class member of the platform:

- **Executive KPI dashboards** &mdash; GMV, net revenue, AOV, refund rate, customer LTV,
  delivery success rate, inventory health.
- **PDF invoice generation** via QuestPDF with gap-free per-year per-kind invoice numbering
  (`INV-2026-000123`, `CR-2026-000018`, `PF-2026-000009`).
- **Excel exports** via ClosedXML (managed-only, no Office install).
- **Daily scheduled reports** dropped into Azure Blob Storage by a `BackgroundService`.
- **Event projection pipeline** &mdash; `OrderConfirmed`, `OrderRefunded` integration events
  consumed by MassTransit, UPSERT-ed into denormalised MySQL tables. Idempotent at every hop
  via the projection inbox.

See [`index.html`](index.html) &rarr; *Reporting & Invoices* tab for the full surface.

---

## Cross-cutting concerns

- **Authentication.** ASP.NET Identity + **HttpOnly + Secure + SameSite=Strict cookies** for
  the browser; JWT bearer for service-to-service. Anti-forgery double-submit on every
  state-changing endpoint.
- **Authorisation.** Per-endpoint policies (`Customer`, `SupportAgent`, `Administrator`,
  `BackOfficeUser`); resource-based authorization handlers defeat BOLA.
- **Observability.** OpenTelemetry &rarr; OTLP &rarr; Azure App Insights + Log Analytics;
  Azure Managed Prometheus + Managed Grafana. W3C TraceContext propagated through HTTP,
  gRPC, MassTransit, SignalR.
- **Resilience.** `AddStandardResilienceHandler()` on every typed HttpClient (retry +
  circuit breaker + timeout + bulkhead).
- **Validation.** FluentValidation dispatched by the MediatR `ValidationBehavior`.
- **Errors.** Single `CustomExceptionHandler` mapping domain exceptions to RFC 7807
  `ProblemDetails` with `traceId`.
- **Outbox.** Transactional outbox in Basket and Ordering; `OutboxPublisher` background
  worker; consumers idempotent via inbox.
- **Security headers.** `UseFreshCartSecurityHeaders()` middleware applies CSP strict,
  `X-Frame-Options=DENY`, `Referrer-Policy`, `Permissions-Policy`, COOP/COEP/CORP.
- **SSRF defence.** `OutboundUrlAllowListHandler` on every typed HttpClient + AKS egress
  NetworkPolicy blocks `169.254.169.254`.
- **Crypto.** Argon2id password hashing (replaces PBKDF2). Refresh-token reuse detection.

Full OWASP Top-10 2025 mapping in
[`docs/adr/ADR-0004-owasp-top-10-control-mapping.md`](docs/adr/ADR-0004-owasp-top-10-control-mapping.md).

---

## Reliability &amp; correctness &mdash; proven, not claimed

The hard parts of a distributed system are the races. Each guarantee below is backed by a
real-database concurrency test (Testcontainers spins up the actual engine and fires N parallel
callers) rather than asserted in prose:

- **Exactly-once read projections (Reporting).** The idempotency record commits in the *same*
  transaction as the projection, so an at-least-once redelivery can never double-count an additive
  aggregate (refund totals, customer lifetime value). *Proven by a redelivery test and a 12-way
  concurrent MySQL test.* (REP-001)
- **No oversubscribed delivery slots.** Booking is a single conditional increment guarded by
  `BookedCount < Capacity`; the loser of a race is redelivered and re-scheduled against the
  remaining slots. *Proven by a 20-way concurrent MongoDB test.* (DLV-001)
- **Gap-free, collision-free invoice numbers.** Allocation is one atomic upsert
  (`INSERT … ON DUPLICATE KEY UPDATE … LAST_INSERT_ID`), never a read-then-write. *Proven by a
  25-way concurrent MySQL test yielding exactly `{1..25}`.* (REP-002)
- **Single-use refresh tokens.** Rotation is claimed with one conditional `UPDATE`; a concurrent
  reuse is treated as a stolen-token replay and revokes the whole token family. *Proven by an
  8-way concurrent SQL Server test.* (ID-COR-01)
- **Idempotent payment refunds.** The refund carries an idempotency key recorded on the
  `PaymentRefunded` event, so a retried refund replays the recorded outcome instead of charging the
  customer twice. (ORD-002)
- **Durable event contracts.** Outboxed events resolve by their version-independent type name, so a
  deployment that bumps an assembly version never dead-letters an in-flight event. (BB-002)
- **No duplicate outbox publishes across replicas.** Each drain *claims* its batch with an atomic
  conditional update (EF Core `ExecuteUpdate` on SQL Server, Marten `Patch` on PostgreSQL) and a
  crash-recovering lease, so two publisher replicas always claim disjoint sets. *Proven by a concurrent
  two-drainer test per store.* (BSK-01)
- **Delivery events never lost on a broker outage.** The delivery document and its `DeliveryScheduled` /
  `DeliveryCompleted` event commit in one MongoDB transaction (a single-node replica set), then a
  claim-by-update outbox publisher delivers the event; a failed business write stages no event and a failed
  event write rolls the delivery back. *Proven by replica-set atomicity, claim-disjointness and
  publish-lifecycle tests.* (DLV-002/003)
- **No double charge from a payment dual write.** The event append and a projection marker commit in one
  MongoDB transaction; a background projector replays each stream to SQL idempotently, and the capture
  idempotency / one-payment-per-order invariant lives in the event store (partial unique index on the
  initiating event's `OrderId`), not the asynchronously-projected read model. *Proven by replica-set
  atomicity, source-of-truth-uniqueness and projector convergence tests.* (PAY-003)
- **Product creation never duplicates a SKU or 500s on a race.** The check-then-write is backed by a unique
  index on `Sku`; the writer that loses a concurrent race has its violation mapped to a 409 instead of a
  raw database error. *Proven by a 15-way concurrent PostgreSQL/Marten test yielding one product and the
  rest conflicts.* (CAT-001)

Every item traces to an audit finding; the fixes and their verification are recorded in
[`CHANGELOG.md`](CHANGELOG.md), and the originating review is
[`docs/google-standards-audit-2026-06-23.md`](docs/google-standards-audit-2026-06-23.md).

---

## Infrastructure &amp; deployment

- **Local.** Docker Compose + .NET Aspire AppHost.
- **Azure.** Bicep modules under [`infra/`](infra/) provision AKS, ACR, Azure SQL,
  Postgres Flexible Server, MySQL Flexible Server, Cosmos DB, Cache for Redis, Service Bus,
  Key Vault, App Configuration, Log Analytics, App Insights, Front Door + WAF, Storage,
  VNet + private endpoints.
- **CI/CD.** Two equivalent paths ship in the repo:
  - [`azure-pipelines/`](azure-pipelines/) &mdash; multi-stage YAML, AZ-204-grade.
  - [`.github/workflows/`](.github/workflows/) &mdash; reusable workflows, OIDC federated
    identity (no client secrets), CodeQL, Dependabot, Playwright e2e on PR.
- **Helm.** One chart per service in [`deploy/helm/`](deploy/helm/) with
  `values-dev.yaml`, `values-staging.yaml`, `values-prod.yaml`. PodSecurity restricted,
  non-root, read-only rootfs, NetworkPolicy default-deny.
- **GitOps.** Optional overlay under [`gitops/`](gitops/) for Flux / ArgoCD.

---

## Repository layout

```
freshcart-backend/
├── index.html                  ← LANDING PAGE — open this first
├── README.md                   ← this file
├── ARCHITECTURE.md             ← C4 narrative (C1 + C2 + C3)
├── CHANGELOG.md                ← gradual version history (Keep-a-Changelog format)
├── docs/                       ← ADRs · threat models · interview-tour cards · conventions
├── src/
│   ├── BuildingBlocks/         ← CQRS, Messaging, Observability, ServiceDefaults
│   ├── Services/               ← twelve bounded contexts (Identity … Reporting)
│   ├── ApiGateways/            ← YARP gateway + gateway tests
│   ├── ModularMonolith/        ← AdminBackoffice (planned)
│   └── AspireAppHost/          ← local orchestration
│   (frontend storefront lives in a separate repo → github.com/amasen02/freshcart-web)
├── deploy/
│   ├── docker/                 ← docker-compose stack
│   ├── helm/                   ← Helm charts
│   └── k8s/                    ← raw manifests (teaching reference)
├── infra/                      ← Bicep modules + env params
├── azure-pipelines/            ← Azure DevOps multi-stage YAML
├── .github/workflows/          ← GitHub Actions
├── gitops/                     ← Flux / ArgoCD manifests
└── tests/
    ├── integration/            ← Testcontainers
    ├── contract/               ← Pact
    ├── load/                   ← k6
    └── e2e/                    ← Playwright (3-browser + mobile matrix)
```

---

## Tests

- **Unit + integration.** xUnit + FluentAssertions + NSubstitute + Testcontainers (real SQL
  Server / Postgres / MongoDB / Redis / RabbitMQ in Docker).
- **End-to-end.** Playwright &mdash; three browser projects (Chromium / Firefox / WebKit) +
  mobile viewport. Tests under [`tests/e2e/specs/`](tests/e2e/specs/) drive the storefront from
  [freshcart-web](https://github.com/amasen02/freshcart-web) and cover the cookie sign-in flow and
  the full customer happy path; the admin reporting dashboard spec is skipped until the admin SPA
  ships.
- **Contract / load.** Pact (provider verification) and k6 are planned phases
  (see [`CHANGELOG.md`](CHANGELOG.md)).
- **Static.** SonarCloud + Trivy + CodeQL + `dotnet list package --vulnerable` &mdash; all
  wired into both CI/CD paths.

---

## Documentation

Open the **[`index.html`](index.html)** landing page for the animated architecture guide. The
deeper references are:

- [`ARCHITECTURE.md`](ARCHITECTURE.md) &mdash; C4 narrative + cross-cutting map.
- [`docs/CONVENTIONS.md`](docs/CONVENTIONS.md) &mdash; naming, async, LINQ discipline.
- [`docs/adr/`](docs/adr/) &mdash; architecture decision records.
- [`docs/interview-tour/`](docs/interview-tour/) &mdash; 90-second pitch per service.
- [`docs/threat-models/`](docs/threat-models/) &mdash; STRIDE per bounded context.
- [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) &mdash; per-service functional + non-functional requirements with a traceability matrix.
- [`docs/CLASS_DIAGRAMS.md`](docs/CLASS_DIAGRAMS.md) &mdash; Mermaid class + sequence diagrams.
- [`docs/INTERNAL_ARCHITECTURE.md`](docs/INTERNAL_ARCHITECTURE.md) &mdash; per-service deep dive.
- [`CHANGELOG.md`](CHANGELOG.md) &mdash; gradual version history (Keep a Changelog format).
- [`SECURITY.md`](SECURITY.md) &mdash; security policy + control table.
- [`CONTRIBUTING.md`](CONTRIBUTING.md) &mdash; coding standards summary.

---

## Contributing &amp; forking

This is open source under the MIT licence &mdash; **fork it, build on it, take it in your own
direction.** To run it locally, clone and follow the quickstart above (`dotnet run` the Aspire
AppHost, or `deploy/docker/docker-compose.yaml`); the seeded demo accounts are in the table near the
top of this README.

- [`CONTRIBUTING.md`](CONTRIBUTING.md) &mdash; build/test/PR workflow and the coding bar.
- [`docs/CONVENTIONS.md`](docs/CONVENTIONS.md) &mdash; the full coding standards.
- [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) &mdash; Contributor Covenant.
- [`SECURITY.md`](SECURITY.md) &mdash; report vulnerabilities privately, never as a public issue.
- Use the issue templates for bug reports and feature/pattern proposals; green CI (build + tests) is
  required on every pull request.

---

## Open source commitments

This project is, and will remain, free and open source. As maintainer I commit to:

- **A permissive licence, kept stable.** [MIT](LICENSE) &mdash; use it commercially, fork it, build
  on it. No relicensing of accepted contributions.
- **No CLA.** Contributions are accepted under the MIT licence; you keep the copyright to your work.
- **An honest history.** Real, walkable commits tied to shippable increments &mdash; no fabricated
  activity and no rewritten releases.
- **Best-effort, transparent triage.** Issues and pull requests are read and answered; security
  reports are acknowledged within 72 hours (see [`SECURITY.md`](SECURITY.md)).
- **A welcoming community** governed by the [Code of Conduct](CODE_OF_CONDUCT.md).
- **Reproducible builds.** Green CI &mdash; build, tests, SonarCloud quality gate, and Trivy image
  scan &mdash; on every change.

---

## License

MIT &mdash; see [`LICENSE`](LICENSE). You are free to use, modify, and distribute this software,
including for commercial purposes, provided the copyright notice is retained.

## Author

**Ama Senevirathne** &mdash; Senior Software Engineer & Tech Lead. Eleven years on
C# / ASP.NET Core / Angular / Azure / AWS.

- [LinkedIn](https://linkedin.com/in/ama-sen)
- [GitHub](https://github.com/amasen02)
- Email: amabandarasp@gmail.com
