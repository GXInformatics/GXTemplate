# Pass 24 — Stamp While It Is Free, and Repair Documents

**Nature:** editing pass — §A stamping, §B Documents repairs, §C documentation correction.
**No git actions.** **Date:** 2026-09-03.

> **Two things in this report were not in the brief and change what it can claim.**
> **(1)** The SQLite log sink is a third-party package with a **fixed INSERT statement** and cannot
> be given a new column, so `SystemLog.TenantId` is permanently null on that provider —
> and `SinkColumnDriftTests` was structurally incapable of noticing, because it defined the SQLite
> sink's columns *as* the entity's. Both are now fixed and stated. See §A.3.2.
> **(2)** Preserving the migration ids — my first attempt, for a readable diff — **broke the
> mechanism that detects a regenerated schema.** The test suite caught it. See §A.4.

---

## 1. Start state

| | |
|---|---|
| HEAD | `4425e1c647000b6301eb7443c743f10bbe5f2466` — *"pass22"* |
| Working tree | clean (except the untracked `pass23-report.md` from the previous pass) |
| Build | **0 errors**, **10 warnings** |
| Warning locations | **10 distinct** — matches |
| Tests | **695 passed, 12 skipped, 0 failed** — matches |

Per-assembly baseline: `Infrastructure.UnitTests` 183 · `Application.IntegrationTests` 9 ·
`Application.UnitTests` 356 (+12 skipped) · `Server.UI.IntegrationTests` 147.

---

## 2. §A — Stage 0: stamp, with no filtering

**Nothing filters on any column added here.** Every query returns exactly what it returned before,
and §2.5 is the measurement rather than the assertion.

### 2.1 `AuditTrail`

**Entity.** `TenantId`, nullable, with the justification on the property itself: the value must be
**stored, not derived**, because `TenantSwitchService.SwitchToTenantAsync` writes
`ApplicationUser.TenantId` in place — so reaching an audit row's tenant by joining to its author
would re-attribute every historical row the moment somebody switched tenants.

**Interceptor.** `GenerateAuditTrails` captures `currentUser?.TenantId` from the same context object
it already reads `UserId` from, and passes it to `CreateAuditTrail`.

**Configuration.** An index on `TenantId` and **deliberately no foreign key**, with the reason
recorded: every available delete behaviour is wrong for an audit row — `Cascade` erases the trail of
the tenant just deleted, `Restrict` makes deleting an audited tenant impossible, `SetNull` rewrites
history to say the change belonged to nobody. An audit row must outlive what it refers to. That
absence is asserted by a test, because "we chose not to add a constraint" is otherwise
indistinguishable from "we forgot to".

**A defect the new test found before it could ship.** `ResolveAuditTrails` projects the captured
trails onto **fresh `AuditTrail` instances**, naming each field by hand, to drop the `PropertyEntry`
references before the rows are handed to the context. A property not named there is silently lost
between capture and save — the column exists, the value was computed, and the row lands null.
`TenantId` was exactly that until `TheAuditRowForAChangeMadeInsideATenant_RecordsThatTenant` failed.
The line is added and the hazard is now commented at the projection.

### 2.2 `PicklistSet`

**The marker was the right mechanism, and the brief asked for that to be checked rather than
assumed.** `IMayHaveTenant` has exactly two consumers in the entire solution —
`AuditableEntityInterceptor.SetCreationAuditInfo:327-328` — and nothing else keys off it. The global
filter helper keys off `ISoftDelete`, not this. So implementing it buys the stamp on insert and
changes nothing else. `PicklistSet` now implements `IMayHaveTenant` and carries `TenantId`.

**The unique index is deliberately left alone.** `(Name, Value)` stays installation-wide rather than
becoming `(TenantId, Name, Value)`: widening it would let two tenants define the same pair, which is
a behaviour change, and this pass stamps without scoping. The consequence is recorded in
`PicklistSetConfiguration` — whoever scopes picklists must widen the index in the same change, or
the first two tenants wanting the same brand name collide on a constraint that has no business
spanning them.

### 2.3 `SystemLog`

Moved as one unit, as Pass 23 §2.3 required:

| Part | Change |
|---|---|
| Entity | `SystemLog.TenantId`, nullable |
| `LogTableDdl` | `TenantId TEXT NULL` (SQLite), `TenantId nvarchar(450) NULL` (SQL Server), `tenant_id text NULL` (PostgreSQL) |
| SQL Server sink | `AdditionalColumns` entry, `AllowNull = true`, following the `ClientIP`/`ClientAgent` idiom |
| PostgreSQL sink | `{ "tenant_id", new SinglePropertyColumnWriter("TenantId", PropertyWriteMethod.Raw, NpgsqlDbType.Text) }` |
| Enricher | `UserInfoEnricher` publishes `TenantId` from `IUserContextAccessor` |
| DTO + page | `SystemLogDto.TenantId`; the log-detail panel shows it, labelled **"Installation"** when null |

