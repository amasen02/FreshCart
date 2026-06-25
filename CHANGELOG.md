# Changelog

All notable changes are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each version corresponds to one shippable increment of the
[phase plan](./README.md#try-it-now--demo-urls--seeded-credentials) so the project history is
walkable end-to-end.

---

## [Unreleased]

### Security

- Eliminated the critical `Marten` full-text-search SQL-injection advisory (GHSA-vmw2-qwm8-x84c) by
  upgrading `Marten` 7.30.0 &rarr; 8.37.0 (the first patched release); bumped the central `Npgsql` pin
  to 9.0.4 to satisfy Marten 8's transitive floor, and migrated the two relocated Marten 8 symbols
  (`AutoCreate`, `ConcurrencyException`, now under the consolidated `JasperFx` namespace) in
  `Basket` and `Catalog`.
- Cleared the high-severity `MessagePack` (GHSA-hv8m-jj95-wg3x &rarr; 2.5.301 transitive pin) and
  `Snappier` (GHSA-pggp-6c3x-2xmx &rarr; 1.3.1) advisories; advanced `SharpCompress` to 0.47.4.
- Replaced the blanket `NU1902;NU1903` warning demotion in `Directory.Build.props` with two targeted,
  documented `NuGetAuditSuppress` entries for the only two transitives with no published fix
  (`SharpCompress` path-traversal, `SQLitePCLRaw` bundled-SQLite), each reached on a non-exploitable
  path &mdash; so any *new* or *patchable* vulnerability now fails the build again.
- Removed committed credentials from every base `appsettings.json` across **10 services** (Basket,
  Catalog, Ordering, Payment, Identity, Inventory, Pricing, Delivery, Notification, CustomerSupport,
  Reviews, Reporting): the credential-bearing `ConnectionStrings` and `MessageBroker` blocks now live in
  `appsettings.Development.json` (dev-only), matching the existing `Jwt:SigningKey` convention. Production
  and the Aspire AppHost inject these via environment at runtime; base config is now credential-free.
- **Service-to-service authentication.** The Ordering saga previously called Payment (HTTP) and
  Inventory (gRPC) with no credential, while those endpoints required authorization &mdash; a latent
  failure of the integrated checkout in any environment that enforces auth. Introduced a shared
  `ServiceAuthenticationDefaults` (role `ServiceAccount`, policy `ServiceCaller`) and a cached
  `ServiceTokenProvider` in Ordering that mints a short-lived JWT signed with the shared key; a
  `DelegatingHandler` attaches it to Payment calls and a gRPC metadata header to Inventory calls. The
  Payment capture endpoint and the Inventory reserve/release gRPC service now require the `ServiceCaller`
  policy instead of "any authenticated user", which also closes the Payment capture BOLA (`CustomerId`
  was trusted from the request body) and the bare-`[Authorize]` gRPC surface.
- **CSRF enforcement (Identity).** Added the missing `app.UseAntiforgery()` to the pipeline and a reusable
  `AntiforgeryConfiguration.ValidateBrowserRequestAsync` that validates the `XSRF-TOKEN` / `X-XSRF-TOKEN`
  double-submit pair (issued but never validated) for cookie-bearing browser requests, while deliberately
  skipping bearer / service callers that carry no ambient cookie and are not CSRF-exposed. The validator is
  safe-method-aware (read endpoints are unaffected) and is enforced on sign-out and on the MFA
  enroll/verify/disable mutations via a group endpoint filter (ID-SEC-01).
- **Invoice-download BOLA and path traversal (REP-005).** The two invoice GET routes
  (`/invoices/{invoiceNumber}` and `.../content.pdf`) carried only the group-level `RequireAuthorization`,
  so any authenticated customer could enumerate the gap-free invoice numbers and download other customers'
  invoice PDFs (names, addresses, line items). Both now require the `BackOfficeUser` role like every other
  reporting route. `StreamPdfAsync` also passed the raw route value straight into the blob path; it now
  validates via `InvoiceNumber.TryParse` and addresses the blob with the normalised `parsed.Value`,
  returning 400 on malformed input.
- Verified: full solution builds with 0 errors; the entire test suite is green &mdash;
  Basket 92, Catalog 155, Payment 82, Ordering 70, Delivery 56, Reviews 53, CustomerSupport 46,
  Pricing 47, Notification 42, Identity 38, Gateway 38, BuildingBlocks 37, Reporting 42, Inventory 7.

### Reliability

- **Outbox poison-message dead-lettering (BB-001).** A message that repeatedly failed to publish was
  re-fetched and re-failed on every drain cycle forever, wedging the queue behind it. `OutboxMessage.MarkFailed`
  now dead-letters a message once it reaches `OutboxPublisherOptions.MaxRetryAttempts` (default 5): it is
  stamped processed so the publisher stops polling it, the reason is recorded with a `DEAD-LETTERED` marker,
  and the publisher raises a Critical alert. The decision is centralised on the entity (unit-tested with no
  database); both the Marten (Basket) and EF Core (Ordering) stores delegate to it. No schema change required.
- **Notification redelivery on a recipient race (NOTIF-001).** `PaymentFailedConsumer` and
  `OrderRefundedConsumer` silently acknowledged (dropped) the event whenever the recipient lookup found
  nothing &mdash; which happens when the event outruns the `OrderPlaced` / `OrderConfirmed` projection that
  records the recipient. They now throw, so MassTransit's bounded exponential retry redelivers (and
  dead-letters to the error queue once the budget is exhausted), turning a silent drop into at-least-once
  delivery with an observable failure.
