# Pass 60: controls and structure (plan)

**Status:** written 2026-11-11 at HEAD `4c33d6a6` (pass 59 report; code at `428d5f0d`, template 1.6.0). Amended the same day with the owner's decisions (§8) and amendments C1 to C4, then implemented in this pass. Text marked **(C1)** to **(C4)** comes from the amendments. Text marked **(P60)** was added during implementation, where the code required something the amendments did not say; each (P60) addition is listed in the pass report (`docs/passes/pass60.md`).
**Prerequisite check:** pass 59's branch UI (its step 7) **has shipped** in 1.6.0: `Pages/Branches`, `UserBranchesSection` in the user dialog, `BranchSelector` in `HeaderMenu` and `ActiveBranchState` (`docs/passes/pass59.md` §1 and §8). Nothing from pass 59 is carried into this pass.
**Database:** PostgreSQL only. Npgsql code sits under `#if (UsePostgreSql)`; the other two providers build and are described in §3.2, but are not targets.
**Source:**
- `docs/gxtemplate-adequacy-review.md`, change list items 8 (approval policy) and 9 (schemas per module and architecture tests).
- The error-handling gaps recorded by pass 58: `FallbackExceptionHandler` (`pass58.md` §7), and `PostingRejectedException`/`ErrorCode` (`pass58.md` §4, A2 row; `PostingRejectedException.cs:13-14`).
- Part D item 4 (deactivated users keep their circuits), added by the owner while this plan was being written, from `pass59.md` §7.

**Unchanged:**
- Pass 58's unit of work, idempotency, numbering and row locks.
- Pass 59's save rules, guards, branch filter, `BranchAccessBehaviour` and `AcrossBranchesAsync`.
- The pinned suites must pass **unmodified**: `SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests` (exactly two `ISaveChangesInterceptor`s), `TransactionalAuditTests` and the publisher suites.

**Ships as:** template 1.7.0. This is the last pass before the first application is generated, so everything that decides where tables live and how modules are laid out is in it.

The first application is a Spare Parts Management System with eight modules: Organisation, Catalogue, Partners, Inventory, Purchasing, Sales, Stock Taking and Accounting. Their needs shaped the mechanisms below. **The template ships no module.** Its tests use two test-only sample modules.

## 1. What is wrong today (verified in code)

### Part A: approval policy

- **There is no approval concept.** `src` has no `IApprovalPolicy`, no approval command contract, no approval-limit table and no "approve" permission. Every template permission is a plain `public const string` in a nested class of the partial `Permissions` class.
  - The registry discovers them by reflection (`AdministratorPermissionRegistry.cs:230-242`), and so do the policies (`Infrastructure/DependencyInjection.cs:576-583`).
  - Nothing distinguishes one kind of permission from another.
- **Authorization answers "may this user send this request?" and nothing more.**
  - `AuthorizationBehaviour` is ANY-OF over `[RequestAuthorize]` attributes. It is fresh per call: `IdentityService.AuthorizeAsync` rebuilds the principal from the database (`IdentityService.cs:66-78`).
  - It knows nothing of the document, its preparer or its value.
- **`BranchAccessBehaviour` is the shape to copy.** It is a constrained behaviour (`where TRequest : IBranchCommand`, `BranchAccessBehaviour.cs:22-23`) that reads everything fresh on each send (`:66-100`), throws `ForbiddenAccessException` with a plain message, and logs at Warning. It installs an AsyncLocal grant (`:60-63`) that the save rules check.
- **The pipeline today** (`Application/DependencyInjection.cs:31-42`), outermost first: `AuthorizationBehaviour`, `BranchAccessBehaviour`, `ValidationBehavior`, `ResultExceptionBehavior`, `CacheInvalidationBehaviour`, `TransactionBehaviour`, `PerformanceBehaviour`, `FusionCacheBehaviour`.
- **The administrator role's permissions already cannot be edited.**
  - `PermissionAssignmentService.AssignRoleAsync` calls `EnsureRolePermissionsCanBeModified(role.Name)` (`PermissionAssignmentService.cs:77`).
  - That method refuses **any** change on the `Admin` role (`AdministratorProtectionService.cs:51-58`).
  - The role is seeded with exactly `AdministratorPermissionRegistry.Granted` (`ApplicationDbContextInitializer.cs:170-174`).
- **Three other routes would still give the administrator an approve permission:**
  - **A direct user grant.** `AssignUserAsync` and `AssignUserBulkAsync` (`PermissionAssignmentService.cs:45-66, 97`), reached from `Users.razor:825, 843`, check only that the actor is not targeting themselves and holds what they grant (`:55-56`).
  - **A second role.** An administrator can be made a member of any other role that holds the permission (`UserFormDialog.razor`, role assignment).
  - **The system account.** It is granted everything in `Granted` (`ApplicationDbContextInitializer.cs:383-389`), so an approve permission placed in `Granted` by mistake would reach it too.
- **The bootstrap registry already fails startup on an undecided permission** (`AdministratorPermissionRegistry.Validate`, `:259-323`, called from `Application/DependencyInjection.cs:61`). It has no notion of a permission that must never be granted.

### Part B: schema per module

- **One schema for every application table.**
  - `GxNamingConventions.BusinessSchema = "core"` (`GxNamingConventions.cs:33`).
  - `ApplyGxTableNaming` sets that schema and a `TBL_UPPER_SNAKE` name on every `IBusinessEntity` that has its own table and is not named by hand (`:46-120`, the setting at `:92-93`).
  - A many-to-many join goes to `core` only if one side is in `core` (`:107-109, 115`).
  - Columns are never renamed.
- **Template tables stay in the default schema.** Each template entity derived from `BaseEntity` opts out with a schema-less `ToTable`:
  - `Documents`, `PicklistSets`, `SecurityPolicies`, `ProcessedRequests`, `DocumentSequences`, `Branches` and `UserBranches` (`Persistence/Configurations/*`).
  - Identity, `Tenants`, `TenantUsers`, `AuditTrails` and `DataProtectionKeys` are not `IBusinessEntity` at all.
  - Today **no table is in `core`**. `GxTableNamingTests.NoTemplateEntity_IsMappedIntoTheCoreSchema` pins this (`GxTableNamingTests.cs:408-421`).
- **There is no module concept.** There is no attribute and no namespace rule. The convention's own comment argues against namespace-string tests, because they "fail SILENTLY" (`GxNamingConventions.cs:56-58`).
- **Module configurations would be dropped silently.** `ApplyConfigurationsFromAssembly` uses the predicate `t => t.Namespace == ConfigurationsNamespace`, an **equality** (`ApplicationDbContext.cs:167-175`). A configuration in `Persistence.Configurations.Inventory` is therefore never applied, and EF builds the entity by convention without a word. The predicate is deliberate: it keeps the log configurations out.
- **The migrations make no schema choice:**
  - no migration calls `EnsureSchema` or `HasDefaultSchema`;
  - no context calls `MigrationsHistoryTable(...)`, so history is `__EFMigrationsHistory` in the default schema;
  - migrations are wired by `MigrationsAssembly(...)` only (`Infrastructure/DependencyInjection.cs:296, 309, 314`).
- **Single-schema assumptions in tooling:**
  - `ModelHarness` drops and recreates `core` and `public` by name (`tests/Application.IntegrationTests/DataFoundations/ModelHarness.cs:63-64`).
  - `GxTableNamingTests` asserts `GxNamingConventions.BusinessSchema` throughout (`:133, 208-454`). `EveryTableOutsideCore_IsOneOfTheTemplates` (`:424-444`) fails for the first table outside `core`.
  - The examples in `AppendOnlyMigrationExtensions.cs:12` and README §"Posted records" (`README.md:746`) name `"core"`. So does README §"Database naming" (`:1352-1408, 1467`).
- **Already schema-agnostic, no change needed:**
  - the test reset (`PostgresTestDatabase.BuildResetBatchAsync`, schema-qualified `%I.%I`, `:210-269`);
  - `DataModelGuard`'s trigger check (`DataModelGuard.cs:220-238`, `GetSchema() ?? "public"`);
  - the append-only trigger helper (`AppendOnlyMigrationExtensions.cs:70-73`);
  - the seeders;
  - `LogDbContext`, which is a separate database.
- **The bulk guard matches a table by name in any schema.** Its regex is `(?:"?\w+"?\s*\.\s*)?"?(?<table>…)"?` (`GuardedTableCommandInterceptor.cs:98-106`). Two modules with tables of the same name would match each other.
- **No health check touches a schema.** `AddHealthChecks()` registers none (`Server.UI/DependencyInjection.cs:99`); that is pass 61's job.

### Part C: module layout and architecture tests

- **Features are flat:**
  - `Application/Features/{AuditTrails, Branches, Documents, Identity, PicklistSets, SecuritySettings, SystemLogs, Tenants}`;
  - `Domain/Entities/*`;
  - `Infrastructure/Persistence/Configurations/*`;
  - `Server.UI/Pages/{Branches, Dashboard, Documents, Identity, PicklistSets, SystemManagement, Tenants}`.
- **How things are registered today:**
  - **Permissions:** nested classes of the partial `Permissions` class, found by reflection (above). A module needs no registration, only a nested class and a decision in the registry.
  - **Menu entries:** one hard-coded list, `MenuService._features` (`Server.UI/Services/Navigation/MenuService.cs:74-130`). A module must edit this shared file.
  - **Posting-event handlers:** found by an assembly scan (`PostingEventDispatcher.cs:49-60`, called at `Application/DependencyInjection.cs:53`). This already works per module.
  - **Entity configurations:** found by an assembly scan with the equality predicate above. This **breaks** for module subfolders.
- **No architecture test exists, and no architecture-test package is referenced** (no NetArchTest or ArchUnitNET in any `.csproj`; there is no `Directory.Packages.props`).
- **The reflection and source-scan precedents are:**
  - `TenantFilterGuard` (`Infrastructure/Persistence/TenantFilterGuard.cs:30`) and `DataModelGuard` (model checks, run at startup and in tests);
  - `RequestAuthorizationRegistry` (a request scan, run at startup);
  - `OutboundAddressTests` (a source-text scan of `src`, which the smoke test shows is generated into every project).
