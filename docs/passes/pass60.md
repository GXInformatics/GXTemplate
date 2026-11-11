# Pass 60: controls and structure, and 1.7.0

**Date:** 2026-11-11.
**Start:** HEAD `4c33d6a6` (pass 59 report; code `428d5f0d`, template 1.6.0), clean.
**Commit:** `254175ba` "Pass60-ControlsStructure and 1.7.0". This report follows in a separate commit, because it quotes that hash and the smoke-test result.

**Plan:** `docs/passes/pass60-controls-structure-plan.md`. It was amended first:

- the owner's 17 decisions are marked decided in §8 (decision 15 not as recommended);
- amendments C1 to C4 are marked **(C1)** to **(C4)**;
- additions made during implementation are marked **(P60)** in the plan, collected at the head of its §3 and listed in §3 here.

It was then implemented in its own order of work, steps 0 to 8.

**Suites** (PostgreSQL 15.7 at 127.0.0.1:5432 via `GX_TEST_PG`, each project alone):

| Suite | Result | Before this pass |
|---|---|---|
| Application.UnitTests | **601 passed, 14 skipped** | 581 + 14 |
| Infrastructure.UnitTests | **239** | 235 |
| Application.IntegrationTests | **136** | 106 |
| Server.UI.IntegrationTests | **348** | 315 |

0 failed. **Build:** 0 errors.

The pinned suites pass **unmodified**: `SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests`, `TransactionalAuditTests` and the three publisher suites. `git diff 4c33d6a6 254175ba` touches none of them. Pass 58's `TransactionPipelineTests`, T20's behaviour order included, is unchanged and green. `ApprovalBehaviour` is constrained to approval commands, so the probe's order is unchanged.

---

## 1. What shipped

**1.7.0 ships all four parts, the approval-limits page included.** The decision-17 fallback (the page in 1.7.1) was not needed.

| Area | Files |
|---|---|
| A. Approvals | `Domain/Common/Entities/IApprovable.cs` (`IApprovable`, `IApprovable<TSelf>`); `Domain/Entities/ApprovalLimit.cs`; `Application/Common/Approvals/ApprovalContracts.cs` (`IApprovalCommand<T>`, `[ApprovalPermission]`, `IApprovalPolicy`, `ApprovalGrant`, `IApprovalDocumentTypes`, `IApprovalRoles`); `Pipeline/ApprovalBehaviour.cs`, placed inside `TransactionBehaviour`; `Infrastructure/Services/Approvals/ApprovalPolicy.cs` and `ApprovalCatalog.cs`; the approval save rule in `DataRules`; the model rules in `DataModelGuard`; `RequestContractRegistry` (request guard); `AdministratorPermissionRegistry.ValidateApprovals`; the refusals in `PermissionAssignmentService` and `AdministratorProtectionService.EnsureApprovalSeparationAsync` (user dialog); `Permissions.ApprovalLimits` (View, Edit; granted to the administrator); `Features/ApprovalLimits`; `Pages/ApprovalLimits` (list and dialog), on the menu under Authorization; migration `Approvals` for all three providers |
| B. Schemas | `Domain/Common/Modules/ModuleDefinition.cs` (`ModuleDefinition`, `[Module<T>]`); `GxNamingConventions` (schema from the module; `core` retired); `TemplateEntities`; the module, navigation, schema and table-name rules in `DataModelGuard`; the configuration scan changed from an equality to an exclusion of the log configurations |
| C. Modules and architecture tests | `IApplicationDbContext.Set<T>()`; `IMenuContributor` and `MenuService`; TestSupport `TypeReferenceScanner`, `ModuleBoundaryRule`, `ModuleDeclarationRules`, `SourceTree`/`SourceRules`; `tests/ArchitectureSamples` (a compiled Razor class library of wrong samples); `CacheFill` with the cached-read check in `GuardedTableCommandInterceptor` (rule 3); `IInsertIfAbsent` / `InsertIfAbsent` (C4) |
| D. Errors | The three unused exception handlers deleted; `ResultExceptionBehavior`'s `PostingRejectedException` arm; `Result.ErrorCode`, `IResult.ErrorCode`, `ErrorCode`, and `ResultFailureFactory.ForRejection`; `FaultReference` for storage; fixed messages for mail |
| D4. Sessions | `Domain/Identity/UserAccess.cs`; detection in `DataRules` (stamp and invalidation); `UserContextLoader`; `UserContextHubFilter` (refuse and abort; refuse a reconnect); `UserAccessCookieCheck` chained into `OnValidatePrincipal`; the revalidation backstop; the startup validation of `Lockout.DefaultLockoutTimeSpan`; both deactivation paths use `UserAccess.AdministrativeLockoutEnd` |
| Docs | README: new sections "Approvals", "Modules and schemas" (with the new-module checklist), "Architecture tests", "Errors" and "Ending a user's session"; `IInsertIfAbsent` beside the lock order in "Posting transactions"; "Database naming" and "Posted records" moved from `core` to module schemas. The review's change list marks items 8 and 9 done. Release notes; `AppConfigurationSettings.Version` is 1.7.0. |

