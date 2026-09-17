# AI_USAGE.md

## Tools used

AI assistance was used for architecture review, test-case generation, code scaffolding, README wording and CI workflow review. All AI output was verified against the financial invariants, transaction boundaries and tests before being considered complete.

## Representative prompts

- "Design a .NET 10 wallet ledger with a Nigerian chart of accounts (ledger/settlement/VAT/income) that cannot double-spend under concurrent transfers."
- "Review idempotency semantics for same-key replay versus same-key payload mismatch, and the constant-time comparison of the request hash."
- "Generate the double-entry ledger legs for an outbound transfer that debits the customer and credits Settlement/Income/VAT."
- "Design a deadlock-free per-customer daily outbound limit that works across a customer's multiple wallets on SQL Server."
- "Create unit tests for money invariants, WAT midnight boundaries, fee/VAT and the account-number check digit."

## Example wrong/unsafe AI output caught and corrected

### 1. In-memory lock around the source wallet (concurrency)

An early design suggested an in-memory lock around the source wallet. That is unsafe for a horizontally scaled API because another instance acquires a different process-local lock and both proceed.

**Correction.** Correctness was moved to SQL Server row locks (`WITH (UPDLOCK, HOLDLOCK, ROWLOCK)`); each transfer path locks the wallets/system accounts it touches inside one DB transaction. The integration concurrency tests exercise multiple simultaneous requests against the same wallet and the same destination.

### 2. Treating a timeout as a failed financial operation

A simplification suggested treating an external payout timeout as a hard failure and refunding immediately. That can double-pay if the bank actually received the request.

**Correction.** The settlement job carries durable state and a lease. A timeout leaves the job `Processing`; the lease-recovery path re-claims expired `Processing` jobs (rather than auto-refunding) so a real NIP/TSQ adapter can reconcile the uncertain outcome using provider references before deciding to reverse.

### 3. Daily limit via a retry loop on a counter row (deadlock)

AI proposed a per-customer daily counter loaded with `SELECT ... WITH (UPDLOCK)`, then insert-on-miss with a C# retry loop on the unique-constraint violation. Under 20 concurrent outbound transfers this deadlocked on SQL Server (the retry loop held locks across the failed insert while system-account locks were taken in a different order by other transactions).

**Correction.** The counter is now incremented with a single atomic `MERGE DailyOutboundCounters WITH (HOLDLOCK)` that performs the existence check, limit check and increment in one statement. The MERGE runs **before** any other row lock, so all outbound transfers for a customer serialize on the counter row first; if a later balance/ownership check fails, the whole transaction rolls back and the reservation is undone. The deadlock disappeared and the concurrency test passes deterministically.

### 4. `Microsoft.OpenApi` transitive vulnerability warning as error

The build (with `TreatWarningsAsErrors`) failed on NU1903 because `Swashbuckle.AspNetCore` 10.0.1 pulled in `Microsoft.OpenApi` 2.3.0, which has a known high-severity advisory (GHSA-v5pm-xwqc-g5wc). An AI suggestion to suppress the warning was rejected — suppressing a security advisory hides the risk.

**Correction.** `Swashbuckle.AspNetCore` was upgraded to 10.2.3 and `Microsoft.OpenApi` was explicitly pinned to 2.12.2, removing the vulnerable transitive dependency. Build is clean (0 warnings, 0 errors).

### 5. Lost-update on credits and destination

Review found that the original `CreditAsync` and the destination credit in the transfer path loaded the wallet with a plain `SingleOrDefaultAsync` (no lock) and overwrote the balance on `SaveChanges`. Two concurrent credits to the same wallet both read 100, both write back 100+own, and the second overwrites the first — losing money. The original concurrency test only fired one→many transfers and never exercised concurrent credits or same-destination transfers, so it never caught this.

**Correction.** `CreditAsync` now loads the wallet `WITH (UPDLOCK, HOLDLOCK, ROWLOCK)`, and the transfer path locks **both** source and destination. New integration tests cover concurrent credits to one wallet and concurrent transfers into one destination.

## Human verification

AI-generated code is not treated as authoritative. Financial invariants, transaction boundaries, idempotency behavior, concurrency behavior, deadlock behavior, fee/VAT accounting and CI gating are all verified through unit and integration tests and database constraints before being considered complete. The full suite (14 unit + 8 SQL Server integration) passes.
