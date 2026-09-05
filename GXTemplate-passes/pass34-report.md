# Pass 34 — System Logs: A Separate Design

**Nature:** investigation with a design gate. **Nothing in the repository was changed.**
**Date:** 2026-09-05.

**Recommendation in one line: do not filter system logs. Treat `Permissions.Logs.*` as an operator
right, say so in the README's Tenancy table, and tell a multi-tenant operator not to grant it to a
customer's administrator.** Three measurements make filtering the wrong answer rather than merely an
expensive one: the tenant column is **not a usable filter key** (only SignalR hub invocations
populate the ambient context, and the notification channel freezes it at construction — measured); a
global filter would **silently make "Clear Logs" partial** (measured); and a tenant-scoped log view
would be an **edited log** that omits startup, seeding, every login and every mail event, which is
precisely the failure `SystemLog.TenantId`'s own remarks warn against.

---

## 1. Start state

| | |
|---|---|
| HEAD | `5d1c433a` — *"Pass33"* ✓ |
| Working tree | clean |
| Spot-check: `ModelMatchesMigrationsTests` | present ✓ |
| Spot-check: `IMayBeShared` | present ✓ (`src/Domain/Common/Entities/IMayBeShared.cs`) |
| Build | **0 errors, 19 warnings across 10 distinct source locations** ✓ |
| Tests | 228 + 12 + 483 + 223 = **946 passed, 12 skipped, 0 failed** ✓ |

Matches the brief exactly. The tree is still clean at `5d1c433a` at the end of this pass; `git status`
is empty and no git command other than `status`/`log`/`check-ignore` was run.

---

## 2. §A — What is in a log row, and who can read it

### 2.1 The payload, from real sampled rows

Two instances of the real application were booted against throwaway databases — one on SQLite, one on
PostgreSQL — driven over HTTP (anonymous page loads, a failed sign-in, a successful sign-in,
authenticated page fetches) and their log databases read directly. **The rows below are verbatim.**

**Row 2 (SQLite run) — seeding, no tenant, no user.** Abridged only where marked:

```
  Message         : Granted 55 permission(s) to the "Admin" role: "Permissions.AuditTrails.View, …"
  MessageTemplate : Granted {Count} permission(s) to the {Role} role: {Permissions}
  Level           : Information
  TimeStamp       : 2026-09-05T17:59:00.876
  Exception       : 
  UserName        : ""
  TenantId        : <null>
  ClientIP        : ""
  ClientAgent     : ""
  Properties      : {"Count":55,"Role":"Admin","Permissions":"…","SourceContext":
                     "CleanArchitecture.Blazor.Infrastructure.Persistence.ApplicationDbContextInitializer",
                     "Application":"GX Application","Environment":"Development","TargetFramework":"net10",
                     "TimeStamp":"2026-09-05T17:59:00.8763353Z","UserName":"","TenantId":null,
                     "ClientIP":"","ClientAgent":""}
  LogEvent        : {"Timestamp":"…","Level":"Information","MessageTemplate":"…","Properties":{ …the whole
                     of Properties again… }}
```

**Row 9 (SQLite run) — a real sign-in:**

```
  Message         : "Administrator" has logged in successfully.
  MessageTemplate : {userName} has logged in successfully.
  UserName        : "Administrator"
  TenantId        : <null>
  ClientIP        : "::1"
  ClientAgent     : "curl/8.19.0"
  Properties      : {"userName":"Administrator","SourceContext":"IEndpointConventionBuilder",
                     "RequestId":"0HNOBETME53SC:00000001","RequestPath":"/pages/authentication/login",
                     "ConnectionId":"0HNOBETME53SC", … ,"UserName":"Administrator","TenantId":null,
                     "ClientIP":"::1","ClientAgent":"curl/8.19.0"}
  LogEvent        : {…,"TraceId":"0e1cd4795feaaf8d15d07bdc466b328b","SpanId":"56cd7672ed0bd7ab", …}
```

**Row 1 (PostgreSQL run) — an infrastructure error:**

```
  message         : An error occurred using the connection to database '"GXP34Probe"' on server
                    '"tcp://localhost:5434"'.
  level           : Error
  tenant_id       : <null>
  properties      : {"database":"GXP34Probe","server":"tcp://localhost:5434","EventId":{"Id":20004,
                     "Name":"Microsoft.EntityFrameworkCore.Database.Connection.ConnectionError"}, …}
```