## 2. Conflicts

**No amendment (C1 to C4) conflicted with the code, and nothing was worked around.**

- **C2's stop condition was not triggered.** The in-house reader detects all six hard cases, each separately (rows 36 to 41). It resolves every token in the four application assemblies, with zero unresolved, so rule 1 ships in the in-house form and ArchUnitNET is not needed.
- **C4: the template had no general insert-if-absent.** `INSERT … ON CONFLICT` existed only inside two pass 58 services, for their own tables.

## 3. Additions not in the plan or amendments (P60)

Each is marked in the plan.

1. **`IApprovable<TSelf>`.** The plan put a `static abstract ApprovalDocumentType` on `IApprovable`. That makes `IApprovable` unusable as a type argument (CS8920), so the code moved to a self-typed `IApprovable<TSelf>`. Approval commands require it, and the guard checks it.
2. **A document outside the approver's read set is not found.** The header is loaded through the branch filter, so a user of branch 1 approving a branch-2 document gets `NotFoundException` and learns nothing about it. Rule 5's wrong-branch refusal applies to an approver who can read both branches. Row 7 asserts both.
3. **Deactivating a limit uses the add/edit command** with `IsActive = false`, as for branches. There is no separate deactivate command.
4. **Two Application services:** `IApprovalDocumentTypes` (the codes in the model) and `IApprovalRoles` (role existence, and whether a role is `Admin`). They keep Identity and the EF model out of Application.
5. **`ApprovalPermissions.DiscoverLoaded()`** finds `[ApprovalPermission]` constants in every loaded application assembly, so a module's permissions are found in any layer, and the tests' too.
6. **Exception text in ten failure results in mail and storage.** The D1 scan found `ex.Message` in failure results in `LocalDiskFileStorage`, `AzureBlobFileStorage`, `MailgunMailService`, `SinkMailService` and `MailTemplateRenderer`.
   - Storage now logs through `FaultReference` (an optional logger was added to both storage constructors) and returns a reference.
   - Mail returns "The reason is in the application log"; it already logged.
   - `MailDeliveryTests` was updated to match.
7. **Module configurations and pages live under `Modules/<M>`** in Infrastructure and Server.UI, so one rule (the namespace segment after `Modules`) places every type in its module. That is the rule that catches a Razor component (C2 case e). The configuration scan became "everything except the log configurations".
8. **`ToTable(name)` records an explicit null schema.** EF reports it with configuration source Explicit, so "named by hand without a schema" is tested by value, not by source. A module entity with a null schema always gets its module's.
9. **Test users are created active.** `ApplicationUser.IsActive` defaults to false, and five test fixtures created users without setting it. The loader now gives such users no context, so 30 tests failed until the fixtures set `IsActive = true`. No real path creates a signed-in user who is inactive: sign-in refuses one, and the Users page sets it.
10. **A nested request's rejection keeps its message but not its code** at the outer caller (README, "Errors").
11. **The hub filter's fallback must not swallow its own refusal.** Its catch-all, "reload failed, use the snapshot", now excludes `HubException`.
12. **Reactivation is seen at once.** Any change to `IsActive` or `LockoutEnd` invalidates the cached context; only a loss of access bumps the security stamp.
13. **`TemplateEntities`.** The list of the template's own entity types moved from a test into Infrastructure, because the guard needs it at startup. `GxTableNamingTests` now reads it.
14. **Pass 59's test entities are in a test module** (`tdf`), since every application entity now needs one. Two pass 59 tests that wrote SQL against `core."TBL_TEST_LEDGER_ROW"` now name `tdf`. `ModelHarness` drops every schema the model uses.

