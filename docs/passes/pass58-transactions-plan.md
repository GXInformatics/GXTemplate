# Pass 58: posting transactions (plan, amended)

**Status:** amended 2026-11-11 with the owner's decisions (§6) and amendments 1 to 6, then implemented in this pass. Text marked **(A1)** to **(A6)** comes from the amendments. Text marked **(P58)** was added during implementation, where the code required something the amendments did not say. Each (P58) addition is listed in the pass report.
**Database:** PostgreSQL only (§6.2). Locks, sequences and idempotency throw `NotSupportedException` on other providers. The unit of work and the transaction behaviour themselves are provider-neutral.
**Source:** `docs/gxtemplate-adequacy-review.md`, areas B, C, D and L. This pass delivers the review's change list items 2, 3, 4 (except the concurrency token) and 5.

## 1. What is wrong today (verified in code)

- **No shared context:** every handler creates and disposes its own context through `IApplicationDbContextFactory.CreateAsync` (`ApplicationDbContextFactory.cs`). A command handler and its event handlers never share a context, a connection or a transaction.
- **Events are published from the save hooks:** `DispatchDomainEventsInterceptor` publishes from `SavingChanges` (deleted entities) and from `SavedChanges` (all other entities). When a handler owns a transaction, that is **before the commit**.
- **Handlers run later, in the background:** `ChannelBasedNoWaitPublisher` (registered in `Application/DependencyInjection.cs:22`) queues handlers to a background loop and returns immediately. The loop logs and swallows any handler exception (`ProcessNotifications`).
- **The handler's view of the database is unreliable:**
  - A queued handler can start before the command commits. Its own connection then cannot see the command's rows, or it blocks on them.
  - The handler still runs if the command later rolls back.
  - It also captures the publisher's `ExecutionContext` (Pass 39), so anything held in an `AsyncLocal` at publish time follows it.
- **Database errors are not logged:** `DbUpdateException`, including `DbUpdateConcurrencyException`, becomes a generic failure with no log entry (`ResultExceptionBehavior` and `DbExceptionHandler`).
- **Cache invalidation is innermost:** `CacheInvalidationBehaviour` sits inside every other behaviour, so if it ran inside a transaction it would clear the cache before the commit.

## 2. Target behaviour

A command marked `ITransactionalRequest` runs in **one context, one connection and one transaction**. The work happens in this order:

1. The behaviour opens a context and a READ COMMITTED transaction and records the **unit of work** in an `AsyncLocal`.
   - **(A6)** The `AsyncLocal` holds a **holder object**, and the holder points to the unit of work. When the unit of work ends, the holder is **emptied**, the same pattern as `HttpContextAccessor`. Any `ExecutionContext` captured while it was active, such as a background notification handler, then sees no unit of work, never a disposed context.
2. If the request is `IIdempotentRequest`, the behaviour claims its `RequestId` (§3.5). If the claim finds a request that has already completed, the behaviour returns that request's stored response and does nothing else.
3. The command handler runs. Any `IApplicationDbContextFactory.CreateAsync` call made inside the unit of work returns the **same** context. Disposing it does nothing until the unit of work ends, so existing handlers that use `await using var db = …` need no change.
   - **(A1)** A Post, Approve or Reverse handler first locks its document header and re-checks the document's status (§3.8).
4. The behaviour saves. The interceptors run as today: the audit interceptor joins the transaction. The events interceptor, because a unit of work is active, **moves events into the unit of work's buffer** instead of publishing them.
5. Posting events (`PostingEvent`) are taken from the buffer and dispatched **inline and in order** to `IPostingEventHandler<T>` handlers, which use the same context. The behaviour saves again and repeats until no posting events remain, with a depth limit of 10 against cycles.
   - **(A3)** A posting handler may raise a further `PostingEvent` on an entity it saves. That event is dispatched in the next round, which is how one module's handler gets another module's result (§3.4).
6. The idempotency response is recorded, and the transaction commits.
7. The holder is emptied and the context is disposed. **Only then** are the buffered ordinary `DomainEvent`s published through `IMediator.Publish` (the channel publisher, as today).
   - **(P58)** The transaction has committed by this point, so a publish failure does not turn the request into a failure the user would retry. It is logged at Error and the committed response is returned. Mediator 3 throws on publish only for a notification type it did not see at compile time.
