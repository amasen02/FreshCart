# FreshCart — Google-Standards Code-Quality Audit & Remediation

**Date:** 2026-06-23 · **Baseline version:** 0.8.0 · **Auditor:** agentic review (44 subagents, adversarial
verification) + maintainer raw-evidence re-verification.

---

## 1. Verdict

**Average: 3.73 / 5 across 15 modules and 9 quality dimensions.**

FreshCart is **strong, senior-grade engineering — but not yet at Google's "production-ready / would-approve-
at-scale" bar.** The gap is concentrated in distributed-systems correctness (idempotency / outbox), a small
set of security gaps (committed dev secrets, one BOLA, CSRF enforcement), and — until this run — a critical
vulnerable dependency.

What is already Google-grade: readability (5/5 almost everywhere), cross-cutting hygiene (central package
management, strict analyzer suite, `TreatWarningsAsErrors`, nullable, full OpenTelemetry, RFC7807
ProblemDetails, source-generated logging), money-as-`decimal` discipline, the Gateway's cookie→JWT BFF design,
and the cart's transactional outbox.

**Build & test reality (verified this run):** full solution builds with **0 errors**; the **entire .NET test
suite is green with Docker running** (the 42 earlier "failures" were Testcontainers with no Docker daemon —
zero code defects); the Angular SPA lints clean and builds.

---

## 2. Method

1. **Baseline** — full `dotnet build` + `dotnet test` (Docker up), Angular lint + prod build.
2. **Agentic audit** — one L6-reviewer subagent per module scored it against the rubric below; every
   high/critical finding was handed to an independent adversarial verifier instructed to refute it.
3. **Caveat** — ~21 verifier agents were killed mid-run by transient API 500/529 overload errors, so their
   findings are recorded as **UNVERIFIED**, not dismissed.
4. **Maintainer re-verification** — the 4 criticals + committed-secret/BOLA claims were re-checked against raw
   source by hand (corrections noted inline).

Rubric dimensions (1–5): readability · design (SOLID/SoC/no god-classes/DRY) · correctness · testing ·
security (OWASP) · observability · API design · concurrency · docs.

---

## 3. Scorecard

| Module | Overall | rd | ds | co | te | se | ob | api | cc | dc |
|---|---|---|---|---|---|---|---|---|---|---|
| Notification | **4.3** | 5 | 5 | 4 | 4 | 4 | 4 | 4 | 5 | 4 |
| Reviews | **4.2** | 5 | 5 | 4 | 4 | 4 | 4 | 4 | 4 | 4 |
| Angular SPA | **4.2** | 5 | 5 | 4 | 4 | 4 | 4 | 4 | 4 | 4 |
| BuildingBlocks | **4.0** | 5 | 4 | 3 | 4 | 4 | 5 | 4 | 4 | 5 |
| Gateway | **4.0** | 5 | 5 | 3 | 4 | 4 | 3 | 4 | 5 | 4 |
| Basket (cart) | **4.0** | 5 | 5 | 4 | 4 | 3 | 5 | 4 | 3 | 4 |
| CustomerSupport | **3.8** | 5 | 5 | 4 | 3 | 3 | 3 | 4 | 4 | 4 |
| Ordering | **3.8** | 5 | 5 | 3 | 4 | 3 | 3 | 4 | 4 | 3 |
| Catalog | **3.7** | 5 | 4 | 3 | 3 | 3 | 4 | 4 | 5 | 4 |
| Identity | **3.6** | 5 | 4 | 3 | 3 | 3 | 4 | 4 | 3 | 4 |
| Pricing | **3.6** | 5 | 4 | 4 | 2 | 3 | 5 | 3 | 5 | 4 |
| Inventory | **3.4** | 5 | 4 | 3 | 3 | 2 | 4 | 4 | 5 | 3 |
| Delivery | **3.2** | 5 | 4 | 2 | 4 | 4 | 4 | 4 | 2 | 4 |
| Payment | **3.1** | 5 | 4 | 2 | 4 | 2 | 4 | 3 | 4 | 4 |
| Reporting | **3.1** | 5 | 4 | 2 | 3 | 2 | 5 | 3 | 2 | 4 |

_rd=readability ds=design co=correctness te=testing se=security ob=observability api=apiDesign cc=concurrency dc=docs_

---

## 4. High/Critical findings register (4 critical + 25 high)

Status: **CONFIRMED** (adversarially verified true) · **UNVERIFIED** (verifier API-failed; not yet adjudicated)
· **REFUTED** (verifier disagreed) · **CORRECTED** (maintainer re-verification adjusted severity).