**`Raw`, not `ToString`, on the PostgreSQL writer** — matching `user_name` and `client_ip`.
`ToString` renders a null as a quoted string, which would turn "no tenant" into a tenant literally
named `null` and destroy the very partition the column exists to make visible.

#### 2.3.1 Reaching the user context from an enricher

The brief required the tenant to come from `IUserContextAccessor` rather than `IHttpContextAccessor`,
because the former survives outside an HTTP context. Making that work needed one change that was not
in the brief, and it is worth stating plainly.

Serilog constructs enrichers itself, through a **parameterless constructor**, and
`Program.cs:13` configures the logger **before** `AddInfrastructure` has registered anything — so
there is no container to resolve from. `UserInfoEnricher` already solves this for the request by
newing up an `HttpContextAccessor`, which works because ASP.NET Core's own accessor keeps its state
in a **static** `AsyncLocal`.

`UserContextAccessor` kept its `AsyncLocal` in an **instance** field. In the application that was
indistinguishable from static — the type is registered as a singleton, so exactly one ever existed —
but it made the ambient value unreachable from anywhere that cannot resolve services. **The field is
now static**, with the reasoning on it, and the nested `Pop` no longer carries an owner reference it
had no further use for.

Nothing relied on per-instance isolation: no test constructs the type (the one test double,
`MutableUserContextAccessor`, implements the interface independently), and the registration has
always been a singleton. `TheEnricherPublishesTheAmbientTenant_WithNoHttpRequestInSight` is what
holds this true — it pushes a context on one accessor and observes it through the one the enricher
builds for itself, with no HTTP request anywhere in the test.

#### 2.3.2 The SQLite sink cannot write it — measured, and the drift test could not have told us

**This is the most important finding in §A and it was not anticipated.**

`AnEventWrittenInsideATenantContext_RecordsThatTenant` failed on its first run. Reading the sink
assembly explains why. `Blazor.Serilog.Sinks.SQLite` writes a fixed statement:

```
VALUES (@timeStamp, @level, @exception, @message, @properties,
        @messageTemplate, @logEvent, @userName, @clientIP, @clientAgent)
```

There is no `AdditionalColumns` and no writer dictionary. **Unlike the other two sinks, its column
set is not configurable at all**, so a new column cannot be given to it.

**And `SinkColumnDriftTests` was structurally incapable of catching that.** For SQLite it defined the
sink's columns as `EntityProperties` — "the fork's fixed statement happens to match the entity" —
which made that provider's three comparisons circular. The sink *was* the entity, so the entity could
never fail to match the sink. Adding a property and watching the sink silently keep writing ten
columns is precisely what the circularity guaranteed could happen.

**Both are fixed:**

- `SqliteSinkColumns` is now a **literal list**, read out of the sink's own INSERT, with the
  circularity and how it was found recorded in its remarks.
- `SinkCannotWrite(provider)` is an explicit allow-list of accepted gaps — today, `TenantId` on
  SQLite — so the gap is *stated* rather than absent, and anything outside it that a sink fails to
  write now fails the test.
- `TheAcceptedSinkGaps_AreRealPropertiesAndReallyUnwritten` fails the list in **both** directions: a
  stale entry naming a property that no longer exists, or one still excusing a column the sink has
  since learned to write.

**The column still exists in the SQLite DDL, and must.** EF reads `SystemLog.TenantId` on every
provider; a missing column fails the read outright rather than returning null.
`EveryPropertyEfReads_HasAColumnInTheDdl` enforces that.

**The trade, stated:** SQLite is the no-server development and test provider. Both providers a GX
installation actually runs on record the tenant. Recorded in the entity, in the drift test, in
`OnSqlite_TheRowLands_ButTheTenantColumnStaysNull_BecauseThatSinkCannotWriteIt`, and in the README.

### 2.4 §A.4 — Migrations, and a decision the tests reversed

**Only two entities changed in the business schema** — `AuditTrail` and `PicklistSet`. `SystemLog`
lives in the log database, which has no migration chain; `LogTableDdl` creates its table from the
arrays §2.3 changed.

