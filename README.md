# NovaWallet Ledger Service — .NET 10

Backend Engineer case-study implementation of a simplified wallet ledger with a Nigerian fintech chart of accounts. The implementation prioritizes **financial correctness, double-entry accounting, persistence, concurrency safety and retry-safe processing** before optional production extensions.

> **Note on framework version:** the brief says .NET 8 or 9. This implementation targets **.NET 10** (the SDK available in the environment). This is documented as an explicit assumption/choice; the codebase uses no .NET-10-only language features that cannot be back-ported, and the design is identical on .NET 8/9.

## Architecture

Clean Architecture with the Repository Pattern and Unit of Work. Web/mobile clients connect to the ProxyApi gateway, which authenticates, rate-limits, and forwards to the private WalletApi.

```
Web/Mobile → ProxyApi (gateway: TLS, HSTS, rate limit, security headers, CORS, JWT) → WalletApi → Services → Repositories → SQL Server
                                                                                    ↑
                                                                       Settlement.Worker (NIP dispatch + EOD reconciliation)
```

### Projects

- `src/Proxy.Api` — hardened gateway/edge API (YARP reverse proxy). Enforces TLS/HSTS, rate limiting (100 req/min API, 20/min transfers, 10/min admin), security headers, CORS, 64KB request limit, JWT validation, admin role checks. Swagger/OpenAPI configurable via `appsettings.json`. Web/mobile integrate here; WalletApi is never exposed directly.
- `src/Wallet.Api` — private wallet/transfer API, JWT auth, Swagger/OpenAPI, dev token endpoint. All financial endpoints live here.
- `src/Wallet.Domain` — DDD aggregates, value objects and financial invariants (no dependencies).
- `src/Wallet.Application` — use-case contracts, repository interfaces, `IPaymentRail` abstraction, WAT/idempotency/fee policy, account-number generator.
- `src/Wallet.Infrastructure` — EF Core / SQL Server persistence, repository implementations, `MockNipPaymentRail`, transactional ledger, audit and outbox.
- `src/Settlement.Worker` — persistent `BackgroundService`s for NIP dispatch (with lease recovery, `Unknown` status-query loop, reversal) and EOD reconciliation.
- `tests/NovaWallet.UnitTests` — fast domain/policy unit tests.
- `tests/NovaWallet.IntegrationTests` — SQL Server concurrency + reconciliation tests against the real persistence boundary.

### Clean Architecture dependencies

```
Wallet.Domain         → (none)
Wallet.Application     → Wallet.Domain
Wallet.Infrastructure  → Wallet.Application, Wallet.Domain
Wallet.Api             → Wallet.Application, Wallet.Infrastructure
Proxy.Api              → (standalone gateway; forwards to WalletApi)
Settlement.Worker      → Wallet.Application, Wallet.Infrastructure
```

### Repository Pattern

Data access is through repository interfaces defined in `Wallet.Application` and implemented in `Wallet.Infrastructure`:

- `IUnitOfWork` — transaction management (`BeginTransactionAsync`, `SaveChangesAsync`)
- `IWalletRepository` — wallet CRUD + row-level locking (`GetByIdForUpdateAsync`, `GetBySystemKeyForUpdateAsync`)
- `ITransferRepository` — transfers + idempotency records
- `ILedgerEntryRepository` — ledger entries + paginated statement
- `IAuditLogRepository` — append-only audit log
- `IOutboxRepository` — outbox messages
- `ISettlementJobRepository` — settlement jobs with claim/lease queries
- `IExternalAccountRepository` — First Bank external accounts + row locking
- `IReconciliationRepository` — reconciliation reports + paged queries
- `IDailyOutboundCounterRepository` — atomic MERGE for per-customer daily limit

Services depend on repository interfaces + `IUnitOfWork`, never on `AppDbContext` directly.

### Payment Rail Abstraction (NIP)

The external NIP integration is abstracted behind `IPaymentRail`:

