# Pass 59: data foundations, and 1.6.0

**Date:** 2026-11-11.
**Start:** HEAD `18e300fe` (pass 58, template 1.5.0), clean.
**Commit:** `428d5f0d` "Pass59-DataFoundations and 1.6.0". This report follows in a separate commit, because it quotes that hash and the smoke-test result.

**Plan:** `docs/passes/pass59-data-foundations-plan.md`. It was amended first with the owner's 13 decisions (§8, all marked decided) and amendments B1 to B5, then implemented in its own order of work (steps 0 to 9). Additions made during implementation are marked **(P59)** in the plan and listed in §3.

**Suites** (PostgreSQL 15.7 at 127.0.0.1:5432 via `GX_TEST_PG`, each project alone):

| Suite | Result | Before this pass |
|---|---|---|
| Application.UnitTests | **581 passed, 14 skipped** | 574 + 14 |
| Infrastructure.UnitTests | **235** | 229 |
| Application.IntegrationTests | **106** | 80 |
| Server.UI.IntegrationTests | **315** | 311 |

0 failed. **Build:** 0 errors.

The pinned suites pass **unmodified**: `SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests`, `TransactionalAuditTests`, the publisher suites, and pass 58's `TransactionPipelineTests` (T20's behaviour order included). `git diff 18e300fe 428d5f0d` touches none of them.

---

## 1. What shipped

**1.6.0 ships all four parts, branch UI included** (decision 13). Nothing was deferred to 1.6.1.

| Area | Files |
|---|---|
| A. Precision | `Domain/Common/Precision/DecimalKindAttribute.cs` (`[Money]`, `[Quantity]`, `[UnitCost]`, `[Rate]`, `[Approximate]`); `Application/Common/Numerics/GxRound.cs`; the convention in `Infrastructure/Persistence/Rules/DataModelConventions.cs`; the scale check in `DataRules` |
| B. Concurrency | `Domain/Common/Entities/IVersioned.cs` (`IVersioned`, `BaseVersionedEntity`); `Application/Common/Concurrency/Versioning.cs` (`IVersionedEdit`, `Saved<TId>`, `ApplyLoadedVersion`, `UseCurrentVersion`, `SavedVersionOf`, `VersionChecks`, `ConcurrencyConflict`); `IApplicationDbContext.Entry`; the version check in `ValidationBehavior`; `PicklistSet` versioned end to end (DTO, command, handler, grid Reload) |
| C. Immutability | `Domain/Common/Entities/IPostable.cs` (`IAppendOnly`, `IPostable`, `IPostableLine<T>`, `[AllowedAfterPosting]`); `Rules/DataRules.cs` (run by an `ApplicationDbContext.SaveChanges` override, before every interceptor); `Interceptors/GuardedTableCommandInterceptor.cs`; `Rules/AppendOnlyMigrationExtensions.cs` (`CreateAppendOnlyTrigger`); `Rules/DataModelGuard.cs` (startup, model and triggers) |
| Users | `Users.razor`: no Delete; "Deactivate selected" with the last-administrator guard checked one user at a time. `Permissions.Users.Delete` retired. Saving a Deleted `ApplicationUser` throws. `AuditTrail.UserId` is `ON DELETE RESTRICT`. |
| D. Branches | `Branch`, `UserBranch` and their configuration; `IMustHaveBranch`, `IBranchVisibility<T>`; `QueryFilters.Branch` and `ApplyBranchFilters`; `BranchAccess` on `UserContext`; `UserContextLoader`, `SystemContext`, and `UserContextHubFilter` (per-invocation refresh); `IUserContextInvalidator`; `IBranchCommand`, `BranchGrant`, `BranchAccessBehaviour`; `IBranchQueryScope.AcrossBranchesAsync`; `Permissions.Branches` (View, Create, Edit, ViewAll, WriteAll); `Features/Branches` (queries, `AddEditBranchCommand`, `SetUserBranchesCommand`, `IUserBranchDirectory`) |
| D. UI | `Pages/Branches` (list, add, edit, deactivate); `UserBranchesSection` on the user dialog; `BranchSelector` in the top bar; `ActiveBranchState` with `ProtectedSessionActiveBranchStore`; both sign-out paths forget the stored branch |
| Model | Migration `DataFoundations` for PostgreSQL, SQL Server and SQLite: `Branches`, `UserBranches`, the `AuditTrail` foreign key, and the deletion of `Permissions.Users.Delete` claims. No `xmin` column: see P59 §3.5. |
| Docs | README "Money and quantities", "Concurrency", "Posted records", "Branches"; the 1.6.0 release notes; `AppConfigurationSettings.Version` set to 1.6.0 |

## 2. Conflict found, and decided by the owner

**The planned save-time version check broke five pinned tests.**