**My first attempt regenerated the migrations and then renamed them back to the original ids**, to
keep the diff purely additive and readable. Everything built. Then six of the nine
`Application.IntegrationTests` failed with a `SqlException` out of the audit interceptor.

**The cause is worth recording, because it is the interesting part of §A.** `Testing.cs:171`
recreates the test database when its `__EFMigrationsHistory` names a migration the assembly no longer
defines — the guard Pass 11B's pain produced, whose remarks say regenerating `InitialCreate` **is**
the established way to change this schema. Keeping the old id made the schema change **invisible to
exactly the mechanism built to detect it**: the applied id still existed, no staleness was seen, the
database kept its old columns, and every audit write failed.

And the same reasoning extends past the test database. Anyone who has already generated a project and
run it has a database built from the old id. Reusing that id means **their** database never gains the
column either, silently. Optimising the diff for review convenience cost the correctness of the
change; the ids were allowed to move.

| Provider | Was | Now |
|---|---|---|
| SQLite | `20260831123533_InitialCreate` | `20260903082935_InitialCreate` |
| PostgreSQL | `20260831123517_InitialCreate` | `20260903082957_InitialCreate` |
| SQL Server | `20260831123525_InitialCreate` | `20260903083020_InitialCreate` |

Diffed old against new with the id masked, the regenerated migrations differ **only** by the three
tenant additions — the two columns and `IX_AuditTrails_TenantId`. The snapshots are additive: 10
lines each, 0 deletions.

### 2.5 §A.5 — Verification that nothing else moved

**Every pre-existing test passed unmodified.** 695 → 695 on the existing set, with the only edited
test file being `SinkColumnDriftTests` (§2.3.2), and that edit *added* capability rather than
relaxing an expectation.

**The three log suites the brief expected to move did not need to.** `LogTableDdlTests`,
`LogTableNamingTests` and — apart from the circularity fix — `SinkColumnDriftTests` derive their
expectations from the entity, the DDL arrays and the sink configurations rather than from hard-coded
column lists, so they absorbed a new column and went on asserting the same invariants. That is the
design working: `LogTableDdlTests.OnSqlite_TheDdlRuns_IsIdempotent_AndProducesTheShapeEfReads`
compares the created table against `typeof(SystemLog).GetProperties()` and therefore validated the
new column without being told about it.

**Stamping, per the brief's four checks:**

| Check | Evidence |
|---|---|
| A write inside a tenant context stamps, per entity | `APicklistWrittenInsideATenant_RecordsThatTenant`, `TheAuditRowForAChangeMadeInsideATenant_RecordsThatTenant`, `ADocumentWrittenInsideATenant_StillRecordsThatTenant` |
| A write with no context leaves null and does **not** throw | `AWriteWithNoAmbientPrincipal_LeavesTheTenantNull_RatherThanFailing` |
| The stored value is history, not a join | `AnAuditRowsTenantSurvivesItsAuthorMovingTenant` — moves the author to tenant B and asserts the audit row still says A |
| The columns are nullable and unconstrained | `TheTenantColumnsExistAndAreNullable`, `TheAuditTrailsTenantColumnCarriesNoForeignKey` |
| A log event with a context is stamped; without one is null | `TheEnricherPublishesTheAmbientTenant_WithNoHttpRequestInSight`, `TheEnricherPublishesANullTenant_WhenThereIsNoAmbientContext` |
| The two server sinks write the column | `TheSqlServerSink_WritesTheTenantColumn_AndAllowsItToBeNull`, `ThePostgresSink_ReadsTheEnrichedTenantProperty` |
| SQLite's limitation is real and bounded | `OnSqlite_TheRowLands_ButTheTenantColumnStaysNull_BecauseThatSinkCannotWriteIt` |

`ADocumentWrittenInsideATenant_StillRecordsThatTenant` covers the one entity whose stamping already
worked, so a regression in the older path has an owner rather than being nobody's test.

### 2.6 The null-tenant partition, stated

Startup logging, database seeding, the bootstrap administrator banner, Hangfire's server heartbeats
and anything logged after a circuit has gone all run with `Current == null`. **Those rows are a third
partition — the installation's own events — and no value was invented for them.** Any future
per-tenant log or audit view has to surface that partition rather than silently omitting it: a tenant
administrator who cannot see that the application restarted is being shown an edited log. Recorded on
`SystemLog.TenantId`, on `AuditTrail.TenantId`, in the README, and in the page, which labels a null
tenant **"Installation"** rather than leaving the field blank.

---

## 3. §B — Stage 2: repair Documents

### 3.1 The "expressed once" decision (§B.1)