## 4. Decision 15: how the save-time detection tells the two lockouts apart

**By value, not by caller.**

- An administrator's lockout, which deactivation also sets, is **permanent**: `UserAccess.AdministrativeLockoutEnd = DateTimeOffset.MaxValue`.
- Identity's automatic lockout after failed sign-ins is always **time-limited**: `UtcNow + DefaultLockoutTimeSpan`, five minutes in the template.
- Startup fails if `DefaultLockoutTimeSpan` is not positive and at most ten years. So no failed sign-in can produce the permanent value.
- `UserAccess.HasAccess` is "active, and not permanently locked". The `SaveChanges` override, the loader, the cookie check and the revalidation backstop all use it.
- A caller-set "administrative" scope was considered and rejected. An administrative path that forgot to set it would leave the session open with nothing failing.

## 5. Amendments and their tests

| | Applied | Tests |
|---|---|---|
| C1 Approvals without branches | The header's branch scope decides. A branch-scoped header needs an `IBranchCommand` (startup), rule 5 runs, and branch rows can apply. An unscoped header needs no branch command, skips rule 5, and uses only rows with no branch. | T34, T35 (with T13), T14 |
| C2 Rule 1's hard cases | Each case is its own compiled type in `tests/ArchitectureSamples`; the Razor ones are real components. Compiler-generated types count against their outermost declaring type. | T36 to T41, T19 |
| C3 No edits to the shared context | Rule 10. `Set<T>()` on `IApplicationDbContext`. The README checklist. A module entity with no DbSet is mapped, filtered, guarded and written. | T42, T43 |
| C4 Insert-if-absent | `IInsertIfAbsent` beside `PostingSql`, on rule 5's list, documented beside the lock order. It runs the branch grant, the decimal kinds and the audit stamps itself, and refuses the types it cannot honour. | T45, T46 |
| Decision 15 | §4 above. | T31, T44 |

## 6. Tests added (plan §5)

**Application.IntegrationTests, 30 tests,** on pass 59's test model unless noted.

- `DataFoundations/ApprovalTests.cs`: rows 1 to 14, 34 and 35 (15 tests).
- `ModuleSchemaTests.cs`: rows 16 (two) and 17.
- `StructureTests.cs`: rows 43, 45 and 46 (six).
- `CacheRuleTests.cs`: row 21 (four).
- `UserAccessTests.cs`: row 32 (two).

**Application.UnitTests, 20 tests.**

- `Pipeline/ErrorHandlingTests.cs`: rows 28 to 30 (ten).
- `DataFoundations/ModuleGuardTests.cs`: rows 15, 16 and 20 (eight).
- `DataFoundations/ApprovableGuardTests.cs`: row 14 (two).

**Infrastructure.UnitTests, 4 tests.**

- `Persistence/ConfigurationScanTests.cs`: row 18 (two).
- `GxTableNamingTests` gained two: a hand-named table in a module goes to the module's schema, and an entity with no module gets no schema.

**Server.UI.IntegrationTests, 33 tests.**

- `Architecture/ModuleBoundaryTests.cs`: rows 19 and 36 to 41 (15).
- `Architecture/ArchitectureRulesTests.cs`: rows 22 to 26 and 42 (12).
- `Architecture/MenuContributorTests.cs`: row 27 (two).
- `UserAccessSessionTests.cs`: rows 31 (two) and 44 (two).

**Row 33:** the pinned suites, unchanged and green.

**Existing tests edited**, none of them pinned. Each edit follows from the plan or a decision:

