# Pass 60b: application extension points, and 1.7.1

**Date:** 2026-11-11. **Plan:** `docs/passes/pass60b-extension-points-plan.md`, amended with the owner's decisions (§8) and amendments E1 and E2, then implemented in its order of work.
**Commit:** `e9085bd4` "Pass60b-ExtensionPoints and 1.7.1". This report follows in a separate commit, because it quotes that hash and the smoke-test result.
**Database:** PostgreSQL 15.7 at 127.0.0.1:5432 via `GX_TEST_PG`.

## Suites

Each project alone, 0 failed. **Build:** 0 errors.

| Suite | Before (1.7.0, `f9f37ae0`) | After | With the sample module in the repository (trial) |
|---|---|---|---|
| Application.UnitTests | 601 passed, 14 skipped | **623 passed, 14 skipped** | 623 + 14 |
| Infrastructure.UnitTests | 239 | **239** | 239 |
| Application.IntegrationTests | 136 | **155** | 162 (155 + the sample's 7) |
| Server.UI.IntegrationTests | 348 | **359** | 361 (359 + the sample's 2) |

The pinned suites pass **unmodified**: `SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests`, `TransactionalAuditTests` and the publisher suites. `git diff f9f37ae0` touches none of them. Pass 58's `TransactionPipelineTests` (T20's order included) is unchanged and green: the new behaviour is constrained to `IBranchDefiningCommand`.

## 1. What was built

- **Item 5, one default branch.**
  - `SetUserBranchesCommandValidator` refuses branches without a default, a default outside the set, and a default with no branches.
  - The handler asserts exactly one default before it saves.
  - A PostgreSQL partial unique index, `IX_UserBranches_UserId_Default` (migration `OneDefaultBranch`, PostgreSQL only; the other two snapshots are unchanged).
  - `ProvisionAsync` repairs users with assignments and no default.
- **Item 2, module services.**
  - `ModuleServices<TModule>` and `AppLevelServices` (Application, `Common/Modules`), each found by scan at the end of `AddApplication`, `AddInfrastructure` and `AddServerUI`.
  - `ModuleDefinition.Order`.
  - `IModuleServiceCollection.Replace<,>`.
  - `ModuleServiceGuard`: placement, one class per layer, no silent replacement, no EF interceptor, and the `Contracts` check, run at the end of `AddServerUI`.
  - `ModuleServicesResolveTests`.
- **Decision 2 and E2, permissions by attribute.**
  - `[AdministratorGrant]` and `[AdministratorExcluded("reason")]`.
  - `AdministratorPermissionRegistry.DecideByAttribute`, `PermissionFields` and `EffectiveGranted`.
  - `AssertNoDivergence` reads the lists and the attributes together.
- **Item 3, role catalogue.**
  - `AddApplicationRole`, `GrantToRole` and `RoleCatalogue.Resolve`.
  - `EnsureRolesAsync` reconciles catalogue roles (throwing for one Identity refuses to create).
  - Decision 8's Error log on every start.
- **Item 4, branch extension points.**
  - **Events and veto (4a):** `AddEditBranchCommand` is `ITransactionalRequest` and raises `BranchCreatedEvent`, `BranchChangedEvent` and `BranchDeactivatingEvent` (posting events), plus the after-commit `BranchActivationChangedEvent`, whose template handler clears the user contexts.
  - **The grant (4b):** `IBranchDefiningCommand`, `BranchDefinitionGrantBehaviour` (after `BranchAccessBehaviour`), `BranchGrant.BeginDefining`, `ExtendDefining` and `IsAwaitingDefinedBranch`, and a clearer save-rule message.
  - **Tightening (4c):** the dialog validates every registered validator on each field.
  - **Screens (4d):** `BranchFormFields`, and `BranchScreenOptions` with its startup validator, used by the page (redirect, configured dialog) and by the menu entry.
- **Item 1, proof with a module.**
  - T20 rewritten; the request count limited to template requests; three more such assertions fixed (§4).
  - `tooling/sample-module` (module `Sample`, schema `smp`, and an application-level page) with its own tests.
  - The smoke's new step 4b and the sample's alone-run check.
- **E2, architecture rules.** `ModuleBoundaryRule` reports `<M> -> AppLevel` and `AppLevel -> <M>` (except `Contracts` and requests), and `ModuleDeclarationRules` refuses an `AppLevel` entity. New wrong samples are in `tests/ArchitectureSamples`.
- **README:** "One default branch" and "Extending branches" (under Branches), and "Module services", "Roles" and "Application-level code" (under Modules and schemas). The checklist steps 4, 7 and 8 were rewritten, and rules 1 and 8 were extended. The nuspec release notes and `AppConfigurationSettings.Version` are now 1.7.1.

## 2. Additions not in the plan or amendments (P60b)

They are listed in the plan, §3:
1. `BranchCreatedEvent` reads its id from the branch.
2. `ModuleServiceLedger`, and a module may not remove registrations.
3. `AddApplication(IConfiguration?)`.
4. `EffectiveGranted`.
5. The approval-separation log covers every role and direct claims.
6. Rule 1's request exemption includes nested types, and module-to-module commands still fail.
7. Rule 3's blind spot when Infrastructure is added first.
8. The context invalidation runs after the commit, on the background publisher.
9. Test seams.
10. Case-insensitive role names.
11. The smoke gives the sample migration a fixed id.

**Two process notes:**
- **The `OneDefaultBranch` migration was regenerated** after the in-repository sample trial, because restoring the snapshot from git had dropped its index. It was given back its first id, `20261111215854`, because the test databases had already recorded that id.
- **The trial left its `SampleModule` history row and `smp` schema in four repository test databases** (`gx_test_cleanarchitectureblazor_{appint,infra,unit,ui}`), plus the schema alone in `…_appint_model`. The harness then refused them as stale, so every suite failed in its setup. I removed exactly what the trial had added (that history row, and `DROP SCHEMA smp CASCADE`) instead of dropping the databases, then reran the suites.
- **(P60b) The smoke gives the sample migration a fixed id**, `20990101000000_SampleModule`, for the same reason: the generated project's test databases persist between smoke runs, and a new timestamp every release would trip the same check on the next run.

## 3. Amendments and their tests

| Amendment | Built | Tests (plan §5) |
|---|---|---|
| **E1**, a `Contracts` interface that is not a service | `[NotAService("reason")]` (Domain); rule 5 exempts it; a blank reason fails; marked and registered fails; unmarked and unregistered fails, naming it. Nothing is inferred from members. The sample's `IHasBinLabel` uses it in a real project. | Row 18: `ModuleServicesTests.T18_TheContractsCheck_FailsUnregistered_PassesMarked_AndFailsMarkedButRegistered`, `T18_TheTemplateItself_HasNoContractsInterface` |
| **E2**, application-level code | `AppLevel` folders in Application, Infrastructure and Server.UI; `AppLevelServices`; attributes decide any constant; rule 1 both ways; rule 8 refuses an entity; the sample's `SampleTools` page with `Permissions.SampleTools.Use` (`[AdministratorExcluded]`), granted to the sample role by `AppServices`, sending the sample module's command | Row 19: `PermissionAttributeTests` (4). Row 20: `AppLevelBoundaryTests` (5). Row 21: `ModuleServicesTests.T21_…`. S9 in the generated project. |
| **Decision 8** (not as recommended) | Error log on every start, startup continues | Row 8: `RoleCatalogueTests.T08_…`: startup succeeds twice, one Error each time naming the user, role and permission; the policy refuses the administrator; nothing is logged once the membership is removed |

## 4. "Nothing else exists" assertions found, and how each was fixed

Rule: assert the template's own part, never that nothing else is there.

| Test | Found by | Fix |
|---|---|---|
| `ModuleGuardTests.T20_TheTemplatesOwnModel_HasNoModuleProblems` | the brief | Now `T20_TheRealModel_HasNoModuleProblems_AndEveryEntityIsTemplateOrModule`: the guard finds nothing; every entity is template or module; every listed template type is in the model (except the passkey table, which needs the application's service provider). A second test proves the check names a stray entity. |
| `RequestAuthorizationRegistryTests.TheApplicationDeclaresTheExpectedNumberOfRequestTypes` | the plan's check | Counts requests outside `Modules`/`AppLevel` namespaces (still 32); a new test pins the filter. |
| `AdministratorPermissionRegistryTests.TheTwoListsCoverEveryConstantBetweenThem` | the sweep | Covered = the lists plus the attribute decisions. |
| `ProvisioningTests.TheAdministratorRole_HoldsExactlyTheExplicitlyGrantedPermissions` | the sweep | Compares with `EffectiveGranted`. |
| `ProvisioningTests.TheSystemAccount_HoldsTheAdministratorRegistryAsUserClaims` | the sweep | Compares with `EffectiveGranted`. |

The three found by the sweep were fixed by reading, before the sample ran: each compares the administrator's claims with `Granted`, which a module's `[AdministratorGrant]` would exceed. The sample module was then run twice, in the repository (all four suites, table above) and in the generated project (the smoke). Neither run found another one.

## 5. Tests added (plan §5)

| Row | Tests |
|---|---|
| 1, 2 | §4 (T20, two tests; the count, one new test) |
| 3, 4 | `ModuleServicesTests` T03, T04 ×4 |
| 5 | `ModuleServicesResolveTests` (vacuous on the template, by `Assert.Pass`; real in the generated project) |
| 6, 7 | `RoleCatalogueTests` T06 ×2, T07 ×2 (integration); `RoleCatalogueResolveTests` ×7 (unit) |
| 8 | `RoleCatalogueTests.T08` |
| 9 to 12 | `BranchExtensionTests` T09, T10, T11 ×4, T12 ×2 (model harness) |
| 13 | `BranchRuleTighteningTests.T13`; `BranchConfigurationExtensionTests.T13`; `BranchScreenTests.T13` (bUnit) |
| 14 | `BranchScreenTests` T14 ×4 |
| 15, 17 | the existing suites, unchanged and green |
| 16 | `DefaultBranchTests` ×5 |
| 18 to 21 | §3 |
| S1 to S9 | `tooling/sample-module/tests/…/SampleModuleTests` (7) and `SampleModuleUiTests` (2), in the generated project only |

## 6. Not done, recorded

- **Row 10 does not assert that the user contexts were not cleared on a veto.** The clearing is an after-commit event, and a rolled-back unit of work publishes none (pass 58's T-tests cover that), so no separate probe was added.
- **The dialog's field validation was not checked in a browser.** It was checked in bUnit only (row 13). No browser check was run in this pass; nothing in it changes sign-in or the circuit.
- **A module's Infrastructure registration cannot see Application template services in a host that adds Infrastructure first** (P60b 7).
- **The SQLite and SQL Server migrators** get the sample migration in a generated project only if the project still carries them; the PostgreSQL one is asserted.

## 7. Release

**Done.** Tag `gx-v1.7.1` is on `e9085bd4`.

- **Commit:** `e9085bd4` "Pass60b-ExtensionPoints and 1.7.1", committed after the four suites passed on the clean tree.
- **Package:** `GX.Blazor.Template.1.7.1.nupkg`, packed AFTER that commit. Its stamp records `e9085bd485c94684a02e0de64b18be790e7d145e`.
- **Install:** both 1.7.0 and 1.7.1 were uninstalled, then 1.7.1 was installed from that package. `dotnet new uninstall` then listed exactly one GX.Blazor.Template: 1.7.1.
- **First smoke attempt (2026-11-11, `C:\gx60b`):** Claude Code stopped it at step 4 because memory was critically low. Up to that point, 95 checks passed and 2 failed. Both failures were the double install (1.7.0 beside 1.7.1), not the template.
- **Smoke (2026-11-12):** `tooling/smoke-generate.ps1 -UseInstalled -WorkRoot C:\gx60c -KeepOutput`, the script and not the manual fallback, run with nothing else running alongside. Free memory never fell below 3 GB. **SMOKE PASSED (postgresql): 122 checks passed, 0 failed.**
  - The content checks ran against the installed 1.7.1 `.nupkg`. The generated solution builds with 0 errors. There is no ImageSharp in any of the 12 restore graphs.
  - **Step 4b, the sample module:** 17 files were copied in under the namespace `SmokeApp`, including the application-level `SampleTools` page. The solution builds with them. Migration `SampleModule` was added to Migrators.PostgreSQL and Migrators.SqLite, which are the migrators the default project carries. The PostgreSQL migration runs `EnsureSchema("smp")`. The solution builds again with the migrations.
  - The outbound-address guard passes on its own (3). Without `GX_TEST_PG`, all four suites fail and name the variable.
  - **The suites with the module in, against GX_TEST_PG:** Application.UnitTests 623 passed, 14 skipped. Infrastructure.UnitTests 218. Application.IntegrationTests 162. Server.UI.IntegrationTests 361. All have 0 failed.
  - The image upload tests pass on their own (28). The sample tests pass on their own: 7 in Application.IntegrationTests, and 3 in Server.UI.IntegrationTests (the 2 UI tests plus `ModuleServicesResolveTests`).
  - **S1 to S9, by name, in the generated project:**
    - S1 `TheBinTable_IsInTheModulesSchema`: passed
    - S2 `ABinIsCreatedInItsBranch_Announced_AndAUserOfAnotherBranchCannotReadIt`: passed
    - S3 `TheContractsInterface_ResolvesToTheInfrastructureImplementation`: passed
    - S4 and S5 `ProvisioningRunsTheModulesSeeder_AndCreatesItsCatalogueRole`: passed
    - S6 `CreatingABranch_OpensItsDefaultBin_InTheSameTransaction`: passed
    - S7 `TheSampleSection_IsBetweenApplicationAndManagement_OnTheRealHost`: passed
    - S8 `TheModulesBranchValidator_RefusesAReservedCode`: passed
    - S9 `TheApplicationLevelPermission_IsDecidedByAttribute_AndNotGrantedToTheAdministrator`: passed
    - S9 `TheApplicationLevelPage_IsRouted_Gated_AndKeepsTheBoundary`: passed
  - In a fresh `git init`, `appsettings.Development.json` is ignored, and the other three appsettings files are not.