**What a cross-tenant reader learns, column by column:**

| Column | What it carries | Disclosure |
|---|---|---|
| `Message` / `MessageTemplate` | the rendered line and its template | **whatever the call site interpolated** — see §2.2 |
| `Level`, `TimeStamp` | severity and instant | activity volume and timing per tenant |
| `Exception` | full `ToString()` of the exception, stack trace included | **the highest-risk column.** Empty in every sampled row here, but populated by `CustomError.razor`, `GlobalExceptionHandler` and `FallbackExceptionHandler`, all of which pass the exception object |
| `UserName` | the HTTP principal's name | **usernames across tenants** |
| `ClientIP`, `ClientAgent` | from `X-Forwarded-For` / the connection, and the UA header | **client addresses across tenants** |
| `TenantId` | the ambient tenant, or null | see §2.3 — null in every sampled row |
| `Properties` | **every** structured message parameter **plus** every enricher property (`SourceContext`, `Application`, `Environment`, `TargetFramework`, `RequestId`, `RequestPath`, `ConnectionId`, `UserName`, `TenantId`, `ClientIP`, `ClientAgent`) as JSON | **the whole payload again, in machine-readable form** |
| `LogEvent` | the **entire serialized event** — everything in `Properties` plus `Timestamp`, `Level`, `MessageTemplate`, `TraceId`, `SpanId` | a second complete copy |

**`Properties` and `LogEvent` are not summaries — they are the full event, twice.** The SQL Server
sink stores `StandardColumn.Properties` and `StandardColumn.LogEvent`; the PostgreSQL writers are
`PropertiesColumnWriter` and `LogEventSerializedColumnWriter`; the SQLite sink writes both in its
fixed `INSERT`. Anything a call site passes as a structured parameter lands in both, whatever the
rendered message shows.

### 2.2 Does anything log an entity, an id, an email, a document name?

**Yes — emails, document storage keys, user ids, request paths and role permission sets.** Across the
109 `LogInformation`/`LogWarning`/`LogError`/`LogCritical` call sites in `src/` (35 + 20 + 54; a
further 4 `LogTrace` and 1 `LogDebug` never reach the sink, whose minimum is Information):

| Structured value | Sites | Where |
|---|---|---|
| `{Email}` | **10** | the four mail notification handlers, `MailgunMailService`, `SinkMailService` |
| `{UserId}` | 20 | `TenantSwitchService`, `UserProfileState`, `UserContextLoader`, `AuthorizationBehaviour`, the identity endpoints |
| `{UserName}` | 11 + 2 | sign-in, tenant switch, `CustomError` |
| `{StorageKey}` | 2 | `DocumentDeletedEventHandler` — a document's storage key |
| `{PicklistSetId}`, `{EntityId}`, `{CredentialId}` | 3 | picklist domain events, permission assignment, passkeys |
| `{TenantId}`, `{TenantName}`, `{OriginalTenantId}`, `{NewTenantId}` | 6 | `TenantSwitchService` |
| `{Path}`, `{ReturnUrl}`, `{@url}` | 4 | mail templates, external login, `CustomError` |
| `{Permissions}` | 1 | seeding — the entire granted permission set, as seen above |

**Two disclosure risks were already found and closed by earlier passes, and both are worth naming
because they show the log database has been treated as a disclosure surface before:**

- `CustomError.razor` strips the **query string** from the URL it logs. Its comment records why: an
  exception raised on `/account/reset-password?userId=…&token=…` used to write *a live reset token*
  into the log database, "readable from `/system/logs` by any `Permissions.Logs.View` holder".
- `Forgot.razor` no longer logs the address an anonymous visitor typed, because doing so "made the
  log database a second oracle for anyone holding `Permissions.Logs.View`".

Conversely `ResetPasswordCommand` logs the recipient address **deliberately**, with a comment saying
so — Pass 22 §D ratified it as an operational record of mail actually sent, on the explicit ground
that "a holder of that is already trusted with far more than the list of addresses that were sent a
reset." **That reasoning is the recommendation of this pass, stated three passes early.**