- **The rules the brief lists, against today's code:**
  1. **Cross-module references:** no modules exist, so there is nothing to check yet.
  2. **Navigations:** the only navigations are on template entities (`AuditTrail.Owner`, `Document.Tenant/CreatedBy/LastModifiedBy`, `Tenant.TenantUsers`, `TenantUser.Tenant/User`, `UserBranch.User/Branch`).
  3. **Cacheable requests:** ten requests are `ICacheableRequest`. They read `Tenant`, `PicklistSet`, `SystemLog`, `AuditTrail` and `Document`. None reads an `IAppendOnly` or branch-scoped entity today. Nothing would stop one from doing so: `FusionCacheBehaviour` has no branch scope (`CacheScope.cs:15`).
  4. **`IgnoreQueryFilters`:** exactly two calls, both named. `BranchQueryScope.cs:40` (`[QueryFilters.Branch]`) is the sanctioned helper. `AuditTrailTenantScope.cs:76` uses `[QueryFilters.Tenant]`.
  5. **Bulk writes and raw SQL:**
     - `LogDbContext.cs:60` (`ExecuteDeleteAsync`);
     - `LogDatabaseStartupCheck.cs:184, 247, 266, 334` (`ExecuteSqlRawAsync`, `CreateCommand`, `GetDbConnection`);
     - `PostingSql.cs:32-35` (`GetDbConnection().CreateCommand()`);
     - `DataModelGuard.cs:230` (`SqlQuery<int>`).

     There is no `ExecuteUpdate`, `FromSql*` or `ExecuteSqlInterpolated` in `src`.
  6. **Idempotent requests:** this rule is **half structural already.** `IIdempotentRequest : ITransactionalRequest` (`ITransactionalRequest.cs:40`), so the first half cannot be broken. The `Result`/`Result<T>` rule is checked only **at the first send** (`TransactionBehaviour.cs:87-93`, `NotSupportedException`), not at startup.
  7. **Floats and unkinded decimals:** **already covered** by `DataModelGuard.CheckNumbers` (`DataModelGuard.cs:52-88`), at startup and in `DataModelGuardTests`. The rules are an unkinded decimal, two kinds, a precision override, `float`/`double` without `[Approximate]`, and a kind on a non-decimal.
- **There is no code generator in the repository:** no `.tt` files, no item templates, only the project template's `.template.config`. Review §I and open question 9 say the upstream generator is an external Visual Studio extension that was never reviewed.

### Part D: error handling and live sessions

1. **`FallbackExceptionHandler` would return `exception.Message`:** `"An unexpected error occurred: {exception.Message}"` (`FallbackExceptionHandler.cs:50`). **It is also dead code.**
   - Nothing registers or constructs it, and Mediator 3 has no exception-handler pipeline.
   - The same is true of `NotFoundExceptionHandler` and `ValidationExceptionHandler`.
   - Only `DbExceptionHandler` is used, constructed inline by `ResultExceptionBehavior` (`ResultExceptionBehavior.cs:58-63`).
   - A search of `src` and `tests` for each type name finds only its own file.
   - The live path is already correct: `ResultExceptionBehavior`'s catch-all returns `UnexpectedErrorMessage(reference)` and logs the exception (`:65-73`).
2. **A `PostingRejectedException` outside a unit of work gets the generic message.**
   - Only `TransactionBehaviour` catches it (`TransactionBehaviour.cs:149-152, 225-229, 255-260`).
   - Thrown by a handler of a non-transactional request, it reaches `ResultExceptionBehavior`'s `catch (Exception)` (`ResultExceptionBehavior.cs:65-73`). There it is logged at **Error** as unhandled, and the user reads "An unexpected error occurred. Reference: …" instead of "Insufficient stock for item X".
3. **`Result` has no error code.**
   - `Result` holds `Succeeded` and `Errors` only (`Result.cs:13-24`); `IResult` declares the same two (`IResult.cs:9-14`).
   - `PostingRejectedException.ErrorCode` is logged and then lost (`TransactionBehaviour.cs:257-259`; `PostingRejectedException.cs:13-14`).
   - Forms recognise a concurrency conflict by comparing message text: `ConcurrencyConflict.Is(result)`, used in `PicklistSets.razor:331` and `BranchFormDialog.razor:40`.
   - The idempotency codec stores only a **successful** response's `Data` (`IdempotentResponseCodec.cs:10, 21-27`). A failure is never recorded, so a code on failures needs no codec change.
4. **A deactivated or locked-out user keeps working.** Five gaps, each verified:
   - **Deactivation does nothing to the session.** It sets `IsActive = false` and `LockoutEnd = MaxValue` and calls `UpdateAsync` (`Users.razor:594-597, 792-795`). It does not bump the security stamp or clear the user context. The role-change path does both (`UserFormDialog.razor:314-320`).
   - **The loader ignores the user's state.** `UserContextLoader.LoadAsync` builds a context for any user that exists and reads neither `IsActive` nor `LockoutEnd` (`UserContextLoader.cs:63-132`). The context is cached for an hour (`:17, 129`).
   - **The hub filter carries on without a user.** Pass 59's per-invocation refresh (`UserContextHubFilter.cs:40-73, 91-117`) only empties the context when the loader returns null. The invocation still runs with no user, so Mediator requests are refused, but a component that calls `UserManager` directly (the Users page does) still works. The connection is not closed.
   - **The authorization check ignores the user's state too.** `IdentityService.AuthorizeAsync` rebuilds the principal without looking at `IsActive` or lockout (`IdentityService.cs:66-78`). A deactivated user's permission claims still satisfy every policy.
   - **Cookie and circuit revalidation are slow:**
     - Cookie validation checks only the security stamp and the idle timeout. The idle chain is installed only when idle timeout is enabled (`Infrastructure/DependencyInjection.cs:625-660`).
     - The circuit's `IdentityRevalidatingAuthenticationStateProvider` checks the stamp every 30 minutes (`:18, 30-44`).

   **The system account is inactive and locked out by design** (`ApplicationDbContextInitializer.cs:334-375`: `IsActive = false`, `LockoutEnd = MaxValue`). It authorizes background work through `IdentityService.AuthorizeAsync`. This constrains the fix (§3.4.4, §7.6).

## 2. Target behaviour

1. **Approval.**
   - A document is approved only by someone other than its preparer, who holds the document type's approve permission and has a limit that covers the document's stored value, in the document's branch (**C1:** when the document has one).
   - The administrator, any member of the administrator role and the system account never approve, whatever they hold.
   - A command that approves without going through the policy fails loudly.
   - An approve permission placed in the administrator's grant list fails startup.
2. **Schemas.**
   - Every application table lives in its module's schema (`org`, `cat`, `ptn`, `inv`, `pur`, `sal`, `cnt`, `acc` in the first application). Template tables stay where they are.
   - An application entity that declares no module fails startup.
3. **Modules.**
   - A module is a folder in each layer, with the existing `Features/<Entity>` layout inside.
   - Modules talk only through published contracts and posting events.
   - The architecture tests every generated project inherits enforce passes 58 and 59 and the module boundaries.
4. **Errors.**
   - No path shows an exception's own text to a user.
   - A business rejection shows its own message wherever it is thrown.
   - If the owner agrees (§8.13), a failure can carry a machine-readable code.
5. **Sessions.** A user deactivated, or locked out by an administrator, while signed in loses access on their next action, and their session is signed out. **(Decision 15)** An automatic lockout after failed sign-ins only blocks new sign-ins.

## 3. Components

**(P60) Additions made during implementation, collected here** (each is also in the pass report):

1. **`IApprovable<TSelf>`.** The static document-type code moved to a self-typed `IApprovable<TSelf>`, because a static abstract member makes `IApprovable` itself unusable as a type argument. `IApprovalCommand<THeader>` requires `IApprovable<THeader>`, and the guard requires every `IApprovable` to implement it.
2. **The header is loaded through the branch filter.** A document outside the approver's read set is therefore *not found* rather than refused as the wrong branch, so a refusal never reveals it. Rule 5 applies to an approver who can read both branches.
3. **Deactivating a limit uses the same command.** It is `AddEditApprovalLimitCommand` with `IsActive = false`, as for branches; there is no separate deactivate command.
4. **Two Application services.** `IApprovalDocumentTypes` (from the model) and `IApprovalRoles` keep Identity and the model out of Application.
5. **`ApprovalPermissions.DiscoverLoaded()`.** It finds approval permissions in every loaded assembly of the application, so a module's permissions are found in any layer.
6. **Exception text in mail and storage.** Ten failure results in storage and mail services carried `ex.Message`. They now return a fixed message, with a logged reference for storage (`FaultReference`). The D1 scan found them.
7. **Configurations and pages under `Modules/<M>`** (see §3.3), so one namespace rule places every type in its module.
8. **`IMenuContributor`** was planned; registering it by assembly scan is the implementation detail.
9. **Test users are created active.** Test fixtures that created users without `IsActive = true` now set it: the loader gives a deactivated user no context. No real path creates a signed-in user that is inactive.
10. **A nested request's rejection code is not carried.** Its message reaches the outer caller, but not its `ErrorCode` (§3.4.3).
11. **The hub filter's catch** excludes `HubException`, so the refusal is not swallowed by the "reload failed, use the snapshot" fallback.

### 3.1 Part A: approval policy

**Contracts (Domain and Application):**

| Type | Layer | Meaning |
|---|---|---|
| `IApprovable` | Domain | A document header that needs approval. It exposes `Guid Id` (pass 58: headers are Guid v7), `string PreparedById`, `[Money] decimal ApprovalValue`, `string? ApprovedById`, `DateTime? ApprovedAt`, and `static abstract string ApprovalDocumentType { get; }`, a stable code such as `"pur.order"` that the module declares. |
| `IApprovalCommand<THeader> where THeader : class, IApprovable` | Application | A command that approves one header. It carries `Guid DocumentId`. It must also be `IIdempotentRequest` (and therefore transactional, pass 58). **(C1)** It must be `IBranchCommand` (pass 59) **when `THeader` is branch-scoped** (`IMustHaveBranch` or `IBranchVisibility<>`); for a header with no branch scope it need not be. |
| `IApprovalCommand` | Application | Non-generic base marker, so a pipeline behaviour can be constrained to it. |
| `[ApprovalPermission]` | Application | On a `Permissions.*` constant field: this permission approves. |
| `IApprovalPolicy` | Application | `Task<ApprovalDecision> DecideAsync(IApprovable header, string userId, int branchId, CancellationToken)`. The one check. It is also callable by a page that wants to grey out an Approve button. |
| `ApprovalLimit` | Domain, template table | Described below. |