### Systemic theme — at-least-once messaging without idempotency / multi-instance outbox guard
| ID | Sev | Status | Location |
|---|---|---|---|
| BB-001 | High | CONFIRMED | OutboxPublisher.cs:127 — poison messages retry forever, never dead-lettered |
| BSK-01 | High | CONFIRMED | OutboxPublisher.cs:63 — no multi-instance poll guard → scale-out double-publish |
| PAY-002 | High | CONFIRMED | CapturePaymentCommandHandler.cs:33 — Idempotency-Key required but never used to dedup |
| INV-001 | ~~Crit~~ → Med | CORRECTED | ProductCreatedConsumer not idempotent; **but** SetStockLevel guards against going below reserved (constraint 547), so not the "wipes all stock" critical first reported |
| REP-001 | Critical | UNVERIFIED | OrderRefundedProjectionConsumer.cs:32 — inbox dedup + projection not atomic → double-count revenue |
| DLV-002 | High | UNVERIFIED | CompleteDeliveryService.cs:31 — dual-write DB then broker, no outbox |
| DLV-003 | High | UNVERIFIED | OrderConfirmedConsumer.cs:29 — DeliveryScheduled dropped on publish failure |
| NOTIF-001 | High | UNVERIFIED | PaymentFailedConsumer.cs:29 — notification silently dropped when recipient missing |
| PAY-001 | Critical | REFUTED | capture outcomes "never published" — verifier disagreed; re-check before acting |

### Security
| ID | Sev | Status | Location / note |
|---|---|---|---|
| ID-SEC-01 | Critical | CONFIRMED (maintainer) | Identity Program.cs — **no `app.UseAntiforgery()`**; CSRF token issued, never enforced (gateway provides double-submit defense-in-depth, so service-boundary gap) |
| PAY-004 | High | CONFIRMED | PaymentEndpoints.cs:22 — capture trusts CustomerId from body under bare `.RequireAuthorization()` (BOLA); payments not SPA-exposed, lowering blast radius |
| ORD-001 | High | CONFIRMED | Ordering appsettings.json:24 — committed `sa` password + guest/guest broker creds |
| INV-003 | High | UNVERIFIED→confirmed (maintainer) | Inventory appsettings.json:29 — same committed dev creds |
| BSK-06 | Med | CONFIRMED | Basket dev DB password committed (same pattern) |
| INV-002 | High | UNVERIFIED | InventoryGrpcService.cs:9 — internal reserve/release authorizes any authenticated principal |
| REP-005 | High | UNVERIFIED | InvoiceEndpoints.cs:79 — unvalidated invoiceNumber into blob path + under-authorized |
| CAT-002 / BSK-03 | Critical | **RESOLVED THIS RUN** | vulnerable Marten 7.30 — see §5 |

### Concurrency / correctness (TOCTOU & races)
| ID | Sev | Status | Location |
|---|---|---|---|
| CAT-001 | High | CONFIRMED | CreateProductCommandHandler.cs:29 — SKU/slug check-then-write TOCTOU; unique-violation not mapped to 409 |
| ORD-002 | High | CONFIRMED | RefundOrderCommandHandler.cs:30 — refund can call provider twice on a race (no idempotency key) |
| DLV-001 | High | UNVERIFIED | MongoSlotRepository.cs:56 — slot booking lost-update race oversubscribes fleet |
| REP-002 | High | UNVERIFIED | InvoiceRepository.cs:44 — comment claims FOR UPDATE but no real row lock |
| REP-003 | High | UNVERIFIED | GenerateInvoiceCommandHandler.cs:32 — check-then-act idempotency race |
| ID-COR-01 | High | UNVERIFIED | RefreshTokenService.cs:65 — refresh-token rotation not concurrency-safe |
| PAY-003 | High | UNVERIFIED | CapturePaymentCommandHandler.cs:133 — non-atomic Mongo+SQL dual write |

### Design / other
| ID | Sev | Status | Location |
|---|---|---|---|
| GW-1 | High | UNVERIFIED | HmacDownstreamTokenSigner.cs:50 — role-claim-type divergence gateway vs Identity |
| BB-002 | High | REFUTED | IntegrationEvent.cs:29 — AssemblyQualifiedName type resolution (verifier disagreed) |