- **Idempotent stock seeding (INV-001).** `ProductCreatedConsumer` overwrote the stock row on every
  delivery via an upsert, so an at-least-once redelivery reset `QuantityOnHand` to the initial value and
  wiped real stock movement. New `IStockRepository.EnsureExistsAsync` (atomic insert-if-absent via
  `MERGE ... WHEN NOT MATCHED`) and `IStockLevelService.EnsureStockItemAsync` seed the row only on first
  sight; the consumer now uses them, and the destructive `SetStockLevel` is reserved for the admin PUT.
- **Atomic delivery-slot booking (DLV-001).** `ScheduleDeliveryService` listed a slot, incremented its
  booked count in memory, then persisted it with an unconditional `Set` filtered only by id, so two
  `OrderConfirmed` consumers that both read a slot at 4/5 each wrote 5 &mdash; oversubscribing the fleet.
  `ISlotRepository.UpdateBookingAsync` becomes `TryBookSlotAsync`, a single `UpdateOne` with
  `Inc(BookedCount, 1)` filtered by `id AND BookedCount < Capacity`; exactly one racing scheduler gets
  `ModifiedCount == 1`. The booking now precedes creating the delivery, and a lost race throws the
  retriable `SlotNoLongerAvailableException` so the MassTransit retry re-schedules against the remaining
  open slots. Proven with a 20-way concurrent Testcontainers test.
- **Exactly-once warehouse projections (REP-001).** The reporting projection consumers checked an EF
  inbox, applied a Dapper projection, then recorded the inbox &mdash; three steps across two connections.
  A crash between the apply and the record re-applied the event on redelivery, and both the refund total
  and the customer lifetime value are additive, so the redelivery silently double-counted. The inbox
  record now shares the projection's Dapper transaction: `WarehouseProjectionWriter` inserts the
  `projection_inbox` row first as a primary-key latch, applies the projection, and commits atomically; a
  duplicate key means already-processed (returns `false`). The standalone `IProjectionInbox` is removed.
  Proven with MySQL Testcontainers tests for redelivery and 12-way concurrent delivery.
- **Atomic invoice-number allocation (REP-002).** `AllocateNextNumberAsync` claimed a `FOR UPDATE` lock
  but did a plain EF read-increment-save with no concurrency token, so concurrent invoice generation
  handed two invoices the same number. It is now a single atomic upsert
  (`INSERT ... ON DUPLICATE KEY UPDATE LastSequence = LAST_INSERT_ID(LastSequence + 1)`) read back via the
  session-scoped `LAST_INSERT_ID()`. Proven with a 25-way concurrent Testcontainers test yielding exactly
  `{1..25}`.

### Fixed

- **AppHost is now runnable headlessly.** Added the missing
  `src/AspireAppHost/FreshCart.AppHost/Properties/launchSettings.json`; without it a plain
  `dotnet run` on the AppHost crashed immediately (`ASPNETCORE_URLS` / `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL`
  not set) and child services started outside Development (so the dev `Jwt:SigningKey` was missing). The
  profile sets the dashboard URLs, OTLP endpoints and `ASPNETCORE_ENVIRONMENT=Development`. Two further
  live-boot blockers (Aspire persistent-container password drift; databases not provisioned on fresh
  volumes) are documented with the required fix in `docs/google-standards-audit-2026-06-23.md` §7.
- **The full stack now cold-boots and the customer-journey E2E passes against the live system (audit §7).**
  Implemented the §7 recipe &mdash; stable AppHost parameter passwords in `appsettings.Development.json`,
  Postgres database creation via Marten `CreateDatabasesForTenants` (Catalog, Basket), and
  `EnableRetryOnFailure` on the Identity and Payment DbContexts &mdash; and fixed the latent integration
  bugs only a live boot exposes: the gateway JWT role-claim type (`ClaimTypes.Role`), dev-cert trust on
  service-to-service HTTP and gRPC channels, Pricing gRPC `[AllowAnonymous]`, the sign-up cookie missing
  its roles, the Payment capture/refund DTO field/shape mismatches, and the Payment read-model
  cross-column CHECK. The Playwright `customer-journey` spec now runs green end-to-end against
  `dotnet run` AppHost + `ng serve`, and a real order reaches `Confirmed`
  (Submitted &rarr; StockReserved &rarr; Confirmed) through the MassTransit saga.

### Planned &mdash; future phases

- **v0.9.0** &mdash; AdminBackoffice modular monolith (`IModule` pattern) and the Angular 20
  admin SPA.
- **v0.10.0** &mdash; Per-service Bicep modules and helm charts for Reporting and
  AdminBackoffice.
- **v0.11.0** &mdash; Contract tests (Pact provider verification) and load tests (k6) for
  every service.
- **v1.0.0** &mdash; Docs polish, interview-tour cards for every service,
  production-readiness sign-off.

---

## [0.8.0] &mdash; 2026-06-18

> **Customer storefront, per-service delivery, deployment wiring.** The Angular 20 signal-based
> SPA, one CI pipeline per service, helm charts for every service, and the docker-compose stack
> wired to the full platform.