- `DataModelGuardTests`: its samples share a test module.
- `GxTableNamingTests`: `core` became a sample module's schema, and the template list is read from `TemplateEntities`.
- `TestModel.cs`, `ModelHarness`, `DataFoundationsTests`: P60 items 13 and 14.
- `DataFoundationsModelTests` T27: a user with no context is now refused and disconnected, not let through without one. This is Part D item 4, deliberately.
- `RequestAuthorizationRegistryTests`: 29 request types become 32.
- `MailDeliveryTests`: P60 item 6.
- `Testing.cs`, `TenantCommandTests`, `UserContextAllowedTenantsTests`, `UserRoleChangeSecurityStampTests`: users are created active (P60 item 9).

## 7. Not done, recorded

- **No deadlock-retry test for approvals.** Row 9 covers two concurrent approvals and the lock. "A deadlock retry re-runs the check" follows from the behaviour sitting inside `TransactionBehaviour`, and pass 58's T11 covers the retry itself. Nothing induces a deadlock in an approval.
- **No bUnit test renders the Approval Limits page or its dialog.** The browser check loaded the page; the command is covered through its permission and registry tests. The page was not exercised with a real approvable document, because the template ships none.
- **The browser check did not show an over-limit refusal on a page.** The template has no approvable document or approval page; the refusal messages are asserted in rows 1 to 7 and 34.
- **No maker-checker on approval limits** (decision 10). The administrator can raise another user's limit; every change is audited.
- **No code generator or `dotnet new gx-module` item template** (decision 16). The README has the checklist.
- **No health checks, CI, Excel, printing or persistent Hangfire.** These are pass 61.
- **Npgsql 11:** unchanged from pass 59. Stay on 10.x.
- **Two commits, not one.** The report needs the code commit's hash and the smoke result.

## 8. Browser check (plan §4 step 6)

**Passed.** The app was run from the repository at the code commit (`dotnet run`, PostgreSQL on the test server, a fresh database `gx_p60_browser`). It was driven by headless Chrome through the local `playwright-core`, with two browser contexts.

1. **Two users signed in.** The administrator signed in with the bootstrap password. A second user, "clerk", was added by copying the administrator's row in SQL (same password hash, no roles, active), and signed in in the second context. The clerk reached the dashboard.
2. **The administrator deactivated the clerk** on the Users page: the clerk's checkbox, "Deactivate selected", "Confirm". The page said "The selected users have been deactivated".
3. **The clerk clicked a button on the page they were already on.** The browser went to `/account/login`, and a reload stayed there.
4. **The server log corroborates it.** "User deactivated: clerk" at 20:12:58, then "User p60-clerk-0001 no longer has access; the connection was closed." at 20:13:01, from the hub filter.
5. **The Approval Limits page loads** for the administrator at `/system/approval-limits`. With no approvable document in the template, it says "No document in this application needs approval yet."

## 9. Release

- **Version 1.7.0.** The release notes are in `GX.Blazor.Template.nuspec`, and `AppConfigurationSettings.Version` is 1.7.0.
- **Packed after the commit.** `dotnet pack build/pack.csproj -o .` produced `GX.Blazor.Template.1.7.0.nupkg`. The pack's own check confirmed the package contents, and that **the nuspec records commit `254175ba0a048c555e76eb7ce402f812121e7e46`**.
- **Installed** in the machine's template store, replacing 1.6.0 (`dotnet new uninstall`, then `install`).
- **Smoke test passed:** `tooling/smoke-generate.ps1 -UseInstalled -WorkRoot C:\gx60` ended with "SMOKE PASSED (postgresql)". 114 checks passed and none failed; it read the installed `GX.Blazor.Template.1.7.0.nupkg`.
  - The generated `SmokeApp` builds with 0 errors. That includes `tests/ArchitectureSamples`, so the wrong samples compile in a generated project.
  - Without `GX_TEST_PG`, its suites fail and name the variable.
  - Against the server, all four pass:
    - Application.UnitTests: 601 passed, 14 skipped;
    - Infrastructure.UnitTests: 218 (the SQL Server-only tests are not generated);
    - Application.IntegrationTests: 136;
    - Server.UI.IntegrationTests: 348.

    The architecture tests therefore run on the generated project's own namespace (`SmokeApp.…`).
- **Tag:** `gx-v1.7.0` on `254175ba`.
- **Branch:** the work was done on `pass60-controls-structure` and fast-forwarded into `main`. Nothing was pushed.