**Where the check runs: `ApprovalBehaviour`, constrained to `IApprovalCommand`, placed immediately inside `TransactionBehaviour`** (between `TransactionBehaviour` and `PerformanceBehaviour`).

- **Why inside the transaction, when the brief asks for the check "before the handler" (§7.3).** The value and the preparer must come from the **stored** header, never from the command. They must also not change between the check and the approval. Pass 58's answer to "must not change" is a row lock, and a row lock exists only inside the unit of work.
- **What the behaviour does, in order.** It runs inside the unit of work, before the handler:
  1. It loads the header through the shared context with `ForUpdate()`.
  2. It runs `IApprovalPolicy`.
  3. It installs an `ApprovalGrant` (AsyncLocal, the `BranchGrant` pattern) for that header id.
  4. It calls the handler. The handler gets the same tracked, locked instance from the same context.
- **Every attempt re-checks.** A deadlock retry re-runs the whole attempt (`TransactionBehaviour.cs:95-108`), so the check runs again on fresh data.
- **Relative to the other two behaviours.**
  - `AuthorizationBehaviour` (outermost) has already checked that the user holds **an** approve permission named on the command. That check is fresh.
  - `BranchAccessBehaviour` has already checked that the user may write in the command's branch. That check is fresh and runs once per send. **(C1)** It runs only for an `IBranchCommand`, so for a header with no branch scope there is no branch check, and none is needed.
  - `ApprovalBehaviour` adds what only the document can answer.
- **The pinned order test is unaffected.** Mediator's generator applies a constrained behaviour only to matching requests, and pass 58's T20 probe (`ImportPicklistSetsCommand`) is not an approval command. The source-order check stays true as well: FusionCache still comes after Transaction (`TransactionPipelineTests.cs:41-67`).
- **Finding `THeader`.** It is read once per request type from the closed `IApprovalCommand<>` interface (cached reflection, as `AuthorizationBehaviour` caches attributes). It is loaded through `IApplicationDbContext.Set<THeader>()`, which is added to the interface; `DbContext` already implements it.

**What `IApprovalPolicy` decides** (all read fresh from the database inside the transaction):

1. **System context:** refused. Background work never approves.
2. **The administrator:** refused if the user is a member of `Roles.Admin`, read fresh as role membership, whatever else they hold.
3. **Preparer:** refused if `userId == header.PreparedById`, or if `userId == header.CreatedById` when the header is auditable (§8.8).
4. **Already approved:** refused if `header.ApprovedById` is not null.
5. **Branch:** the command's `BranchId` must be the header's branch (`IMustHaveBranch.BranchId`), or one of its `InvolvedBranchIds` for an `IBranchVisibility<>` header. This stops a branch-A grant being used to approve a branch-B document. **(C1)** Skipped when the header is not branch-scoped.
6. **Limit:** the user's limit for `(THeader.ApprovalDocumentType, branch)` must be at least `header.ApprovalValue`. **(C1)** For a header with no branch scope, only the roles' rows **with no branch** count; a branch row can never apply to it.

**Approval limits held as data: one row per role and document type, optionally per branch** (§8.6).

- **The `ApprovalLimit` table:**
  - `Id`, `RoleId` (FK to roles, `Restrict`), `DocumentType` (string), `BranchId` (nullable FK to `Branches`, `Restrict`), `MaxValue` (`[Money]`) and `IsActive`.
  - It derives from `BaseVersionedEntity` and is `IAuditable`, so every change has an author in the audit trail.
  - It is a template table in the default schema, like `Branches`.
- **The rules:**
  - A user's limit is the **highest** `MaxValue` over their **roles'** rows for that document type. For each role, a row for the document's branch overrides that role's row with no branch.
  - **No row means no authority:** "*You have no approval limit for {document type}.*" It fails closed.
  - A user-level permission claim gives the approve permission but never a limit. A direct grant therefore cannot approve anything without a role's row.
- **"Higher role approves" needs no extra machinery.** A branch manager's row is 500,000 and the Budget Holder's row is 5,000,000. A 2,000,000 order is refused for the manager ("needs an approver whose limit covers it") and approved by the Budget Holder. The approval is **single-step**: one approval by someone whose limit covers the value (§8.7).
- **Unique** on (`RoleId`, `DocumentType`, `BranchId`). A null `BranchId` makes a portable unique index impossible: pass 59 met the same problem (P59, `UserBranches`). The maintenance command enforces it, as `SetUserBranchesCommand` does for "one default".
- **Document types come from the model.** `ApprovalDocumentTypes.Discover(model)` lists every `IApprovable` type's code. The guard refuses two types with the same code. A row whose code no longer exists is reported at startup as a Warning, not a failure, because data must not stop startup.
- **Maintenance page:**
  - `Pages/ApprovalLimits/ApprovalLimits.razor`: a list by document type, add, edit and deactivate.
  - Commands `AddEditApprovalLimitCommand` (versioned, pass 59) and `DeactivateApprovalLimitCommand`.
  - Permissions `Permissions.ApprovalLimits.View` and `.Edit`, granted to the administrator (§8.10).
  - The page shows no limit for the `Admin` role: its command refuses `RoleId` = Admin, because such a row could never be used.

**Approve permissions:**

- **How they are identified.** `[ApprovalPermission]` on the constant field, for example:

  ```csharp
  public static partial class Permissions
  {
      public static class PurchaseOrders
      {
          [ApprovalPermission] public const string Approve = "Permissions.PurchaseOrders.Approve";
      }
  }
  ```

  It is explicit and survives renames. A naming convention (`*.Approve`) would silently miss `ApproveAboveLimit` or `Authorise`, and the review's lesson (`GxNamingConventions.cs:56-58`) applies.
- **The startup guard**, in `AdministratorPermissionRegistry.Validate`, which already runs at startup (`Application/DependencyInjection.cs:61`). It gains a fourth list: every `[ApprovalPermission]` constant must be in `Excluded`, with a reason, and never in `Granted`. Otherwise startup fails: "*{permission} approves documents and is in Granted. The administrator never approves (pass 60).*" Because the system account is granted `Granted` (`ApplicationDbContextInitializer.cs:383-389`), the same guard keeps it away from the system account.
- **Request guard**, beside `RequestAuthorizationRegistry.AssertAllRequestsAreMarked` (startup and test). Every `IApprovalCommand` must:
  - be `IIdempotentRequest`;
  - be `IBranchCommand` **if its `THeader` is branch-scoped (C1)**. A branch-scoped header approved by a command that is not an `IBranchCommand` fails startup: "*{Command} approves the branch-scoped {Header} and must implement IBranchCommand.*" A header with no branch scope may be approved by either kind;
  - carry at least one `[RequestAuthorize(Policy = …)]`, with every policy an `[ApprovalPermission]` constant.

  Conversely, no request that is not an `IApprovalCommand` may carry an approve policy. That catches a command that approves while dressed as an edit.
- **The UI refuses where it can.** The send-time check (rule 2) is the guarantee; these refusals give an early, clear message.
  - **Roles:** already refused, because the `Admin` role's permissions are unmodifiable (`AdministratorProtectionService.cs:51-58`). No change.
  - **Direct user grants:** `AssignUserAsync` and `AssignUserBulkAsync` refuse an `[ApprovalPermission]` for a member of `Admin`: "*The administrator cannot be given approval rights.*"
  - **Role membership:** the user dialog refuses to make an `Admin` member also a member of a role holding an approve permission, and the reverse. The check is in a service method, called from `UserFormDialog`'s save.

**Freshness: re-check at send time. I recommend this** (§8.11).

- The permission is fresh already (`AuthorizationBehaviour` → `IdentityService.AuthorizeAsync`, `IdentityService.cs:66-78`).
- Role membership and limits are read from the database inside the transaction on each attempt. That is one query joining `AspNetUserRoles` to `ApprovalLimits`.
- Nothing comes from the cached `UserContext`. A removed role or a lowered limit therefore takes effect on the very next approval, as a removed branch assignment does for writes (pass 59). The cost is one indexed query per approval, which is negligible.

**Omission fails loudly:**

- **Save rule (`DataRules`, the pass 59 home).** An `IApprovable` entry whose `ApprovedById` or `ApprovedAt` changed needs an `ApprovalGrant` for that header id, and `ApprovedById` must equal the current user. Otherwise the save throws: "*{Entity} {Id} was approved outside an approval command. Implement IApprovalCommand<{Entity}> on {RequestType}.*" A command that approves without the contract therefore fails on its first exercise. The rule also refuses clearing an approval (back to null) outside a grant.
- **Model guard (`DataModelGuard`).** An `IApprovable` must be:
  - `IVersioned`, so the approval conflicts with a concurrent edit (pass 59);
  - not `ISoftDelete`.

  **(C1)** Branch scope is **not** required. An application without branches approves documents with no dummy branch. A branch-scoped header gets everything above (`IBranchCommand`, rule 5, branch rows); a header with no branch scope gets the rest.

  Its `ApprovalValue` must be `[Money]`. Two types must not share a document-type code.
- **Request guard:** above.

**Refusal messages** (`ForbiddenAccessException`, like the branch refusal, so the UI treats every access refusal alike; §8.12):

| Case | Message |
|---|---|
| Administrator | "The administrator account cannot approve documents." |
| System | "Background work cannot approve documents." |
| Preparer | "You prepared this {document}. Someone else must approve it." |
| Already approved | "This {document} is already approved." |
| Wrong branch | "This {document} belongs to branch {code}, not {code}." |
| No limit | "You have no approval limit for {document type}." |
| Over limit | "This {document} is {value}. Your approval limit is {limit}, so it needs an approver whose limit covers it." |

**Logging:** each refusal is a Warning with `UserId`, `RequestType`, `DocumentType`, `DocumentId`, `BranchId`, `Value`, `Limit` and a reason code (`ADMIN`, `SYSTEM`, `PREPARER`, `APPROVED`, `BRANCH`, `NO_LIMIT`, `OVER_LIMIT`). Each approval is an Information entry with the same fields. The header's audit row (`IAuditable`) records the change itself.