### Added

#### Frontend

- `clients/freshcart-customer` &mdash; Angular 20 standalone SPA, signals for synchronous state,
  RxJS terminated with `toSignal()` for HTTP and SignalR streams.
- Feature areas: catalog browse, basket, checkout, orders, account, support chat, dashboard,
  home. Lazy-loaded routes per feature.
- Core layer: functional `authGuard`, `ErrorInterceptor` mapping status codes to typed error
  signals, `XsrfInterceptor` reading the `XSRF-TOKEN` cookie into the `X-XSRF-TOKEN` header,
  `SignalrService` singletons for the notification and support hubs with exponential-backoff
  auto-reconnect and connection state exposed as a signal.
- `proxy.conf.json` &mdash; `ng serve` proxies `/api` and `/hubs` (WebSocket upgrade) to the
  gateway at `https://localhost:7100`.
- Bootstrap 5.3 + ng-bootstrap; strict CSP, no sensitive state in `localStorage`.

#### CI/CD

- Per-service GitHub Actions workflows: `catalog-ci.yml`, `pricing-ci.yml`, `basket-ci.yml`,
  `ordering-ci.yml`, `inventory-ci.yml`, `payment-ci.yml`, `delivery-ci.yml`,
  `notification-ci.yml`, `customersupport-ci.yml`, `reviews-ci.yml`, `gateway-ci.yml`
  (alongside the existing `identity-ci.yml`).
- `spa-ci.yml` &mdash; install, lint, build, and bundle-budget check for the customer SPA.
- Every service workflow extends `reusable-build-test.yml` and `reusable-container.yml`.

#### Deployment

- Helm charts under `deploy/helm/` for catalog, pricing, basket, ordering, inventory, payment,
  delivery, notification, customersupport, reviews, and gateway, each with
  `values-dev.yaml`, `values-staging.yaml`, `values-prod.yaml`.
- `deploy/helm/freshcart` &mdash; umbrella chart aggregating the per-service charts.
- `deploy/docker/docker-compose.yaml` wired to the full backing-services topology consumed by
  the twelve services (SQL Server, Postgres, MySQL, MongoDB, Redis, RabbitMQ, Seq, Prometheus,
  Grafana, OpenTelemetry Collector).

---

## [0.7.0] &mdash; 2026-06-18

> **Last-mile services and the public edge.** Delivery, Notification, CustomerSupport, Reviews,
> and the YARP gateway with the cookie-to-JWT BFF exchange.

### Added

#### Delivery (Hexagonal, Ports & Adapters, MongoDB)

- Domain ports and MongoDB adapters; geo indexes for slot and zone lookup.
- `BasketCheckoutStartedConsumer` opens a shipment; `OrderConfirmedConsumer` schedules a slot
  and publishes `DeliveryScheduledIntegrationEvent`; completion publishes
  `DeliveryCompletedIntegrationEvent`.

#### Notification (pipes-and-filters, SignalR + Redis backplane)

- `NotificationHub` pushes to connected clients through the Redis backplane.
- Consumers for `OrderPlaced`, `OrderConfirmed`, `OrderCancelled`, `OrderRefunded`,
  `PaymentFailed`, `DeliveryScheduled`, `DeliveryCompleted` fan integration events out to the
  customer's live connections.

#### CustomerSupport (stateful WebSocket, MongoDB + Redis presence)

- SignalR chat hub with round-robin agent assignment; transcripts persisted to MongoDB; agent
  presence tracked in Redis.

#### Reviews (Vertical Slice, MongoDB)

- Feature slices for submitting and listing product reviews; `OrderConfirmedConsumer` records
  verified-purchase eligibility.

#### Gateway (YARP, BFF)

- `FreshCart.Gateway.Yarp` &mdash; single public ingress; routes to every downstream service via
  Aspire service discovery.
- `CookieToJwtTokenExchanger` + `TokenExchangeTransformProvider` &mdash; the browser holds an
  opaque session cookie, the gateway mints a short-lived HS256 bearer token per identity and
  forwards it downstream; services never see the cookie. Tokens are cached per identity and
  per authentication instant so a re-authenticated principal mints a fresh token.
- `AntiforgeryValidationMiddleware` enforces the double-submit token on state-changing requests
  at the edge; rate limiting, CORS, and the shared Redis data-protection key ring configured for
  the gateway.

---

## [0.6.0] &mdash; 2026-06-18

> **Order lifecycle core.** The six services behind checkout: Catalog, Pricing, Basket,
> Ordering, Inventory, Payment, with the MassTransit saga driving stock, payment, and
> confirmation.

### Added

#### Catalog (Vertical Slice + Carter + MediatR + Marten, PostgreSQL)

- Feature slices per use case; Marten document store for the product schema.
- Publishes `ProductCreatedIntegrationEvent` and `ProductPriceChangedIntegrationEvent`.

#### Pricing (gRPC, SQLite reference data)

- Stateless price calculator on the synchronous hot path; no domain state.

#### Basket (Vertical Slice + Outbox + Decorator, PostgreSQL + Redis HybridCache)

- `CachedBasketRepository` decorator over the persistent repository; HybridCache L1 + L2.
- Checkout writes through the transactional outbox and publishes
  `BasketCheckoutStartedIntegrationEvent`.