### 2.3 Who reads it

| Surface | Gate | Notes |
|---|---|---|
| `/system/logs` page | `[Authorize(Policy = Permissions.Logs.View)]` | the only page |
| `SystemLogsWithPaginationQuery` | `[RequestAuthorize(Permissions.Logs.View)]` | the grid |
| `SystemLogsTimeLineChatDataQuery` | `[RequestAuthorize(Permissions.Logs.View)]` | the chart, used only inside that page |
| `ClearSystemLogsCommand` | `[RequestAuthorize(Permissions.Logs.Purge)]` | the only write |
| **Export** | — | **does not exist.** `Permissions.Logs.Export` was deleted in Pass 11C with its dead query |
| Menu entry "Logs" | **ungated** | `MenuService.cs:147` carries no permission; the link renders for everyone and the page then refuses. Cosmetic, not a disclosure |
| **`./log/log-*.txt`** | **no application gate at all** | see below |

Granted to the `Admin` role by `AdministratorPermissionRegistry` (`Logs.View`, `Logs.Search`,
`Logs.Purge`) and to nothing else; `Basic` receives only `Documents.View` and `Documents.Download`.
Confirmed live — the seeding banner in both probe runs lists exactly those three.

**The revealing surface is the ungated one, exactly as Pass 30 §A.4 found for presence.** Every event
that reaches the database sink *also* reaches `WriteTo.File("./log/log-.txt")`, with **no tenant
column, no filter and no application permission**. Verified in the probe run:

```
2026-09-05 18:59:53.777 +01:00 [INF] Administrator has logged in successfully.
```

The file sink drops only the bootstrap-password banner (`IsExcludedFromFileSink`). Anyone with
filesystem access to the deployment has the entire log, tenant-mixed, whatever the application does.
**A tenant filter on the database view would therefore be a boundary that half-holds** — which the
brief itself names as the outcome to avoid.

### 2.4 What proportion of rows carry a tenant

**Measured: none of them, in either run.**

| Run | Provider | Rows | With a tenant |
|---|---|---|---|
| A | SQLite | 10 | **0** |
| B | PostgreSQL | 11 | **0** |

Run A is uninformative on its own — the SQLite sink **cannot write the column at all**
(`LogTenantStampingTests.OnSqlite_TheRowLands_ButTheTenantColumnStaysNull_BecauseThatSinkCannotWriteIt`;
that package writes a fixed `INSERT` with no configurable columns, and the README already documents
it). Run B is the informative one: on PostgreSQL the column is genuinely writable, and it was null on
every row including the successful sign-in.

**The mechanism explains it, and it generalises further than the sample.** `IUserContextAccessor` is
an `AsyncLocal`, and **exactly one place in the entire codebase pushes it**:

```
src/Infrastructure/Services/Identity/UserContextHubFilter.cs:52:  using (_userContextAccessor.Push(user))
```

— a SignalR **hub-method** filter. `App.razor` states the same fact from the other direction, about
authorization rather than logging:

> `prerender: false` is load-bearing for authorization. `AuthorizationBehaviour` denies any request
> dispatched with no ambient `IUserContextAccessor` context, **and that context is only populated for
> circuit invocations**.

So a log row can carry a tenant only if it is written synchronously inside a Blazor circuit's hub
invocation. Classifying all 109 call sites by execution context:

| Bucket | Sites | Tenant on the row |
|---|---|---|
| **Startup / host / HTTP-only** — the 21 identity endpoints, `ApplicationDbContextInitializer` (10), `LogDatabaseStartupCheck` (5), `MailStartupCheck` (4), `Program` (1), `StaleCookieMiddleware` (2), `GlobalExceptionHandler` (2), `IdleSessionEnforcer` (1) | **46** | **always null** |
| **Notification channel** — `ChannelBasedNoWaitPublisher` (2), the document and picklist event handlers (5), the four mail notification handlers (8), `MailgunMailService` / `SinkMailService` / `MailTemplateRenderer` (10) | **25** | **frozen at publisher construction — see §3.3** |
| **Anonymous circuit pages** — `Register`, `Forgot`, `ResetPassword`, `ConfirmEmail` | **6** | null (a circuit, but no principal) |
| **Authenticated circuit** — `Users.razor`, `SystemLogs.razor`, `LogsLineCharts`, `IdleTimeoutMonitor`, `CustomError`, `TenantSwitchService` (5), `UserProfileState` (4), the Mediator pipeline and exception handlers (11), `PermissionAssignmentService` (2), `UserContextLoader` (1), `IdleTimeoutPolicyProvider` (1) | **32** | **tenanted** |