```csharp
public interface IPaymentRail
{
    Task<PaymentResult> SendAsync(PaymentInstruction instruction, CancellationToken ct);
    Task<PaymentStatusResult> GetStatusAsync(string externalReference, CancellationToken ct);
}
```

`MockNipPaymentRail` simulates NIBSS NIP for the case study. A production `NibssNipPaymentRail` would POST to the NIP API. The ledger transaction is **never** coupled to the external rail — the worker submits the payment asynchronously and reconciles via status query.

## Chart of accounts (Nigerian wallet model)

Every new customer gets a 10-digit NUBAN-style account number (prefix `90` + serial + mod-11 check digit). Beside customer wallets, four system accounts are seeded on startup and participate in double-entry transfers:

| Account | SystemKey | Purpose |
| --- | --- | --- |
| Settlement | `SETTLEMENT` | Float for outbound transfers to other banks; credited on submit, debited on payout/reversal. |
| Income | `INCOME` | NovaWallet fee revenue. |
| VAT | `VAT` | 7.5% VAT on the fee. |
| Customer (per customer) | — | Customer balance; the aggregate "ledger" of customer money. |

### External bank accounts (First Bank of Nigeria)

Four physical accounts at First Bank mirror the internal system accounts and are seeded on startup:

| External Account | AccountKey | Mirrors (DB) | Purpose |
| --- | --- | --- | --- |
| Ledger Holding | `LEDGER_HOLDING` | Sum of customer balances | Physical customer money at First Bank. |
| Settlement Holding | `SETTLEMENT_HOLDING` | Settlement system account | Float for outbound payouts to other banks. |
| Income Holding | `INCOME_HOLDING` | Income system account | Fee revenue at First Bank. |
| VAT Holding | `VAT_HOLDING` | VAT system account | VAT collected at First Bank. |

Customer deposits credit both the DB wallet and the external Ledger Holding (physical money arrives). Outbound transfers update the internal DB only at submission; the **EOD reconciliation** updates the external accounts and verifies they balance.

## Money representation

All money is integer **kobo** (`long` / SQL Server `BIGINT`). No float/double is used in the money path. `Money` subtraction throws on a negative result, and all arithmetic is `checked` for overflow.

## Transfer flows

### Internal (NovaWallet → NovaWallet)
1. Validate request + `Idempotency-Key`.
2. Begin DB transaction.
3. Idempotency replay check (constant-time hash compare).
4. Lock source wallet `WITH (UPDLOCK, HOLDLOCK, ROWLOCK)`; verify ownership.
5. Verify destination **account number** exists and is a customer wallet.
6. Lock destination wallet (UPDLOCK); source and destination are never the same.
7. Debit source / credit destination (double-entry: `SourceLedger` / `DestinationLedger` legs).
8. Record transfer (status `Settled` — internal transfers settle in-ledger), idempotency record, audit, outbox event.
9. Commit. Internal transfers are free and do **not** consume the daily outbound limit.

### Outbound (NovaWallet → other bank)
1. Validate request + `Idempotency-Key`.
2. Begin DB transaction.
3. Idempotency replay check.
4. **Reserve against the daily limit** via a single atomic `MERGE ... WITH (HOLDLOCK)` on the per-customer daily counter. This serializes all outbound transfers for a customer (across all of their wallets) and is rolled back if any later check fails — so a failed transfer consumes no daily limit.
5. Lock source wallet (UPDLOCK); verify ownership; check balance ≥ amount + fee + VAT.
6. Lock Settlement / Income / VAT system accounts (UPDLOCK, fixed order).
7. Debit source by `amount + fee + VAT`; credit Settlement (amount), Income (fee), VAT (VAT).
8. Record transfer (`Verified`), idempotency record, ledger legs, audit, outbox event, and a `Pending` settlement job.
9. Commit.

## Financial correctness