8. **On failure:** if anything throws, or the handler returns `IResult.Succeeded == false`, the transaction rolls back and both event buffers are discarded. Nothing is published and no idempotency row is written.
   - **(A2)** A `PostingRejectedException`, thrown by the command handler or by any posting handler, gets the same rollback. It is **not retried**, is logged at **Information**, and returns a failure result carrying the exception's message.
9. **On deadlock or serialisation failure:** for PostgreSQL error `40P01` or `40001`, the whole attempt is retried with a fresh context and fresh event buffers. There are at most 3 attempts, with jittered backoff. Any other error is not retried.
   - **(P58)** EF's default, non-retrying execution strategy wraps any error Npgsql classes as transient (deadlock, serialisation failure, `lock_not_available`) in an `InvalidOperationException`. The retry check therefore walks the inner-exception chain.   - **(A6, verified)** Mediator's `next` delegate can be invoked more than once. The generated `RequestHandlerWrapper.Handle` builds it as a closure chain over one handler instance, resolved once from the scope. Each retry therefore calls `next` again: the inner behaviours re-run, and the **same handler instance** handles the request again. A transactional handler must hold no state between attempts and must not mutate the request object (§3.4).

**(A5) Nested transactional requests.** When a unit of work is already active, `TransactionBehaviour` **joins** it. The inner request:
- does not open a context or a transaction, and does not commit;
- does not dispatch or publish events (the outer loop does);
- does not retry, and does not claim its own `RequestId`.

A failure result, a `PostingRejectedException` or any other exception from the inner request **marks the unit of work as failed**. The outer behaviour then rolls back, even if the outer handler ignored the inner result. It returns the outer handler's failure result if there is one, and otherwise a failure carrying the inner errors. **(P58)** If the inner exception was a deadlock or serialisation failure, the outer attempt is retried as a whole. This is needed because the inner `ResultExceptionBehavior` turns the exception into a result before the outer behaviour can see it.

Requests that are not `ITransactionalRequest` behave exactly as they do today.

## 3. Components

### 3.1 Application (contracts)

| Type | Purpose |
|---|---|
| `ITransactionalRequest` | Marker interface. Requests with it run inside a unit of work. |
| `IIdempotentRequest { Guid RequestId }` | Requests with it are deduplicated by `RequestId`. The behaviour refuses `Guid.Empty`. Every Post, Approve and Reverse command implements both interfaces. **(P58)** Idempotent requests must return `Result` or `Result<T>`, because the stored response is rebuilt from those types. |
| `PostingEvent : DomainEvent` | Base class for events handled inside the transaction, raised with the existing `AddDomainEvent`. Mediator finds no handlers for it. |
| `IPostingEventHandler<TEvent>` | `ValueTask Handle(TEvent e, CancellationToken ct)`. Registered by an assembly scan (`AddPostingEventHandlers`), scoped. |
| **(A2)** `PostingRejectedException` | A business rejection with a user-facing `Message` and an optional `ErrorCode`. The code is logged; the failure result carries the message (`Result` has no code field). |
| `IUnitOfWork` | `bool IsActive`, `IApplicationDbContext Context`. Used by services that must refuse to run outside a transaction. |
| `IDocumentNumberAllocator` | `ValueTask<long> NextAsync(string documentType, int branchId, int year, ct)` (§3.6). |
| `QueryableLockExtensions.ForUpdate()` | Tags a query for `SELECT … FOR UPDATE` (§3.7). |

### 3.2 Pipeline

New order (outermost first), in `Application/DependencyInjection.cs`:

```
Authorization → Validation → ResultException → CacheInvalidation → Transaction → Performance → FusionCache
```