**Fewer than a third of the log sites can carry a tenant at all**, and the 25 in the middle bucket
carry one that may be wrong. Pass 29 A4 recorded that a freshly seeded installation shows a
tenant-scoped principal an empty audit trail; **here it is worse, and it is not confined to first
run** — a steady-state installation writes untenanted rows for every login, every password reset,
every mail send and every HTTP-level exception, indefinitely.

---

## 3. §B — Can it be filtered, and where

### 3.1 `LogDbContext`'s shape — a filter IS expressible

`LogDbContext` is a **full `DbContext`**, not a thin wrapper: it overrides `OnModelCreating` and
`ConfigureConventions`, applies configurations from an assembly, and calls
`builder.Entity<SystemLog>().ToTable(...)`. A `HasQueryFilter` call would compile and work.

Three details would have to be got right, and all three are already solved on the business side:

1. **The context takes no accessor today** — its only constructor parameter is
   `DbContextOptions<LogDbContext>`. `ApplicationDbContext` shows the fix: an optional
   `IUserContextAccessor?` second parameter, which EF resolves from the container because the context
   is created through `IDbContextFactory` (`AddDbContextFactory<LogDbContext>(…, ServiceLifetime.Scoped)`).
2. **Pass 29's model-cache trap applies unchanged.** A filter expression is compiled into the model
   and the model is cached per context type, so a filter closing over a *local* would bake the first
   request's tenant in for the process's life. `ApplicationDbContext` avoids it with a **member** —
   `private string? CurrentTenantId => _userContextAccessor?.Current?.TenantId` — and the same shape
   would be required here.
3. **Both cache scopes would have to move.** `SystemLogsWithPaginationQuery.Scope` and
   `SystemLogsTimeLineChatDataQuery.Scope` are both `CacheScope.Global`, and both say so in comments
   ("system logs are not principal-scoped"). Filtering makes those comments false and the entries
   cross-contaminating. This is Pass 31 A1's lesson exactly: **a scope is a claim about a query's
   inputs, and scoping a query invalidates the declaration without touching the line that declares
   it.**

So: **yes, expressible.** The obstacles are not mechanical.

### 3.2 The infrastructure paths — and the one that breaks

Only four things touch the log database, and Pass 29 §Q2's happy result does **not** repeat here.

| Path | Uses | Under a global filter |
|---|---|---|
| `SystemLogsWithPaginationQuery` | `db.SystemLogs` + specification | filtered — the intent |
| `SystemLogsTimeLineChatDataQuery` | `db.SystemLogs.Where(…).GroupBy(…)` | filtered — the intent |
| `LogDatabaseStartupCheck` | `Database.CanConnectAsync`, and a raw ADO catalogue query for the table's existence | **unaffected** — it issues no LINQ over `SystemLog` |
| **`ClearSystemLogsCommand` → `ILogDbContext.PurgeAsync`** | `Set<SystemLog>().ExecuteDeleteAsync()` | **BREAKS — see below** |

**Measured, not assumed.** A scratch EF/SQLite probe with a `(TenantId == CurrentTenantId)` global
filter, three rows in three partitions:

```
before purge: 3 rows -> <null>, tenant-a, tenant-b
  filtered read (ambient null)     sees: 1 row(s)
  filtered read (ambient tenant-a) sees: 1 row(s)
  ExecuteDelete under ambient tenant-a reported: 1
after purge:  2 rows -> <null>, tenant-b
```

**EF applies global query filters to `ExecuteDelete`.** So a filtered log context turns "Clear Logs"
into "clear my own tenant's logs" — silently, while `ILogDbContext.PurgeAsync`'s summary says
*"Deletes every log row"*, the interface's remarks call it *"the one destructive operation"*, and the
button says *"Clear Logs"*. Worse, because the purge is invoked from a circuit it would use the
caller's tenant, so an operator clearing a full log database would be told it succeeded and would
find almost all of it still there. Any filtering design must exempt the purge explicitly by name
(`IgnoreQueryFilters([QueryFilters.Tenant])`), and that is one more thing to get right that fails
silently when got wrong.