The brief asked whether the visibility rule could be stated once rather than repeated per branch.
**It can, and the structure is the fix rather than a side-benefit of it.**

`VisibleDocumentSpecification` now exposes the rule as a static expression,
`IsVisibleTo(userId, tenantId)`, and the specification is a thin wrapper over it. That lets a caller
which already has a `Specification<Document>` of its own — the listing, which also has list views and
a keyword — apply the same expression without inheriting from it or restating it.

`AdvancedDocumentsSpecification` applies it **unconditionally and first**. A list view now says only
what it *adds*:

```csharp
Query.Where(VisibleDocumentSpecification.IsVisibleTo(
        filter.CurrentUser.UserId, filter.CurrentUser.TenantId))
    .Where(p => p.CreatedById == filter.CurrentUser.UserId, filter.ListView == DocumentListView.My)
    .Where(x => x.CreatedAt >= todayrange.Start && …,       filter.ListView == DocumentListView.TODAY)
    .Where(x => x.CreatedAt >= last30daysrange.Start,       filter.ListView == DocumentListView.LAST_30_DAYS)
    .Where(x => x.Title.Contains(filter.Keyword) || …,      !string.IsNullOrEmpty(filter.Keyword));
```

`All` adds nothing, `My` adds an owner, the date views add a window — **and none of them can
subtract, because the rule is no longer theirs to restate.** Four copies of a security rule is three
copies waiting to drift, which is the argument `VisibleDocumentSpecification`'s own remarks already
made and which the two date views had already lost.

**One behaviour change falls out, and it is a correction.** The old `All` branch applied its tenant
test only to the public half — `(mine && private) || (public && sameTenant)` — so a principal's own
private document in *another* tenant was listed, while `VisibleDocumentSpecification`, which governs
the download button and the `/files` endpoint, refused to serve it. The listing and the download now
agree. `EveryListView_AgreesWithTheDownloadRule` asserts that structurally rather than by example.

The conditional tenant clause — absent when the caller has no tenant — is **preserved exactly**. That
is the rule the download path has always enforced; narrowing it is a scoping decision and belongs
with the isolation work, not in a repair.

### 3.2 The three repairs

| # | Site | Before | After |
|---|---|---|---|
| B.1 | `AdvancedDocumentsSpecification` | `TODAY`/`LAST_30_DAYS` filtered on **date alone** — no tenant clause, no owner clause | visibility applied to every view first |
| B.2 | `DeleteDocumentCommandHandler` | `Where(x => request.Id.Contains(x.Id))` — id only | intersected with the visibility rule; fails closed on a null principal |
| B.3 | `AddEditDocumentCommandHandler` | `FindAsync(request.Id)` — primary key only; mapper copied `TenantId` from the request | visibility applied before the edit; the stored tenant is captured and restored across the map; on create the tenant is cleared so the interceptor stamps it |

**A refused delete is reported like a missing id, not like a refusal.** Deleting an unreachable id
already returned success; answering "forbidden" instead would tell any caller whether an id exists in
some other tenant — the same id-enumeration reasoning `GetFileStreamQueryHandler` documents for the
download path. A refused edit returns the existing `"Document Not Found!"`, identical to a genuinely
missing id, for the same reason.

**The delete defect was the worst of the three**, because `DocumentDeletedEvent` removes the stored
object too: an id from another tenant destroyed both the row and the blob. The test asserts the blob
survives, through the real disk storage provider, not just the row.

**On create, `TenantId` is cleared rather than trusted.** The interceptor fills only a `TenantId`
that is null, so a client-supplied one would win. `UploadDocumentCommand` already relies on the same
mechanism.

### 3.3 A fourth defect, found by the red capture

Removing only the guard lines to capture red made
`AnOrdinaryEditOfAnOwnDocumentStillWorks` fail with `TenantId` **null** — not tenant-B, null.

That is HEAD's behaviour, not an artefact: the mapper copies `TenantId` from the request onto the
existing document, so **an ordinary edit that did not carry a tenant silently erased the document's
tenant**, orphaning it from every tenant-filtered query including the download rule. The edit path
could not only re-parent a document, it could de-parent one. The same fix — capture the stored value,
restore it after the map — covers both, and the test now pins it.

### 3.4 Red before, green after

Captured by removing the guards from the three sites while leaving the constructors intact, so the
tests compiled against the same handlers, then restoring. Backups were taken to the scratchpad first
and the restore verified by grep before rebuilding.

**7 red / 2 green at HEAD-equivalent → 9 green after:**