- `ProductPriceChangedConsumer` keeps cached line prices current.

#### Ordering (Clean Architecture + DDD + MassTransit saga, SQL Server)

- Order aggregate with value objects and domain events; EF Core writes, Dapper reads.
- `CheckoutSagaStateMachine` orchestrates the flow: on `BasketCheckoutStarted` it submits the
  order and requests stock reservation, transitions through `AwaitingStockReservation` and
  `AwaitingPayment`, confirms on `PaymentCaptured`, and compensates to `Cancelled` on
  `StockReservationFailed` or `PaymentFailed`.
- Work consumers (`SubmitOrderFromCheckoutConsumer`, `ReserveOrderStockConsumer`,
  `CaptureOrderPaymentConsumer`) isolate each external side effect for testing.

#### Inventory (Layered + Dapper + gRPC, SQL Server)

- Repository + Unit of Work over Dapper; gRPC reserve endpoint on the hot path.
- `ProductCreatedConsumer` seeds stock; `OrderCancelledConsumer` releases reservations.
- Publishes `StockReservedIntegrationEvent` and `StockReservationFailedIntegrationEvent`.

#### Payment (Clean Architecture + Event Sourcing, MongoDB event store + SQL projection)

- Append-only MongoDB event store with a SQL read projection.
- Publishes `PaymentCapturedIntegrationEvent` and `PaymentFailedIntegrationEvent`.

---

## [0.5.0] &mdash; 2026-06-18

> **Shared contracts and platform hardening.** Cross-service event contracts pulled into
> BuildingBlocks, the liveness/readiness probe fix, central package consolidation, advisory
> package bumps, and the Identity MFA and data-protection additions.

### Added

- `BuildingBlocks.Messaging/IntegrationEvents` &mdash; the shared integration event contracts every
  producer and consumer binds to: `ProductCreated`, `ProductPriceChanged`,
  `BasketCheckoutStarted` (with `CheckoutAddress` and `CheckoutLine`), `OrderPlaced`,
  `OrderConfirmed` (with `OrderConfirmedLine`), `OrderCancelled`, `OrderRefunded`,
  `StockReserved`, `StockReservationFailed`, `PaymentCaptured`, `PaymentFailed`,
  `DeliveryScheduled`, `DeliveryCompleted`. Additive-only contract; breaking changes require a
  new event type.
- Identity multi-factor enrollment: `EnrollMultiFactorCommand`,
  `VerifyMultiFactorEnrollmentRequest`, `DisableMultiFactorRequest`, and the
  `MultiFactorEndpoints` Carter module (authenticator shared key + recovery codes).
- Identity data protection persisted to Redis so the cookie key ring is shared across replicas.

### Fixed

- `MapDefaultHealthEndpoints` now maps `/alive` and `/ready` in every environment instead of
  Development and Staging only. The Kubernetes liveness/readiness probes and the Docker
  HEALTHCHECK call them unconditionally; a pod whose probes returned 404 in Production was
  killed and restarted in a loop. The detailed `/health` endpoint stays Development/Staging-only
  so it does not leak dependency topology from a production cluster.

### Changed

- Central Package Management consolidation: every NuGet version moved to
  `Directory.Packages.props`; per-project version attributes removed.
- Package bumps to clear advisory and CVE findings reported by
  `dotnet list package --vulnerable`.

---

## [0.4.0] &mdash; 2026-05-25

> **Readability + coverage pass.** Stripped AI-fingerprint patterns from the most visible
> files, added the missing architecture documents (requirements, class diagrams, internal
> architecture) and a unit-test suite covering the pure-logic surface.

### Added

- `docs/REQUIREMENTS.md` &mdash; per-service functional and non-functional requirements
  with stable IDs and a traceability matrix mapping each requirement to its implementation
  and test.
- `docs/CLASS_DIAGRAMS.md` &mdash; Mermaid class diagrams for BuildingBlocks, Identity and
  Reporting plus two sequence diagrams (checkout saga, invoice generation).
- `docs/INTERNAL_ARCHITECTURE.md` &mdash; per-service deep dive: purpose, internal shape,
  state machines, technology choices with trade-offs, failure model.
- `tests/coverlet.runsettings` &mdash; shared coverage configuration with branch coverage
  and proper exclusions for migrations and bootstrap files.
- `tests/README.md` &mdash; how the five test layers fit together.
- New test project `FreshCart.BuildingBlocks.Tests` (xUnit + FluentAssertions + NSubstitute)
  with eight specs covering pagination, behaviors, exception mapping and the SSRF guard.
- `Argon2PasswordHasherTests` &mdash; parameter encoding, success / failure / rehash paths,
  salt randomness.
- `InvoiceNumberTests`, `KpiMetricTests`, `ReportingPeriodTests` &mdash; pure-domain coverage
  for the Reporting service.

### Changed

- `SignUpCommandHandler`, `SignInCommandHandler`, `IdentityDataSeeder`,
  `Argon2PasswordHasher`, `RefreshTokenService`, `ValidationBehavior`, `LoggingBehavior`,
  `CustomExceptionHandler`, `OutboundUrlAllowListHandler`, `PaginationRequest`,
  `PaginatedResult` &mdash; rewritten: stripped numbered "Steps:" comment blocks, removed
  em-dashes and marketing prose, kept only WHY-comments. Method signatures and behaviour
  preserved.