Test impact is small: the row-reading log tests use raw ADO (`SqliteConnection`), which bypasses EF
filters entirely; only `LogTableDdlTests:172` reads through the context, and it asserts emptiness.

### 3.3 The finding that decides it — the tenant column is not a reliable key

`ChannelBasedNoWaitPublisher` starts its consumer in its **constructor**:

```csharp
public ChannelBasedNoWaitPublisher(ILogger<…> logger, int capacity = 1000)
{
    …
    _processingTask = Task.Run(ProcessNotifications);   // captures the ExecutionContext HERE
}
```

`Task.Run` captures the ambient `ExecutionContext` — and therefore every `AsyncLocal` value — at the
point of the call. Every notification handler then runs on that loop. **Measured** with a scratch
console reproducing the exact shape (static `AsyncLocal`, bounded channel, `Task.Run` in the
constructor):

```
constructed under A, published under B -> handler saw: tenant-A-at-construction
constructed under none, published under C -> handler saw: <null>
```

Mediator is configured with `options.ServiceLifetime = ServiceLifetime.Scoped`, so one publisher
exists per DI scope — per Blazor circuit. Consequences for the 25 call sites in that bucket:

- Handlers reached from an **HTTP** scope log with **no tenant**.
- Handlers reached from a **circuit** log with the tenant that was ambient **when the publisher was
  first resolved in that circuit**, not the tenant at publish time.
- **A tenant switch does not move it.** `TenantSwitchService` exists precisely so a user can change
  tenant within a session; after a switch, that circuit's publisher keeps labelling mail and
  domain-event rows with the *previous* tenant.

So the rows carrying emails (`{Email}` ×10), document storage keys (`{StorageKey}`) and picklist ids
are the ones whose `TenantId` is least trustworthy. **A filter keyed on that column would not merely
hide too much — it would route some rows to the wrong tenant.**

**The blast radius was checked and is confined to logging.** No `INotificationHandler` in the codebase
writes to the business database (checked: none references `IApplicationDbContext`,
`IApplicationDbContextFactory` or `SaveChangesAsync`), so `AuditableEntityInterceptor`'s stamping is
not affected and no business row can acquire a frozen tenant this way. Today the only consumer of the
ambient context inside those handlers is `UserInfoEnricher`.

### 3.4 The alternative: filter at the two queries

Smaller and honest about its own limits. Two call sites, a predicate each, no context change, no
model-cache trap, **and the purge is untouched by construction** — which removes §3.2's silent
breakage entirely.