1. Money is integer kobo; `checked` arithmetic; no float/double.
2. Each transfer commits debit + credit + transfer + idempotency + ledger legs + audit + outbox + (outbound) settlement job in **one database transaction**.
3. Concurrency correctness comes from SQL Server row locks (`WITH (UPDLOCK, HOLDLOCK, ROWLOCK)`), **not** in-memory locks. Correctness survives horizontal scaling because each API instance acquires the same DB locks.
4. `Wallet.Version` is an EF concurrency token as a defense-in-depth backstop.
5. DB constraints backstop app rules: non-negative balance, positive amounts, unique account numbers, unique idempotency keys, unique system-account keys, and a non-negative daily counter.
6. Audit storage is append-only; a SQL Server `FOR UPDATE, DELETE` trigger raises and rolls back any mutation.

## Idempotency

`Idempotency-Key` is mandatory on transfers.
- Same customer + same key + same payload → original transfer result is returned.
- Same key + different payload → rejected (`idempotency.payload_mismatch`).
- A unique DB constraint protects concurrent duplicates.
- Request fingerprint is SHA-256 over the canonical transfer payload; comparison is constant-time.

## Daily outbound limit / WAT

Default limit is **₦500,000/day = 50,000,000 kobo**, enforced on the transfer **amount** (not fee/VAT). The WAT business-day window is computed at request time (`W. Central Africa Standard Time`); no midnight reset job is required. The counter is incremented atomically per customer per WAT day via `MERGE`, so concurrent outbound transfers from different wallets of the same customer cannot exceed the limit.

## Fees

Outbound default: flat **₦50 fee** (50,000 kobo) + **7.5% VAT on the fee** (3,750 kobo). Configurable in `appsettings.json` under `Fees`. Internal transfers are free.

## Settlement BackgroundService (NIP dispatch + reconciliation)

Outbound transfers create a `Pending` settlement job. The worker:

1. **Publishes pending outbox messages** (replace with a durable broker in production).
2. **Claims jobs** that are `Pending`, `Failed`-and-due, or whose **lease has expired** (`Processing` with `LeaseUntil < now`) — recovering work after a crash.
3. **Dispatches to NIP** via `IPaymentRail.SendAsync`:
   - `Sent` → stores `ExternalReference`, marks transfer `Settled`, job `Succeeded`.
   - `Failed` → reverses ledger legs (refund customer, debit Settlement/Income/VAT), marks transfer `Failed`.
   - `Unknown` (timeout) → stores `ExternalReference` if available, marks transfer `Unknown`, does **NOT** reverse. **Timeout ≠ failure.**
4. **Status-query loop**: queries `IPaymentRail.GetStatusAsync` for all `Unknown` transfers and resolves them to `Settled` or `Failed`.

### Transfer state machine

```
Submitted → [NIP dispatch] → Settled (success)
                        ↘ Failed   (reversal)
                        ↘ Unknown  (status query loop → Settled | Failed)
```

The key invariant: **the ledger transaction and the external NIP call are never in the same database transaction.** The worker submits asynchronously and reconciles via status query.

## ProxyApi Gateway (hardened edge)

Web/mobile clients connect to the ProxyApi gateway. The WalletApi is never exposed directly.

- **TLS/HSTS**: HTTPS redirect + HSTS in production.
- **Rate limiting**: 100 req/min (general API), 20/min (transfers), 10/min (admin). 429 with `Retry-After` on exceed.
- **Security headers**: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `X-XSS-Protection`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, `Cache-Control: no-store`, no `Server` header.
- **CORS**: restricted to `localhost` and `*.novawallet.ng` / `*.novawallet.com` origins. Only `GET`/`POST` methods, specific headers.
- **Request size limit**: 64KB max body. 413 on exceed.
- **Connection limits**: 200 max concurrent, 30s keep-alive timeout.
- **JWT validation**: validates issuer/audience/signing key at the gateway before forwarding.
- **Admin role check**: `/admin/**` paths require `admin` or `product-owner` role.
- **Correlation ID**: propagates `X-Correlation-Id` or generates one per request.
- **Swagger/OpenAPI**: configurable via `appsettings.json` (`Swagger:Enabled`, `Swagger:RoutePrefix`, `Swagger:Title`, `Swagger:Description`, `Swagger:Version`). Enabled by default.
- **YARP reverse proxy**: forwards authenticated requests to the private WalletApi at `https://localhost:51077` (local dev) or `http://wallet-api:8080` (Docker, overridden via env var).

