# Pass 23 — Tenant Isolation: What It Would Actually Take

**Nature:** investigation only, ending in a gate. **Nothing in the repository was changed.**
**No git actions.** **Date:** 2026-09-03.

> **Read §7.0 first if you read nothing else.** The premise holds, the defect is real and wider
> than Pass 22 measured, and there is one finding that changes the sequencing: **the log database
> has no migration chain**, so stamping `SystemLog` is the one decision that is genuinely cheap
> now and genuinely expensive later. Everything else can wait; that cannot.

---

## 1. Start state

| | |
|---|---|
| HEAD | `4425e1c647000b6301eb7443c743f10bbe5f2466` — *"pass22"* |
| Branch | `main` |
| Working tree | **clean** (`git status --porcelain` empty) |
| Build | `dotnet build CleanArchitecture.Blazor.slnx` — **0 errors**, **10 warnings** |
| Warning locations | **10 distinct** — matches expectation |
| Tests | **695 passed, 12 skipped, 0 failed** — matches expectation |

Per-assembly: `Infrastructure.UnitTests` 183 · `Application.IntegrationTests` 9 ·
`Application.UnitTests` 356 (+12 skipped) · `Server.UI.IntegrationTests` 147.

**Spot-checks both pass.** `ConfirmEmail.razor` no longer sets `IsActive` — the only surviving
mention is the comment at `:59` recording that it used to. `Register.razor:155` sets
`user.IsActive = false;` explicitly. No mismatch; the pass proceeded.

---

## 2. §A — Is the data even stamped?

### 2.1 The mechanism that exists

Two marker interfaces are declared in `Domain/Common/Entities/IMustHaveTenant.cs`:
`IMustHaveTenant` (non-null `TenantId`) and `IMayHaveTenant` (nullable). They are honoured by
`AuditableEntityInterceptor.SetCreationAuditInfo` (`:327-328`), which stamps `TenantId` from
`IUserContextAccessor.Current.TenantId` on insert.

**Exactly one entity in the template implements either interface: `Document`** (`IMayHaveTenant`).
So the stamping machinery is built, wired into the save pipeline, and applied to one entity.

### 2.2 Per-entity stamping table

`ApplicationDbContext` (`ApplicationDbContext.cs:23-29`) plus the Identity sets it inherits from
`IdentityDbContext`:

| Entity | DbSet | Carries `TenantId`? | Reaches one by relation? | Configuration |
|---|---|---|---|---|
| `Document` | `Documents` | **Yes** — `string? TenantId`, `IMayHaveTenant` | — | `DocumentConfiguration` (`Navigation(e => e.Tenant).AutoInclude()`) |
| `ApplicationUser` | `Users` | **Yes** — `string? TenantId` | plus `TenantUsers` many-to-many | `ApplicationUserConfiguration:35-36` — `HasOne(x => x.Tenant)`, autoincluded |
| `TenantUser` | `TenantUsers` | **Yes** — it *is* the join | — | `TenantUserConfiguration` (cascade both ways) |
| `Tenant` | `Tenants` | n/a — it *is* the tenant | — | `TenantConfiguration` (unique `Name`) |
| `AuditTrail` | `AuditTrails` | **No** | **No** — `UserId → ApplicationUser` only | `AuditTrailConfiguration` — no tenant of any kind |
| `PicklistSet` | `PicklistSets` | **No** | **No** | `PicklistSetConfiguration` — unique on `(Name, Value)` |
| `SecurityPolicy` | `SecurityPolicies` | **No** — one row per installation, by design | **No** | `SecurityPolicyConfiguration`; README:456 states the limitation |
| `ApplicationRole` | `Roles` | **No** | **No** | `ApplicationRoleConfiguration` — unique `NormalizedName` **globally** |
| `ApplicationRoleClaim` | `RoleClaims` | **No** | via `Role`, which has none | cascade from role |
| `ApplicationUserRole` | `UserRoles` | **No** | via `User` → yes, indirectly | cascade both ways |
| `ApplicationUserClaim` | `UserClaims` | **No** | via `User` → yes, indirectly | cascade from user |
| `ApplicationUserLogin` | `UserLogins` | **No** | via `User` → yes, indirectly | cascade from user |
| `ApplicationUserToken` | `UserTokens` | **No** | via `User` → yes, indirectly | cascade from user |
| `DataProtectionKey` | `DataProtectionKeys` | **No** | **No** — installation-wide by nature, correctly so | `DataProtectionKeyConfiguration` |
| **`SystemLog`** | *(separate context)* | **No** | **No** | `LogDbContext`, separate database — see §2.3 |

**Summary: two of fifteen entities carry a tenant in their own right** (`Document`,
`ApplicationUser`), one is the join table, one is the tenant. The four that matter for the
isolation story — `AuditTrail`, `PicklistSet`, `ApplicationRole`, `SystemLog` — carry nothing and
reach nothing.

### 2.3 `SystemLog` — and the finding that reorders the whole plan

The brief predicted this would be the hard one. It is harder than predicted, but for a reason that
makes it **more urgent, not less**.

**What stamping it would require, measured:**

1. **A new column in three provider DDLs.** `LogTableDdl.cs` holds the column set as data —
   `SqliteColumns`, `SqlServerColumns`, `NpgsqlColumns` — three arrays, three additions
   (`TenantId` / `tenant_id`).
2. **A new sink writer, twice.** `SerilogExtensions.BuildSqlServerColumnOptions()`
   (`AdditionalColumns`, the `ClientIP`/`ClientAgent` idiom at `:263-278`) and
   `BuildNpgsqlColumnWriters()` (`:298-341`, a `SinglePropertyColumnWriter`). The SQLite sink takes
   the column from the same property.
3. **An enricher change.** `UserInfoEnricher` (`SerilogExtensions:450+`) already publishes
   `UserName`, `ClientIP` and `ClientAgent` from `IHttpContextAccessor`. A tenant would come from
   `IUserContextAccessor` (a singleton over `AsyncLocal` — see §4.1), which is the *better* source
   because it survives outside an HTTP context.