| Test | Failure with the guards removed |
|---|---|
| `TheTodayListView_ShowsNoOtherTenantsDocuments_AndNoOtherUsersPrivateOnes` | *"Expected `{"a-public"}`, but `{"a-public", "a2-private", "b-public"}` contains 2 item(s) too many"* |
| `TheLast30DaysListView_IsScopedTheSameWay` | same |
| `EveryListView_AgreesWithTheDownloadRule` | *"items `{"a2-private", "b-public"}` are not part of the superset"* |
| `ATenantsDocumentCannotBeDeletedFromAnotherTenant_AndItsStoredObjectSurvives` | *"Expected … to be True because the other tenant's document is still there, but found False"* |
| `ATenantsDocumentCannotBeEditedFromAnotherTenant` | *"Expected result.Succeeded to be False, but found True"* |
| `AnEditCannotReParentADocumentIntoAnotherTenant` | *"Expected document!.TenantId to be `"tenant-a"` … but `"tenant-b"` differs"* |
| `AnOrdinaryEditOfAnOwnDocumentStillWorks` | *"Expected document.TenantId to be `"tenant-a"`, but found `<null>`"* — §3.3 |

**The two that stayed green in both states are the evidence, not the tally.**
`AUserStillSeesTheirOwnTenantsPublicDocuments` and `AUsersOwnDocumentIsStillDeletable` passed before
and after — so the change narrowed what it should and nothing else. A scoping rule that returns
nothing would satisfy all seven red tests and be useless; those two are what says it did not.

The harness reuses the existing two-tenant shape (`FileEndpointsAuthorizationTests`,
`GetFileStreamQueryHandlerTests`) with real files through `LocalDiskFileStorage`, per Pass 23 §6.3 —
reused rather than built.

---

## 4. §C — The documentation correction

### 4.1 The headline, replaced

`README:3-5` said the template ships *"multi-tenant ASP.NET Core Identity"*. It now reads:

