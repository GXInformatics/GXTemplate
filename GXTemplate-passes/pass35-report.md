# Pass 35 — Say What the Log Actually Is

**Nature:** small editing pass. Documentation, three description strings, one menu gate that turned
out not to need gating.
**Date:** 2026-09-05.

**Result in one line:** the README now answers the question its Tenancy table used to defer, the
three `Logs.*` descriptions say in the role editor that they span every tenant, and **§C's premise
was wrong** — the menu entry is gated exactly like its ten siblings, which this pass proves with a
test rather than "fixing". **946 → 964 tests**, warnings unchanged, boundary suites byte-unmodified.
One out-of-brief correction: a Known-limitations bullet about picklists had been false since Pass 32.

---

## 1. Start state

| | |
|---|---|
| HEAD | `3f2f3034` — *"pass34"* ✓ |
| Working tree | clean |
| Build | **0 errors, 19 warnings across 10 distinct source locations** ✓ |
| Tests | 228 + 12 + 483 + 223 = **946 passed, 12 skipped, 0 failed** ✓ |

Matches the brief exactly.

---

## 2. §A — The README

### 2.1 The Tenancy table row

**Before:**

> | System logs | No — and **not reachable by that filter**: `SystemLog` is not on `ApplicationDbContext` at all, only on `LogDbContext`. Scoping them is a separate design, not a deferred switch |

**After:**