- **`CacheInvalidationBehaviour` moves outward**, so it runs only after the transaction has committed or the handler has returned.
- **`TransactionBehaviour<TRequest,TResponse>`** applies when `TRequest : ITransactionalRequest` and `TResponse : IResult`. It owns steps 1 to 9 of §2 and the nested case **(A5)**. Idempotency is part of it, not a separate behaviour, because the claim must happen in the same transaction.
- **(P58) `FusionCacheBehaviour` bypasses the cache while a unit of work is active.** A cacheable query sent from inside a transaction would otherwise cache rows that are not committed yet and may roll back.
- **(P58) `ValidationBehavior` runs validators one at a time while a unit of work is active.** A nested request's validators would otherwise query the one shared context in parallel (`Task.WhenAll`), and EF throws on that.
- **(P58) A real request now probes the order:** `ImportPicklistSetsCommand` becomes `ITransactionalRequest`, so the import is atomic. It is the template's working example, and the probe the ordering test reads.

### 3.3 Infrastructure

- **`UnitOfWorkScope`:** an `AsyncLocal<Holder>` **(A6)**, plus a `ConditionalWeakTable<DbContext, UnitOfWork>` (like `AuditWriteScope`) that lets the interceptor ask whether a context belongs to a unit of work.
  - **(P58)** The holder is installed **synchronously in the caller's execution context**, in the non-async part of `BeginAsync`, and the session is attached to it once the context is open. An `async` method's writes to an `AsyncLocal` are undone when it returns, so a holder installed inside an async `BeginAsync` was invisible to the handler. The first test run caught this.
- **`ApplicationDbContextFactory.CreateAsync`:** returns the active unit of work's context when there is one, and a new context otherwise.
- **`ApplicationDbContext.DisposeAsync`/`Dispose`:** overridden to do nothing while the context is leased to an active unit of work. Only the unit of work disposes it.
- **`DispatchDomainEventsInterceptor`:**
  - Inside a unit of work, it moves events to the buffer at the same two points it publishes today. It stays silent during audit writes, as now.
  - Outside a unit of work, it behaves exactly as today. The pinned suites (`SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests`) must pass **unmodified**.
  - A `PostingEvent` raised outside a unit of work **throws** `InvalidOperationException`, naming the event, in `SavingChanges`, before anything is written.
- **Logging and error mapping:** `ResultExceptionBehavior`'s `DbUpdateException` branch logs at Error. `DbUpdateConcurrencyException` gets its own message: "This record was changed by someone else. Reload and try again." Unexpected exceptions return a fixed message with a reference id instead of `exception.Message`. **(A2)** A `PostingRejectedException` never reaches this branch inside a transaction; `TransactionBehaviour` handles it first.

### 3.4 Event dispatch rules

- Handlers run in the order events were raised, and each event's handlers run in registration order. Module authors may not depend on the order between different modules' handlers.
- **(A3) Dependencies between modules go through chained events.** A handler that needs another module's result subscribes to **that module's** event, never to the original one.
  - **Example:** `SalesInvoicePosted` is handled by Inventory, which issues stock and raises `StockIssued` carrying the unit cost. Accounting handles `StockIssued`.
  - The chained event is raised on an entity the handler saves, and the loop dispatches it in the next round, in the same transaction.
- **No external I/O in a posting handler** (mail, HTTP, files). That belongs in an ordinary `DomainEvent` handler, which runs after the commit.
- **No parallel calls on the shared context** (`Task.WhenAll` over queries). EF throws on concurrent use.
- **(A6) A transactional handler holds no state between attempts and does not mutate the request object.** A retry calls the same handler instance again with the same request.
- **(A2) Business rejections are thrown as `PostingRejectedException`.** Any other exception is treated as a fault.

### 3.5 Idempotency (PostgreSQL)