- **The cause.** Plan §3.2 made any save of a Modified or Deleted `IVersioned` entity fail unless a version had been applied. Decision 5 versions `PicklistSet`. `SaveChangesInterceptorRegressionTests` and `TransactionalAuditTests` edit and delete a `PicklistSet` through a plain context, so five of their tests failed.
- **What I did.** I stopped and asked rather than change a pinned suite or work around it.
- **The owner's choice: the check applies inside Mediator requests only.** `ValidationBehavior`, which runs for every request, marks the call chain (`VersionChecks.EnterRequest`).
  - Any handler that saves a versioned entity without applying a version still fails, whatever its command type.
  - Direct context use outside Mediator (seeding, infrastructure services, the pinned suites) is not checked. EF still compares against the version it read.
- **Where it is recorded:** the plan (§3.2, P59) and the README "Concurrency".

**No amendment (B1 to B5) conflicted with the code.**

## 3. Additions not in the plan or amendments (P59)

Each is marked in the plan where it applies.

1. **The version check is scoped to Mediator requests** (§2 above; owner decision).
2. **`IVersionedEdit` carries `bool IsEdit`.** The validator must tell an edit from a create, and the interface cannot know the key's name.
3. **A delete by id uses `UseCurrentVersion`** (`DeletePicklistSetCommand`). It carries no user-loaded version.
4. **The loader reads `ViewAll` and `WriteAll` from claims directly.** It uses the user's and their roles' permission claims, through the scope's own `ApplicationDbContext`, not `IIdentityService` or the unit of work's context.
   - The loader's existing test containers register no `IIdentityService`; eight tests failed until this changed.
   - The claims are exactly what the policies of the same names require.
5. **The PostgreSQL migration's C# lists an `AddColumn` for `xmin`, but the SQL does not.** EF's differ sees a property; Npgsql's SQL generator skips system columns. Checked with `dotnet ef migrations script`. Test 9 asserts on the generated SQL.
6. **No filtered unique index for "one default branch per user".** No boolean filter is valid on all three providers. `SetUserBranchesCommand`, the only writer, keeps the rule.
7. **The branch tables are template tables** in the default schema. `GxTableNamingTests` lists them; that test exists to require exactly this.
8. **The test reset no longer TRUNCATEs a trigger-guarded table.** The append-only `TRUNCATE` trigger refuses it. `PostgresTestDatabase` now disables the table's user triggers around a `DELETE`, and `SharedDatabaseTests` pins the new form (its probe gained a `TRUNCATE` trigger).
9. **A property with two kinds is reported by the guard, not by reflection.** `GetCustomAttribute` threw `AmbiguousMatchException` in the middle of model building. The kind lookup now returns none for two, and the guard names the property.
10. **The grant extension is per branch, not per row.** After a transfer is saved, any row of the receiving branch may be written in that command, because a row cannot say which document it derives from. Test 24 asserts the boundary that holds: before the transfer the receiving branch is refused, and a third branch is refused throughout. Plan row 24's last sentence is corrected accordingly.
11. **The cache invalidation points.**
    - Contexts carry the FusionCache tag `user-context`.
    - `IUserContextInvalidator` is called by `SetUserBranchesCommand`, by `AddEditBranchCommand` when activation changes, and by `PermissionAssignmentService` on user and role permission changes.
12. **The hub filter's reload has two fallbacks.** If the reload fails, it uses the snapshot and logs a warning. A user who no longer exists gets no context.
13. **The stored active branch is keyed by user.** Both sign-out paths remove it: the sign-out form's `onsubmit` and `gxIdleTimeout.js`.
14. **The user-dialog section saves on its own** ("Save branch assignments"), and only for an existing user.
15. **`DataRuleViolationException`** (an `InvalidOperationException`) is the one exception type for every refused save, so tests and callers can tell a rule from a fault.
16. **`BranchAccess.IsSystem`.** System work needs no command-level grant.
17. **A test model and harness.** `TestModelDbContext` adds the test entities before the base model is built, so the conventions apply to them. `ModelHarness` gives it its own database (`gx_test_cleanarchitectureblazor_appint_model`, schemas rebuilt with `EnsureCreated`, never the database dropped) and its own service provider, with the factory swapped.
18. **Deactivate-selected checks the last-administrator guard one user at a time.** The old delete checked every user first and then deleted, so a selection of every administrator could pass.

## 4. B4: is the version available to the handler inside a unit of work?

**Partly, and the plan says so rather than working around it.**

- **What EF does.** It reads the new `xmin` back in the same statement as a save (`RETURNING xmin`). A handler that saves therefore sees the new version at once.
- **Why that is not enough.** Inside a unit of work, `TransactionBehaviour` saves again after the handler returns, and posting-event rounds may update the same row again. A version copied into the result by the handler would be stale at commit.
- **How the behaviour supplies it.** `Saved<TId>` is a reference type registered with the context (`SavedVersionOf`). The `SaveChanges` override refreshes it after every save, so the result holds the committed version, and an idempotent response is recorded after the last save.
- **The proof.** Test 34 (second case): a posting handler updates the row after the handler has returned. The returned version equals the stored one and differs from the one the handler's own save produced.

## 5. Amendments and their tests