> | System logs | **No, and deliberately not — `Logs.*` is an operator right, not a tenant one.** `SystemLog` lives on `LogDbContext` in a separate database, so the global filter cannot reach it; but the reason it is not scoped is not mechanical. Most log rows carry no tenant and never can. See [What a `Logs.View` holder can see](#what-a-logsview-holder-can-see) before granting it to anyone |

The old row was true and had stopped being useful: it explained why the *filter* could not reach the
entity, which invites the reader to conclude that a different mechanism would. Pass 34 established
that the mechanism was never the obstacle.

### 2.2 The new section, quoted in full

Inserted directly after the "treat everything below the Picklists row as installation-wide"
paragraph, before the roles passage, at `README.md:469`.

---

### What a `Logs.View` holder can see

**The system log is installation-wide, it will not be tenant-scoped, and the boundary is the
permission rather than a filter.** This is the one row in the table above where the honest answer is a
grant decision rather than a query predicate, so it is worth the paragraphs.

**Everything in the log, for every tenant.** A holder of `Permissions.Logs.View` reads
`/system/logs`, which is the whole table. Concretely, the rows carry:

- the **username, client IP address and user agent** of whoever produced the event — for every
  tenant's users, including on the sign-in path;
- the **recipient email address** of every message the application sent: fourteen log statements
  across the mail pipeline record one, and `ResetPasswordCommand`'s does so deliberately, as an
  operational record of mail that actually went out;
- **document storage keys**, picklist and entity ids, passkey credential ids, and request paths;
- **full exception detail including stack traces** — thirty-one call sites pass the exception object,
  and a stack trace can quote entity names, ids and values from whichever tenant's request faulted;
- and, in the `Properties` and `LogEvent` columns, **the complete serialized event twice over** —
  every structured parameter the call site passed plus every enricher property (`SourceContext`,
  `RequestPath`, `RequestId`, `ConnectionId`, `UserName`, `TenantId`, `ClientIP`, `ClientAgent`,
  `TraceId`, `SpanId`). Whatever the rendered message shows, these two columns hold the rest.

Two disclosure defects have already been found and fixed here, which is the best evidence that this
surface deserves the care: `CustomError.razor` used to write a live password-reset token into the log
when an exception was raised on `/account/reset-password?userId=…&token=…`, and `Forgot.razor` used to
log the address an anonymous visitor typed, making the log a second account-enumeration oracle.

**Build the customer-administrator role without `Logs.View`, `Logs.Search` and `Logs.Purge`.** This is
the actionable instruction, and the trap it avoids is specific: the shipped `Admin` role holds all
three, and `Admin` is the role a customer's administrator is most likely to be given. Copy it,
remove the three log rights, and grant that instead. `Logs.Purge` deserves its own thought — it
erases every tenant's rows at once, permanently, with no export to fall back on.

**It is not scoped because most rows have no tenant to scope by.** The ambient tenant is populated
only inside a Blazor circuit — one SignalR hub filter pushes it and nothing else does — so startup,
seeding, every sign-in and sign-out, every password reset, every mail send and every HTTP-level
exception are recorded with no tenant at all. Around two-thirds of the application's log statements
cannot carry one even in principle, and a measured run of the real application produced **zero**
tenanted rows. A per-tenant log view would therefore not be a smaller log; it would be an *edited*
one — a tenant administrator who cannot see that the application restarted, or that one of their
users failed to sign in, is being shown something worse than nothing.

**The menu entry is gated by role, not by this permission.** The navigation menu filters on role
names, and the whole `MANAGEMENT` section is gated on `Admin`. So a customer administrator built by
copying `Admin` and removing the log rights will still *see* the Logs link and be refused at the
page. That is cosmetic, not a leak — the page, both queries and the purge each check the permission —
but it is the first thing you will notice after following the instruction above.

**`./log/log-*.txt` carries the same content with no gate at all.** Every event that reaches the
database also reaches a rolling file sink, which drops only the one-off bootstrap password. There is
no tenant column there, no permission, and no filter. **Filesystem access to the deployment is
therefore equivalent to `Logs.View`, and more**, so treat the log directory as you would the database
backups. This is also why filtering the database view would be a boundary that only half held.

**On SQLite the `TenantId` column is always null.** That sink is a third-party package with a fixed
`INSERT` statement and no configurable columns, unlike the SQL Server and PostgreSQL sinks, which
both record it. The column exists in the SQLite DDL regardless, because EF reads the property on
every provider. SQLite is the no-server development and test provider; both providers a GX
installation runs on record the tenant — but, per the paragraph above, on most rows there is no
tenant to record. `SinkColumnDriftTests` and `LogTenantStampingTests` name the gap so it cannot widen
unnoticed.

---

### 2.3 Every factual claim, traced

The brief required each claim to come from a Pass 34 measurement rather than from its paraphrase.
Each was re-confirmed against the source at the point of writing:

| Claim in the section | Re-confirmed by |
|---|---|
| username / client IP / user agent enriched onto every row | `SerilogExtensions.cs:506-509` — four `AddPropertyIfAbsent` calls |
| **fourteen** mail log statements carry `{Email}` | `grep` over `src/`: 2 each in `ResetPasswordCommand`, `SendMailCommand`, `SendWelcomeCommand`, `UserActivationCommand`, `SinkMailService`; 4 in `MailgunMailService` |
| **thirty-one** call sites pass the exception object | `grep -c "LogError(ex\|LogError(exception\|LogWarning(ex\|LogError(Exception"` over `src/` |
| `Properties` and `LogEvent` carry the whole event | `StandardColumn.Properties` / `StandardColumn.LogEvent` (SQL Server, lines 257/261) and `PropertiesColumnWriter` / `LogEventSerializedColumnWriter` (PostgreSQL, lines 340/341) |
| the two fixed disclosure defects | the comments in `CustomError.razor` and `Forgot.razor`, read in place |
| `Admin` holds all three log rights and nothing else does | `AdministratorPermissionRegistry.cs:90-92`; `Basic` gets only `Documents.View`/`Download` |
| the ambient tenant exists only in a circuit | `UserContextHubFilter.cs:52` is the only `Push` of a user context in `src/` |
| the readers are the page, two queries and the purge; no export | `[RequestAuthorize]` on the three requests, `[Authorize]` on the page, and `Permissions.Logs.Export` absent since Pass 11C |
| the file sink drops only the bootstrap banner | `IsExcludedFromFileSink` = `CarriesBootstrapSecret`, `SerilogExtensions.cs:96` |
| SQLite cannot write `TenantId` | `LogTenantStampingTests.OnSqlite_TheRowLands_ButTheTenantColumnStaysNull_BecauseThatSinkCannotWriteIt`, re-run green |
| zero tenanted rows in a measured run | Pass 34 §2.4 — two application runs, 10/10 and 11/11 null |

**Two numbers were corrected upward from Pass 34's own table**, which had counted `{Email}` at 10 and
did not count exception-object call sites at all. See A1.

### 2.4 Two other README statements updated

The Known-limitations list carried a summary of the same fact. It now points at the new section and
gains a bullet of its own:

> - **The system log is installation-wide and always will be, so `Logs.*` is an operator right.** A
>   holder of `Permissions.Logs.View` reads every tenant's usernames, client addresses, mail recipient
>   addresses, document storage keys and full exception stack traces — and the same content is written
>   to `./log/log-*.txt` with no permission gate at all. Build a customer-administrator role **without**
>   `Logs.View`, `Logs.Search` and `Logs.Purge`; the shipped `Admin` role holds all three. See
>   [What a `Logs.View` holder can see](#what-a-logsview-holder-can-see).

The pre-existing bullet in that list also said *"System logs and roles remain installation-wide to
whoever holds the relevant view permission"*, which had been half-stale since Pass 33; it now notes
that roles are governed by `Roles.ManageDefinitions`.

The **stamping** section's SQLite note (`README.md:634`) was kept as the brief required, with three
lines added so a reader who meets it there learns the larger fact rather than filing it as an
isolated curiosity:

> That gap matters less than it looks, and the reason is worth knowing before you plan around it: on
> **every** provider most log rows carry no tenant anyway, because the ambient tenant exists only
> inside a Blazor circuit. See [What a `Logs.View` holder can see](#what-a-logsview-holder-can-see).

---

## 3. §B — The permission descriptions

### 3.1 Before and after

| Constant | Before | After |
|---|---|---|
| module `Logs` | `Set permissions for log operations` | `Set permissions for log operations - all of them installation-wide` |
| `Logs.View` | `Allows viewing log details` | `Allows viewing the installation's system log - every tenant's activity, in full` |
| `Logs.Search` | `Allows searching for log records` | `Allows searching the installation's system log, across every tenant` |
| `Logs.Purge` | `Allows purging log records` | `Allows permanently erasing the entire system log, for every tenant at once` |

All four stay inside the register the neighbours use — `PicklistSets.ManageShared` is 81 characters
and `Roles.ManageDefinitions` 85; the longest here is 79. They are grid text, not paragraphs.

The file gains a `<remarks>` block saying why the strings are load-bearing, and `Purge` gains a
three-line comment recording that it is an unfiltered `ExecuteDelete` with no export to fall back on.

### 3.2 The path these strings take, which is not the obvious one

Worth recording because a pass that changed the attribute and assumed it surfaced would have been
half right. The **per-constant** `[Description]` does **not** become `PermissionModel.Description`:

- `PermissionQueryService.BuildRolePermissionModels` puts it in **`HelpText`** (line 145), and
  `PermissionsDrawer.razor:72-75` renders `HelpText` under the permission's name;
- `PermissionModel.Description` carries the **class-level** attribute and renders as the **group
  header** (`PermissionsDrawer.razor:34`).

So both attributes needed changing, and they reach two different places on screen. Both are asserted.

One asymmetry found while tracing it: `BuildUserPermissionModels` **overwrites** `HelpText` with
*"This permission is inherited and cannot be modified."* for an inherited permission, so on the
**user** permission editor an inherited `Logs.View` shows the inheritance notice instead of the
scope warning. On the **role** editor — where the grant decision is actually made — the description
always shows. Recorded as A2, not changed.

### 3.3 The neighbour survey

Every other permission description was read. **None makes a false claim**, and none says "your
tenant" or similar. But the question the brief asked — is `Logs.*` an isolated case or a pattern? —
has a clear answer: **it is a pattern, and `Logs.*` is simply the worst instance.**

Every description in the template is scope-neutral ("Allows viewing X details"). In a product whose
default is tenant-scoped, neutral text reads as tenant-scoped. The rights where that reading is
**wrong** are:

| Group | Rights | Actual scope | Reads as |
|---|---|---|---|
| **`Logs.*`** | `View`, `Search`, `Purge` | installation-wide | tenant-scoped — **fixed in this pass** |
| **`Roles.*`** | `View`, `Create`, `Edit`, `Delete`, `Search`, `Import`, `Export`, `ManagePermissions` | installation-wide — one role set, unique `NormalizedName` across the installation | tenant-scoped. `ManageDefinitions` (Pass 33) enumerates its verbs but also says nothing about tenancy |
| **`SecuritySettings.*`** | `View`, `Edit` | installation-wide — `SecurityPolicies` holds one row; `Edit` sets the idle window for every tenant | tenant-scoped. `Edit`'s "including the idle timeout" hints at reach without stating it |
| **`Tenants.*`** | `View`, `Create`, `Edit`, `Delete`, `Search` | installation-wide | arguably self-evident — administering tenants is not a per-tenant act |
| `Dashboards.View` | — | n/a | the feature is `Excluded`; the constant is not grantable in practice |

The four rights that **do** state their scope are the cross-tenant escapes and the shared-write
right — `Users.ViewAllTenants`, `AuditTrails.ViewAllTenants`, `Users.SwitchToAnyTenant`,
`PicklistSets.ManageShared` — all of which name it explicitly. So the convention already exists; it
has only been applied to rights that *widen* a scope, never to rights that never had one.

**Not fixed beyond `Logs.*`, per the brief.** `Roles.*` is the strongest candidate for a follow-up:
eight descriptions, and unlike security settings it is a surface a customer administrator will
routinely reach.

---

## 4. §C — The ungated menu entry

**Pass 34 §2.3 was wrong, and nothing needed changing.** Reported rather than papered over.

### 4.1 The idiom

The menu gates on **role names**, not permissions, and the filter is applied at three levels in
`NavigationMenu.razor` — section (line 18), item (line 33) and sub-item (line 38) — each as
`x.Roles == null || x.Roles.Any(r => Roles.Contains(r))`, where `Roles` is
`UserProfileState.Value.AssignedRoles` (`AppLayout.razor:23`).

**There is exactly one gate in the entire menu**: `MenuService.cs:71`,
`Roles = new[] { Roles.Admin }` on the `MANAGEMENT` section. **No section item and no sub-item
anywhere carries a `Roles` array.** So the Logs entry is gated identically to Multi-Tenant, Users,
Roles, Profile, Login History, Picklist, Security Settings, Audit Trails, Email Templates and Jobs —
all eleven inherit the section's gate.

Pass 34's finding — *"carries no permission, unlike its neighbours"* — was right on the first half
and wrong on the second. Following the brief's own instruction (*"if the other entries gate on roles
rather than permissions, follow that rather than introducing a second mechanism"*) leads to **no
change**: adding `Roles = [Admin]` to the Logs sub-item would have been redundant with the section
gate and would have made it the only item in the menu with a gate of its own — creating the
inconsistency the finding described rather than removing it.

`NoMenuEntryCarriesAGateOfItsOwn` asserts that idiom, so a future "fix" of this shape fails.

### 4.2 The real mismatch, which is not about Logs

**The menu gates by role; every page under it gates by permission.** The two can disagree in both
directions, and one direction matters directly to this pass's own recommendation: a customer
administrator built by copying `Admin` and removing `Logs.*` keeps the role name, still sees the Logs
link, and meets the refusal at the page. `MenuSectionSubItemModel` has a `Roles` array and no
permission field, so the menu cannot express it.

This is cosmetic — the page, both queries and the purge each check the permission — but it is a
consequence of the instruction the README now gives, so the README says so and
`ARoleHolderWhoLacksTheLogPermissionStillSeesTheLink` pins it as known behaviour.

---

## 5. §D — Verification

### 5.1 The descriptions, asserted at both ends

**`LogPermissionScopeTests`** (6 tests, Application.UnitTests) reads each constant's attribute by the
same reflection `PermissionQueryService` uses:

| Claim | Test |
|---|---|
| each of View/Search/Purge states installation scope | `EveryLogRightStatesThatItIsInstallationWide` (3 cases) |
| Purge says it *erases* | `ThePurgeRightSaysThatItErases` |
| the group header states it too | `TheModuleDescriptionAlsoStatesTheScope` |
| **none claims a tenant scope it does not have** | `NoLogRightClaimsATenantScopeItDoesNotHave` — the inverse control: an affirmative false statement would be worse than the vague original |

Assertions are about **meaning** (the scope must be stated) rather than an exact sentence, so the
wording can be improved without a test failing for no reason.

**`LogPermissionDescriptionComponentTests`** (6 tests, Server.UI.IntegrationTests) is the rendering
half — it was cheap, so it was written rather than settled by inspection. It builds
`PermissionModel`s from the **real attributes** by the same reflection the service uses, renders the
real `PermissionsDrawer`, and asserts the markup:

| Claim | Test |
|---|---|
| the harness itself is sound — three rights, all with help text | `TheDrawerRendersThreeLogRights` |
| View's sentence reaches the screen | `ViewSaysItSpansEveryTenant` |
| Purge's does | `PurgeSaysItErasesEveryTenantsHistory` |
| Search's does | `SearchSaysItCrossesTenants` |
| the group header does, on its separate path | `TheGroupHeaderCarriesTheScopeToo` |
| nothing rendered promises a tenant-scoped log | `NothingInTheDrawerPromisesATenantScopedLog` |

### 5.2 The menu gate (§D.3)

**`SystemMenuGateComponentTests`** (6 tests) renders the real `NavigationMenu` over the real
`MenuService` — circuit-level, since at `prerender: false` an HTTP response carries no menu at all:

| Claim | Test |
|---|---|
| a non-holder does not see the entry | `WithoutTheAdminRole_TheLogsEntryIsNotRendered` |
| a holder does | `WithTheAdminRole_TheLogsEntryIsRendered` |
| an unrelated role does not open the section | `AnUnrelatedRoleDoesNotOpenTheSection` |
| all seven management hrefs move together | `TheWholeManagementSectionMovesTogether` |
| the idiom: exactly one gate, at the section | `NoMenuEntryCarriesAGateOfItsOwn` |
| the documented papercut | `ARoleHolderWhoLacksTheLogPermissionStillSeesTheLink` |

The positive cases matter more than usual here: a negative assertion about a menu is satisfied by a
menu that renders nothing.

### 5.3 Red before, green after

The four descriptions reverted to their originals, everything else untouched:

```
Application.UnitTests       Failed: 5,  Passed: 1
Server.UI.IntegrationTests  Failed: 4,  Passed: 8
```

Red: the three `EveryLogRightStatesThatItIsInstallationWide` cases,
`TheModuleDescriptionAlsoStatesTheScope`, `ThePurgeRightSaysThatItErases`,
`ViewSaysItSpansEveryTenant`, `SearchSaysItCrossesTenants`,
`PurgeSaysItErasesEveryTenantsHistory`, `TheGroupHeaderCarriesTheScopeToo`.

Green throughout: both inverse controls, `TheDrawerRendersThreeLogRights`, and **all six menu
tests** — correctly, since this pass changed nothing about the menu. Restored byte-identically from
a copy taken beforehand, verified by `diff`.

### 5.4 Counts

| | Before | After | Delta |
|---|---|---|---|
| `Infrastructure.UnitTests` | 228 | 228 | — |
| `Application.IntegrationTests` | 12 | 12 | — |
| `Application.UnitTests` | 483 (+12 skipped) | **489** (+12 skipped) | **+6** |
| `Server.UI.IntegrationTests` | 223 | **235** | **+12** |
| **Total** | **946 passed, 12 skipped** | **964 passed, 12 skipped** | **+18, 0 failed** |

The +18 is exactly the three new files: 6 `LogPermissionScopeTests`, 6
`LogPermissionDescriptionComponentTests`, 6 `SystemMenuGateComponentTests`. No pre-existing test
changed count or outcome.

**Warnings: unchanged.** `dotnet build --no-incremental` gives **19 warnings across the same 10
distinct source locations** — `DescriptionAttributeExtensions.cs` ×4, `MapsterConfiguration.cs` ×2,
`MudDateTimeField.razor`, `TenantSelect.razor`, `Dashboard.razor`, `AuditTrails.razor` — plus
`NETSDK1206`, which has no source location. One new warning was introduced during the pass
(`CS8602` in the drawer fixture, from `m.HelpText.Length`) and removed before the count was taken;
it is not in the final build.

### 5.5 Boundary suites

`git diff --quiet` per file — **all 26 unmodified**, covering Passes 26–34's scope, isolation,
filter, presence, guard, role-definition and migration suites. `git status tests/` shows three
additions and no modifications.

### 5.6 Generation probe

```
dotnet pack (nuspec) → dotnet new install → dotnet new gxblazor -n P35
  → README.md carries "What a `Logs.View` holder can see" (4 matches incl. cross-references)
  → LogsPermissions.cs carries all four new description strings
  → build: 0 Error(s), 19 Warning(s)
  → dotnet test: 228 + 12 + 489 + 235 = 964 passed, 12 skipped, 0 failed
  → dotnet new uninstall; probe directory removed
```

The README ships in generated projects, so its presence there was checked explicitly, as the brief
required — including the operator instruction and the file-sink paragraph by line number.

---

## 6. File map, diffstat and edit fidelity

### 6.1 File map

**New (3):**

| File | |
|---|---|
| `tests/Application.UnitTests/Security/LogPermissionScopeTests.cs` | **6 tests** — the attributes, by the service's own reflection |
| `tests/Server.UI.IntegrationTests/LogPermissionDescriptionComponentTests.cs` | **6 tests** — the real `PermissionsDrawer`, rendered |
| `tests/Server.UI.IntegrationTests/SystemMenuGateComponentTests.cs` | **6 tests** — the real `NavigationMenu` over the real `MenuService` |

**Modified (2):**

| File | |
|---|---|
| `README.md` | the Tenancy row, the new section, two Known-limitations bullets, the stamping-section cross-reference |
| `src/Application/Features/SystemLogs/Security/LogsPermissions.cs` | four description strings, a `<remarks>` block, one comment |

**No production behaviour changed.** The only `src/` edit is attribute text and comments; nothing
compiles differently.

### 6.2 Diffstat

```
 README.md                                                        | 98 +++++++++++++++++++---
 .../Features/SystemLogs/Security/LogsPermissions.cs              | 34 +++++++-
 2 files changed, 116 insertions(+), 16 deletions(-)
```

Plus 3 new test files.

### 6.3 Edit fidelity

- **No git actions.** Nothing staged, committed, stashed or reset; only `status`, `log`, `diff` and
  `check-ignore`.
- **The red-before demonstration was reverted byte-identically**, verified by `diff` against a copy
  taken beforehand.
- **No existing test file was touched.** The +18 is entirely in three new files.
- **One change outside the brief**, made deliberately and flagged here rather than buried — see §7.
- The generation probe packed, installed, generated, built, tested, uninstalled and removed itself;
  the nupkg in the repository root is a gitignored build artifact, rebuilt by `dotnet pack`.

---

## 7. One correction outside the brief

**A Known-limitations bullet about picklists had been false since Pass 32**, and it sat eleven lines
from a bullet this pass was rewriting in a document whose stated value is that it is true. It was
corrected rather than left.

**Before** — four claims, three of them false:

> - **A shared picklist value is editable by any tenant's administrator.** […] Editing one changes it
>   for every tenant. Nothing in the admin page distinguishes a shared row from a private one today,
>   and `PicklistSetDto` carries no `TenantId` for it to distinguish them by. Gate it before you rely
>   on multi-tenant picklists.

Verified against the code: `PicklistSetDto` has carried `TenantId` and `IsShared` since Pass 32
(`PicklistSetDto.cs:37,40`); the grid marks shared rows with a chip and tooltip and renders them
read-only without the right (`PicklistSets.razor:106-133`); and both command handlers refuse the
write. The instruction "gate it before you rely on multi-tenant picklists" told a reader to build
something that already exists.

**After:**

> - **A shared picklist value reaches every tenant, and writing one needs `PicklistSets.ManageShared`.**
>   Picklists are shared plus per-tenant: the values the installation seeds carry no tenant and are
>   visible to everyone, and the filter admits them for reading, so the edit and delete commands —
>   which address rows by id through that same filter — reach them too. Changing one changes it for
>   every tenant, which is why the write is gated: the two command handlers refuse it without the
>   right, and the admin grid marks shared rows and renders them read-only without it. The right is
>   granted to `Admin` by default; revoke it in a multi-tenant installation where one customer's
>   administrator should not redefine the installation's reference data.

Pass 32 §6 replaced the *Tenancy section's* version of this limitation and did not reach the
Known-limitations list, which is a second copy of the same fact — see A3.

---

## 8. What remains

| Surface | Status |
|---|---|
| **System log scoping** | **closed.** Not scoped, by decision; the boundary is `Logs.*` as an operator right, now stated in the README, in the role editor, and in the permission file |
| **`ChannelBasedNoWaitPublisher`'s frozen `ExecutionContext`** | **the next pass, and a correctness bug rather than a documentation one.** `Task.Run(ProcessNotifications)` in the constructor captures the ambient context, so ~25 log sites — including all fourteen that record an email address — are labelled with the tenant that was ambient when the publisher was first resolved in that scope, and a tenant switch does not move it. Confined to `SystemLog.TenantId` today because no notification handler writes through `ApplicationDbContext`; it becomes a data defect the day one does, since `AuditableEntityInterceptor` would stamp the frozen tenant. The fix is small — capture at publish time and restore around the callback, or suppress the flow at construction — but it needs its own tests |
| **Security settings (idle policy)** | **unscoped, and the answer should be given rather than assumed.** `SecurityPolicies` holds one row and the cache key is a constant, so one tenant's administrator sets the idle window for all of them; the README says so plainly and calls it "a deliberate starting point". What has never been decided is whether that is *right* — the reading path already goes through `IIdleTimeoutPolicyProvider` precisely so a tenant column plus a cache key would suffice, so the cost is known and small. Unlike logs, this one is a genuine product question rather than a category error, and it is the last row of the Tenancy table still answered with "by design" instead of with a decision |
| **SQLite/PostgreSQL shared-picklist duplicate gap** | **known, asserted, unchanged.** Both treat NULLs as distinct in a unique index, so the shared partition is unprotected there; SQL Server is protected. Closing it portably needs a partial unique index whose filter SQL differs per provider. `TheSharedPartitionIsNotProtectedFromDuplicatesOnThisProvider` fails if it is ever closed |
| **`Roles.*` description scope** | **surveyed, not fixed** (§3.3). Eight descriptions on an installation-wide surface a customer administrator routinely reaches, all reading as tenant-scoped. The strongest follow-up candidate |
| **Menu by role, page by permission** | **documented, not changed** (§4.2). Affects all eleven `MANAGEMENT` entries, not just Logs; cosmetic, since every page checks its own permission |

---

## 9. Scratch probe disclosure

Two, both removed:

1. **A green-file backup** of `LogsPermissions.cs` for the red-before demonstration, in the session
   scratchpad. Deleted after the byte-identical restore.
2. **The generation probe** — packed nupkg, installed template, generated `P35` at `C:\gxp35`;
   template uninstalled, directory removed.

No database, application run or external service was involved in this pass.

---

## 10. Anomalies

**A1 — two of Pass 34's payload counts were low, and the report's own method explains why.** Its
§2.2 table gave `{Email}` as 10; the actual number of log statements carrying an email address is
**14**. It also gave no figure at all for call sites passing an exception object, which is **31** and
is the highest-severity item in the payload — a stack trace can quote another tenant's data
verbatim. The undercount came from extracting placeholder occurrences out of a truncated regex over
`Log*(` lines rather than counting statements. Recorded because the numbers were about to be copied
into a document whose whole purpose is to be accurate, and because it is the second time in three
passes that a count derived by `grep -o` needed correcting when it was counted properly.

**A2 — the same description reaches two editors and only one of them always shows it.**
`PermissionQueryService.BuildUserPermissionModels` replaces `HelpText` with *"This permission is
inherited and cannot be modified."* whenever the permission comes from a role, so on the **user**
permission editor an inherited `Logs.View` shows the inheritance notice in place of the scope
warning. `BuildRolePermissionModels` has no such branch, so the **role** editor — where the grant
decision is actually made — always shows it. The behaviour is defensible (an inherited permission
cannot be toggled there, so the notice is the more useful text) but it means the warning has a hole
in exactly one view, and nothing says so. Not changed; recorded because the two builders are a
near-copy that differ in one line.

**A3 — the README states the picklist limitation twice, and Pass 32 updated only one copy.** The
Tenancy section's version was rewritten in Pass 32; the Known-limitations version went on asserting
that `PicklistSetDto` carries no `TenantId` and that nothing in the grid distinguishes a shared row —
both false the moment that pass shipped, and both still false three passes later. This is the
two-copies-of-one-rule defect this programme keeps meeting in *code*, appearing in prose, where there
is no compiler and no test to catch it. The same duplication exists for the tenancy summary
(Tenancy table plus Known-limitations bullet), which is why this pass updated both. Worth a future
pass deciding whether the Known-limitations list should summarise-and-link rather than restate.

**A4 — Pass 34's §2.3 menu finding was wrong, and the shape of the error is worth keeping.** It
observed correctly that the Logs entry carries no permission, and inferred incorrectly that this made
it inconsistent with its neighbours. The inference failed because the comparison was never made: the
neighbours carry no permission either, and the gate they all share sits one level up, on the section.
An investigation pass that had rendered the menu — as this pass did in six lines of bUnit — would
have seen it. Recorded because "X lacks the thing its neighbours have" is a claim about the
neighbours, and this one was asserted without reading them.

**A5 — `dotnet new install` silently produced two registrations of the same template.** Installing
the stale nupkg and then force-installing the freshly packed one left the engine with two entries for
`GX.Blazor.Template`, and generation failed with *"Sequence contains more than one matching
element"* out of `TemplatePackageManager` — a stack trace with no mention of duplicate installs.
`dotnet new uninstall` had to be run twice before a clean install worked. Harmless once diagnosed,
but recorded for the next pass that runs a generation probe: uninstall before installing, rather than
relying on `--force`.