> A Blazor Server solution template for .NET 10, laid out in Clean Architecture layers, with a
> multi-tenant **data model**, deny-by-default request authorization, transactional audit trails, and
> pluggable file storage.
>
> **Multi-tenant data, not yet multi-tenant isolation.** The distinction is deliberate and is
> described in full under [Tenancy](#tenancy): tenants, per-user tenant membership and tenant-stamped
> rows all exist, and Documents is scoped end to end — but the administration surfaces are **not**
> tenant-scoped. A holder of the relevant permission sees every tenant's users, audit trails, system
> logs, roles and picklists. Read that section before quoting this one.

### 4.2 A new `Tenancy` section under The GX standards

Written in the shape of the idle-policy paragraph the brief named as the model — contract first, then
the limit stated plainly. It carries:

- **The contract, split in two** because only half of it holds everywhere: *"rows record which tenant
  they were written in. Only Documents is filtered by it."*
- **What is stamped**, and the note that a project's own entity is stamped automatically the moment
  it implements `IMayHaveTenant`/`IMustHaveTenant` — no query changes, no per-feature code.
- **Why an audit row's tenant is stored rather than derived** (§2.1).
- **The null partition** (§2.6), with the "edited log" consequence spelled out.
- **What is actually filtered** — Documents, on all five entry points, naming
  `VisibleDocumentSpecification.IsVisibleTo` as the single definition.
- **What is not**, as a table: users and the user export, audit trails, system logs, roles,
  picklists, security settings, presence/chat — each with why.
- **The SQLite sink gap** (§2.3.2).

The table's Documents row reads **Yes** only because §B made it true. Nothing §A merely stamped is
claimed as a boundary — the audit-trail and system-log rows say *"stamped, not filtered"*.

### 4.3 Known limitations, and the nuspec

A `Known limitations` entry now points at the new section rather than leaving the claim only in
prose.

**The nuspec carried the same false claim** in its `<description>` and `<summary>` — the text NuGet
shows on the package listing, where nobody would see the README correction. Both were amended to say
"multi-tenant **data model**" and to name the limit. Correcting the README while leaving the package
saying the opposite would have moved the problem rather than fixed it.

### 4.4 Localisation

The page label for a null tenant is `L["Installation"]`, and the column header resolves through
`L["Tenant"]`. **Both keys were added to all four `SystemLogs` resx files** —
`Mandant`/`Installation` for de-DE, `租户`/`系统` for zh-CN — rather than left as English fallbacks.
Verified at the byte level: no BOM introduced, LF preserved, `系统` = `E7B3BB E7BB9F` in UTF-8, and
all four diffs are 6 added lines with 0 deletions.

*(Observation, not fixed: that page already uses several literals with no resx entry — `Anonymous`,
`Client Information`, `User Information`, `Log Details`. The resource files have not kept pace with
the page. Adding the two new keys correctly does not oblige this pass to close a pre-existing gap,
but it is recorded rather than left to be discovered.)*

---

## 5. §D — Verification

### 5.1 Counts

| Suite | Baseline | After | Delta |
|---|---:|---:|---:|
| `Infrastructure.UnitTests` | 183 | **192** | +9 |
| `Application.IntegrationTests` | 9 | **9** | 0 |
| `Application.UnitTests` | 356 (+12 skipped) | **372** (+12 skipped) | +16 |
| `Server.UI.IntegrationTests` | 147 | **147** | 0 |
| **Total passed** | **695** | **720** | **+25** |
| Skipped / Failed | 12 / 0 | 12 / 0 | 0 |

**+25 is exactly the new tests**, all of them new files: `TenantStampingTests` 7,
`DocumentTenantIsolationTests` 9, `LogTenantStampingTests` 6, plus
`SinkColumnDriftTests.TheAcceptedSinkGaps_AreRealPropertiesAndReallyUnwritten` ×3 providers.
**No test was deleted, renamed, or had an expectation relaxed.**

### 5.2 Warnings

**10 distinct locations, identical to the baseline** — same files, same line and column, no
additions and no removals:

```
AuditTrails.razor(100,72) CS8602      DescriptionAttributeExtensions.cs(23,46) CS8603
Dashboard.razor(202,60)   CS8604      DescriptionAttributeExtensions.cs(33,20) CS8603
DescriptionAttributeExtensions.cs(12,45) CS8600   MapsterConfiguration.cs(26,32) CS8601
DescriptionAttributeExtensions.cs(20,32) CS8600   MapsterConfiguration.cs(28,29) CS8601
MudDateTimeField.razor(1,1) MUD0002               TenantSelect.razor(13,44)      CS8603
```

Every file this pass touched compiles warning-free. Nothing to explain because nothing changed.

### 5.3 A fresh run per provider

The application was booted against **fresh, throwaway databases on all three providers**, and each
log database was then inspected directly.

| Provider | Business DB | Log DB | Tenant column | Startup rows | With a tenant |
|---|---|---|---|---:|---:|
| SQLite | created | created | `TenantId` present | 8 | 0 — sink cannot write it (§2.3.2) |
| PostgreSQL | created | created | `tenant_id` present | 9 | 0 |
| SQL Server LocalDB | created | created | `TenantId` present | 8 | 0 |

Business-side on SQL Server: `AuditTrails` 13 rows, `PicklistSets` 11 rows, **all null tenant** —
correct, because every one of them is seeding, which runs with no ambient principal. PostgreSQL
matched at 13.

**All startup rows carrying null is the expected result, not a missing positive.** The positive case
— a request-scoped row carrying a tenant — is proved at every link of the chain rather than by one
end-to-end row:

1. **The ambient context is established on a real request.** `UserContextAccessor.Push` is called by
   `UserContextHubFilter`, which wraps every circuit invocation — and in Blazor Server every
   interactive user action arrives that way. `AuthorizationBehaviour:82` already **fails closed**
   when that context is absent, so no Mediator request reaches a handler without one.
2. **The interceptor stamps from it** — `TheAuditRowForAChangeMadeInsideATenant_RecordsThatTenant`
   and the picklist and document equivalents, against a real SQLite database.
3. **The enricher reads it** — `TheEnricherPublishesTheAmbientTenant_WithNoHttpRequestInSight`,
   through the real Serilog pipeline, with no HTTP context anywhere.
4. **Both server sinks write the property the enricher publishes** —
   `TheSqlServerSink_WritesTheTenantColumn_AndAllowsItToBeNull`,
   `ThePostgresSink_ReadsTheEnrichedTenantProperty`.

All probe databases were dropped afterwards (§6).

### 5.4 Generation probe

```
dotnet pack build/pack.csproj -o .          → GX.Blazor.Template.1.0.0.nupkg, 1.38 MB
dotnet new install ./GX.Blazor.Template.1.0.0.nupkg
dotnet new gxblazor -n P24 -o P24           → created
dotnet build P24.slnx                       → 0 errors
dotnet test P24.slnx                        → 720 passed, 12 skipped, 0 failed
dotnet new uninstall GX.Blazor.Template     → uninstalled
```

The generated project's README carries the new `Tenancy` section, so the correction ships rather than
living only in the source repository.

**The generated project's `Application.IntegrationTests` passed** — which is itself the confirmation
that §2.4's reversal was right. Those 9 tests run against the shared LocalDB
`BlazorDashboard.Test` database, which the changed migration id caused `Testing.cs`'s guard to
recreate. With the ids preserved they would have failed exactly as they did in §2.4.

The generated project was deleted and the template uninstalled; `dotnet new list` confirms no
`gxblazor` template remains registered.

### 5.5 The named boundary suites

§A touches the audit interceptor and the log DDL, so these are the proof that neither moved. Run as a
filtered set and all green:

| Suite | Result |
|---|---|
| `TransactionalAuditTests` (Pass 5) | green |
| `TimestamptzModelInvariantTests` (Pass 14B) | green |
| `LogDatabase*Tests` (Pass 15B) | green |
| `LogTableDdlTests`, `LogTableNamingTests`, `SinkTimestampTests` | green |
| `SinkColumnDriftTests` | green, and now able to fail on a case it previously could not (§2.3.2) |

124 tests across those filters, 0 failures.

---

## 6. Scratch probe disclosure

Everything below was created outside the repository, used, and removed. Nothing remains.

| Probe | Purpose | Disposed |
|---|---|---|
| `scratchpad/probe/` — a throwaway .NET console project referencing `Microsoft.Data.Sqlite`, `Npgsql` and `Microsoft.Data.SqlClient` | no `sqlite3` CLI is installed; needed to read the three log databases and to create/drop the probe databases | deleted |
| `scratchpad/live/` — SQLite business and log databases from the live boot | §5.3 | deleted |
| `scratchpad/mig.db` — SQLite target for `dotnet ef` | design-time only; never written to | deleted |
| `scratchpad/pass24-backup/` — copies of the three §B files | so the red capture could be restored exactly | deleted |
| PostgreSQL `gx_pass24_probe`, `gx_pass24_probe_logs` on `localhost:5434` | §5.3 | **dropped** |
| LocalDB `gx_pass24_probe`, `gx_pass24_probe_logs` | §5.3 | **dropped** |
| `C:\src\P24` — the generated project | §5.4 | deleted, template uninstalled |

The existing `GXApplication` / `GXApplication_Logs` databases named in `appsettings.json` were
**not** touched: the live runs were pointed at freshly created databases under different names.

The MSSQL LocalDB database `BlazorDashboard.Test`, used by `Application.IntegrationTests`, **was**
recreated — by the suite's own stale-migration guard, which is its designed response to a
regenerated `InitialCreate` (§2.4). That is a normal consequence of the change, not a probe.

`GX.Blazor.Template.1.0.0.nupkg` at the repository root was rebuilt by §5.4. It is
**gitignored** (`.gitignore:200`, `*.nupkg`) and does not appear in the working tree as a change.

---

## 7. File map

**Modified — §A stamping (11)**

| File | Change |
|---|---|
| `src/Domain/Entities/AuditTrail.cs` | `TenantId` + the stored-not-derived justification |
| `src/Domain/Entities/PicklistSet.cs` | `IMayHaveTenant`, `TenantId`, + the marker-choice reasoning |
| `src/Domain/Entities/SystemLog.cs` | `TenantId` + the five-part-change and no-migration-chain notes |
| `…/Persistence/Interceptors/AuditableEntityInterceptor.cs` | capture the tenant; pass it; **set it in the reprojection** (§2.1) |
| `…/Persistence/Configurations/AuditTrailConfiguration.cs` | index, and the deliberate absence of an FK |
| `…/Persistence/Configurations/PicklistSetConfiguration.cs` | note on the unique index not being widened |
| `…/Persistence/Logging/LogTableDdl.cs` | the column, three providers |
| `src/Infrastructure/Extensions/SerilogExtensions.cs` | both sink column sets; enricher takes and reads `IUserContextAccessor` |
| `…/Services/Identity/UserContextAccessor.cs` | `AsyncLocal` made static; `Pop` simplified (§2.3.1) |
| `…/Features/SystemLogs/DTOs/SystemLogDto.cs` | `TenantId` |
| `src/Server.UI/Pages/SystemManagement/SystemLogs.razor` | detail-panel row, "Installation" when null |

**Modified — §B repairs (4)**

`AdvancedDocumentsSpecification.cs`, `VisibleDocumentSpecification.cs`,
`DeleteDocumentCommand.cs`, `AddEditDocumentCommand.cs`.

**Modified — §C and localisation (6)**

`README.md`, `GX.Blazor.Template.nuspec`, and the four `SystemLogs*.resx`.

**Modified — tests (1)**

`tests/Infrastructure.UnitTests/Logging/SinkColumnDriftTests.cs` — the circularity fix (§2.3.2).

**Migrations — 3 replaced pairs + 3 snapshots** (§2.4).

**New (3 test files)**

| File | Lines | Tests |
|---|---:|---:|
| `tests/Application.UnitTests/Common/Interceptors/TenantStampingTests.cs` | 250 | 7 |
| `tests/Application.UnitTests/Features/Documents/DocumentTenantIsolationTests.cs` | 326 | 9 |
| `tests/Infrastructure.UnitTests/Logging/LogTenantStampingTests.cs` | 274 | 6 |

### Diffstat

```
31 files changed, 547 insertions(+), 3942 deletions(-)
```

plus 9 untracked new files (3 test files, 6 migration files). The large deletion count is entirely
the three replaced `InitialCreate` pairs (~3,900 lines); the six new migration files replace them at
comparable size. **Excluding migrations, the change is 547 insertions against ~46 deletions.**

### Edit fidelity

- **Line endings unchanged.** The whole working tree is LF on disk — verified against files this pass
  never touched (`src/Domain/Entities/Tenant.cs`, `src/Server.UI/Program.cs`). The
  `LF will be replaced by CRLF` notices from git are a pre-existing property of `core.autocrlf=true`
  against this repo's `* text=auto`, not something these edits introduced.
- **No BOM added or removed.** The four resx files still begin `3c 3f 78`.
- **Every diff hunk is intentional.** The migration diffs were verified with the id masked to confirm
  they differ only by the tenant additions; the resx diffs are 6 added lines each with 0 deletions.

---

## 8. Anomalies

**A1 — `SinkColumnDriftTests` was circular for SQLite, and had been since it was written.** Fixed in
this pass (§2.3.2), and recorded here because the class of defect is more interesting than the
instance: a test that derives an expectation from the thing it is checking will pass forever. The
same shape is worth looking for elsewhere.

**A2 — `ResolveAuditTrails` is a hand-written field-by-field copy with no compiler assistance.** A
property added to `AuditTrail` and not added there is silently dropped between capture and save
(§2.1). It cost this pass one red test run. A projection like that wants either a test asserting
every mapped property round-trips, or a shape that cannot omit one; neither is this pass's work, and
a comment now names the hazard at the site.

**A3 — `UserContextAccessor.Push` has exactly one caller** — `UserContextHubFilter` — and `Set()` has
**none**. That is correct for Blazor Server, where interactive work arrives over the circuit, and
`AuthorizationBehaviour` fails closed without it. But it means a plain HTTP endpoint outside the
circuit has no ambient user context, so anything reached that way sees `Current == null`. The
`/files` endpoint already takes its principal from `HttpContext` rather than the accessor, so nothing
is broken today. Worth knowing before any new non-circuit endpoint is added.

**A4 — the `SystemLogs` resource files have fallen behind the page.** Four literals the page
localises have no resx entry at all (§4.4). Not fixed; the two keys this pass introduced were added
properly rather than joining them.

**A5 — `Permissions.Documents.Export` and `Documents.Import` remain inert**, and
`ExportDocumentsQuery.cs` / `GetAllDocumentsQuery.cs` remain 3-byte empty files (Pass 23 A1).
Untouched here — deleting a permission constant is a decision, not a repair, and it is not what this
pass was asked to do.

**A6 — the `All` list view's semantics changed** (§3.1). It is a correction and it is tested, but it
is a user-visible behaviour change and not a pure repair: a principal who has switched tenants no
longer sees their own private documents from the tenant they left, in the listing. The download
button already refused them. Flagged because it is the one place §B went beyond restoring the stated
rule to making two disagreeing rules agree.

---

## 9. What was deliberately not done

**No filtering was begun outside Documents.** `AuditTrail`, `PicklistSet` and `SystemLog` carry a
tenant and nothing reads it. Stage 1 (repairing the permission set, the `TenantId`/`TenantUsers`
divergence, and `CanSwitchToTenantAsync`) and Stages 3–7 remain separate passes, in that order —
Stage 1 first, because tenant scoping will lean on permissions that Pass 23 §5 found dead or inert.

`AuditTrailDto` was **not** given a `TenantId`, unlike `SystemLogDto`. The brief asked for the DTO
and page column on `SystemLog` only, and adding one to the audit trail would put a tenant column on a
grid that cannot filter by it — an invitation to read it as a boundary. It is a one-line addition
whenever the audit trail is actually scoped.