- Magic strings extracted to private const fields (`CredentialMismatchMessage`,
  `RotationReason`, `ReuseDetectedReason`, `BlockedReasonPhrase`).
- GitHub Actions build pipeline now consumes `tests/coverlet.runsettings` so coverage
  configuration lives in source control.

---

## [0.3.0] &mdash; 2026-05-25

> **Landing page.** Architecture guide promoted to `index.html` at the repo root so
> reviewers (and GitHub Pages) land directly on the walkable HTML. Demo credentials and
> seeded accounts wired end-to-end.

### Added

- `index.html` at the repo root &mdash; copy of the architecture guide, served as the
  default page by GitHub Pages / direct folder open.
- Prominent **"Try it now"** card at the top of the Overview tab listing every login URL
  (local + Azure-deployment placeholders) and the three demo credentials.
- `IdentityDataSeeder` hosted service that boots the three canonical roles
  (`Customer`, `SupportAgent`, `Administrator`) and the three demo accounts on first start
  in the Development environment. Production safety: guarded by
  `IHostEnvironment.IsDevelopment()` and the sign-up validator rejects every `*.test` email.
- Demo accounts now seeded:
  - `demo@freshcart.test` / `Demo-P@ssw0rd-2026` (Customer)
  - `support@freshcart.test` / `Support-P@ssw0rd-2026` (SupportAgent)
  - `admin@freshcart.test` / `Admin-P@ssw0rd-2026` (Administrator)
- 90-second guided tour written into both `index.html` and `README.md`.

### Changed

- `README.md` rewritten to lead with the `index.html` link, demo URLs and credentials.
  Sections beyond the demo retained verbatim.
- `CHANGELOG.md` restructured into proper gradual version history per phase (this entry,
  the v0.1.0 / v0.2.0 entries, plus retroactive v0.0.x entries for each P0 sub-phase).

---

## [0.2.0] &mdash; 2026-05-25

> **Reporting service end-to-end.** Invoices, KPI dashboards, exports, scheduled jobs,
> projection consumers &mdash; the full back-office data surface.

### Added

#### Domain

- `Invoice` aggregate with `InvoiceKind` (Sale / CreditNote / ProForma).
- `InvoiceNumber` value object with gap-free per-year per-kind sequencing
  (`INV-2026-000123`, `CR-2026-000018`, `PF-2026-000009`); `TryParse` round-trips.
- `SalesSnapshot`, `ReportingPeriod` (half-open interval), `AggregationBucket`.
- `KpiMetric` with current/previous values, delta percentage, trend; `KpiCodes` catalog
  (GMV, net revenue, AOV, refund rate, customer LTV, delivery success rate, …).
- `TopEntityRanking` for generic top-N rows.

#### Application (Clean Architecture, CQRS)

- Queries: `GetSalesOverviewQuery`, `GetSalesTimeSeriesQuery`, `GetRevenueBreakdownQuery`,
  `GetTopProductsQuery`, `GetCustomerLeaderboardQuery`, `GetInventoryHealthQuery`,
  `GetDeliveryPerformanceQuery`, `DownloadInvoiceQuery`.
- Commands: `GenerateInvoiceCommand` (idempotent per `OrderId`),
  `ExportSalesTransactionsCommand`.
- Ports: `ISalesReadWarehouse`, `IProductReadWarehouse`, `ICustomerReadWarehouse`,
  `IDeliveryReadWarehouse`, `IInvoiceRepository`, `IInvoiceRenderer`,
  `IExcelExporter`, `IDocumentStore`, `IProjectionInbox`, `IProjectionWriter`.
- Projection consumers: `OrderConfirmedProjectionConsumer`,
  `OrderRefundedProjectionConsumer` (idempotent via inbox).

#### Infrastructure

- `WarehouseDbContext` with `invoices`, `invoice_lines`, `invoice_number_sequences`,
  `projection_inbox` tables on MySQL.
- `InvoiceRepository` &mdash; allocates gap-free numbers under InnoDB row lock.
- `DapperSalesReadWarehouse`, `DapperProductReadWarehouse`,
  `DapperCustomerReadWarehouse`, `DapperDeliveryReadWarehouse` &mdash; hand-tuned
  set-based queries with `ROW_NUMBER() OVER (…)` for top-N rankings.
- `WarehouseProjectionWriter` &mdash; UPSERT into `sales_facts`, `sales_line_facts`,
  `customer_lifetime_value`.
- `EntityFrameworkProjectionInbox` &mdash; idempotency table.
- `QuestPdfInvoiceRenderer` &mdash; A4 PDF with header, billing + shipping addresses,
  itemised line table, totals block, footer. QuestPDF Community licence (managed-only,
  no native binaries; works in chiselled Linux containers).
- `ClosedXmlExcelExporter` &mdash; styled `.xlsx` with frozen header + auto-width.
- `AzureBlobDocumentStore` &mdash; user-delegation SAS URIs for short-lived downloads.
- `DailySalesReportBackgroundService` &mdash; hourly tick, idempotent skip when
  yesterday's file already exists, ClosedXML export to Blob Storage.

#### API