**Composition with pass 58 and 59:**

- **Unit of work:**
  - The behaviour runs inside it, so the lock, the check, the handler and the commit are one transaction.
  - A refusal throws `ForbiddenAccessException`. `TransactionBehaviour` rolls back and rethrows. It is not retryable (`UnitOfWork.IsRetryable` matches only `40001`/`40P01`), and `ResultExceptionBehavior` rethrows it (`:29-35`).
- **Idempotency.**
  - The claim is taken before `next` (`TransactionBehaviour.cs:117-139`). A replayed `RequestId` therefore returns the recorded success **without** re-running the policy. That is correct: the approval already happened.
  - A refused attempt records nothing (`:154-155`), so a retry after a limit is raised runs the check again.
- **Document lock:**
  - `ForUpdate()` on the header. Pass 59 established that the lock does not change `xmin` and that the branch filter adds no `JOIN`.
  - A concurrent edit of the header waits for the lock, then gets pass 59's conflict message.
  - A concurrent second approval waits, then sees `ApprovedById` set and is refused with "already approved".
- **Posted records:** `ApprovedById` and `ApprovedAt` are not `[AllowedAfterPosting]`. Approval comes before posting, and a posted document cannot be approved again or un-approved.

### 3.2 Part B: database schema per module

**How an entity declares its module: a module definition type and a generic attribute. I recommend this** (§8.1).

```csharp
// Domain/Modules/Inventory/InventoryModule.cs (in the application, not the template)
public sealed class InventoryModule : ModuleDefinition
{
    public override string Name => "Inventory";
    public override string Schema => "inv";
}

// Domain/Modules/Inventory/Entities/StockItem.cs
[Module<InventoryModule>]
public class StockItem : BaseVersionedEntity<Guid>, IMustHaveBranch { … }
```

- **Why this form:**
  - **A typo cannot create a schema.** `[Module("invt")]` would compile and silently make a ninth schema; `[Module<InvtModule>]` does not compile.
  - **The schema is written once,** on the module definition.
  - **It is a type test,** which the naming convention's comment asks for.
- **The architecture test** (§3.3, rule 8) checks that an entity under `Domain.Modules.<M>` declares `[Module<<M>Module>]`. A file moved between folders is therefore caught as well. The two alternatives each lose something:
  - A **namespace convention** alone changes a table's schema silently when a file moves. The migration would show it, but nothing fails.
  - A **configuration base class** (`ModuleEntityConfiguration<T>`) misses entities that have no configuration class.
- **The template ships `ModuleDefinition` and `ModuleAttribute<TModule>`** (Domain/Common/Modules) and **no module**.

**`GxNamingConventions`:**

- **`ApplyGxTableNaming`:** for each `IBusinessEntity` with its own table that is not named by hand, it sets the schema to `ModuleOf(clr).Schema`, and leaves the `TBL_UPPER_SNAKE` name and the column names unchanged.
- **Joins:** a many-to-many join takes the schema of the side that declares the skip navigation. Two modules cannot be joined anyway (rule 2).
- **`BusinessSchema = "core"` is retired** (§8.2). No table is in `core` today (§1), so no existing migration changes.
- **A hand-named table** (`ToTable` or `[Table]`) keeps its explicit schema. If it has none, the module's schema is applied. Template entities are unaffected, because they are not module entities: the template's own configurations name them by hand with no schema, and they carry no `[Module<>]`.

**Startup guard (`DataModelGuard`; the same messages in its unit tests).** It fails when:

- an `IBusinessEntity` that is not a template entity has no `[Module<>]`: "*{Entity} declares no module. Add [Module<…Module>] (pass 60).*" Template entities are identified by assembly and namespace: the template's own `Domain.Entities` namespace, `Domain.Identity`, and the test-only entities. An application entity placed in `Domain.Entities` by mistake therefore fails too, through rule 8 of §3.3;
- a type is a TPH leaf with a module different from its root's;
- two module definitions share a schema or a name;
- a schema is not 1 to 16 lower-case ASCII letters, or is a reserved name (`public`, `pg_*`, `information_schema`, `dbo`);
- **two tables share a name across schemas.** `GuardedTableCommandInterceptor` matches by name in any schema (§1), and SQLite has no schemas. Unique names keep both correct;
- an owned type declares a module different from its owner's.

**Entity configurations:** ~~the predicate becomes "namespace equals `ConfigurationsNamespace` or starts with `ConfigurationsNamespace + "."`"~~ **(P60)** the predicate becomes "every configuration in the assembly **except** those in the log configurations' namespace (`Persistence.Logging.Configurations`)", because module configurations live under `Infrastructure/Modules/<M>/Configurations` (§3.3). The reason for the old equality, keeping `SystemLog` out of the business model, is kept by the exclusion. A new test asserts that every `IEntityTypeConfiguration<>` in the Infrastructure assembly is either applied to `ApplicationDbContext` or is a log configuration. A forgotten folder therefore fails rather than silently falling back to conventions.

**Migrations:**

- **Schema creation:** EF emits `EnsureSchema("inv")` before the first `CreateTable` in that schema, on PostgreSQL and SQL Server. Nothing is hand-written.
- **History table:** it stays `public."__EFMigrationsHistory"`. There is one context and one history. Splitting history per module would mean a context per module, which the review rejected (one `ApplicationDbContext`, review §I).
- **A generated project's first application migration** (for example `AddInventory`) contains `EnsureSchema` for each new module schema, the `CreateTable`s with `schema: "inv"`, their indexes, and any append-only triggers through `CreateAppendOnlyTrigger(table, schema: "inv")`. That helper already takes a schema (`AppendOnlyMigrationExtensions.cs:25, 50`).
- **README "Database naming"** gains a worked example: the generated C# and the SQL.
- **A module's schema is permanent.** Renaming one is a hand-written migration (`RenameSchema`). The guard does not stop it; the README says so.

**Cross-schema foreign keys:**

- PostgreSQL enforces a foreign key across schemas of one database, and EF emits `REFERENCES cat."TBL_PART" ("Id")`.
- **I recommend foreign keys across modules, without navigations** (§8.3). They are declared in the referencing module's configuration as `builder.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict)`.
  - That configuration must name the other module's entity type. Rule 1 (§3.3) therefore exempts **Infrastructure configuration classes**, and only for `HasOne<T>()`/`HasMany<T>()` FK declarations. That is checked by the test reading the configuration class's type references: an entity type from another module may appear only in a type that implements `IEntityTypeConfiguration<>`.
  - The integrity is worth the coupling. A stock row pointing at a deleted part is the failure a financial system cannot afford, and parts are deactivated, not deleted.
- **References to template entities** (users, branches) are ordinary foreign keys, as today (`UserBranch`, `Document.CreatedBy`).

**Tooling that assumed one schema:**

| Item | Change |
|---|---|
| `GxTableNamingTests` (`:133, 208-454`) | Asserts module schemas through the two test modules. `EveryTableOutsideCore_IsOneOfTheTemplates` becomes "every table outside a module schema is a template table". Not a pinned suite. |
| `ModelHarness.cs:63-64` | Drops every schema in the test model (read from the model) plus `public`, instead of `core` and `public` by name. |
| `AppendOnlyMigrationExtensions.cs:12`, README `:746, 1352-1408, 1467` | Examples name `inv`, not `core`. |
| `GuardedTableCommandInterceptor` | No change. Unique table names (guard) keep its name-only match correct. |
| `PostgresTestDatabase` reset, `DataModelGuard` trigger check, seeders, `LogDbContext` | Already schema-agnostic (§1). No change. |
| Health checks | None exist; pass 61. |
| Smoke test | No change. The generated project's tests carry the module-schema rows (§5). |

**The three providers:**

- **PostgreSQL** is the supported target: schemas, cross-schema foreign keys and triggers as above.
- **SQL Server:** EF emits `EnsureSchema` and schema-qualified tables, so the template's migrations build and the module mechanism works. Nothing PostgreSQL-only is involved in Part B. It is not tested beyond the build, as in passes 58 and 59.
- **SQLite** has no schemas. EF's SQLite provider ignores the schema, so every table lands in the one database file. Unique table names (guard) make that collision-free. It is not a target.

### 3.3 Part C: module layout and architecture tests

**Folders and namespaces** (`<M>` is the module name, for example `Inventory`):