Its cost is the usual one: a future third reader is unscoped by default, and this programme has met
that defect repeatedly (`VisibleDocumentSpecification`, Pass 28's third consumer). But the population
of readers here is unusually closed — one page, one chart, one purge, no export, and
`ILogDbContext.SystemLogs` is an `IQueryable` on an interface whose whole design is to be minimal.

**On the merits the surface approach would be the right one *if* filtering were worth doing at all.**
It is not — for reasons that have nothing to do with mechanism.

---

## 4. §C — The partition problem

### 4.1 Should a tenant-scoped reader see installation events?

**They would have to, and that is most of the argument against filtering.**
`SystemLog.TenantId`'s own remarks already state the rule:

> a tenant administrator who cannot see that the application restarted is being shown an edited log.

So the shape would have to be the **picklist** one — `TenantId == null || TenantId == current` —
rather than the audit trail's strict equality. Which means the tenant-scoped view returns:

- every startup, seeding and provisioning line (**46 sites**),
- every login, logout, external-login, passkey and personal-data event (**within those 46**),
- every mail send and every domain-event row (**25 sites**, frozen tenant),
- every HTTP-level unhandled exception with its full stack trace,
- plus the caller's own circuit-side rows (**32 sites**).

**That is nearly the whole log.** The filter would exclude only other tenants' circuit-side rows —
under a third of the call sites, and in practice a small fraction of volume, since circuit-side
logging at Information and above is sparse (the Mediator pipeline's per-request lines are `LogTrace`
and never reach the sink at all).

### 4.2 What is in the installation events

Re-using §2.1's payload analysis: **the null-tenant partition is where the most sensitive rows
already are.**

- The seeding rows list the installation's entire permission model.
- The sign-in rows carry username, client IP and user agent for **every tenant's users**.
- The mail rows carry **recipient email addresses**.
- `GlobalExceptionHandler` logs `LogError(exception, …)` from HTTP with a **full stack trace**, which
  can quote entity names, ids and values from whichever tenant's request faulted — and it lands with
  no tenant, so it stays visible to everyone under the picklist shape.

The brief asks whether an unhandled exception's stack trace may name another tenant's data even when
the row carries no tenant. **Yes — and the exceptions most likely to do so are exactly the ones the
filter cannot hide**, because they are raised outside a circuit and therefore untenanted.

### 4.3 Is a tenant-scoped log view useful at all?

**No.** It would show a tenant administrator a view that is simultaneously *too narrow to diagnose
anything* — no logins, no mail, no password resets, no document events, no HTTP exceptions, because
all of those are untenanted — and *too wide to be an isolation boundary*, because the untenanted
partition it must include is where the cross-tenant usernames, addresses and stack traces live.

It would also be **wrong** on the rows it did filter, per §3.3.

That is three independent failures of the same design, and none of them is fixed by implementing it
more carefully.

---

## 5. §D — The escape

**If filtering were built, an escape would be mandatory — and that is itself an argument against
building it.**

Pass 30 declined an escape for presence on the ground that no administrative task needed one. **Here
the opposite is plainly true:** diagnosing a deployment *is* an operator task, and §4 shows the
diagnostic content is spread across every partition. An operator who could not read across tenants
could not use the page for its purpose.

So the design would be `Permissions.Logs.ViewAllTenants`, following `AuditTrailTenantScope`'s
established shape — right resolved once through `IPermissionQueryService`, then
`IgnoreQueryFilters([QueryFilters.Tenant])`, failing closed on every non-affirmative path.

And then: **who holds it?** Everyone who holds `Logs.View` today — the `Admin` role — because anyone
expected to read the log needs the whole log. The net effect of building a filter, an exemption, two
cache-scope changes and a purge exemption would be *a page that behaves exactly as it does now for
everyone who uses it*, plus a new way for the purge to go quietly wrong.

**A boundary whose only correct configuration is "exempt everybody who uses it" is not a boundary.**

---

## 6. §E — Recommendation

### 6.1 The recommendation, without hedging

**Do not filter system logs. Do not add a cross-tenant right. Make the installation-wide nature of
`Permissions.Logs.*` explicit in the README and in the permission's own description, and tell a
multi-tenant operator not to grant `Logs.*` to a customer's administrator.**

This is the "**permission only, and document it**" option, and the evidence is not close:

| Option | Verdict |
|---|---|
| **Global query filter on `LogDbContext`** | **Reject.** Expressible, but the key is unreliable (§3.3), it silently breaks the purge (§3.2), it needs two cache-scope changes, it requires an escape that everyone who uses the page must hold (§5), and the view it produces is both too narrow and too wide (§4.3) |
| **Predicate at the two queries** | **Reject** — smaller and it avoids the purge trap, but it produces the same useless view for the same reasons. The mechanism was never the problem |
| **Permission only + document** | **Recommend.** The data is genuinely installation-wide; the honest move is to say so and to gate it as an operator capability |
| **Leave and document nothing** | Reject. The README's Tenancy table is valuable because it is true, and today it says only that scoping is "a separate design" — which is now answered |

### 6.2 Why this is the right answer and not the cheap one

**The log is evidence about the deployment, not about a tenant's data.** The brief puts this exactly
right, and the measurements confirm it: two-thirds of the call sites cannot carry a tenant even in
principle, because they run before, outside or after any circuit. An audit trail answers *"who changed
this tenant's data"* and is properly scoped; a log answers *"what is this installation doing"*, and
scoping it per tenant is a category error that the null-tenant partition then forces you to undo.

**The template already reached this conclusion once, in code.** `ResetPasswordCommand`'s comment —
"reading this requires `Permissions.Logs.View`, and a holder of that is already trusted with far more
than the list of addresses that were sent a reset" — is precisely the operator-right model, ratified
in Pass 22 §D. This pass makes the same rule explicit at the boundary instead of implicit at one call
site.

**And the file sink settles it.** Every row also lands in `./log/log-*.txt` with no tenant column and
no permission at all. Filtering the database view would leave the identical content readable, in
full, tenant-mixed, to anyone with deployment access — a boundary that half-holds, which is the one
outcome the brief rules out.

### 6.3 The staged plan, with honest cost

**Stage 1 — documentation (recommended; ~1 hour, no production code).**
The README's Tenancy table currently says of system logs: *"No — and not reachable by that filter …
Scoping them is a separate design, not a deferred switch."* That must become a statement of the
answer, not of the open question. It must say:

- **Roughly what a `Logs.View` holder can see**, from §2.1: usernames, client IPs and user agents for
  every tenant; recipient email addresses of every message sent; document storage keys; request paths;
  full exception stack traces; and — in `Properties` and `LogEvent` — the complete serialized event for
  every row, twice.
- **That `Logs.*` is an operator right**, and that a multi-tenant operator should build the
  customer-administrator role *without* `Logs.View`, `Logs.Search` and `Logs.Purge`. This is the
  actionable instruction; today's `Admin` role holds all three and is the role a customer
  administrator would most likely be given.
- **That the log is not scoped and will not be**, with the reason in one line: the ambient tenant
  exists only inside a circuit, so most rows have none, and a scoped view would be an edited log.
- **That `./log/log-*.txt` carries the same content with no gate**, so filesystem access to the
  deployment is equivalent to `Logs.View`.
- Keeping the existing SQLite note (`TenantId` always null there), which stays true and is now part
  of a bigger picture.

**Stage 2 — one honest line of code (optional; ~10 minutes).**
`Permissions.Logs.View`'s `[Description]` is *"Allows viewing log details"*. That text is what the
role editor shows an administrator who is deciding whether to grant it. It should say the right is
installation-wide — e.g. *"Allows viewing the installation's system log, which spans every tenant"* —
so the decision is made with the fact in view. Same for `Purge`, which erases every tenant's rows.
This is the only code change this pass recommends, and it is a description string.

**Stage 3 — not recommended, costed for completeness.**
If a future requirement genuinely demands a per-tenant log view, the cheapest defensible route is
*not* a filter but a **second, narrower feed**: log the small set of tenant-meaningful events to the
audit trail, which is already tenant-scoped, correctly stamped and has a working cross-tenant right.
That reuses a boundary that holds instead of building a second one that does not.

### 6.4 What this pass does **not** recommend fixing here

Two real defects were found and are recorded rather than acted on, because this pass changes nothing:

- **`ChannelBasedNoWaitPublisher` freezes the ambient context** (§3.3, A2). It is a correctness bug in
  the `TenantId` of ~25 log sites and it would become a data bug the day any notification handler
  writes through the business context. The fix is small — capture the context at publish time and
  restore it around the callback, or suppress the flow at construction — but it is a change to a
  shared publisher, needs its own tests, and belongs to a pass that can build.
- **The `MenuService` "Logs" entry is ungated** (§2.3). Cosmetic — the page refuses — but it is the
  only menu entry examined here that carries no permission.

---

## 7. Scratch probe disclosure

Five, all outside the repository, all removed:

1. **A real application run on SQLite** — `dotnet run` on `src/Server.UI` with
   `DatabaseSettings__DBProvider=sqlite` and both connection strings pointed at the session
   scratchpad. Stopped explicitly (Pass 29 A7's lesson: a still-listening probe is not evidence that
   *this* run answered). Databases deleted with the scratchpad.
2. **A real application run on PostgreSQL** against `localhost:5434`, using **throwaway databases named
   `GXP34Probe` and `GXP34Probe_Logs`** so that nothing pre-existing was touched. Both were
   `DROP DATABASE … WITH (FORCE)`-ed afterwards and their absence verified by querying `pg_database`.
   No existing database on that server was read or written.
3. **`LogDump`** — a scratch console (Microsoft.Data.Sqlite + Npgsql) that read the two probe log
   databases. Deleted.
4. **`FlowProbe`** — a scratch console reproducing `ChannelBasedNoWaitPublisher`'s shape, which
   produced §3.3's measurement. Deleted.
5. **`FilterProbe`** — a scratch console (EF Core + SQLite) that produced §3.2's `ExecuteDelete`
   measurement. Deleted.

**One artefact inside the repository directory, and it is gitignored.** The application writes
`src/Server.UI/log/log-*.txt`; `.gitignore:34` (`[Ll]og/`) excludes it, `git check-ignore` confirms
it, and `git status` is empty. The directory already held files dated 29 August through 3 September
from earlier passes' probes; today's runs appended to `log-20260905.txt`, which was also read as
evidence for §2.3. Nothing tracked was created, modified or deleted.

The repository is byte-identical to `5d1c433a`. No production code, test or document was changed by
this pass other than this report.

---

## 8. Anomalies

**A1 — the ambient tenant exists only inside a SignalR hub invocation, and that fact is written down
in a comment about something else.** `UserContextHubFilter.cs:52` is the sole `Push` call site in the
codebase, and `App.razor` states the consequence — "that context is only populated for circuit
invocations" — while explaining why `prerender: false` is load-bearing for *authorization*. Nothing
connects it to tenancy, yet it is the single fact that determines what fraction of audit rows, log
rows and stamped entities can carry a tenant at all. Recorded because it is the root cause of Pass 29
A4 as well as of this pass's §2.4, and both passes had to rediscover it.

**A2 — `ChannelBasedNoWaitPublisher` gives every notification handler the ambient context of its own
constructor.** Measured, not inferred: `Task.Run(ProcessNotifications)` in the constructor captures
the `ExecutionContext`, so a publisher first resolved inside a circuit under tenant A goes on running
handlers under tenant A for the life of that scope, whatever the publish site's context is — including
after `TenantSwitchService` moves the user to tenant B. Today the only reader of that context inside
a handler is `UserInfoEnricher`, so the damage is confined to `SystemLog.TenantId` on ~25 call sites,
including all ten that log an email address. It becomes a data defect the day a notification handler
saves through `ApplicationDbContext`, because `AuditableEntityInterceptor` would stamp the frozen
tenant. Recorded prominently because it is invisible from both files.

**A3 — a global query filter applies to `ExecuteDelete`, so filtering would have made "Clear Logs"
silently partial.** Measured (§3.2). Recorded because the failure is exactly the shape this programme
keeps meeting — Pass 32 §3's "a query filter narrows what a query SEES; a unique index constrains what
the table HOLDS" — with a third variant: *a query filter also narrows what a bulk DELETE removes*, and
the method's own summary would go on saying "deletes every log row".

**A4 — the provider that makes tenant stamping observable is the one nobody develops on.** SQLite
cannot write `SystemLog.TenantId` at all (a third-party sink with a fixed `INSERT`), so on the
development and test provider the column is permanently null and "no ambient tenant" is
indistinguishable from "sink cannot write it". The first probe run of this pass was on SQLite and its
100%-null result was therefore worthless as evidence; the finding only became real on PostgreSQL.
Already documented in the README and asserted by `LogTenantStampingTests`, but recorded again because
it is a trap for *measurement*, not only for deployment: any future attempt to observe log tenancy
must use a provider whose sink can write the column.

**A5 — two log-disclosure defects were found and fixed by earlier passes, and neither reached the
Tenancy table.** `CustomError.razor` once wrote live password-reset tokens into the log database, and
`Forgot.razor` once made it an account-enumeration oracle. Both are fixed, both carry good comments,
and both are invisible to a reader of the README trying to decide who may hold `Logs.View`. Recorded
because it shows the log database has been treated as a disclosure surface three times now — twice by
accident and once, in `ResetPasswordCommand`, deliberately — without that ever being consolidated into
the one document that is supposed to answer the question.

**A6 — the failed sign-in produced no log row.** A deliberate wrong-password POST was issued in both
probe runs and neither log database recorded it, while the successful sign-in recorded one line. Not
investigated further, because it is outside this pass's scope and changes nothing about the
recommendation — but a security log that records successes and not failures is worth a look, and it
is exactly the kind of gap a "system logs are the operator's evidence" posture depends on not having.
Recorded for a future pass rather than acted on.
