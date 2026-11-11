# Pass 58: posting transactions, and 1.5.0

**Date:** 2026-11-11.
**Start:** HEAD `d9d9c75f` (pass47b), with the organisation-switcher change pending.

**Plan:** `docs/passes/pass58-transactions-plan.md`, amended first with the owner's three decisions and amendments A1 to A6, then implemented in its own order of work (steps 0 to 9). Additions made during implementation are marked **(P58)** in the plan and listed in §3.

**Suites** (PostgreSQL 15.7 at 127.0.0.1:5432 via `GX_TEST_PG`, each project alone):

| Suite | Result | Before this pass |
|---|---|---|
| Application.UnitTests | **574 passed, 14 skipped** | 568 + 14 |
| Infrastructure.UnitTests | **229** | 227 |
| Application.IntegrationTests | **80** | 60 |
| Server.UI.IntegrationTests | **311** | 311 |

0 failed. **Build:** 0 errors. The only warnings are the 10 already present in files this pass does not touch.

---

## 1. Step 0: 1.4.0 closed

- **1.4.0 stays as it was packed in pass 57**, ICU only (owner's choice).
  - It is installed in the machine's template store, replacing 1.3.0.
  - It is tagged **`gx-v1.4.0`** on `a8a89d63`. The prefix keeps it apart from the upstream template's tags, which this repository also carries (it has an upstream `v1.4`).
- **The package's commit stamp is wrong.** The package says `5a3c1144`, the parent commit, because it was packed before its changes were committed. 1.5.0 is packed **after** its commit for that reason (§6).
- **The organisation-switcher work is stopped.** The `TenantSelector.razor` change and `docs/HANDOFF-tenant-switcher.md` are in `git stash` ("org-switcher work, stopped by owner 2026-11-11 …"), uncommitted, recoverable.
- **Commit `89726c76` "Close 1.4.0"** records this in `docs/passes/pass57-icu.md`.

## 2. What was built

| Area | Files |
|---|---|
| Contracts | `Domain/Common/PostingEvent.cs`; `Application/Common/Interfaces/Transactions/*` (`ITransactionalRequest`, `IIdempotentRequest`, `IUnitOfWork`, `IUnitOfWorkManager`, `IUnitOfWorkSession`, `IPostingEventHandler<T>`, `IPostingEventDispatcher`, `IIdempotencyStore`, `IDocumentNumberAllocator`); `PostingRejectedException` |
| Application | `Pipeline/TransactionBehaviour.cs`; `Common/Transactions/` (`ForUpdate()`, `PostingEventDispatcher` + `AddPostingEventHandlers`, `IdempotentResponseCodec`); the pipeline order in `DependencyInjection.cs` |
| Infrastructure | `Persistence/Transactions/` (`UnitOfWorkScope`, `UnitOfWorkSession`, `UnitOfWorkManager`, `IdempotencyStore`, `DocumentNumberAllocator`, `PostingSql`); `Interceptors/ForUpdateCommandInterceptor.cs`; the factory lease; dispose suppression on `ApplicationDbContext`; buffering in `DispatchDomainEventsInterceptor` |
| Model | `ProcessedRequest`, `DocumentSequence` and their configurations. Migration `PostingTransactions` for PostgreSQL, SQL Server and SQLite (the template builds all three). |
| Changed behaviour | `ResultExceptionBehavior`: logs `DbUpdateException`, gives `DbUpdateConcurrencyException` its own message, and shows a reference id instead of the exception's text. `FusionCacheBehaviour` and `ValidationBehavior`: see §3. `ImportPicklistSetsCommand` is now `ITransactionalRequest`. |
| Docs | README "Posting transactions" (step 8); the 1.5.0 release notes; `AppConfigurationSettings.Version` set to 1.5.0 |

The pinned interceptor suites (`SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests`, `TransactionalAuditTests`) and the publisher suites pass **unmodified** (plan row 13). One existing test was edited: `GxTableNamingTests` now lists the two new template tables. That test exists to require exactly this.

## 3. Additions not in the amendments (P58)

Each one is needed for the amended design to hold.

1. **The holder is installed synchronously in the caller's execution context.** The first test run failed (T04): the handler saw no unit of work. The cause was that an `async` method's writes to an `AsyncLocal` are undone when it returns to its caller, and `BeginAsync` was async.
   - `BeginAsync` is now a non-async wrapper. It installs an empty holder in the behaviour's flow, and the session is attached once the context is open.
   - Amendment 6B (empty the holder at the end) is unchanged and tested (T19).
2. **A publish failure after the commit is logged at Error, not returned as a failure.** The data is committed. Mediator 3 throws on `Publish` for a notification type it did not see at compile time, which a module could hit.
3. **Nested deadlocks still retry.** The inner `ResultExceptionBehavior` turns an inner deadlock into a result before the outer behaviour sees it. The unit of work keeps the inner exception as `FailureCause`, and the outer attempt retries if that cause is retryable.
4. **Cacheable queries bypass FusionCache inside a unit of work.** Otherwise uncommitted rows that may still roll back would be cached.
5. **Validators run one at a time inside a unit of work.** A nested request's validators would otherwise use the one shared context in parallel (`Task.WhenAll`), and EF throws on that.
6. **`ForUpdate()` also refuses a query with neither `ORDER BY` nor `LIMIT`.** This enforces the A4 rule that the lock order is stated in the query.
7. **`ImportPicklistSetsCommand` became transactional.** No other template request implements the marker, so without it the generated pipeline would never place `TransactionBehaviour`. It serves as the order probe for T20 and as the working example. The import is now atomic.
8. **EF wraps transient errors.** EF's default, non-retrying execution strategy wraps errors Npgsql classes as transient (deadlock, serialisation failure, `lock_not_available`) in an `InvalidOperationException`. `IsRetryable` walks the inner-exception chain. T10 asserts on the inner `PostgresException`.
9. **Npgsql code sits under `#if (UsePostgreSql)`,** as `LogDatabaseStartupCheck` does, so a project generated for SQL Server still compiles. On other providers the PostgreSQL-only services throw `NotSupportedException` at run time (owner decision 2).

## 4. Amendments as written

| | Applied | Test |
|---|---|---|
| A1 Document lock | §3.8 rule, README "The document lock" with an example | T14 |
| A2 PostingRejectedException | As written. **One limitation:** `Result` has no code field, so `ErrorCode` is logged but not returned to the caller. | T15 |
| A3 Chained events | §3.4, README with the `SalesInvoicePosted` → `StockIssued` example | T16 (×2) |
| A4 One lock order | §3.6 and §3.7 replaced; "allocate last" removed; `ORDER BY` in the query (and enforced, P58 §3.6) | T11 kept as the safety net |
| A5 Nested requests | As written, plus P58 §3.3 for deadlocks | T17 (×2) |
| A6A `next` invoked again | **Verified, no restructure needed.** Mediator 3.0.2's generated `RequestHandlerWrapper` builds `next` as a closure chain over one handler instance resolved per send. README states the no-state, no-mutation rule. | T18 |
| A6B Holder pattern | As written (and see P58 §3.1) | T19 |

No amendment conflicted with the code.

## 5. Tests added (plan §5)

- **Application.IntegrationTests, `Transactions/PostingTransactionTests.cs`:** rows 1 to 11 and 14 to 19, 20 tests in all.
  - Row 5 runs here against the real interceptor rather than as a unit test.
  - Concurrent tests use real separate connections. Every wait between tasks is bounded at 30 s, so a failed partner fails the test instead of hanging the run (one hang happened during development).
- **Application.UnitTests, `Pipeline/TransactionPipelineTests.cs`:** rows 12 and 20, 6 tests.
- **Harness:** `Testing.cs` registers the test assembly's posting handlers with `AddPostingEventHandlers`, as a project registers its own.

Server.UI.IntegrationTests is unchanged in count (311) and green.

## 6. Release

- **Version 1.5.0.** The release notes cover pass 58 and pass 47b (SkiaSharp), which was in no earlier package.
- **Packing happens after the pass 58 commit,** so that the package's `commit` stamp is that commit. The 1.4.0 stamp mismatch (§1) is not repeated.
- **Release procedure:** pack, install in the template store (replacing 1.4.0), then run `tooling/smoke-generate.ps1 -UseInstalled`.

## 7. Not done, recorded

- **No purge of `ProcessedRequests`.** It waits for persistent Hangfire storage (review item 10).
- **No concurrency token on entities.** That is the review's step 4, a later pass.
- **No branch filter.** That is the review's step 7.
- **`FallbackExceptionHandler` still puts `exception.Message` in its result.** It was outside this pass's scope (plan §3.3 names `ResultExceptionBehavior` only).