## API

All `/api/v1/**` endpoints require a JWT bearer token with a `sub` claim. In development, mint one with:

```
POST /dev/token?sub=customer-1                    # customer token
POST /dev/token?sub=admin-1&role=admin             # admin token (for reconciliation endpoints)
```

### Wallet endpoints
- `POST /api/v1/wallets` — create wallet (returns allocated account number).
- `GET  /api/v1/wallets/{walletId}/balance`
- `POST /api/v1/wallets/{walletId}/credits` — deposit/top-up (simulates inbound NIP).
- `GET  /api/v1/wallets/{walletId}/transactions?page=1&pageSize=25` — paginated statement.
- `GET  /api/v1/name-enquiry/{accountNumber}` — NIP name enquiry (verify account → return name).

### Transfer endpoints (`Idempotency-Key` header required)
- `POST /api/v1/transfers/internal` — NovaWallet→NovaWallet (destination by account number, free, no daily limit).
- `POST /api/v1/transfers/outbound` — NovaWallet→other bank (destination bank code + account number + name, fee + VAT, daily limit, settlement job created).
- `POST /api/v1/transfers/inbound` — NIP inbound (other bank→NovaWallet customer, debits Settlement, credits customer).
- `GET  /api/v1/transfers/{transferId}` — get transfer details.
- `GET  /api/v1/transfers/{transferId}/status` — query NIP status (resolves `Unknown` → `Settled`/`Failed`).

### Admin/PO endpoints (requires `admin` or `product-owner` role)
- `GET  /api/v1/admin/reconciliation?page=1&pageSize=25` — list reconciliation reports.
- `GET  /api/v1/admin/reconciliation/{watDate}` — get reconciliation report for a specific WAT date.
- `POST /api/v1/admin/reconciliation/run` — run reconciliation for today (EOD balancing).
- `GET  /api/v1/admin/external-accounts` — list First Bank external accounts and balances.

### System endpoints (no auth)
- `/swagger` (WalletApi + ProxyApi), `/health`, `/ready`

Errors use RFC 7807 Problem Details with a stable `code` extension.

## Run

```bash
docker compose up --build
```

The public proxy is exposed on `http://localhost:8080`; in production, terminate TLS at the WAF/load balancer and expose only the proxy/edge tier. The wallet API, worker and database remain private. The compose file starts **SQL Server 2022** as the datastore.

## Tests

```bash
dotnet test tests/NovaWallet.UnitTests/NovaWallet.UnitTests.csproj
dotnet test tests/NovaWallet.IntegrationTests/NovaWallet.IntegrationTests.csproj   # needs NOVAWALLET_TEST_CONNECTION
```

Integration tests create an isolated database per test. Concurrency scenarios covered:
- **Same-source N2N**: 20 concurrent transfers of 100 kobo from a 1,000-kobo wallet → exactly 10 succeed, final balance 0, never negative.
- **Concurrent credits**: 20 concurrent deposits of 1,000 kobo to one wallet → final balance 20,000 (no lost update).
- **Same-destination N2N**: 20 sources each send 100 kobo into one destination → destination balance 2,000 (no lost credit).
- **Per-customer daily limit across wallets**: 20 concurrent outbound transfers of ₦50,000 from two wallets of one customer → exactly 10 succeed, total outbound ₦500,000 (the limit).

## CI quality gate

`.github/workflows/ci.yml` separates the quality gate:

**Unit tests → Build → SQL Server integration/concurrency tests → Container build**

The `build` job `needs: unit-tests`, so a failing unit test prevents the build job from running. Container images are built only after the integration/concurrency stage passes.

## AI usage

See [`AI_USAGE.md`](AI_USAGE.md). The assessment expects documented AI usage, including an example of an unsafe/wrong AI suggestion and how it was caught and corrected.