- Carter modules:
  - `DashboardEndpoints` &mdash; `/dashboards/sales/{overview,time-series,breakdown}`,
    `/dashboards/inventory/health`, `/dashboards/delivery/performance`.
  - `CatalogReportsEndpoints` &mdash; `/reports/products/top`,
    `/reports/customers/leaderboard`.
  - `InvoiceEndpoints` &mdash; `POST /invoices`, `GET /invoices/{number}` (signed SAS),
    `GET /invoices/{number}/content.pdf` (streamed).
  - `ExportEndpoints` &mdash; `GET /exports/sales-transactions.xlsx`.
- `Program.cs` with JWT bearer auth, `BackOfficeUser` policy (`Administrator` +
  `SupportAgent` + `Manager` roles), security headers, OpenAPI, health checks.
- Multi-stage chiselled-runtime `Dockerfile`.

#### Tests

- `GetSalesOverviewQueryHandlerTests` &mdash; xUnit + NSubstitute + FluentAssertions +
  `FakeTimeProvider`.

#### Documentation

- New **Reporting & Invoices** tab in the architecture guide HTML with three additional
  animated flows: invoice generation, event projection, daily scheduled report.

---

## [0.1.0] &mdash; 2026-05-25

> **Identity service end-to-end.** First feature release. Cookie + JWT dual authentication
> with Argon2id, MFA scaffold, refresh-token rotation with reuse detection.

### Added

#### Domain

- `ApplicationUser : IdentityUser<Guid>` with `DisplayName`, `MarketingConsent`,
  audit timestamps and `SecurityStampUpdatedOnUtc`.
- `ApplicationRole : IdentityRole<Guid>` + canonical role constants.
- `RefreshToken` &mdash; stored as SHA-256 hash; reuse detection revokes the entire
  family.
- `AuditEvent` &mdash; append-only security log.

#### Application (Clean Architecture, CQRS)

- Commands: `SignUpCommand`, `SignInCommand`, `SignOutCommand`,
  `RefreshAccessTokenCommand`; queries: `GetCurrentUserQuery`.
- FluentValidation validators with min-12-char password + three-of-four character-class
  rule.
- Application ports: `IAccessTokenIssuer`, `IRefreshTokenService`, `IIdentityAuditLog`,
  `ICurrentRequestContext`.

#### Infrastructure

- `IdentityDbContext` on SQL Server with `identity` schema, retry-on-failure.
- `Argon2PasswordHasher<ApplicationUser>` &mdash; replaces PBKDF2 with memory-hard Argon2id
  (64 MiB memory, 3 iterations, parallelism 4); stores cost parameters inline so they
  can be bumped without invalidating old hashes.
- `JwtAccessTokenIssuer` &mdash; HS256-signed access tokens, configurable lifetime.
- `RefreshTokenService` &mdash; SHA-256-hashed storage, rotation on every refresh,
  reuse detection revokes the family.
- `EntityFrameworkAuditLog`.
- EF Core configurations + per-table indexes.

#### API

- Carter modules: `AuthenticationEndpoints` (sign-up / sign-in / sign-out / refresh /
  anti-forgery-token), `AccountEndpoints` (me).
- Cookie scheme &mdash; HttpOnly + Secure + SameSite=Strict, 8-hour sliding expiry, 401/403
  responses instead of redirects.
- JWT bearer scheme for service-to-service and mobile clients.
- Anti-forgery (XSRF-TOKEN cookie + X-XSRF-TOKEN header double-submit).
- Authorization policies: `Customer`, `SupportAgent`, `Administrator`.
- Multi-stage chiselled-runtime `Dockerfile`.

#### Tests

- `SignUpAndSignInIntegrationTests` &mdash; full HTTP end-to-end against a real SQL Server
  container via Testcontainers; asserts cookie issuance + protected-endpoint access.
- `IdentityApiFactory` &mdash; `WebApplicationFactory<Program>` with `MsSqlContainer`.

#### Documentation

- `docs/interview-tour/identity.md` &mdash; 90-second pitch + 5-minute drill-down +
  15-minute architect conversation.

---

## [0.0.13] &mdash; 2026-05-25 &mdash; Playwright e2e scaffold (P0.13)

### Added

- `tests/e2e/package.json` (Playwright 1.49), `tsconfig.json` (strict mode),
  `playwright.config.ts` (3-browser + mobile matrix, JUnit + HTML reporters).
- Page-object pattern: `CustomerJourneyPage`.
- Specs:
  - `auth.spec.ts` &mdash; cookie flags + sign-up + sign-out + sign-in.
  - `customer-journey.spec.ts` &mdash; sign-up → basket → checkout → SignalR
    notification toast.
  - `reporting-dashboard.spec.ts` &mdash; admin sign-in + dashboard tiles (skipped
    unless `ADMIN_BASE_URL` is set).
- `tests/e2e/README.md`, `.gitignore`.

---

## [0.0.12] &mdash; 2026-05-25 &mdash; Raw k8s manifests + Bicep Azure infrastructure (P0.12)

### Added

- `deploy/k8s/identity/` &mdash; 12 raw manifests as teaching reference (Deployment,
  Service, Ingress, ConfigMap, Secret, HPA, PDB, ServiceAccount with Workload Identity,
  NetworkPolicy default-deny, ServiceMonitor, SecretProviderClass for Azure Key Vault CSI,
  Namespace with PodSecurity restricted).