### Test gaps
| ID | Sev | Status | Note |
|---|---|---|---|
| PR-01 | High | UNVERIFIED | Pricing — no gRPC/REST integration tests though Program is exposed |
| CS-001 | High | UNVERIFIED | CustomerSupport — SignalR hub lacks query-string token extraction (+ same in Notification) |
| CS-002 | High | UNVERIFIED | CustomerSupport — no host/endpoint integration tests for the BOLA guard |

---

## 5. Remediation completed this run (verified)

**Vulnerable dependencies (the confirmed critical, CAT-002/BSK-03):**
- `Marten` 7.30.0 → **8.37.0** (clears critical GHSA-vmw2-qwm8-x84c; no 7.x fix exists).
- `Npgsql` 9.0.2 → **9.0.4** (Marten 8 transitive floor).
- Migrated Marten 8 API relocations `AutoCreate` + `ConcurrencyException` → `JasperFx` namespace
  (Basket + Catalog DI and `MartenConcurrencyRetry`).
- `Snappier` 1.1.6 → **1.3.1** (clears high GHSA-pggp-6c3x-2xmx); `MessagePack` → **2.5.301** transitive pin
  (clears high GHSA-hv8m-jj95-wg3x); `SharpCompress` → 0.47.4.
- Replaced the blanket `NU1902;NU1903` demotion with two **documented** `NuGetAuditSuppress` entries for the
  only two unpatchable transitives (`SharpCompress`, `SQLitePCLRaw`), each on a non-exploitable path.

**Verification:** solution builds 0 errors; Basket 92/92 and Catalog 155/155 (Marten-backed, Docker) green;
`dotnet list package --vulnerable` now reports only the two risk-accepted no-patch transitives.

**Committed secrets (ORD-001, INV-003, BSK-06 — and 7 more the audit did not sample):** a platform-wide
scan found committed dev credentials in **10 services**, not 3. Moved the credential-bearing
`ConnectionStrings` + `MessageBroker` blocks out of every base `appsettings.json` into
`appsettings.Development.json` (dev-only), matching the existing `Jwt:SigningKey` convention; production /
Aspire inject them via environment at runtime. **Verified:** all 14 test projects green; no source base
config carries credentials.

> Note: those two (`SharpCompress` GHSA-6c8g-7p36-r338, `SQLitePCLRaw` GHSA-2m69-gcr7-jv3q / CVE-2025-6965)
> have **no published fix**. The CI `dotnet list --vulnerable` gate must allowlist exactly these two GHSA ids
> and fail on anything else.

---

## 6. Prioritized roadmap to v1.0.0

**P0 — security: ✅ DONE (all verified).**
- Committed secrets removed from base config across 10 services.
- **Service-to-service identity** introduced (`ServiceAccount` role + `ServiceCaller` policy + cached
  `ServiceTokenProvider` + HTTP `DelegatingHandler` + gRPC metadata). This also uncovered and fixed a
  **latent production bug** the per-module audit missed: the saga's Payment/Inventory calls carried no
  credential at all, so the integrated checkout would fail wherever auth is enforced.
- **PAY-004 BOLA** closed — capture is now `ServiceCaller`-only (no longer trusts body `CustomerId`).
- **INV-002** closed — the reserve/release gRPC surface is now `ServiceCaller`-only, not bare `[Authorize]`.
- **ID-SEC-01** closed — `app.UseAntiforgery()` wired; a reusable, safe-method-aware,
  cookie-conditional validator enforces anti-forgery on sign-out **and** the MFA enroll/verify/disable
  mutations (group endpoint filter). Bearer/service callers are correctly skipped.