| Layer | Folder | Holds |
|---|---|---|
| Domain | `src/Domain/Modules/<M>/` | `<M>Module.cs` (the definition), `Entities/`, `Contracts/` (posting events and value types other modules may use) |
| Application | `src/Application/Modules/<M>/` | `Features/<Entity>/{Commands,Queries,DTOs,EventHandlers,Caching,Security,Specifications}` (the existing layout, unchanged inside), `Contracts/` (query interfaces and DTOs offered to other modules), `<M>Permissions.cs` (nested classes of the partial `Permissions`) |
| Infrastructure | `src/Infrastructure/Modules/<M>/` | `Configurations/` (entity configurations), implementations of the module's `Contracts` interfaces, module services |
| Server.UI | `src/Server.UI/Modules/<M>/` | `Pages/<Entity>/`, `Components/`, `<M>Menu.cs` (the module's menu section) |

Namespaces follow the folders: `CleanArchitecture.Blazor.Application.Modules.Inventory.Features.StockItems.Commands`. Template features stay where they are; they are the "template" part, not a module.

**(P60) One rule for "which module is this type in", in every layer:** the namespace segment after `Modules`. That is why Infrastructure configurations and Server.UI pages sit under `Modules/<M>/` rather than `Persistence/Configurations/<M>/` and `Pages/<M>/`: with those, rule 1 would need a second, layer-specific rule to place a page or a configuration in its module, and a Razor page is exactly where C2 case e must be caught. Routes are unaffected (`@page` decides them, not the folder). The configuration scan therefore changes from "this namespace only" to "every configuration in the assembly **except** the log configurations' namespace", which also keeps the reason for the old equality (§3.2).

**(C3) A module never edits the shared context.**
- Module entities are **not** DbSet properties. A handler reaches them through `IApplicationDbContext.Set<T>()` (added in §3.1), and the entity enters the model through its configuration, which the assembly scan applies.
- An entity with a configuration and no DbSet property is a full model entity: the conventions (`ConfigureConventions`, the decimal kinds, the version mapping), `ApplyGxTableNaming`, the branch, tenant and soft-delete filter loops and both guards all iterate `builder.Model.GetEntityTypes()`, not DbSet properties, and migrations diff the model. Test C3-b proves it end to end.
- **Architecture rule 10:** neither `ApplicationDbContext` nor `IApplicationDbContext` declares a `DbSet<T>` property whose `T` carries `[Module<>]`.
- The module checklist (README) says: no DbSet; `Set<T>()`; a configuration in `Infrastructure/Modules/<M>/Configurations`.

**Registration:**

- **Permissions:** nested classes of the partial `Permissions`, as today. They are discovered by the existing reflection, and each must be decided in `AdministratorPermissionRegistry`, which already fails startup otherwise. No change.
- **Menu:**
  - `IMenuContributor { int Order; MenuSectionModel Section; }`. Each module implements one, registered by an assembly scan in `Server.UI/DependencyInjection.cs`.
  - `MenuService` appends the contributed sections, by `Order`, between "Application" and "MANAGEMENT" (`MenuService.cs:74-130`).
  - A module therefore never edits a shared file.
- **Posting-event handlers:** found by the existing assembly scan (`PostingEventDispatcher.cs:49-60`). No change. A module handles another module's event by naming the event type from that module's `Contracts`.
- **Entity configurations:** the assembly scan with the widened predicate (§3.2).

**Architecture tests: what is "published".**

- A module's published surface is every type in a namespace segment `Contracts` under `Modules.<M>`, in any layer.
- Posting events (`PostingEvent` subclasses) that other modules handle must be in `Domain/Modules/<M>/Contracts`.
- Everything else in a module is internal to it, whatever its C# accessibility. The layers are shared assemblies, so `internal` cannot express a module boundary.

**The tests.** They are shipped in the template's test projects, so every generated project runs them on its own modules. Each gets a deliberately wrong sample, built in a test-only assembly or model, on which it must fail.

| # | Rule | How it is checked | Wrong sample |
|---|---|---|---|
| 1 | A module's code references another module only through that module's `Contracts` namespaces, never its entities, handlers or features. Exemption: an Infrastructure configuration's FK declaration (§3.2). | **Type references from IL**, per type and method body, in Domain, Application, Infrastructure and Server.UI (§8.4). The module of a type comes from its namespace (`…Modules.<M>…`); **(C2)** a compiler-generated type (async state machine, iterator, lambda closure, local-function display class) is attributed to the module of its outermost declaring type. | **(C2)** Six samples, each failing on its own: (a) `db.Set<OtherEntity>()`, a generic method argument; (b) a LINQ lambda reading another module's entity property; (c) the same in an `async` method; (d) the same in an iterator and in a local function; (e) a Razor component using another module's entity and component; (f) another module's entity as a generic argument of a field type, a base type and an attribute. |
| 2 | No navigation crosses a module boundary. Navigations to template entities (users, branches) are allowed. | `DataModelGuard`, on the built model, at startup and in a test: for each navigation and skip navigation, declaring and target modules must be equal or the target must be a template entity. | A test model with `B.Entity.A` navigation. |
| 3 | **A cacheable request does not read an append-only or branch-scoped table.** | **A runtime guard,** not a static test (§7.10). `FusionCacheBehaviour` marks the factory call (AsyncLocal). The pass 59 `GuardedTableCommandInterceptor` gains a `ReaderExecuting` check: while the mark is set, SQL that reads a table mapped to `IAppendOnly`, `IMustHaveBranch` or `IBranchVisibility<>` throws, naming the request and the table. A static rule cannot see which DbSet a handler body reads. This guard fails on the first exercise, which any test of the query hits. | A test cacheable query over `TestLedgerRow` and one over `TestBranchRow`. |
| 4 | The only `IgnoreQueryFilters` naming `QueryFilters.Branch` is in `BranchQueryScope`, and no unnamed `IgnoreQueryFilters()` exists anywhere. | **Source scan** of `src/**/*.cs`, in the style of `OutboundAddressTests`, which is generated into every project. A call naming only other filters (`AuditTrailTenantScope.cs:76`) is allowed. | A sample source string in the test, run through the same scanner. |
| 5 | `ExecuteUpdate`, `ExecuteDelete`, `ExecuteSql*`, `FromSql*`, `SqlQuery*`, `GetDbConnection` and `CreateCommand` appear only in the listed files: `LogDbContext.cs`, `LogDatabaseStartupCheck.cs`, `PostingSql.cs`, `DataModelGuard.cs`, **(C4)** `InsertIfAbsent.cs`, and the migrations projects. | Source scan, with the allow-list in the test. A new file needs a reviewed list change. | A sample source string. |
| 6 | Every `IIdempotentRequest` returns `Result` or `Result<T>`. (`ITransactionalRequest` is already implied by inheritance, §1.) | **Startup check** beside `RequestAuthorizationRegistry`, and a test. This moves the check from first send (`TransactionBehaviour.cs:90-92`) to startup. It also checks every `IApprovalCommand` (§3.1). | A test assembly type `IIdempotentRequest` returning `int`. |
| 7 | No `float`/`double` without `[Approximate]`, and no decimal without a kind. | **Already covered** by `DataModelGuard.CheckNumbers` and `DataModelGuardTests` (pass 59 tests 1 to 3). Nothing is duplicated. The architecture suite has one test that asserts the guard runs at startup, so it cannot be dropped silently. | Pass 59's samples. |
| 8 | An entity under `Domain.Modules.<M>` declares `[Module<<M>Module>]`. A type in a module namespace declares no other module. Template namespaces hold no `[Module<>]` entity. | Reflection over the Domain assembly. | A sample entity in `Modules.Alpha` marked `[Module<BetaModule>]`. |
| 9 | No module page or handler calls another module's commands. | Covered by rule 1: a command is not in `Contracts`. | Sample of rule 1. |
| 10 | **(C3)** Neither `ApplicationDbContext` nor `IApplicationDbContext` declares a `DbSet<T>` whose `T` carries `[Module<>]`. | Reflection over the two types' properties. | A test context subclass with a planted `DbSet<AlphaThing>`. |

**(C2) What the scanner reads, and how each hard case is caught.**

| Case | Where the reference is in the compiled assembly | Read by |
|---|---|---|
| a. `db.Set<OtherEntity>()` | a `MethodSpec` token (generic method instantiation) on `call`/`callvirt` | resolving the operand with `Module.ResolveMethod` and reading `GetGenericArguments()` |
| b. LINQ lambda `x => x.Other.Name` | an expression tree: `ldtoken` of the property getter and of the type, plus `Expression.Property` calls; for a delegate lambda, the body of a method on a compiler-generated closure class | `ldtoken`, `ldfld`, `call` operands; every method of every nested type |
| c. `async` method | the body lives in `MoveNext` of a nested `<M>d__N` struct or class, with locals and fields of the referenced types | nested compiler-generated types, attributed to their declaring type's module |
| d. iterator, local function | iterator: a nested `<M>d__N` class; local function: a static `<M>g__Local|N_0` method on the declaring type, or a method on a display class | same |
| e. Razor component | the component is a C# class generated from `.razor` (`BuildRenderTree`): an entity appears in field types, `typeof`, generic method arguments (`OpenComponent<OtherComponent>`, `AddComponentParameter`) | the same scan over the Server.UI assembly; a component type is in its module by namespace |
| f. generic arguments in signatures | field types, base types and interfaces, attribute constructor arguments and named `Type` arguments | reflection over the type's metadata, recursively through generic arguments, arrays, by-ref and pointer types |

Locals and method signatures (parameters, return types, generic constraints) are read too. **Stop condition (C2):** if any of a to f cannot be detected reliably, rule 1 does not ship in the in-house form; the pass stops, reports which case, and recommends ArchUnitNET for rule 1.

**(C4) A sanctioned insert-if-absent.**
- **Today:** the template has no general helper. `INSERT … ON CONFLICT` appears only inside two pass 58 services for their own tables (`IdempotencyStore.cs:9-27`, `DocumentNumberAllocator.cs:31`).
- **The helper:** `IInsertIfAbsent.EnsureAsync<TEntity>(TEntity candidate, Expression<Func<TEntity, object?>> key, CancellationToken)` in Application (`Common/Interfaces/Transactions`), implemented in `Infrastructure/Persistence/Transactions/InsertIfAbsent.cs` beside `PostingSql`. It issues `INSERT INTO <table> (<columns>) VALUES (…) ON CONFLICT (<key columns>) DO NOTHING` on the unit of work's connection and transaction, and returns whether it inserted. The caller then reads and locks the row with `ForUpdate()`, in the lock order (README, "Posting transactions").
- **Where it may be used:** inside a unit of work only (`IUnitOfWorkManager.Current` must be set, or it throws), on PostgreSQL only (`NotSupportedException` elsewhere, pass 58 rule).
- **The key** must be the primary key or exactly the columns of a **unique** index or alternate key in the model; anything else throws, because `ON CONFLICT` needs a unique constraint to arbitrate. A high-volume row with an identity key is ensured by its natural key, for example (`ItemId`, `BranchId`).
- **Columns written:** every mapped scalar property except store-generated ones (identity key, `xmin`).
- **The rules it honours, by running them itself on the candidate** (it bypasses `SaveChanges`, so nothing runs otherwise):
  - **branch grant** (pass 59): an `IMustHaveBranch` candidate needs a grant including its `BranchId`; an `IBranchVisibility<>` candidate needs one of its involved branches;
  - **decimal kinds** (pass 59): a value with more scale than its kind is refused, as at save;
  - **audit fields:** `Created`/`LastModified` at and by are stamped as `AuditableEntityInterceptor` would.
- **What it refuses outright, and why:**
  - `IAuditable` types: the audit-trail row is written by `AuditableEntityInterceptor` in `SaveChanges`, and a row written without its audit entry would break the README's "Transactional audit" guarantee;
  - `IPostable` and `IPostableLine<>` types: a document is never created by key, and a line needs the header touch (pass 59 §3.3);
  - tenant-scoped types (`IMustHaveTenant`, `IMayHaveTenant`): tenancy is dormant and the stamp lives in the interceptor;
  - an entity with pending domain events: they would never be dispatched.
- **What is allowed and needs no check:** `IAppendOnly` (an insert is the one allowed change); `IVersioned` (a new row has no version to check).
- **Not intercepted:** the command is created on the connection, as `PostingSql` does, so `GuardedTableCommandInterceptor` does not see it. It is an `INSERT`, which that interceptor allows anyway.

**Approach: reflection and source scans in the style of the existing guards, plus a small in-house IL reader for rule 1. I recommend this** (§8.4).

- **Rules 2, 6, 7 and 8** need the EF model or type metadata. Reflection does that and already does it (`TenantFilterGuard`, `DataModelGuard`, `RequestAuthorizationRegistry`).
- **Rules 4 and 5** are about call sites with arguments. A source scan is simpler and more precise than IL for "which filter name". There is a precedent that ships in every generated project.
- **Rule 1** needs references inside method bodies, which reflection does not expose.
  - The helper, `TypeReferenceScanner` (TestSupport, about 150 lines), walks `MethodBody.GetILAsByteArray()`, decodes the operand tokens with the `OpCodes` table, and resolves them with `Module.ResolveType`/`ResolveMember`.
  - It also reads signatures, fields, base types, generic arguments and attributes.
  - It needs no package.
- **The alternative, ArchUnitNET** (Mono.Cecil-based), is a maintained, fluent library. But it adds a third-party dependency to every generated project's tests, it has its own version cadence against .NET 10, and the template's practice so far is to own its guards. NetArchTest is the third option; its last release is old.

**Code generator:**

- There is none in the repository to keep (§1). The external Visual Studio extension the review mentions emits the flat `Features/<Entity>` layout.
- **I recommend** shipping a "New module" and "New feature in a module" checklist in the README: the folder table above, the definition, the attribute, the menu contributor, the registry decision and a migration.
- A `dotnet new gx-module` item template can follow in a later pass (§8.16). The architecture tests catch a feature placed outside its module whichever tool made it.

### 3.4 Part D: error handling and live sessions

**1. `FallbackExceptionHandler`: delete it, with the two other unused handlers. I recommend this** (§8.14).

- `FallbackExceptionHandler`, `NotFoundExceptionHandler` and `ValidationExceptionHandler` are never registered or constructed (§1).
- "Bringing it into line" with `ResultExceptionBehavior` would mean maintaining a second copy of a path that never runs, which could drift back.
- `ResultExceptionBehavior` is the one place exceptions become results. It already returns `UnexpectedErrorMessage(reference)` and logs the detail (`:65-73`).
- `DbExceptionHandler` and `RequestExceptionHandlerState` stay, because `ResultExceptionBehavior` uses them.
- **New test:** a source scan that no file under `src/Application` or `src/Infrastructure` builds a failure result from `.Message` of a caught exception (`ResultFailureFactory.Create`/`Result.Failure` with `ex.Message`/`exception.Message`). The allowed list is `NotFoundException` and `PostingRejectedException`, whose messages are written for users.
- If the owner prefers to keep the class, its default arm becomes the fixed message and reference, as in `ResultExceptionBehavior`, and the class is documented as unused.

**2. `PostingRejectedException` everywhere.** `ResultExceptionBehavior` gains an arm **before** `catch (Exception)`:

```csharp
catch (PostingRejectedException rejection)
{
    _logger.LogInformation("{RequestType} was rejected: {Reason} (code {ErrorCode})", …);
    return ResultFailureFactory.Create<TResponse>(rejection.Message);   // + code, if §8.13 = yes
}
```

- Inside a unit of work, `TransactionBehaviour` still handles it first, unchanged.
- This arm sees only rejections thrown outside a unit of work, or by a nested non-transactional request.
- It is logged at Information, as pass 58 logs a rejection, not at Error. A rejection is a business outcome, not a fault.
- `PostingRejectedException`'s remarks (`:7-14`) are updated.

**3. An optional error code on `Result`: add it. I recommend this, and the owner decides** (§8.13).

- **The shape:**
  - `Result` gains `public string? ErrorCode { get; init; }`. `IResult` gains `string? ErrorCode => null` as a default interface member, so external `IResult` implementers still compile.
  - New factories `Result.Failure(ErrorCode code, params IEnumerable<string> errors)` and `Result<T>.Failure(ErrorCode code, …)`, where `ErrorCode` is a small record struct wrapping the string.
  - Because the overload takes a distinct type, it cannot be confused with `Failure("a", "b")`, which still means two messages.
  - `ResultFailureFactory.Create<TResponse>(string? code, params string[] errors)` gives the pipeline the same option.
  - `Map`/`Bind` carry the code through on failure.
- **Who sets codes:**
  - `PostingRejectedException.ErrorCode`, in both `TransactionBehaviour.Rejected` and the new arm.
  - `ResultExceptionBehavior` sets `GX.CONCURRENCY_CONFLICT` and `GX.UNEXPECTED`.
  - `ConcurrencyConflict.Is(result)` checks the code first and falls back to the message text, so existing forms keep working.
- **Compatibility:**
  - Every existing call to `Success`/`Failure`, every `Errors`/`ErrorMessage` reader and every `ResultFailureFactory.Create` call is unchanged.
  - The idempotency codec stores successes only, so stored responses are unaffected.
  - The serialised shape gains one nullable property.
- **The alternatives:**
  - **(b) No change:** codes stay in the log only, and forms keep comparing text.
  - **(c) Structured errors** (`IReadOnlyList<Error>` with code and message): cleaner, but it breaks every `Errors` reader in the template and in `Server.UI`.

**4. A deactivated user, or one locked out by an administrator, loses access on their next action, and is signed out.**

- **Definition (decision 15).** A live session ends only when:
  - the user is **deactivated** (`IsActive` goes from true to false); or
  - the user is **locked out by an administrator**, which the template writes as a **permanent** lockout, `LockoutEnd = DateTimeOffset.MaxValue` (`Users.razor:595, 793`).

  **An automatic lockout after failed sign-ins does not end a live session.** It only blocks new sign-ins, as today.
- **How the save-time detection tells the two apart: by the value, not by the caller.**
  - Identity's automatic lockout is always **time-limited**. `UserManager.AccessFailedAsync` sets `LockoutEnd = UtcNow + options.Lockout.DefaultLockoutTimeSpan` (`Infrastructure/DependencyInjection.cs:546`, `DEFAULT_LOCKOUT_TIME_SPAN_MINUTES`).
  - An administrative lockout is **permanent**. The two administrative paths are named by one constant, `UserAccess.AdministrativeLockoutEnd` (`= DateTimeOffset.MaxValue`), which both deactivation paths use instead of the literal.
  - So the rule is "`LockoutEnd` became `AdministrativeLockoutEnd`", which no failed sign-in can produce.
  - **A startup assertion** keeps the two apart: `DefaultLockoutTimeSpan` must be finite and shorter than ten years. A configuration that made automatic lockouts look permanent therefore fails startup instead of turning failed sign-ins into sign-outs.
  - **Why not an ambient "administrative" scope.** A scope is set by the caller, so an administrative path that forgot it would leave the session open with nothing failing. The value travels with the save, from whichever path wrote it.
- **The system account is exempt by identity** (`Users.System`). It is inactive and permanently locked out by design, and it never has a hub connection or a cookie.
- **Central detection, in the `SaveChanges` override** (pass 59 §3.0; no third interceptor).
  - It sees every `ApplicationUser` save: `UserManager.UpdateAsync` from both deactivation paths (`Users.razor:597, 795`), the user dialog's `IsActive` toggle, and Identity's own `AccessFailedAsync` (through `UserStore`), which the rule above ignores.
  - For a Modified user whose `IsActive` went true to false, or whose `LockoutEnd` became `AdministrativeLockoutEnd`, it:
    1. sets a new `SecurityStamp` in the same save, as `UpdateSecurityStampAsync` would;
    2. after the save succeeds, invalidates that user's cached context through `IUserContextInvalidator`. The invalidator is resolved from the application service provider the options carry, and is absent in directly constructed contexts, where there is no cache to clear.

  No caller can forget either step, so no new deactivation path can reopen the gap.
- **The loader returns no context for such a user.** `UserContextLoader.LoadAsync` returns null when `!IsActive || LockoutEnd == AdministrativeLockoutEnd` (`UserContextLoader.cs:71-77`).
  - The null is cached for `NotFoundCacheDuration` (1 minute).
  - A time-limited lockout is **not** checked, so an automatically locked-out user's open connection keeps its context.
  - `SystemContext` does not use the loader, so background work is unaffected.
- **The hub filter refuses the invocation and closes the connection.**
  - In `UserContextHubFilter.RefreshAsync`, when an **authenticated** principal's fresh context is null, the filter:
    1. logs at Information ("User {UserId} no longer has access; the connection was closed");
    2. calls `invocationContext.Context.Abort()`;
    3. throws `HubException`.
  - The invocation does not run, whether it would have gone through Mediator or straight to `UserManager`.
  - This replaces today's "continue with no user" (`:101-105`). `OnConnectedAsync` refuses a reconnect the same way.
- **Signed out.**
  - `OnValidatePrincipal` gains an access check that reads the cached context, which is a memory hit in the ordinary case. It is chained after the security-stamp validator, and **before** the `if (!idle.Enabled) return;` at `Infrastructure/DependencyInjection.cs:625`, so it applies with idle timeout off too.
  - When the loader returns no context for an authenticated principal, the check calls `RejectPrincipal()` and `SignOutAsync`. That also removes the server-side ticket from `MemoryCacheTicketStore`.
  - After the hub closes, the client reconnects over HTTP with that cookie. The fallback policy requires an authenticated user (`Infrastructure/DependencyInjection.cs:563-584`), so the reconnect fails, `gxReconnect.js` reloads on the terminal state, and the reload lands on the sign-in page.
  - **This client-side sequence is verified in a browser in step 6.** It cannot be proved by an in-process test.
- **Defence in depth.** `IdentityRevalidatingAuthenticationStateProvider.ValidateAuthenticationStateAsync` (`:30-44`) also returns false for a deactivated or administratively locked-out user. This is its 30-minute backstop.
- **Not changed: `IdentityService.AuthorizeAsync`.** Making it refuse inactive users would refuse the system account and break every background job (§7.6). The hub filter and the cookie are where interactive access is decided.

## 4. Order of work

The structural parts come first, because the first application cannot be generated without them.

0. **Start.** Branch from `main` at `4c33d6a6`. Prove the four suites green (pass 59's counts: 581+14, 235, 106, 315).
1. **Part D, items 1 to 3:**
   - delete the unused handlers;
   - add the `PostingRejectedException` arm;
   - the error code, as decided (§8.13);
   - the source-scan test.

   These are small and independent, and they unblock the approval messages.
2. **Part B:**
   - `ModuleDefinition` and `[Module<>]`;
   - the naming convention, the configuration predicate and its test;
   - the `DataModelGuard` additions;
   - the two test modules in `TestModelDbContext`;
   - `ModelHarness`, `GxTableNamingTests` and the docs.

   Prove that the template's own migration snapshot is unchanged (`ModelMatchesMigrationsTests`).
3. **Part C:**
   - the folder convention;
   - `IMenuContributor`;
   - `TypeReferenceScanner` and rules 1, 2, 4, 5, 6, 8 and **(C3)** 10 with their wrong samples; **(C2)** rule 1's six hard-case samples, each failing separately, before rule 1 is trusted (stop condition in §3.3);
   - **(C4)** `IInsertIfAbsent` and its file on rule 5's allow-list;
   - the rule 3 runtime guard;
   - the rule 7 assertion;
   - the README module checklist.
4. **Part A:**
   - the contracts, `ApprovalBehaviour`, `IApprovalPolicy`, the grant and the save rule;
   - the registry and request guards;
   - `ApprovalLimit` with its migration (all three providers);
   - the maintenance page;
   - the user-grant and role-membership refusals;
   - test-only `TestApprovableDocument` and approval command.
5. **Part D, item 4:** the `SaveChanges` detection, the loader, the hub filter, the cookie check and the revalidation backstop.
6. **Browser check:** generate a project from the packed template, sign in as two users, deactivate one from the other's session, and see the first land on the sign-in page at their next click. Use the local `playwright-core`, as in pass 48. Also see a refusal message for an over-limit approval on the test page.
7. **Documentation:**
   - README sections "Approvals", "Modules and schemas" (replacing the `core` text in "Database naming"), "Architecture tests" and "Errors";
   - an update to "Posted records", and to "Branches" (the approval composition);
   - the review's change list: items 8 and 9 done.
8. **Release 1.7.0.** Write the release notes and set `AppConfigurationSettings.Version`. Commit, **then** pack, install, run `tooling/smoke-generate.ps1 -UseInstalled` and tag `gx-v1.7.0`.

**Effort:**

| Part | Work | Effort |
|---|---|---|
| D 1-3 | handlers removed, rejection arm, error code, scan test | S, 1 day (0.5 without the code) |
| B: schemas | attribute, convention, predicate, guards, harness, tests, docs | M, 2 days |
| C: modules and tests | layout, menu contributor, IL scanner, eight rules with samples, runtime cache guard, checklist | M/L, 3.5 days, **+1 day** for C2's hard cases and C3, **+0.5 day** for C4 |
| A: approvals | contracts, behaviour, policy, grant, save rule, guards, table, page, UI refusals, migrations | L, 4 days (the page about 1 of it) |
| D 4: sessions | save detection, loader, hub filter, cookie, backstop, tests, browser check | M, 1.5 days |
| Docs and release | README, notes, pack, smoke | 1 day |
| **Total** | | **About 14.5 days with the amendments.** B, C and D 1-3 (about 6.5 days) must ship in 1.7.0, because they decide where tables live and how modules are laid out. If time runs short, A's maintenance page (§8.17) can follow as 1.7.1; limits can be seeded by migration meanwhile. |

## 5. Tests (Application.IntegrationTests on `GX_TEST_PG` unless noted)

- **Test-only types:**
  - two sample modules, `AlphaModule` (`tsa`) and `BetaModule` (`tsb`), each with one entity, added to pass 59's `TestModelDbContext`;
  - `TestApprovableDocument` (Guid key, `IVersioned`, `IMustHaveBranch`, `IAuditable`) with `ApproveTestDocumentCommand`;
  - **(C1)** `TestPlainApprovable` (Guid key, `IVersioned`, **no branch scope**) with `ApproveTestPlainCommand`, which is not an `IBranchCommand`;
  - **(C2)** `tests/ArchitectureSamples`, a Razor class library holding the wrong samples for rule 1 in two sample modules, so case e is a real compiled component;
  - a wrong-sample assembly, or in-test source strings, for each architecture rule.
- **Users:** a preparer, two approvers in roles "Branch Manager" (limit 1,000) and "Budget Holder" (limit 10,000), and the administrator.

| # | Test |
|---|---|
| 1 | **A preparer cannot approve their own request:** refused with the preparer message, logged with reason `PREPARER`, nothing saved. Also refused for the document's creator (if §8.8 is decided as recommended). |
| 2 | **An approver within the limit succeeds:** `ApprovedById`/`ApprovedAt` set, `Version` changed, approval logged at Information. |
| 3 | **Above the limit is refused** for the Branch Manager (1,500 against 1,000) with the over-limit message, and the same document is approved by the Budget Holder. |
| 4 | **The administrator is refused**, even after being made a member of "Budget Holder" by direct database seeding that bypasses the UI refusal. The system context is refused. |
| 5 | A user with the approve permission but no role row is refused ("no approval limit"). A branch-specific row overrides the role's general row in that branch only. |
| 6 | Freshness: lowering a role's limit, or removing the user from the role, refuses that user's next approval without a sign-out. |
| 7 | Branch: an approval command naming branch 1 for a branch-2 document is refused. A user without write access to the branch is refused by `BranchAccessBehaviour` first (pass 59, unchanged). |
| 8 | **Omission fails loudly:** a command that sets `ApprovedById` without `IApprovalCommand` fails at save, naming the request. Clearing an approval outside a grant fails. |
| 9 | Concurrency: two approvers approve the same document at once. One succeeds, the other gets "already approved". An edit racing an approval gets pass 59's conflict message. A deadlock retry re-runs the check. |
| 10 | Idempotency: the same `RequestId` sent twice approves once and returns the recorded success. A refused attempt records nothing, and the same `RequestId` succeeds after the limit is raised. |
| 11 | **Granting an approve permission to the administrator role is refused** (`AssignRoleAsync`: the role is protected). A direct user grant to an `Admin` member is refused. Making an `Admin` member a member of an approving role is refused (Server.UI and unit). |
| 12 | **A registry that grants an approve permission fails startup** (`AdministratorPermissionRegistry.Validate` on a planted list), naming the permission (unit). |
| 13 | Startup fails for an `IApprovalCommand` that is not idempotent, **(C1)** that approves a branch-scoped header without being a branch command, or that is authorized by a non-approve policy, and for a non-approval request carrying an approve policy. An approval command for a header with no branch scope that is not a branch command passes (unit). |
| 14 | Startup fails for an `IApprovable` that is not versioned, whose value is not `[Money]`, or that shares a document-type code with another. **(C1)** A header with no branch scope is accepted (unit). |
| 15 | **An entity with no declared module fails startup**, naming it. So do two modules with one schema, an invalid schema name, a TPH leaf in a different module and two tables with one name in different schemas (unit). |
| 16 | **A generated project's tables land in their module schemas:** the test model creates `tsa."TBL_ALPHA_THING"` and `tsb."TBL_BETA_THING"` (checked in `information_schema.tables`). A cross-module foreign key is enforced across schemas. Template tables are still in `public`. |
| 17 | The PostgreSQL migration generated for the test modules contains `EnsureSchema("tsa")` and `EnsureSchema("tsb")`, and the history table stays `public."__EFMigrationsHistory"`. The template's own snapshot is unchanged (unit). |
| 18 | **(P60)** A configuration in `Infrastructure.Modules.<Module>.Configurations` is applied, and no log configuration is. Every `IEntityTypeConfiguration<>` in Infrastructure is applied or is a log configuration (unit). |
| 19 | **Architecture rule 1** fails on sample module B reading module A's entity, and passes on B using A's `Contracts` and on a configuration's FK declaration. It passes on the template (unit). The hard cases are rows 36 to 41. |
| 20 | **Rule 2** fails on a cross-module navigation and passes on a navigation to a template entity. It passes on the template (unit). |
| 21 | **Rule 3**: a cacheable test query reading `TestLedgerRow`, and one reading `TestBranchRow`, throw naming the request and table. A cacheable `PicklistSet` query is unaffected. The same append-only query without `ICacheableRequest` is unaffected. |
| 22 | **Rule 4** fails on a sample `IgnoreQueryFilters([QueryFilters.Branch])` outside `BranchQueryScope` and on a bare `IgnoreQueryFilters()`. It passes on the template, including `AuditTrailTenantScope` (unit). |
| 23 | **Rule 5** fails on a sample `ExecuteUpdate` in an unlisted file and passes on the template's listed files (unit). |
| 24 | **Rule 6** fails at startup on an idempotent request returning `int` (moved from first send). It passes on the template (unit). |
| 25 | **Rule 7:** the startup path runs `DataModelGuard.CheckNumbers`; pass 59's rows 1 to 3 are the samples (unit). |
| 26 | **Rule 8** fails on an entity in `Modules.Alpha` marked `[Module<BetaModule>]` and on a module entity in `Domain.Entities` (unit). |
| 27 | Menu: two contributors appear in `Order`, between "Application" and "MANAGEMENT" (Server.UI). |
| 28 | **D1:** an unexpected exception from any handler reaches the caller as `UnexpectedErrorMessage(reference)` with the detail logged. The source scan finds no result built from an exception's message, and fails on a planted one (unit). |
| 29 | **D2:** a `PostingRejectedException` thrown by a **non-transactional** handler returns its own message, logged at Information with its code. The pass 58 T15 path inside a unit of work is unchanged. |
| 30 | **D3** (if decided): the rejection's `ErrorCode` reaches the caller's `Result.ErrorCode` inside and outside a unit of work. A conflict carries `GX.CONCURRENCY_CONFLICT`, and `ConcurrencyConflict.Is` still matches an old text-only result. `Failure("a", "b")` still yields two messages (unit). |
| 31 | **D4: a user deactivated while signed in is refused on their next action.** User A's hub connection is established. B deactivates A through the Users page's path (`UserManager.UpdateAsync`). A's next invocation throws `HubException`, its handler does not run, and the connection is aborted. A's cookie on the next HTTP request is rejected and redirected to sign-in, and the server-side ticket is gone. Repeated for a lockout by an administrator (`LockoutEnd = AdministrativeLockoutEnd`) without deactivation. **(Decision 15)** Not for a lockout from failed sign-ins: see row 44 (Server.UI). |
| 32 | D4: the deactivation save bumps the security stamp and clears the cached context. Background work under `ISystemContext` still authorizes and runs. Reactivating the user gives them a context again (integration). |
| 33 | Unchanged: the interceptor regression, ordering, transactional audit and publisher suites, pass 58's transaction tests (T20's order included), and pass 59's rows 1 to 35. |
| 34 | **(C1)** `TestPlainApprovable`, with no branch scope, is approved within the limit by `ApproveTestPlainCommand` (not a branch command), using the role's row with no branch, and refused above it. A branch row for the same role and document type is ignored for it. |
| 35 | **(C1)** A branch-scoped header approved by a command that is not an `IBranchCommand` fails startup, naming both (unit). |
| 36 | **(C2a)** Rule 1 fails on a sample method calling `db.Set<AlphaThing>()` from module Beta, and on nothing else in that sample (unit). |
| 37 | **(C2b)** Rule 1 fails on a sample whose only reference is a LINQ lambda reading `AlphaThing.Name` (expression tree and delegate forms, separately) (unit). |
| 38 | **(C2c)** Rule 1 fails on the same reference inside an `async` method, and the violation is reported against the declaring Beta type, not the compiler's state machine (unit). |
| 39 | **(C2d)** Rule 1 fails on the same reference inside an iterator, and separately inside a local function (unit). |
| 40 | **(C2e)** Rule 1 fails on a Beta Razor component that declares a parameter of type `AlphaThing`, and separately on one that renders an Alpha component (unit). |
| 41 | **(C2f)** Rule 1 fails, separately, on a Beta field of type `List<AlphaThing>`, a Beta class deriving from `Base<AlphaThing>`, and a Beta class carrying `[Sample(typeof(AlphaThing))]` (unit). |
| 42 | **(C3)** Rule 10 fails on a test context type declaring `DbSet<AlphaThing>`, and passes on `ApplicationDbContext` and `IApplicationDbContext` (unit). |
| 43 | **(C3)** A module entity with no DbSet property, reached only through `Set<T>()`, is created in its module schema by the model, inserted and queried through a command, filtered by branch (a user of branch 1 does not read branch 2's row), refused outside its branch grant at save, and reported by the guard when its decimal has no kind. |
| 44 | **(Decision 15)** A lockout produced by failed sign-in attempts (`AccessFailedAsync` until locked) leaves the user's open hub connection working: the next invocation runs with the user's context, the security stamp is unchanged and the cookie is still accepted. A new sign-in is refused while the lockout lasts. Startup fails if `DefaultLockoutTimeSpan` is not finite (Server.UI and unit). |
| 45 | **(C4)** Two concurrent units of work each ensure the same missing row. Both proceed, exactly one row exists, neither fails with a unique violation, and both then lock it with `ForUpdate()` in turn. |
| 46 | **(C4)** The helper refuses: outside a unit of work; a key that is not the primary key or a unique index; an `IAuditable`, postable or tenant-scoped type; a branch-scoped row outside the branch grant; a decimal with excess scale. It stamps the audit fields. Rule 5 passes with `InsertIfAbsent.cs` on the allow-list (integration and unit). |

## 6. Effort summary

About **14.5 days** with amendments C1 to C4 (table in §4). B, C and D 1-3 are about 6.5 days and must ship. A is the largest single part, mostly because of its guards and page. D4 is small but needs the browser check.

## 7. Conflicts with the brief or with passes 58 and 59, and what I recommend

1. **"Two error-handling gaps recorded in pass58.md section 7."** Section 7 records only one, `FallbackExceptionHandler`. The other two come from the A2 row of §4 and from `PostingRejectedException`'s remarks. Both are real (§1 Part D). No change in substance.
2. **`FallbackExceptionHandler` never runs.** Nothing registers or constructs it, nor `NotFoundExceptionHandler` or `ValidationExceptionHandler`. "Bringing it into line" would maintain dead code.
   - **Recommend:** delete the three, and add a scan test that no result is built from an exception's message (§3.4.1, §8.14).
3. **"One check, IApprovalPolicy, run before the handler," placed relative to the authorization and branch behaviours.** The check needs the stored value and preparer under a lock, and pass 58's lock exists only inside the unit of work.
   - **Recommend:** `ApprovalBehaviour` inside `TransactionBehaviour`, still before the handler, after both outer behaviours (§3.1).
4. **"The role-permission UI refuses to grant one to the administrator role."** It already refuses **every** change to that role (`AdministratorProtectionService.cs:51-58`). The open routes are direct user grants, a second role, and the system account's seed.
   - **Recommend:** close those three, and treat the send-time "member of Admin" check as the guarantee (§3.1).
5. **The review proposed limits by "role or user" with a currency** (review §F). A user-level limit bypasses role review, and the first application has one company and one currency.
   - **Recommend:** limits by role only, in the single currency `[Money]` (§8.6, §8.9).
6. **D4 and the system account.** The system account is inactive and locked out by design (`ApplicationDbContextInitializer.cs:334-375`) and authorizes through `IdentityService.AuthorizeAsync`. Refusing inactive users there, the obvious place, would stop all background work.
   - **Recommend:** enforce at the hub filter and the cookie, which only interactive users pass through. Exempt the system account by identity, and leave `AuthorizeAsync` unchanged (§3.4.4).
7. **"The code generator, if kept."** None is in the repository.
   - **Recommend:** a README checklist now, an item template later (§8.16).
8. **Module configurations would be silently ignored.** The configuration predicate is an equality (`ApplicationDbContext.cs:173`).
   - **Recommend:** widen it to the namespace subtree, and add a test that every configuration is applied (§3.2). Not in the brief, but required by Part B. **(P60)** Implemented as "every configuration except the log namespace", because module configurations live under `Infrastructure/Modules/<M>/` (§3.3).
9. **Rules 6 and 7 are partly done.** `IIdempotentRequest` already inherits `ITransactionalRequest`, and the decimal and float rules are pass 59's `DataModelGuard.CheckNumbers`.
   - **Recommend:** move rule 6's return-type check from first send to startup. For rule 7, assert only that the guard runs, with no duplicate.
10. **Rule 3 cannot be a static test.** Which table a handler reads is in its method body and its LINQ, which no reflection or IL rule can resolve reliably to a table.
    - **Recommend:** a runtime guard in the existing command interceptor. It fails on first exercise, the same standard pass 59 accepted for the version check (§3.3).
11. **Rule 1 versus database integrity.** A cross-module foreign key needs the other module's entity type in a configuration.
    - **Recommend:** allow it only there, and only for FK declarations (§8.3).
12. **The `core` schema.** The review proposed choosing a schema "instead of always `core`". With a schema per module, `core` holds nothing.
    - **Recommend:** retire `BusinessSchema` (§8.2). No existing table moves.
13. **The bulk guard ignores schemas** (`GuardedTableCommandInterceptor.cs:98-106`).
    - **Recommend:** require unique table names across schemas, rather than teach the regex schemas. This also keeps SQLite collision-free.
14. **"Locked-out" includes automatic lockouts.** This gives a third party a way to end a session (§3.4.4).
    - ~~Recommend: follow the brief.~~ **Decided otherwise (decision 15):** only deactivation and an administrator's lockout end a live session. The two kinds of lockout are told apart by value: an administrator's lockout is permanent (`AdministrativeLockoutEnd`), Identity's is always time-limited, and startup fails if the configured lockout is not (§3.4.4, rows 31 and 44).

## 8. Decisions for the owner

All decided by the owner on 2026-11-11.

1. **Module declaration: decided.** A module definition type and `[Module<TModule>]` on each entity, checked against the namespace by an architecture test.
2. **The `core` schema: decided.** Retired. Template tables stay in `public`; every application table goes to its module's schema; nothing existing moves.
3. **Foreign keys across modules: decided.** Yes, navigation-less `HasOne<T>().WithMany()` with `Restrict`, declared only in configurations, which are exempt from rule 1 for that purpose.
4. **Architecture-test approach: decided.** Reflection, source scans and a small in-house IL reader for rule 1, with no new package. **(C2)** It ships only if it detects all six hard cases; otherwise the pass stops and ArchUnitNET is recommended for rule 1.
5. **Rule 3 form: decided.** A runtime guard refusing a cacheable request that reads an append-only or branch-scoped table.
6. **Approval limits: decided.** By role and document type, optionally per branch (a branch row overrides). A user's limit is the highest of their roles'. No row means no authority. **(C1)** For a header with no branch scope, only rows with no branch count.
7. **Single-step approval: decided.** One approver whose limit covers the value.
8. **Preparer: decided.** Both the creator (`CreatedById`) and the submitter (`PreparedById`) are barred.
9. **Currency: decided.** One currency; limits and values are `[Money]`.
10. **Who maintains limits: decided.** `ApprovalLimits.View`/`.Edit` granted to the administrator, who configures but can never approve. Maker-checker on limits is not in this pass.
11. **Freshness: decided.** Role membership and limits are re-read from the database at send time, inside the transaction.
12. **Refusal type: decided.** `ForbiddenAccessException` with the messages in §3.1.
13. **Error code on `Result`: decided.** Option (a): an optional `ErrorCode` property and a distinct-typed `Failure(ErrorCode, …)` overload.
14. **Unused exception handlers: decided.** `FallbackExceptionHandler`, `NotFoundExceptionHandler` and `ValidationExceptionHandler` are deleted, with the source-scan test.
15. **Lockout scope: decided, not as recommended.** A live session ends only when the user is deactivated (`IsActive` goes false) or locked out by an administrator's action. An automatic lockout after failed sign-ins does not end a live session; it only blocks new sign-ins, as today. The save-time detection tells them apart by value (§3.4.4); rows 31 and 44.
16. **Code generator: decided.** A README checklist in this pass; no item template.
17. **Version and fallback: decided.** Ships as 1.7.0. If time runs short, B, C and D ship with A's policy, guards and table, and only the limits maintenance page moves to 1.7.1 (limits seeded by migration meanwhile). The pass report says which was done.