| | Applied | Test |
|---|---|---|
| B1 Write-all | `Permissions.Branches.WriteAll`, checked fresh in `BranchAccessBehaviour` for an active branch. Excluded from the administrator registry, with a reason. `BranchAccess.WritesAll`. Refusal text unchanged. | T31; T22 unchanged |
| B2 Allowed line properties | `[AllowedAfterPosting]` on `IPostableLine<>` properties; a Modified line of a posted header passes on allowed properties only. The header touch still applies. Guard updated. | T32 |
| B3 Exemption covers lines | The lines of a header posted in the current unit of work may be Modified, not Added or Deleted, until commit. | T16/T33 |
| B4 Saves return the version | `Saved<TId>`, `SavedVersionOf`, refreshed after every save; the `PicklistSet` command and grid and the branch dialog use it; README "Concurrency". See §4. | T34 (two cases) |
| B5 Reading across branches | `IBranchQueryScope.AcrossBranchesAsync(query, permission)`, the only sanctioned removal of the branch filter. | T35 |

## 6. Tests added (plan §5)

**Application.IntegrationTests, 26 tests.**

- `DataFoundations/DataFoundationsTests.cs`, on the test model: rows 4, 10, 12, 13, 14, 15, 16 with 33, 17, 19, 21, 22, 23, 24, 26, 28, 31, 32 and 35 (18 tests).
- `DataFoundations/PicklistVersionTests.cs`, on the ordinary harness: rows 6, 7, 8, 11, 20, 27 and 34 (two cases), 8 tests.

**Application.UnitTests, 7 tests.** `DataFoundations/DataModelGuardTests.cs`: rows 1, 2, 3, 5, 18 and 25, plus "the template's own model is sound".

**Infrastructure.UnitTests, 4 tests.** `Persistence/DataFoundationsModelTests.cs`: row 9 (the model, and the migration SQL) and row 27's hub-filter refresh (two cases). `GxTableNamingTests` gained two data rows.

**Server.UI.IntegrationTests, 4 tests.** `ActiveBranchStateTests.cs`: row 29.

**Row 30:** the pinned suites, unchanged and green.

**Existing tests edited**, none of them pinned. Each edit follows from a decision or an amendment:

- `AddEditPicklistCommandTests`, `DeletePicklistTests`, `SharedPicklistWriteTests` and `SharedPicklistCreationTests`: the edit command returns `Saved<int>`, and an edit carries `Version`. Two `FindAsync(result.Data)` calls would still have compiled but failed at run time; they now pass `.Id`.
- `GenericKeyEntityTests`: `PicklistSet` derives from `BaseVersionedEntity`, which is itself a `BaseAuditableEntity<int>`.
- `AdministratorPermissionRegistryTests`: `Branches.WriteAll` is excluded (B1).
- `RequestAuthorizationRegistryTests`: 25 request types become 29.
- `GxTableNamingTests`: the two branch tables.
- `SharedDatabaseTests`: the reset form (P59 §3.8).
- `PermissionAssignmentGuardTests` still uses the literal `"Permissions.Users.Delete"` as a permission nobody holds. It passes, and now names a retired permission. Left as it is.

## 7. Not done, recorded

- **No architecture test that `AcrossBranchesAsync` is the only removal of the branch filter.** B5 leaves that to the architecture-test pass; the helper makes every use searchable.
- **No triggers on posted documents** (decision 6).
- **Deactivation does not end a user's live circuits.** Pass 59 did not change deactivation itself.
- **No component (bUnit) tests render the Branches page, the user-dialog section or the switcher.** `ActiveBranchState`'s behaviour is tested directly. The Users page's lack of Delete is asserted through the permission and access-right types, not a render.
- **The application was not driven in a browser this pass.** The UI suites, the smoke test's generated build and suites, and the startup path in `Server.UI.IntegrationTests` (which runs `InitialiseAsync`, and with it both guards) are the evidence.
- **Npgsql 11:** see the README "Concurrency". T11 pins today's behaviour so an upgrade fails loudly.
- **Two commits, not one.** The report needs the code commit's hash and the smoke result, so it follows in its own commit. The package and the tag name the code commit.

## 8. Release

- **Version 1.6.0.** It ships all four parts, branch UI included (decision 13). The release notes are in `GX.Blazor.Template.nuspec`, and `AppConfigurationSettings.Version` is 1.6.0.
- **Packed after the commit.** `dotnet pack build/pack.csproj -o .` produced `GX.Blazor.Template.1.6.0.nupkg`. The pack's own check confirmed the package contents, and that **the nuspec records commit `428d5f0de879f167953841dc74681de278131eb0`**.
- **Installed** in the machine's template store, replacing 1.5.0 (`dotnet new uninstall` and then `install`).
- **Smoke test passed:** `tooling/smoke-generate.ps1 -UseInstalled -WorkRoot C:\gx59` ended with "SMOKE PASSED (postgresql)", 114 checks ok and none failed.
  - The generated `SmokeApp` builds with 0 errors.
  - Without `GX_TEST_PG`, its suites fail and name the variable.
  - Against the server, all four pass: Application.UnitTests 581 passed and 14 skipped; Infrastructure.UnitTests 214 (the SQL Server-only tests are not generated); Application.IntegrationTests 106; Server.UI.IntegrationTests 315.
- **Tag:** `gx-v1.6.0` on `428d5f0d`.