- `infra/main.bicep` &mdash; subscription-scope deployment.
- 12 Bicep modules: `aks` (Cilium + Workload Identity + OIDC + autoscaler),
  `acr` (Premium for prod), `sql` (4 DBs), `postgres` (2 DBs), `mysql`, `cosmos`,
  `redis`, `service-bus`, `key-vault` (RBAC + purge protection),
  `log-analytics` (+ App Insights), `front-door` (+ WAF), `storage` (4 containers),
  `network` (VNet + subnets).
- `infra/env/{dev,staging,prod}.bicepparam` &mdash; environment overrides via
  `readEnvironmentVariable`.
- `infra/README.md`.

---

## [0.0.11] &mdash; 2026-05-25 &mdash; Helm chart for Identity (P0.11)

### Added

- `deploy/helm/identity/Chart.yaml` + `values.yaml` baseline +
  `values-dev.yaml`, `values-staging.yaml`, `values-prod.yaml`.
- 11 templates: `deployment`, `service`, `ingress`, `configmap`, `secret`, `hpa`,
  `pdb`, `serviceaccount`, `servicemonitor`, `networkpolicy`,
  `secretproviderclass`, `_helpers.tpl`.
- PodSecurity restricted, non-root, read-only rootfs, NetworkPolicy default-deny,
  topology spread constraints, Azure Workload Identity + Key Vault CSI annotations.

---

## [0.0.10] &mdash; 2026-05-25 &mdash; GitHub Actions workflows (P0.10)

### Added

- Reusable workflows:
  - `reusable-build-test.yml` &mdash; restore + build + test + coverage + SonarCloud +
    vuln scan + publish artifact.
  - `reusable-container.yml` &mdash; docker buildx + Trivy SARIF + Cosign keyless sign
    (OIDC) + push to both GHCR and ACR.
  - `reusable-helm-deploy.yml` &mdash; Azure OIDC + helm upgrade + smoke probe.
- Per-service workflow `identity-ci.yml` with three environment gates.
- `codeql.yml` &mdash; C# + TypeScript security analysis (extended + security-and-quality
  queries).
- `e2e-playwright.yml` &mdash; 3-browser matrix on PR, nightly cron against staging.
- `infrastructure.yml` &mdash; Bicep what-if on PR, apply on `infra-v*` tag.
- `dependabot.yml` &mdash; weekly NuGet (grouped), npm, Docker, GitHub Actions.

---

## [0.0.9] &mdash; 2026-05-25 &mdash; HTML architecture guide (P0.9)

### Added

- `docs/FreshCart_Architecture_Guide.html` &mdash; self-contained dark-theme HTML with
  10 tabs (Overview, Domain, Microservices, Patterns, Animated Flows, Security,
  Developer Guide, DevOps, Testing, Frontend) and 5 initial animated flow diagrams
  (sign-in, checkout saga, outbox, real-time notification, CI/CD pipeline). Full system
  SVG block diagram. No external dependencies beyond Google Fonts.

---

## [0.0.8] &mdash; 2026-05-25 &mdash; Azure DevOps pipeline templates (P0.8)

### Added

- `azure-pipelines/templates/build-test-template.yaml` &mdash; restore, build, test with
  coverage, SonarCloud begin/end, vuln scan, publish artifact.
- `azure-pipelines/templates/container-template.yaml` &mdash; build, Trivy scan, Cosign
  sign, push to ACR.
- `azure-pipelines/identity-pipeline.yaml` &mdash; per-service multi-stage pipeline with
  three environment approvals (DEV auto, STAGING 1 approver, PROD blue/green
  2 approvers).

---

## [0.0.7] &mdash; 2026-05-25 &mdash; Root docs + 4 ADRs (P0.7)

### Added

- `README.md` (initial), `ARCHITECTURE.md` (C1/C2/C3 narrative + cross-cutting map),
  `docs/CONVENTIONS.md` (naming, async, LINQ discipline).
- ADR-0001 Hybrid Modular Monolith + Microservices.
- ADR-0002 Cookie + JWT Dual Authentication.
- ADR-0003 Polyglot Persistence.
- ADR-0004 OWASP Top 10 2025 Control Mapping.
- `docs/interview-tour/00-overview.md` and `docs/interview-tour/identity.md`.

---

## [0.0.6] &mdash; 2026-05-25 &mdash; .NET Aspire AppHost (P0.6)

### Added

- `src/AspireAppHost/FreshCart.AppHost` &mdash; local orchestration via Aspire 9. Wires
  SQL Server, Postgres, MySQL, MongoDB, Redis, RabbitMQ as persistent containers and
  references the Identity service project.

---

## [0.0.5] &mdash; 2026-05-25 &mdash; docker-compose stack (P0.5)

### Added

- `deploy/docker/docker-compose.yaml` &mdash; full local backing-services stack:
  SQL Server 2022, PostgreSQL 17, MySQL 9, MongoDB 8, Redis 7, RabbitMQ 4 with
  management plugin, Seq, Grafana, Prometheus, OpenTelemetry Collector, pgAdmin,
  mongo-express.
- `otel-collector-config.yaml`, `prometheus.yaml`, `grafana-datasources.yaml`.

---

## [0.0.4] &mdash; 2026-05-25 &mdash; FreshCart.ServiceDefaults (P0.4)

### Added