4. **A property on the entity**, plus `SystemLogDto`, plus the page's column.
5. **Three test suites move**: `LogTableDdlTests`, `SinkColumnDriftTests` (which pins the entity
   model against each sink's configuration, per provider), and `LogTableNamingTests`.

**The events with no user context are a real, permanent hole, not an edge case.** Startup logging,
`ApplicationDbContextInitializer`'s administrator-provisioning warning, Hangfire's server
heartbeats (`AddHangfireServer()` is registered — `Server.UI/DependencyInjection.cs:84`), and every
unhandled-exception log written after the circuit is gone all run with `Current == null`. Those
rows would carry a null tenant forever. **That is not fatal — it is a third partition, "the
installation's own events", which is honest — but it means a tenant administrator's log view can
never be complete, and the page has to say so rather than silently omitting them.**

**And here is the finding that matters most in the whole pass.** `LogTableDdl`'s own remarks state
it, and the README repeats it:

> **There is still no migration chain.** This creates a table and never alters one. A log database
> deployed before a property was added to `SystemLog` keeps its old columns, and the guards below
> will not touch it: **adding a property carries a manual ALTER on every deployed log database.**

So for `SystemLog` the "now versus later" question has a different answer from everything else.
The business schema regenerates its `InitialCreate` (three providers, one file each,
`20260831123533_InitialCreate.cs` and siblings) — a schema change today is a regeneration, tomorrow
a data migration, which is the Pass 14 `timestamptz` argument and it still holds. **The log
database has no such story at all.** Today, adding `TenantId` to `SystemLog` costs three array
entries, two writer entries, one enricher line and three test updates. After a customer is
deployed it costs a hand-written `ALTER TABLE` executed against every log database in the estate,
per provider, with no tooling and no guard — and until it runs, the application starts fine and
log writes fail.

### 2.4 `AuditTrail` — the compliance story is weaker than it sounds

`AuditTrail` (`Domain/Entities/AuditTrail.cs`) carries `UserId`, `AuditType`, `TableName`,
`DateTime`, `Changes`, `AffectedColumns`, `PrimaryKey`. **No tenant, and no relation that reaches
one** — `Owner` is an `ApplicationUser`, and while *that* has a `TenantId`, joining through it
records **the tenant the actor is in now, not the tenant the change was made in**. Those differ
the moment somebody uses the tenant switcher (§3.5), which mutates `ApplicationUser.TenantId`
in place. A join-based reconstruction would therefore silently rewrite history.

`AuditTrailAdvancedSpecification` filters on `AuditType`, the `My`/`TODAY`/`LAST_30_DAYS` list
views and a `TableName` keyword. **No tenant clause exists or could be written.** Any
`Permissions.AuditTrails.View` holder sees every tenant's change history, including the before/after
values stored in `Changes`.

This is the sharpest gap between what GX sells and what the template does. Pass 5 built a genuinely
good transactional audit — rows commit in the same transaction as the change they describe — and it
is currently **installation-wide**. On a revenue system sold on its audit trail, "your auditor can
read the other customer's field-level changes" is the finding that matters.

**Stamping cost: low, and lower now than later.** `AuditTrail` is created in exactly one place,
`AuditableEntityInterceptor.CreateAuditTrail`, which already holds `_userContextAccessor.Current`
in the same method (`GenerateAuditTrails` reads `currentUser?.UserId` two lines above). One
property, one assignment, one column in the regenerated `InitialCreate` ×3.

### 2.5 Identity entities — roles are global, and that is a design question

`ApplicationRole` has no `TenantId`, and `ApplicationRoleConfiguration` puts a **unique index on
`NormalizedName` across the whole installation**. The consequences are concrete:

- **Two tenants cannot both have a role called "Manager".** The second creation fails on the unique
  index. Tenant A's administrator naming a role takes that name away from tenant B.
- **A role's permission set is installation-wide.** Editing "Manager" in tenant A changes what
  "Manager" grants in tenant B. `PermissionAssignmentService` writes `RoleClaims`, which hang off
  the global role.
- **Role assignment is not itself tenant-crossing** (Pass 22 §4.1 got this right) — assigning a
  role to a user touches only that user — **but the roles list a tenant administrator picks from,
  and can edit, is everyone's.**

*Should* they be per-tenant? That is a product decision, and there is a defensible answer either
way: global roles keep one permission vocabulary across the installation, which is simpler to
support; per-tenant roles are what a customer expects when told they administer their own tenant.
**What is not defensible is the current middle**, where a tenant administrator holding
`Permissions.Roles.Edit` silently reconfigures another customer's authorization.

`ApplicationUser` carries **both** `TenantId` (the current/primary one) and a `TenantUsers`
many-to-many. That duality is deliberate and useful, but it is not consistently maintained — see
§3.3.

### 2.6 `PicklistSet` — a design question, not a defect

No tenant, `CacheScope.Global` on its by-name queries. As shipped these are reference data (Status,
Unit, Brand) seeded once, and sharing them is a reasonable default. It only becomes a defect once a
project adds a picklist whose *values* are customer data — which is exactly what a project using
this template will do. **Mark: design decision, with a documentation obligation.**

---

## 3. §B — What isolation means per surface

**D = defect** (the code claims or implies a boundary it does not hold).
**Q = product decision** (no boundary is claimed; someone has to choose).

| Surface | What a tenant administrator should see | Can the code express it today? | Verdict |
|---|---|---|---|
| Users grid | own tenant only, unless cross-tenant permission | **Yes** — `ApplicationUser.TenantId` exists; `_selectedTenantId` starts `string.Empty` so no filter applies | **D** |
| **User export** | same rows as the grid | **Yes** — shares `CreateSearchPredicate()`; leaks email + phone for every tenant | **D** |
| Tenant dropdown (Users page) | own/allowed tenants | **Yes** — `AllowedTenantIds` exists, unread; page binds `TenantService.DataSource` = all tenants | **D** |
| Tenant select (`TenantSelect.razor`) | own/allowed tenants | **Yes**, same | **D** |
| `PickSuperiorAutocomplete` | own tenant only | **Partly** — see §3.2 | **D** |
| `PickUserAutocomplete` | own tenant only | filter correct — **but the component has zero call sites** | dead code |
| User create | may not assign into a tenant the admin cannot see | assigns from the unrestricted `TenantSelect` | **D** |
| User edit — moving between tenants | a stated policy either way | **Neither** — see §3.3 | **D** (incoherent) |
| Roles list / edit | global today | `ApplicationRole` has no tenant; name is globally unique | **Q**, with a defect inside it (§2.5) |
| Audit trails | per-tenant | **No** — unstamped, and unreachable by relation (§2.4) | **D**, blocked on stamping |
| System logs | per-tenant + an "installation" partition | **No** — unstamped, separate DB, no migration chain (§2.3) | **D**, blocked on stamping |
| Log purge | per-tenant, or refused | `PurgeAsync` is `ExecuteDeleteAsync` over the whole table | **D** if logs are ever scoped; **Q** today |
| Picklists | shared reference data? | no tenant column | **Q** (§2.6) |
| Documents | own tenant only | **partly — and the parts that fail are the interesting ones** (§3.4) | **D** |
| Security settings (idle policy) | per-tenant, arguably | one row, constant cache key; README:456 **states this plainly** | **Q** — declared, not hidden |
| Hangfire / background jobs | n/a | server registered, **no jobs enqueued anywhere** (`BackgroundJob.`/`RecurringJob.` — zero hits; no `IHostedService`) | **no exposure today** |
| **SignalR presence & chat** | own tenant only | **No** — `Clients.All`, six broadcast sites (§3.6) | **D**, not in the brief's list |

### 3.1 Users grid and export — as Pass 22 found, confirmed

`Users.razor:316-327`. The predicate's last clause is
`(string.IsNullOrEmpty(_selectedTenantId) || x.TenantId == _selectedTenantId)` and
`_selectedTenantId` initialises to `string.Empty` (`:265`), so the default is no filter.
`ExportUsersAsync` (`:781-796`) calls the same `CreateSearchPredicate()` and projects
`Email`, `PhoneNumber`, `DisplayName`, `TenantId` into a spreadsheet. Nothing to add to Pass 22's
account; it is accurate.

### 3.2 Correction to Pass 22 §4.1 — the autocompletes do not hold

Pass 22 recorded `PickUserAutocomplete` / `PickSuperiorAutocomplete` as **"Yes — both filter on a
`TenantId` parameter."** That is true of the source and false of the running application:

- **`PickUserAutocomplete` has no call sites at all** (`grep '<PickUserAutocomplete'` over `src/`
  returns nothing). Its filter is correct and never runs. It is dead code.
- **`PickSuperiorAutocomplete` has exactly one call site — `UserFormDialog.razor:65-71` — and it
  does not pass `TenantId`.** Its predicate is
  `(x.TenantId != null && x.TenantId.Equals(TenantId) || TenantId == null)`, so with the parameter
  unset the clause is `|| true` and the filter is fully open. It searches
  `UserName` **or** `Email`, case-insensitively, over `UserService.DataSource` — every user in the
  installation — and renders the matching usernames.

So the "Superior" picker in the user dialog is a **live cross-tenant user directory search**,
reachable by any `Permissions.Users.Edit` or `Users.Create` holder. It belongs in the same finding
as the grid and the export, and Pass 22 scored it green.

This is the failure mode the brief's own instruction anticipates for Documents — *"verify it is
actually correct, not merely present"* — appearing in the Users area instead.

### 3.3 User edit — the tenant move is incoherent in a way worth naming

`UserFormDialog.razor:211-214`, on save of an **existing** user:

```csharp
if (string.IsNullOrEmpty(existingUser.TenantId) && Model.Tenants.Any())
{
    existingUser.TenantId = Model.Tenants.First().Id;
}
```

Then `:229-241` **unconditionally** deletes every `TenantUsers` row for that user and rewrites it
from `Model.Tenants`.

So editing a user's tenants rewrites the *membership* join completely, while `TenantId` — the
field every query in the template actually filters on, and the one the interceptor stamps new
`Document` rows with — is only ever written when it was empty. **An administrator moving a user
from tenant A to tenant B gets: membership B, primary tenant still A.** The user keeps creating
documents in A, keeps being found by A's grid filter, and now has an `AllowedTenantIds` of `[B]`
that nothing reads.

**This is a defect independent of isolation** — it is already wrong in a single-tenant-per-user
installation — and it will bite whichever isolation design is chosen, because it means the two
tenant sources disagree by construction. Fixing it is a prerequisite, not a consequence.

### 3.4 Documents — "already scoped" is half true, and the failing half is the dangerous half

The brief asked for this to be verified rather than accepted. It does not survive verification.

**What is correct:**

- `VisibleDocumentSpecification` — one shared definition of visibility, used by
  `GetFileStreamQueryHandler` and `FileEndpoints`. Genuinely good: the remarks explain that a
  security rule with two copies is a rule with one copy that is out of date, and that is right.
- `GetFileStreamQueryHandler` — fails closed on a null user context, refuses a caller asking on
  another principal's behalf, and reports invisible-but-existing exactly like missing so ids cannot
  be enumerated. This is careful code.
- `DocumentsWithPaginationQuery` declares `CacheScope.PerUserAndTenant`.

**What is not:**

**(a) Two of the four list views have no tenant clause and no owner clause.**
`AdvancedDocumentsSpecification`:

```csharp
Query.Where(p => (p.CreatedById == …UserId && p.IsPublic == false) ||
                 (p.IsPublic == true && p.TenantId == …TenantId),   filter.ListView == All)
     .Where(p => p.CreatedById == …UserId && p.TenantId == …TenantId, filter.ListView == My)
     .Where(x => x.CreatedAt >= todayrange…,  filter.ListView == TODAY)           // ← no tenant
     .Where(x => x.CreatedAt >= last30daysrange…, filter.ListView == LAST_30_DAYS) // ← no tenant
```

The `TODAY` and `LAST_30_DAYS` branches filter on date **only**. The list view is a dropdown on the
page (`Documents.razor:38-42`, `MudEnumSelect<DocumentListView>`), so **selecting "Created today"
in the Documents grid lists every tenant's documents created today, public and private, including
other users' private ones** — it is not even owner-scoped. The cache is
`PerUserAndTenant`, so the leak is not cached across principals; it is simply computed fresh for
each of them.

**(b) `DeleteDocumentCommand` resolves by id with no visibility check at all.**

```csharp
var items = await db.Documents.Where(x => request.Id.Contains(x.Id)).ToListAsync(…);
```

`Permissions.Documents.Delete` plus an id from another tenant deletes that tenant's document —
and, through `DocumentDeletedEvent`, its stored object.

**(c) `AddEditDocumentCommand` edits by id with no visibility check, and takes `TenantId` from the
request.** `FindAsync(request.Id)` then `_objectMapper.Map(request, existingDocument)`, where the
command carries `TenantId`. So a `Documents.Edit` holder can both edit another tenant's document
and **re-parent any document into any tenant**.

**The idiom does not generalise, and that is the actual answer to §C.3.** Documents scopes at the
*specification* layer, which is applied only where somebody remembered to apply it. Three of the
five entry points to the Documents feature — the two date list views, delete, and edit — do not
apply it. The one feature the template holds up as tenant-scoped is scoped on its read paths and
open on its write paths.

### 3.5 The tenant switcher — a second dead-permission finding

Pass 22 §4.2 found that `CanSwitchToTenantAsync` requires **both** `SwitchTenants` and
`SwitchToAnyTenant`, so the finer-grained permission is dead. Confirmed, and there is more:

```csharp
public async Task<bool> CanSwitchToTenantAsync(string userId, string tenantId)
{
    var hasSwitchPermission = await _permissionService.HasPermissionAsync(Permissions.Users.SwitchTenants);
    if (!hasSwitchPermission) return false;
    var hasAnyTenantPermission = await _permissionService.HasPermissionAsync(Permissions.Users.SwitchToAnyTenant);
    if (!hasAnyTenantPermission) return false;
    return true;
}
```

**Neither parameter is used.** It answers "may this principal switch tenants at all?", never "may
this principal switch to *this* tenant?" — which is what its name, its signature and its one caller
all say it answers. `SwitchToTenantAsync` then writes `user.TenantId = tenantId` for any
`tenantId` supplied, **with no membership check against `TenantUsers`**.

Today the only caller is `TenantSelector.razor`, which renders `UserProfile.AvailableTenants` —
mapped from `TenantUsers` (`MapsterConfiguration.cs:20`) — so the UI offers only legitimate
tenants and the exposure is contained by the UI. **That is precisely the shape of thing tenant
scoping must not be built on top of:** the moment any other caller appears, or any parameter
becomes attacker-influenced, membership is unchecked. And this method is the natural place for a
"may see across tenants" check to be reused from, which is what Pass 22 proposed.

Also worth stating: **switching tenants is a persistent write to `ApplicationUser.TenantId`**, not
a session-scoped view change. That is why §2.4's audit-trail-by-join reconstruction cannot work.

### 3.6 SignalR — a leak the brief's table does not list

`ServerHub` broadcasts to **`Clients.All`** at six sites: `Connect`/`Disconnect` (username),
`SendMessage` (chat), `SendNotification`, `PageComponentOpened`/`PageComponentClosed` (user id,
username and which page they are on). `UserLoginState.razor` gates *rendering* the login/logout
snackbars on `Permissions.Users.ViewOnlineStatus` — but the messages are delivered to every
connected circuit regardless, and the presence tracker consumes them.

So a tenant administrator with `ViewOnlineStatus` watches another tenant's users sign in, sign out
and navigate. No query filter reaches this: it needs SignalR **groups keyed by tenant**, joined in
`OnConnectedAsync` from the connection's principal. `UserContextHubFilter` already exists and
already establishes the user context on hub calls, so the hook point is there.

---

## 4. §C — The machinery that already exists

### 4.1 `AllowedTenantIds` — what it contains, and the trap in it

`UserContextLoader.cs:74-77`:

```csharp
var allowedTenantIds = await userManager.Users.Where(x => x.Id == user.Id)
    .Include(x => x.TenantUsers).ThenInclude(tu => tu.Tenant)
    .SelectMany(x => x.TenantUsers.Where(tu => tu.Tenant != null).Select(tu => tu.Tenant!.Id))
    .ToListAsync(ct);
```

- **Contents:** the ids of every tenant the user has a `TenantUsers` row for, whose `Tenant`
  navigation resolves. **It is sourced *only* from the join table.**
- **Population:** inside `LoadAsync`, cached under `UserCacheKeys.GetCacheKey(userId, Context)`
  for `ContextCacheDuration` = **1 hour** (a genuine "no such user" caches for 1 minute).
  `ClearUserContextCache(userId)` invalidates; `TenantSwitchService` calls it.
- **A user with no tenant rows gets an empty list — not null.** The record's parameter is
  `IReadOnlyList<string>? AllowedTenantIds = null`, so `null` means "context built some other way"
  and `[]` means "genuinely belongs to nothing". Any consumer must distinguish them.
- **Read nowhere.** Confirmed: the only occurrences are the record declaration and this assignment.

**The trap:** `AllowedTenantIds` does **not** include `user.TenantId`. The two are independent, and
§3.3 shows the code lets them diverge. A user whose `TenantId` is set but who has no `TenantUsers`
row — which is what `IdentityComponentsEndpointRouteBuilderExtensions` external provisioning and
several edit paths can produce — gets `AllowedTenantIds == []` while `TenantId` is populated. If
scoping is written as `AllowedTenantIds.Contains(x.TenantId)`, **that user sees nothing, including
their own tenant.** If it is written as `TenantId == current || AllowedTenantIds.Contains(...)`,
the union is right but two sources of truth persist.

**Recommendation: make `AllowedTenantIds` the single answer, computed as the union of the join rows
and `user.TenantId`, before anything consumes it.** One line in the loader, and it removes the
divergence §3.3 creates rather than papering over it.

### 4.2 Global query filter versus per-query predicates

**The mechanism is already present and already used.** `ModelBuilderExtensions.ApplyGlobalFilters<T>`
reflects over the model, finds every entity implementing the marker, and calls `HasQueryFilter`.
`ApplicationDbContext.OnModelCreating` invokes it:
`builder.ApplyGlobalFilters<ISoftDelete>(s => s.DeletedAt == null)`.

**Two facts make the ground unusually clean:**

1. **No entity implements `ISoftDelete`.** `BaseAuditableSoftDeleteEntity` exists and nothing
   derives from it. So the one live global filter is a no-op today — the mechanism is proven to
   compile and to be invoked, and has never actually filtered a row.
2. **There are zero `IgnoreQueryFilters` call sites** in `src/` or `tests/`. Nothing currently
   depends on bypassing a filter, so adding one breaks no existing bypass — but it also means
   every bypass a tenant filter needs would be new, and new bypasses are where filters go wrong.

**Feasibility, measured:**

- `IUserContextAccessor` is registered **singleton** (`Infrastructure/DependencyInjection.cs:610`)
  over an `AsyncLocal` stack. It is safely injectable into `ApplicationDbContext`'s constructor and
  reads correctly from any call chain.
- `AddDbContextFactory<ApplicationDbContext>` is registered with `ServiceLifetime.Scoped`
  (`:129-135`), so constructor injection into the context works.
- `AuditableEntityInterceptor` already takes the accessor, so precedent exists for the persistence
  pipeline knowing the current tenant.

**What a global filter would break — the honest list:**

| Site | Breakage | Bypass needed |
|---|---|---|
| `Tenants.razor` / `TenantsWithPaginationQuery` | the tenant admin page must list all tenants | yes |
| `TenantDataSourceService` | feeds `TenantSelect`; must show all for a cross-tenant holder | yes, conditionally |
| `ApplicationDbContextInitializer` | `EnsureDefaultTenantAsync`, `EnsureAdministratorAsync`, `SeedSampleTenantAsync` all run with `Current == null` | yes |
| `UserContextLoader` | loads a user's own tenants — filtering it on the tenant it is computing is circular | yes |
| `TenantSwitchService` | must find the target tenant before switching to it | yes |
| `AdministratorProtectionService` | "is this the last administrator?" is an installation-wide question | yes |
| `UserDataSourceService`, `RoleDataSourceService` | see §4.3 — worse than a bypass | **redesign** |
| Login / `SignInManager` / `UserManager.FindBy*` | Identity resolves users before any tenant is known | **yes, and this is the hard one** |

**The last row is the one that decides the architecture.** A global filter on `ApplicationUser`
runs inside every `UserManager` call, including the ones that happen *before* a principal exists —
`FindByEmailAsync` during login, `FindByIdAsync` during password reset, the confirmation-token
lookups. With `Current == null` the filter either matches nothing (**nobody can log in**) or
no-ops (**open by default**, the wrong failure mode for a security control). Getting that right
means `ApplicationUser` is filtered by something other than a naive global filter, whatever else is.

### 4.3 The cache layer is the constraint nobody has costed

**This is the finding that most changes the estimate, and it is not in the brief.**

`DataSourceServiceBase<T>` backs `TenantSelect`, `PickSuperiorAutocomplete`, the Users page's role
and tenant dropdowns, and the picklist selectors. It caches through `IFusionCache` under a
**constant key with no principal dimension**:

- `UserDataSourceService` → `"ALL-ApplicationUserDto"`
- `TenantDataSourceService` → `TenantCacheKey.TenantsCacheKey`

and holds the loaded list in an instance field `Items`.

The template *has* the right abstraction for this — `CacheScope` (`Global` / `PerUser` /
`PerTenant` / `PerUserAndTenant`), documented in README:386-392 with **"`PerUserAndTenant` is the
right default when in doubt"** — but it applies only to MediatR `ICacheableRequest`s. **The
datasource services predate it and bypass it entirely.**

The consequence is sharp: **if `ApplicationUser` gains any tenant filter — global, specification,
or otherwise — `UserDataSourceService` caches the first tenant's result under a process-wide key
and serves it to the next tenant.** That is worse than today's behaviour, because today's
behaviour is at least consistently wrong; the cached version is intermittently wrong and will not
reproduce.

Current declared scopes, and where they must move if their area is scoped:

| Query | Declares | Must become |
|---|---|---|
| `DocumentsWithPaginationQuery` | `PerUserAndTenant` | correct already |
| `GetFileStreamQuery` | `PerUserAndTenant` | correct already |
| `AuditTrailsWithPaginationQuery` | `PerUser` | `PerUserAndTenant` |
| `PicklistSetsWithPaginationQuery` | `PerUser` | `PerUserAndTenant` if picklists are scoped |
| `PicklistSetsQueryByName`, `GetAllPicklistSetsQuery` | `Global` | `PerTenant` if picklists are scoped |
| `SystemLogsWithPaginationQuery`, `SystemLogsChatDataQuery` | `Global` | `PerTenant` if logs are scoped |
| `TenantsWithPaginationQuery`, `GetAllTenantsQuery` | `Global` | `PerUser` (an admin's visible tenants differ) |
| `UserDataSourceService`, `TenantDataSourceService`, `RoleDataSourceService` | **no scope concept** | **must adopt `CacheScope`** |

### 4.4 Where tenant scoping belongs — the recommendation

Four candidate layers, with the trade-offs the brief asked for:

**1. Specification layer** (where Documents does it). *For:* matches the house idiom; visible at
the query; easy to reason about one query at a time. *Against:* **§3.4 is the measurement of this
approach's failure mode** — it is opt-in, and three of five Documents entry points opted out. It
also does not exist for the Users area at all: `Users.razor` queries `_userManager.Users` directly
with an inline `Expression`, no specification anywhere.

**2. DbContext global filter.** *For:* closes by default; the one place that cannot be forgotten;
`ApplyGlobalFilters<T>` already exists and is already called. *Against:* the bypass list in §4.2,
and Identity's pre-authentication lookups make `ApplicationUser` genuinely hard. Also invisible at
the call site, which is a real maintenance cost — a query returning fewer rows than expected has no
local explanation.

**3. Authorization behaviour** (Pass 4B's `RequestAuthorize` pipeline). *For:* already
deny-by-default and already covers every `ICacheableRequest`. *Against:* **it authorizes requests,
it does not filter rows.** It can refuse a whole query; it cannot make a list query return a subset.
It is the right place for "may this principal act across tenants?" and the wrong place for "which
rows".

**4. Split by entity class.** What the evidence actually supports.

**Recommendation — a split, and it falls out of the measurements rather than being a compromise:**

- **`Document`, `AuditTrail`, `PicklistSet`, and every future project entity** → **global query
  filter**, keyed off `IMayHaveTenant`/`IMustHaveTenant`, which already exist and which the
  interceptor already stamps on insert. This is the case the mechanism was built for: business
  entities, never touched before authentication, with one obvious bypass site each. **It also fixes
  §3.4(b) and (c) for free** — `FindAsync`/`Where(id)` on a filtered set cannot reach another
  tenant's row, which per-query predicates would have had to be remembered at every write path.
- **`ApplicationUser`** → **explicit predicates at the four surfaces** (grid, export, `TenantSelect`,
  `PickSuperiorAutocomplete`), because Identity's pre-authentication lookups make a global filter
  unsafe here and the surfaces are few and enumerable. Pass 22's estimate of the shape was right;
  it just missed the autocomplete.
- **`ApplicationRole`** → **no filter.** Answer §2.5's product question first; a filter on an
  unstamped, globally-unique-named entity is not implementable regardless.
- **Cross-tenant escape** → the **authorization behaviour**, consulted once and carried on
  `UserContext`, not re-evaluated per query.
- **The cache layer** → `CacheScope` extended to `DataSourceServiceBase`, **before** any of the
  above ships (§4.3).

---

## 5. §D — The permission audit

Every `Permissions.*` constant, and its enforcement. **64 constants.**

**Enforcement kinds.** *Handler* = `[RequestAuthorize(Policy = …)]` on a MediatR request, checked
server-side by Pass 4B's behaviour. *Page* = `@attribute [Authorize(Policy = …)]`, checked by the
router. *UI* = an `*AccessRights` property read in a Razor `@if`, or a direct `AuthorizeAsync`
call, gating rendering only. *Inert* = appears only in `AdministratorPermissionRegistry` (a grant
list) and in `PermissionService`'s reflection, and gates nothing anywhere.

| Permission | Handler | Page | UI | Verdict |
|---|---|---|---|---|
| `NavigationMenu.View` | — | — | — | **Inert** |
| `Hangfire.View` | — | — | `HangfireDashboardAuthorizationFilter` ×2 | **Enforced** (dashboard filter — a real server-side gate, not a render gate) |
| `EmailTemplates.View/Create/Edit/Delete` | — | — | — | **Inert ×4** — knowingly so; the registry excludes them with a stated reason (no such page exists) |
| `Dashboards.View` | — | — | — | **Inert** |
| `Roles.View` | — | `Roles.razor` | — | **Enforced (page)** |
| `Roles.Create` | — | — | `Roles.razor:46` | UI only |
| `Roles.Edit` | — | — | `:98, :103` | UI only |
| `Roles.Delete` | — | — | `:53, :98, :107` | UI only |
| `Roles.Search` | — | — | `:84` | UI only |
| `Roles.Import` | — | — | `:67` | UI only |
| `Roles.Export` | — | — | `:61` | UI only |
| `Roles.ManagePermissions` | — | — | `:98, :111` | UI only |
| `Roles.ManageClaimsInRole` | — | — | — | **Inert** |
| `Roles.ManageUsersInRole` | — | — | — | **Inert** |
| `Roles.ViewPermissions` | — | — | — | **Inert** |
| `Roles.ViewClaimsInRole` | — | — | — | **Inert** |
| `Roles.ViewUsersInRole` | — | — | — | **Inert** |
| `Users.View` | — | `Users.razor` | — | **Enforced (page)** |
| `Users.Create` | — | — | `Users.razor:64` | UI only |
| `Users.Edit` | ×1 request | — | `:128, :134` | **Enforced (handler)** + UI |
| `Users.Delete` | — | — | `:70, :138` | UI only |
| `Users.Search` | — | — | `:103` | UI only |
| `Users.Import` | — | — | `:86` | UI only |
| `Users.Export` | — | — | `:80` | UI only |
| `Users.ManageRoles` | — | — | `:128` | UI only |
| `Users.RestPassword` | — | — | `:129, :150` | UI only |
| `Users.SendRestPasswordMail` | — | — | `:129, :146` | UI only |
| `Users.ManagePermissions` | — | — | `:128, :142` | UI only |
| **`Users.Deactivation`** | — | — | — | **Inert** — Pass 22 finding 3, confirmed. The activate/deactivate toggle at `Users.razor:201-222` checks **nothing**; it is reachable by any `Users.View` holder |
| `Users.ViewOnlineStatus` | — | — | `UserLoginState.razor:30` | UI only — and see §3.6 |
| `Users.SuppressLoginNotification` | — | — | `UserLoginState.razor:31` | UI only |
| `Users.SwitchTenants` | — | — | `TenantSwitchService:107`, `TenantSelector:100` | **Enforced (service)** |
| **`Users.SwitchToAnyTenant`** | — | — | `TenantSwitchService:112` only | **Dead as written** — Pass 22 finding 4, confirmed and extended (§3.5) |
| `AuditTrails.View` | `AuditTrailsWithPaginationQuery` | `AuditTrails.razor` | — | **Enforced (both)** |
| `AuditTrails.Search` | — | — | — | **Inert** |
| `AuditTrails.Export` | `ExportAuditTrailsQuery` | — | — | **Enforced (handler)** |
| `Documents.View` | `DocumentsWithPaginationQuery` | `Documents.razor` | — | **Enforced (both)** |
| `Documents.Create` | `AddEditDocumentCommand`, `UploadDocumentCommand` | — | `:54` | **Enforced (handler)** |
| `Documents.Edit` | `AddEditDocumentCommand` | — | `:87, :92` | **Enforced (handler)** |
| `Documents.Delete` | `DeleteDocumentCommand` | — | `:61, :87, :98` | **Enforced (handler)** |
| `Documents.Download` | `GetFileStreamQuery` (+ `FileEndpoints`) | — | `:104` | **Enforced (handler + endpoint)** |
| `Documents.Search` | — | — | `:70` | UI only |
| `Documents.Export` | — | — | — | **Inert** |
| `Documents.Import` | — | — | — | **Inert** |
| `PicklistSets.View` | ×3 queries | `PicklistSets.razor` | — | **Enforced (both)** |
| `PicklistSets.Create` | `AddEdit…Command` | — | `:49` | **Enforced (handler)** |
| `PicklistSets.Edit` | `AddEdit…Command` | — | — | **Enforced (handler)** |
| `PicklistSets.Delete` | `Delete…Command` | — | `:56, :116, :119` | **Enforced (handler)** |
| `PicklistSets.Search` | — | — | `:93` | UI only |
| `PicklistSets.Export` | `Export…Query` | — | `:66` | **Enforced (handler)** |
| `PicklistSets.Import` | `Import…Command` | — | `:73` | **Enforced (handler)** |
| `SecuritySettings.View` | ×1 query | `SecuritySettings.razor` | — | **Enforced (both)** |
| `SecuritySettings.Edit` | ×1 command | — | `AuthorizeView` `:66` | **Enforced (handler)** |
| `Logs.View` | ×2 queries | `SystemLogs.razor` | — | **Enforced (both)** |
| `Logs.Search` | — | — | — | **Inert** |
| `Logs.Purge` | `ClearSystemLogsCommand` | — | `:45` | **Enforced (handler)** |
| `Tenants.View` | ×2 queries | `Tenants.razor` | — | **Enforced (both)** |
| `Tenants.Create` | `AddEditTenantCommand` | — | `:38` | **Enforced (handler)** |
| `Tenants.Edit` | `AddEditTenantCommand` | — | `:74, :83` | **Enforced (handler)** |
| `Tenants.Delete` | `DeleteTenantCommand` | — | `:45, :74, :89` | **Enforced (handler)** |
| `Tenants.Search` | — | — | `:55` | UI only |

### 5.1 Tally, and what it means for building on top of this

| | Count |
|---|---:|
| Enforced in a handler (or an equivalent server-side filter) | **22** |
| Enforced by a page `[Authorize]` only | 2 (`Roles.View`, `Users.View`) |
| **UI rendering gate only** | **25** |
| **Inert — nothing anywhere** | **15** |

**Fifteen inert permissions.** Four are knowingly inert and documented (`EmailTemplates.*`).
**Eleven are not:** `NavigationMenu.View`, `Dashboards.View`, `Users.Deactivation`, the five
`Roles.*` view/manage constants, `AuditTrails.Search`, `Documents.Export`, `Documents.Import`,
`Logs.Search`. Each appears in the role editor as a grantable, revocable right and changes nothing
when revoked. That is a false statement about what the system enforces, made 11 times.

**Twenty-five UI-only.** In Blazor Server a render gate is genuinely load-bearing — there is no
separate HTTP endpoint to bypass, and the whole Users administration area is written this way,
calling `UserManager` directly from `Users.razor` with no MediatR request in between. But it is
weaker than a server-side check in one specific way that matters here: **it protects the button,
not the query.** `Users.razor`'s own `LoadServerData` and `ExportUsersAsync` are exactly that
shape — the export button is gated on `Users.Export`, and the query behind it is gated on nothing.

**The consequence for the design, and it is the point of running this sweep:** Pass 22 proposed
leaning tenant scoping on `SwitchToAnyTenant`. **That permission is dead as written** (§3.5), and
the two permissions closest to it, `Users.Deactivation` and the whole `Roles.*` manage/view family,
are inert. Building an isolation boundary on this permission set without first repairing it means
the boundary's escape hatch inherits the same defect — and unlike a missing render gate, an
isolation escape hatch that is wrong in the permissive direction is a data breach.

---

## 6. §E — Blast radius and cost

### 6.1 "Scope everything that can be scoped"

**Schema.** `TenantId` on `AuditTrail`, `PicklistSet`, `SystemLog`; a decision on `ApplicationRole`;
optionally `SecurityPolicy`. Business side: **regenerate `InitialCreate` ×3 providers** — the same
operation Pass 14 and the idle-timeout work already performed, well-understood, cheap **today**.
Log side: **three DDL arrays + two sink writer sets + the enricher**, and after deployment a manual
`ALTER` per log database with no migration chain (§2.3).

**Queries.** Roughly: `AuditTrailAdvancedSpecification`, `SystemLogAdvancedSpecification`,
`PicklistSetAdvancedSpecification`, `AdvancedDocumentsSpecification` (fixing the two date views),
`DeleteDocumentCommand`, `AddEditDocumentCommand`, `Users.razor` ×2 (grid + export),
`TenantSelect.razor`, `PickSuperiorAutocomplete`, `TenantDataSourceService`, `UserDataSourceService`,
`TenantsWithPaginationQuery`. **~14 query sites**, plus the global-filter bypasses in §4.2's table
(**~8 more**).

**Caching.** Eight `CacheScope` declarations to revisit, and `DataSourceServiceBase` to give a scope
concept it does not have. **This is not optional and it is not small** (§4.3).

**Not reachable by scoping at all:** `ServerHub`'s six `Clients.All` broadcasts need tenant groups
(§3.6); `PurgeAsync` needs a predicate or a refusal; `ApplicationRole`'s global unique name needs a
schema decision.

**Prerequisites that are defects in their own right:** §3.3 (the `TenantId`/`TenantUsers`
divergence), §3.5 (`CanSwitchToTenantAsync` ignoring both parameters), and §5's inert permissions.
**All three must be fixed before scoping, not after** — each is a source of truth the scoping would
otherwise consume while it is wrong.

**Generated project's behaviour.** Every project generated from the template inherits an
`IMayHaveTenant` filter that silently narrows its own entities' queries. That has to be documented
prominently, with the `IgnoreQueryFilters` escape named — today there is not one such call site to
copy from.

### 6.2 "Scope Users and Documents properly, document the rest"

**Users:** `CreateSearchPredicate` (grid + export share it), `TenantSelect`,
`PickSuperiorAutocomplete`'s call site, `UserDataSourceService`'s cache key. **4 sites.**

**Documents:** the two date list views in `AdvancedDocumentsSpecification`, plus the visibility
check missing from `DeleteDocumentCommand` and `AddEditDocumentCommand`. **4 sites** — and note
these are *repairs to a boundary the template already claims*, so they are owed regardless of the
answer to the wider question.

Plus the three prerequisites above, plus a README statement.

**But Pass 22's own warning applies with more force now than when it was written.** It said a
partial boundary is worse than a stated absence. §2.4 sharpens that: with Users scoped and
`AuditTrails` not, a tenant administrator who cannot list another tenant's users can still read
their **field-level change history**, including the values. The boundary would be visibly, checkably
false from inside the product.

**So option 6.2 is only honest if the README says exactly which surfaces hold** — and that
statement, written truthfully, reads: *"user administration and documents are tenant-scoped; audit
trails, system logs, roles, picklists, presence and chat are installation-wide."* Whether that is
sellable is Yoab's call, not the code's.

### 6.3 What an isolation test looks like — and the harness is ready

**The harness can already create two tenants and two administrators cheaply.** Evidence:

- **The seeder already makes two tenants.** `EnsureDefaultTenantAsync` creates the default;
  `SeedSampleTenantAsync` creates **"Europe"** and enrols every administrator in it
  (`ApplicationDbContextInitializer:340-366`). It is called from `SeedSampleDataAsync`, which since
  Pass 7-3 is no longer behind an `IsDevelopment()` gate.
- **Two-tenant fixtures already exist in unit tests**, with the exact shape a scoping test needs:
  `FileEndpointsAuthorizationTests` (`TenantId = "tenant-1"`, `OtherTenantId = "tenant-2"`, users in
  each), `UserRoleChangeSecurityStampTests` (`TenantA`/`TenantB` plus `TenantUsers` rows),
  `ApplicationUserProjectionTests`.
- **`GxWebApplicationFactory`** boots the real pipeline over throwaway SQLite and exposes
  `BusinessConnectionString`; `CookieLogin` drives real cookie sign-in. A second administrator is a
  `UserManager.CreateAsync` + `AddToRoleAsync` in the fixture.

**Shape, per area:** create tenant A and tenant B; an administrator in each holding the same
permissions; one row of the area's entity in each; sign in as A's administrator and assert the
listing, the **export**, and the by-id fetch all fail to reach B's row. **Assert on the export
separately from the grid** — that is the surface Pass 22 identified as the one a partial fix
misses, and in this template they share a predicate, so a test that only reads the grid would pass
while the export leaked if they were ever separated.

Cost: **low.** Nothing new is needed in the harness.

### 6.4 Interaction with work already done

| Prior work | Interaction |
|---|---|
| **Audit interceptor** (Pass 5) | **Positive.** Already holds `IUserContextAccessor.Current` and already stamps `IMayHaveTenant`. Stamping `AuditTrail` is one assignment in a method that already has the value in scope |
| **Log database** (Pass 11/11B/14B) | **The binding constraint.** No migration chain; sink/DDL/enricher move together or the write fails; `SinkColumnDriftTests` pins all three (§2.3) |
| **Storage keys** | Neutral. `StorageKey` is `{UploadType}/{Folder?}/{FileName}`, no tenant segment. Isolation comes from `Documents` rows, so §3.4(b)'s unchecked delete is also a **cross-tenant blob delete** — fixing the query fixes the storage exposure |
| **Idle policy** (Pass 16A) | Already **declared** installation-wide in README:456, with the "migration plus a cache key" path written down. Unchanged by this pass; it is the model for how to state a limit honestly |
| **`core`/`TBL_` naming** | Neutral for the template's own tables (`ToTable` pins them out of `core`). A generated project's `core."TBL_*"` entities pick up an `IMayHaveTenant` filter automatically if they implement the marker — which is the desired behaviour and needs documenting |
| **`CacheScope`** (README:386-392) | **Right abstraction, wrong coverage.** Extending it to `DataSourceServiceBase` is a prerequisite, not a follow-up (§4.3) |
| **`RequestAuthorize` pipeline** (Pass 4B) | Right layer for the cross-tenant *escape*, wrong layer for row filtering (§4.4) |
| **`AdministratorPermissionRegistry`** | Any new permission fails startup and the test run until explicitly granted or excluded. So a `Users.ViewAllTenants` constant cannot be added silently — **this is working as intended and is a help, not a friction** |

---

## 7. §F — Recommendation

### 7.0 The three things that decide everything else

1. **The defect is real and wider than Pass 22 measured.** Users, the user export and the tenant
   dropdown, yes — plus a **live cross-tenant user search in the user-edit dialog** (§3.2), plus
   **two Documents list views and both Documents write paths** in the one area the template holds
   up as scoped (§3.4), plus **installation-wide presence and chat broadcast** (§3.6). The audit
   trail — the thing GX sells on revenue systems — is installation-wide and **cannot be scoped at
   all until it is stamped** (§2.4).

2. **One decision is time-sensitive and the rest are not.** The business schema regenerates its
   `InitialCreate`, so stamping `AuditTrail` and `PicklistSet` is cheap today and a data migration
   later — the Pass 14 argument, still sound. **The log database has no migration chain at all.**
   Stamping `SystemLog` today is ~6 small edits and 3 test updates; after the first customer
   deployment it is a hand-written `ALTER TABLE` per log database per provider, unguarded,
   untooled, with the application starting happily and log writes failing.

3. **Nothing should be built on the current permission set.** `SwitchToAnyTenant` is dead,
   `CanSwitchToTenantAsync` ignores both its parameters and checks no membership, and 11 permissions
   are inert without knowing it. An isolation escape hatch built on that inherits the defect.

### 7.1 Pass 22's sequencing: amended

Pass 22 proposed: (1) settle single vs multi-tenant → **done, ratified**; (2) fix the dead switch
permission; (3) scope Users grid and export together; (4) then the remaining areas; (5) a harness
test per area.

**The shape is right. Three amendments, each forced by evidence in this pass:**

- **Stamping moves to the front.** Not because stamping is urgent in itself, but because *the
  window in which it is cheap* closes at first deployment (§2.3). It is also a pure schema change
  with no behaviour attached, so it can land before any policy decision is made and commits to
  nothing.
- **Two prerequisites join step 2.** §3.3's `TenantId`/`TenantUsers` divergence and §4.1's
  `AllowedTenantIds` union. Both are sources of truth the scoping would consume while they are
  wrong.
- **Documents' own repairs are not "the remaining areas" — they are owed now.** §3.4(b) and (c)
  are cross-tenant *write* access in the area the template claims is scoped, and (b) deletes the
  stored blob too. They are not part of an isolation programme; they are a bug in shipped code.

### 7.2 The staged plan

**Stage 0 — Stamp, while it is free. (small; must precede a customer deployment)**
`TenantId` on `AuditTrail` (one assignment in `AuditableEntityInterceptor`, which already holds the
value), `PicklistSet`, and `SystemLog` (three DDL arrays, two sink writer sets, the enricher, three
test suites). Regenerate `InitialCreate` ×3. **No filtering, no behaviour change** — columns
written and unread. Stage 0 commits to nothing and can be reverted by ignoring the columns; not
doing it commits to a manual `ALTER` on every deployed log database, forever.

**Stage 1 — Repair the ground. (small–medium)**
`CanSwitchToTenantAsync` to use its parameters and check `TenantUsers` membership, with
`SwitchToAnyTenant` as the documented escape (§3.5). The `TenantId`/`TenantUsers` divergence in
`UserFormDialog` (§3.3). `AllowedTenantIds` as the union of the join and `user.TenantId` (§4.1).
`Permissions.Users.Deactivation` wired to the activate/deactivate toggle, or deleted. Each of these
is independently correct and none needs the isolation decision made.

**Stage 2 — Fix Documents, because it is already broken. (small)**
Tenant + owner clauses on the `TODAY`/`LAST_30_DAYS` list views; visibility checks in
`DeleteDocumentCommand` and `AddEditDocumentCommand`; refuse a client-supplied `TenantId` that
re-parents a document. **A test per repair.** This is bug-fixing, not programme work.

**Stage 3 — The cache layer, before any user-facing scoping. (medium)**
`CacheScope` extended to `DataSourceServiceBase`. §4.3 is the reason: scoping `ApplicationUser`
without this makes the system intermittently wrong, which is worse than consistently open.

**Stage 4 — Scope Users. (medium)**
Grid and export together — they share `CreateSearchPredicate`, so they cannot diverge. Plus
`TenantSelect`, plus `PickSuperiorAutocomplete`'s call site (§3.2). Escape via the Stage 1
permission. Harness test asserting grid **and export** separately (§6.3).

**Stage 5 — Global filter for business entities. (medium)**
`ApplyGlobalFilters<IMayHaveTenant>`, with the §4.2 bypass list made explicit and each bypass
justified in a comment — the template's existing discipline. `Document`, `AuditTrail`,
`PicklistSet` (if scoped) come in together. **`ApplicationUser` stays out** (§4.4).

**Stage 6 — Presence and chat. (small–medium)**
Tenant groups in `ServerHub`, joined in `OnConnectedAsync`. `UserContextHubFilter` is the hook.

**Stage 7 — Harness test per area, then the README. (small)**
The README paragraph is written **last and truthfully**, listing exactly which surfaces hold.

### 7.3 What makes full isolation impractical — stated plainly

**Nothing makes it impossible. Four things make parts of it disproportionate, and each has an
honest alternative.**

1. **The log database, if Stage 0 is skipped.** With no migration chain, retrofitting `TenantId`
   after deployment is manual `ALTER TABLE` per database per provider. **If Stage 0 is skipped, do
   not scope logs later — say so instead**, and keep `Permissions.Logs.View` an installation-wide
   right whose description says so. That is a defensible product position: log access is an
   operator capability, not a tenant-administrator one.

2. **Untenanted log events.** Startup, seeding, Hangfire heartbeats and post-circuit exceptions
   have no tenant and never will. Even fully stamped, a tenant's log view is necessarily
   incomplete. **The page must say "installation events are not shown"** rather than silently
   omitting them.

3. **`ApplicationRole`.** A global unique name means two tenants cannot share a role name, and a
   filter cannot be added without deciding whether roles are per-tenant. **Recommend: leave global,
   and restrict *editing* to a holder of a cross-tenant right** — a tenant administrator who can
   assign roles but not redefine them is coherent, and much cheaper than per-tenant roles.

4. **`SecurityPolicy` / idle timeout.** One row by design, already declared in README:456. **Leave
   it.** It is the template's best existing example of a limit stated rather than hidden, and it
   should be the model for how §7.3's other answers are written down.

### 7.4 What the template should claim

Today README:4 says the template ships *"multi-tenant ASP.NET Core Identity"*. That is true of the
data model and false of the isolation, and it is the sentence a customer would quote back.

**Before the stages land**, it should read something like: *multi-tenant data model — tenants,
per-user tenant membership, and tenant-stamped documents. Tenant isolation is not enforced in the
administration surfaces: any holder of the relevant permission sees every tenant's users, audit
trails, logs, roles and picklists.*

**After Stage 5**, it should name the boundary and its exceptions rather than claiming a whole one.
An accurate list of where the boundary holds is worth more than a boundary that half-holds — and
this template already knows how to write that paragraph, because README:456 is one.

**Then STOP.** Nothing was built.

---

## 8. Scratch probe disclosure

**No scratch probes were created, inside or outside the repository.** Every finding in this report
comes from reading the working tree at HEAD plus two read-only commands: `dotnet build` and
`dotnet test --no-build`, both of which write only to `bin/` and `obj/` (already git-ignored).
`git status --porcelain` is empty at the end of the pass, as it was at the start.

The only file this pass creates is this report.

---

## 9. Anomalies

**A1 — `ExportDocumentsQuery.cs` and `GetAllDocumentsQuery.cs` are empty files.** Three bytes each
(a BOM and a space). They are the only two files in `Features/Documents/Queries/` with no content,
and their directories exist solely to hold them. Related: `Permissions.Documents.Export` and
`Documents.Import` are both **inert** (§5) — the constants outlived the queries. Same class of
residue Pass 11B/11C cleared for `Logs.Export`, and it wants the same treatment.

**A2 — `PickUserAutocomplete` has zero call sites.** Dead component. Its tenant filter is the
correct one; the component that *is* used (`PickSuperiorAutocomplete`) has the permissive
`|| TenantId == null` variant and is called without the parameter. Deleting the dead one and fixing
the live one are the same small change.

**A3 — `ISoftDelete` has no implementors.** `ApplyGlobalFilters<ISoftDelete>` runs on every model
build and filters nothing; `BaseAuditableSoftDeleteEntity` is unreferenced. Harmless, and useful —
it means the global-filter mechanism is proven to be invoked without any live filter to disturb.

**A4 — Zero `IgnoreQueryFilters` call sites** anywhere in `src/` or `tests/`. Worth recording
before Stage 5 adds the first ones: today there is no example to copy, so the first bypass sets
the idiom for every one after it.

**A5 — `SetCreationAuditInfo(IAuditableEntity, string userId, string tenantId, DateTime)`** declares
non-nullable `string` parameters and is called with `currentUser?.UserId` / `currentUser?.TenantId`,
both of which are null during seeding and background work. It works — the body only compares and
assigns — but the signature states a guarantee the call site does not provide, and Stage 0 will add
a third such assignment to the same method.

**A6 — `LogsAccessRights` and `Permissions.Logs` are kept in sync by hand,** with comments in both
files explaining that `PermissionService` manufactures the claim string from the *property name*.
That mechanism is load-bearing and undefended: an `AccessRights` property with no matching constant
produces a claim no role can hold, silently. `AdministratorPermissionRegistry` catches the inverse
direction (a constant nobody decided about) but not this one. Not this pass's work; worth a test.

**A7 — Pass 22 §4.1 scored `PickUserAutocomplete`/`PickSuperiorAutocomplete` as tenant-scoped.**
Corrected in §3.2. Recorded here rather than only in the body because it is the second time in two
passes that a scoping claim held in the source and failed at the call site — first the Documents
specification (§3.4), now the autocompletes. **That pattern is the argument for Stage 5's global
filter over per-query predicates**, and it is worth more than either individual finding.