- **Table** `ProcessedRequests`, in the default schema like the template's other infrastructure tables:
  - `RequestId uuid PK`
  - `RequestType text`
  - `UserId text`
  - `CreatedAt timestamptz`
  - `Response text NULL` **(P58: `text`, not `jsonb`, because the model is shared with the other providers' migrations)**
- **Claim:** at the start of the transaction, run `INSERT … ON CONFLICT ("RequestId") DO NOTHING`.
  - **One row inserted:** the claim is new; carry on.
  - **No row inserted:** the request already exists. If another transaction holds it, PostgreSQL **waits** for that transaction to finish, so a retry sent during the first attempt blocks rather than posting twice. Then:
    - If the stored `RequestType` or `UserId` differs, refuse.
    - Otherwise return the stored response and roll back.
  - **The first attempt rolled back:** its claim row is gone, so the retry's insert succeeds and the retry runs normally.
- **Record:** in step 6, write the response. Only successful responses are ever recorded, so the stored form is `Data` alone, rebuilt as `Result.Success()` or `Result<T>.Success(data)`.
- **Retention:** a purge job (rows older than 30 days) is added once Hangfire has persistent storage, in a later pass. Until then rows stay.
- **(A1)** Idempotency stops one `RequestId` posting twice. It does **not** stop two different requests posting one document. The document lock in §3.8 does that.

### 3.6 Gap-free numbering

- **Table** `DocumentSequences` (`DocumentType`, `BranchId int`, `Year`, `NextValue bigint`), with a composite primary key.
- **Allocation statement:** `INSERT … VALUES (…, 2) ON CONFLICT (…) DO UPDATE SET "NextValue" = "DocumentSequences"."NextValue" + 1 RETURNING "NextValue" - 1`.
- **Locking:** the row lock is held until commit, and a rollback undoes the increment, so numbers have no gaps. Posting of the same type, branch and year is serialised, which is acceptable for 20 users.
- **Usage rules:** the allocator throws unless a unit of work is active. **(A4)** Allocation is lock **2** in the global lock order (§3.7). Number formatting (`GRN-ABJ-2026-000123`) belongs to the modules.

### 3.7 Row locks

- **How it works:** `query.ForUpdate()` adds `TagWith("gx:for-update")`. A new `ForUpdateCommandInterceptor` (a `DbCommandInterceptor`) appends ` FOR UPDATE` to commands carrying that tag.
- **It refuses** when:
  - no transaction is open (the lock would be meaningless);
  - the provider is not Npgsql;
  - the SQL contains `JOIN` (PostgreSQL rejects `FOR UPDATE` on the nullable side of an outer join), so `Include` cannot be used;
  - **(P58)** the query has neither `ORDER BY` nor `LIMIT`, because an unordered multi-row lock breaks the rule below.
- **(A4) One global lock order, matching the flow of a posting:**
  1. the document header (§3.8);
  2. the document number (`DocumentSequences`);
  3. item cost rows, ordered by `ItemId`;
  4. stock balance rows, ordered by (`ItemId`, `WarehouseId`).
- **The ordering must be an `ORDER BY` inside the locked query itself**, not a sort applied afterwards. PostgreSQL takes the row locks in the order rows are returned.
- **Missing balance rows:** if one does not exist, the caller creates it with `INSERT … ON CONFLICT DO NOTHING` and then locks it.
- **Retry as a safety net:** test 11 (deadlock retry) remains the safety net for callers that break the rule.

### 3.8 Document lock (A1)

- **The rule:** every Post, Approve and Reverse handler **first** loads its document header with `ForUpdate()` and re-checks the document's status inside the transaction. If the status has moved (already posted, approved or reversed), it returns a failure result and writes nothing.
- **Why idempotency is not enough:** idempotency stops one `RequestId` posting twice, but two users posting the same draft send different `RequestId`s. The header lock serialises them, and the second user sees the new status under the lock.

## 4. Order of work

0. **Close 1.4.0** (§6.3).
   - 1.4.0 stays the ICU-only package already built in pass 57.
   - Install it in the machine's template store and tag `a8a89d63` as `v1.4.0`.
   - Stop the organisation-switcher work: stash the pending `TenantSelector.razor` change and its handoff note, uncommitted.
   - Commit the release bookkeeping.
1. **Contracts** (§3.1) and the pipeline change (§3.2), with an empty `TransactionBehaviour` that simply calls the handler. The suites stay green.
2. **Unit of work:** the unit-of-work scope with its holder **(A6)**, factory lease, dispose suppression and interceptor buffering. Prove the pinned suites unchanged.
3. **The full `TransactionBehaviour`:** commit and rollback, failure results, `PostingRejectedException` **(A2)**, the posting-event loop with chaining **(A3)**, nested joining **(A5)**, publishing after the commit, and retry **(A6)**.
4. **Error handling and logging** (§3.3, last bullet).
5. **`ProcessedRequests`** and idempotency, with a migration for each provider (the template builds all three).
6. **`DocumentSequences`** and the allocator.
7. **`ForUpdate`** and its interceptor. The rules in §3.7 and §3.8.
8. **Documentation:** a README section, "Posting transactions", covering the rules in §2 (nested requests), §3.4, §3.5, §3.6, §3.7 and §3.8, with the `SalesInvoicePosted` → `StockIssued` example **(A3)**.
9. **Release:** template 1.5.0. The release notes cover pass 58 and pass 47b (SkiaSharp), which is in no earlier package. Pack, install and run the smoke test.

**Effort:** L, about 5 days. Steps 1 to 4 take about 3 days and are the part to protect if time runs short.

## 5. Tests (Application.IntegrationTests on `GX_TEST_PG` unless noted)

No template entity is a real stock document, so the tests use `PicklistSet` and `Tenant`, with test-only `PostingEvent` subclasses and handlers. `Tenant` stands in for a document header: its key is a `Guid.CreateVersion7()` string set when the object is created, and `Description` stands in for the status.

| # | Test |
|---|---|
| 1 | The command and its posting handler each write; the posting handler throws; **nothing** is committed, audit rows included. |
| 2 | The handler returns `Result.Failure`; the transaction rolls back and nothing is published. |
| 3 | The after-commit handler runs only after the commit, sees the committed rows, and sees `IUnitOfWork.IsActive == false`. |
| 4 | Inside a unit of work, `CreateAsync` returns the same instance, and disposing it inside the scope leaves it usable. |
| 5 | A `PostingEvent` raised outside a unit of work throws, naming the event, and writes nothing. **(P58)** Run as an integration test against the real interceptor, not a unit test. |
| 6 | The same `RequestId` sent twice in sequence: the handler runs once and both calls get the same response. |
| 7 | The same `RequestId` sent concurrently from two scopes: one runs, the other waits and gets the same response. |
| 8 | The first attempt rolls back; a retry with the same `RequestId` runs normally. A different user with that `RequestId` is refused. |
| 9 | 50 concurrent allocations give 1 to 50 with no gaps or duplicates; a rolled-back allocation is reused. |
| 10 | `ForUpdate` blocks a second transaction (shown with `lock_timeout`); it throws outside a transaction, on a query with a join, and **(P58)** on an unordered query. |
| 11 | Opposite lock order in two commands produces a deadlock; one command is retried and both succeed. This is the safety net for callers that break the lock order **(A4)**. |
| 12 | `DbUpdateConcurrencyException` gives the specific message and an Error log entry (unit test). |
| 13 | Unchanged: the interceptor regression, ordering, transactional audit and publisher suites. |
| 14 | **(A1)** Two concurrent Post commands for the same document, with different `RequestId`s. Exactly one succeeds; the other returns a failure and writes nothing. |
| 15 | **(A2)** A posting handler throws `PostingRejectedException`. Nothing is committed, and the caller receives a failure result with the handler's message. |
| 16 | **(A3)** A posting handler raises a second posting event. Its handler runs in the same transaction and sees the first handler's writes. A failure in the second handler rolls back everything, including the command's own writes. |
| 17 | **(A5)** An outer transactional command sends an inner one. Both writes commit together; when the inner one fails, nothing is committed. |
| 18 | **(A6)** A retried attempt starts with empty event buffers and a fresh context, and the handler runs once per attempt. |
| 19 | **(A6)** An `ExecutionContext` captured inside the unit of work reports `IsActive == false` after it ends. |
| 20 | **(P58)** The pipeline order puts CacheInvalidation outside Transaction, and Transaction outside FusionCache. A cacheable query inside a unit of work bypasses the cache (unit tests). |

## 6. Decisions for the owner

1. **Keys: decided.** Document headers use `Guid.CreateVersion7()`, set in the constructor, so events carry final ids at the moment they are raised. High-volume line and ledger tables keep `bigint` identity keys. (Pass 55 already supports non-int keys.)
2. **Providers: decided.** PostgreSQL only. Locks, sequences and idempotency throw `NotSupportedException` on other providers. No MSSQL equivalents are built.
3. **Versions: decided.**
   - 1.4.0 is closed before this pass starts.
   - The organisation-switcher work is stopped: the pending `TenantSelector` change is not committed into 1.4.0.
   - Tenancy stays dormant with one seeded tenant. The pattern will be reused later for a branch switcher.
   - This pass ships as 1.5.0.