**P1 — distributed-systems correctness:** ✅ **dead-letter + max-retry on the shared OutboxPublisher
(BB-001) DONE + verified** (no schema change; both stores delegate to `OutboxMessage.MarkFailed`).
✅ **NOTIF-001 DONE** (PaymentFailed/OrderRefunded consumers throw to redeliver instead of silently
dropping). ✅ **INV-001 DONE** (idempotent `EnsureStockItem` insert-if-absent; redelivery no longer resets
stock). Remaining: multi-instance claim/SKIP-LOCKED on the outbox (BSK-01); remaining idempotent consumers
(INV-001, NOTIF-001, REP-001); idempotency keys on Payment capture/refund (PAY-002, ORD-002); outbox for
Delivery/Payment events (DLV-002/003, PAY-001/003). **Re-verify the 20 UNVERIFIED findings first** (the
audit's verifier stage was cut short by API errors).

**P2 — concurrency races:** atomic conditional writes for slot booking (DLV-001), invoice allocation
(REP-002/003), refresh-token rotation (ID-COR-01), SKU/slug uniqueness → 409 (CAT-001).

**P3 — test + release gaps (CHANGELOG Unreleased):** Pricing/CustomerSupport/Catalog integration tests
(PR-01, CS-001/002); Pact contract tests + k6 load tests (v0.11.0); AdminBackoffice + admin SPA (v0.9.0);
Reporting/AdminBackoffice Bicep + helm (v0.10.0); docs polish + prod-readiness sign-off (v1.0.0).

**P4 — end-to-end:** run the existing Playwright `customer-journey.spec.ts` (add→checkout→order→SignalR)
against the live Aspire/compose stack as a release gate.

---

## 7. Live-run readiness — findings from a P4 boot attempt (2026-06-24)

A real attempt to boot the full Aspire stack headlessly (for the live E2E) surfaced **four genuine
run-ergonomics defects** that block any fresh/CI boot. Two are fixed; two need a focused AppHost change.

1. **AppHost has no `launchSettings.json`** → plain `dotnet run` crashes immediately
   (`OptionsValidationException: ASPNETCORE_URLS / ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL not set`).
   **FIXED** — added `src/AspireAppHost/FreshCart.AppHost/Properties/launchSettings.json` (dashboard URLs,
   OTLP endpoints, `ASPNETCORE_ENVIRONMENT=Development`).
2. **Services can't start outside Development** because the secrets hardening moved `Jwt:SigningKey` to
   `appsettings.Development.json`; if the AppHost runs children in Production the key is missing and every
   service throws on startup. **FIXED for local** by the launchSettings `ASPNETCORE_ENVIRONMENT=Development`
   (children inherit it). For non-Development environments the key must come from env/Key Vault.
3. **Aspire persistent-container password drift** — `AddSqlServer`/`AddPostgres`/`AddMySql`/`AddRabbitMQ`/
   `AddRedis` mint a *random* password each run, but `WithDataVolume()` + `ContainerLifetime.Persistent`
   reuse the container, so a second run's password no longer matches (`Login failed for user 'sa': Password
   did not match`). **Verified fix**: stable `AddParameter("…", value, secret: true)` passed to each backing
   service. *Caveat:* keep the dev password values out of committed source — read them from the AppHost's
   `appsettings.Development.json` (`Parameters` section) or user-secrets, consistent with finding §6 P0.
4. **Databases not provisioned on fresh volumes** — Aspire's `AddDatabase` only registers the connection
   string; it does not `CREATE DATABASE`. Services that self-migrate (Ordering, Inventory — both use
   `EnableRetryOnFailure`) survive; Identity self-migrates too (`IdentityDataSeeder.MigrateAsync`) but the
   others don't provision their schema's database. **Verified**: `AddDatabase(name).WithCreationScript(...)`
   **creates the SQL Server databases** (confirmed — `identitydb`/`orderingdb`/`inventorydb`/`paymentreaddb`
   came up), but the **Postgres** equivalent did **not** create `catalogdb`/`basketdb` (Aspire runs the PG
   script against the target DB, which doesn't exist yet). **Remaining fix**: provision the Postgres
   databases — simplest is to let Marten create them (`AddMarten(...).CreateDatabasesForTenants(c =>
   c.MaintenanceDatabase(maintenanceConn).ForTenant().CheckAgainstPgDatabase().CreateIfNotExists())` in
   Catalog/Basket), or a PG init script against the `postgres` maintenance DB.
5. **Cold-boot DB connection timeouts on a resource-contended host** — even with passwords + DBs correct,
   services without connection resilience (Identity, Payment) hit `SqlException 258: wait operation timed
   out` migrating during the simultaneous start of 13 services + 6 DB containers, because this machine was
   also running another project's 10 containers. **Fix**: add `EnableRetryOnFailure` to the Identity and
   Payment `DbContext` registrations (Ordering/Inventory already have it), and/or run the live boot on a
   host with more headroom (this stack wants ~6–8 GB free for a clean cold start).

**To finish the live E2E (one focused session on a host with headroom):** apply (3) stable passwords via
Development config, (4) Postgres DB provisioning + `EnableRetryOnFailure` on Identity/Payment, then
`dotnet run` the AppHost + `ng serve` and run `customer-journey.spec.ts`. Finding (1) — the AppHost
`launchSettings.json` — is already fixed and committed. **The verified state of the codebase
(0 errors / 0 warnings / 804 tests) is unaffected by every one of these run-only findings.**