- `AddFreshCartServiceDefaults<TBuilder>()` extension wiring OpenTelemetry tracing /
  metrics / logging, service discovery, `AddStandardResilienceHandler()` on every typed
  HttpClient, default `live` health check tag.
- `MapDefaultHealthEndpoints()` for `/health`, `/alive`, `/ready` (Development +
  Staging only).
- Conditional OTLP exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured.

---

## [0.0.3] &mdash; 2026-05-25 &mdash; FreshCart.BuildingBlocks.Messaging (P0.3)

### Added

- `IntegrationEvent` base record with `EventId`, `OccurredOnUtc`, `EventType`.
- `OutboxMessage` entity + `IOutboxStore` port.
- `OutboxPublisher : BackgroundService` &mdash; drains the outbox, publishes via
  MassTransit, marks `ProcessedOn`; records failures with retry-attempt + error message.
- `MessageBrokerExtensions.AddRabbitMqMessageBroker(...)` &mdash; canonical
  MassTransit + RabbitMQ wiring with kebab-case endpoint formatter, exponential retry
  middleware.

---

## [0.0.2] &mdash; 2026-05-25 &mdash; FreshCart.BuildingBlocks (P0.2)

### Added

- CQRS abstractions: `ICommand`, `ICommand<TResponse>`, `ICommandHandler<TCommand>`,
  `ICommandHandler<TCommand, TResponse>`, `IQuery<TResponse>`,
  `IQueryHandler<TQuery, TResponse>`.
- Pipeline behaviors: `ValidationBehavior<TRequest, TResponse>`,
  `LoggingBehavior<TRequest, TResponse>` (with slow-handler threshold).
- Exception types: `DomainException`, `NotFoundException`, `BadRequestException`,
  `ConflictException`, `ForbiddenException`, `InternalServerException`.
- `CustomExceptionHandler : IExceptionHandler` &mdash; single sink mapping every known
  exception to an RFC 7807 `ProblemDetails` with `traceId` extension.
- Pagination: `PaginationRequest` (with `Normalise()`), `PaginatedResult<TItem>`.
- Security: `OutboundUrlAllowListHandler` (SSRF defence) and
  `SecurityHeadersMiddleware` (CSP / X-Frame-Options / Referrer-Policy / COOP / COEP /
  CORP / Permissions-Policy).
- `Result` / `Result<TValue>` lightweight envelopes.

---

## [0.0.1] &mdash; 2026-05-25 &mdash; Repo skeleton + conventions (P0.1)

### Added

- Monorepo directory tree (`src/`, `clients/`, `deploy/`, `infra/`, `azure-pipelines/`,
  `.github/`, `gitops/`, `tests/`, `tools/`, `docs/`).
- Strict-by-default MSBuild posture:
  - `Directory.Build.props` &mdash; `TreatWarningsAsErrors=true`, `Nullable=enable`,
    `ImplicitUsings=enable`, `EnforceCodeStyleInBuild=true`, analyzer suite
    (Meziantou, AsyncFixer, Sonar, Roslynator).
  - `Directory.Packages.props` &mdash; Central Package Management for every NuGet
    version used across the repo.
  - `global.json` &mdash; pins .NET 10.0.100 with latestFeature roll-forward.
  - `.editorconfig` &mdash; PascalCase / camelCase / `_camel` field rules, four-space
    C#, two-space TS/YAML/JSON, file-scoped namespaces, LF line endings.
- `.gitignore`, `.gitattributes`, `LICENSE` (MIT), `CODEOWNERS`, `SECURITY.md`,
  `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md`, initial `CHANGELOG.md`.
- Empty initial `FreshCart.sln`.

---

[Unreleased]: https://github.com/amasen02/FreshCart/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/amasen02/FreshCart/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/amasen02/FreshCart/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/amasen02/FreshCart/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/amasen02/FreshCart/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/amasen02/FreshCart/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/amasen02/FreshCart/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/amasen02/FreshCart/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/amasen02/FreshCart/compare/v0.0.13...v0.1.0
[0.0.13]: https://github.com/amasen02/FreshCart/compare/v0.0.12...v0.0.13
[0.0.12]: https://github.com/amasen02/FreshCart/compare/v0.0.11...v0.0.12
[0.0.11]: https://github.com/amasen02/FreshCart/compare/v0.0.10...v0.0.11
[0.0.10]: https://github.com/amasen02/FreshCart/compare/v0.0.9...v0.0.10
[0.0.9]: https://github.com/amasen02/FreshCart/compare/v0.0.8...v0.0.9
[0.0.8]: https://github.com/amasen02/FreshCart/compare/v0.0.7...v0.0.8
[0.0.7]: https://github.com/amasen02/FreshCart/compare/v0.0.6...v0.0.7
[0.0.6]: https://github.com/amasen02/FreshCart/compare/v0.0.5...v0.0.6
[0.0.5]: https://github.com/amasen02/FreshCart/compare/v0.0.4...v0.0.5
[0.0.4]: https://github.com/amasen02/FreshCart/compare/v0.0.3...v0.0.4
[0.0.3]: https://github.com/amasen02/FreshCart/compare/v0.0.2...v0.0.3
[0.0.2]: https://github.com/amasen02/FreshCart/compare/v0.0.1...v0.0.2
[0.0.1]: https://github.com/amasen02/FreshCart/releases/tag/v0.0.1
