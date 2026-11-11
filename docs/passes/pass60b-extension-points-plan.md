# Pass 60b: application extension points (plan)

**Status:** written 2026-11-11 at HEAD `f9f37ae0` (pass 60 report; code at `254175ba`, template 1.7.0, tag `gx-v1.7.0`). Amended the same day with the owner's decisions (§8) and amendments E1 and E2, then implemented in this pass. Text marked **(E1)** or **(E2)** comes from the amendments. Text marked **(P60b)** was added during implementation, where the code required something the amendments did not say; each (P60b) addition is listed in the pass report (`docs/passes/pass60b.md`).
**Database:** PostgreSQL only. Npgsql-only code sits under `#if (UsePostgreSql)`. The other two providers still build, but they are not targets.
**Source:** the first application's build plan (Spare Parts Management System: `org cat ptn inv pur sal cnt acc`), which found five gaps in 1.7.0. Each gap was checked in code before it was planned (§1). Two related gaps found during the check are in §1 and §7.

**Unchanged:**
- Pass 58's unit of work, idempotency, numbering and row locks.
- Pass 59's save rules, branch filter, `BranchAccessBehaviour` and `AcrossBranchesAsync`.
- Pass 60's approval policy, module schemas, architecture rules, error handling and session ending.
- The pinned suites must pass **unmodified**: `SaveChangesInterceptorRegressionTests`, `InterceptorOrderingTests` (exactly two `ISaveChangesInterceptor`s), `TransactionalAuditTests` and the publisher suites.

**Ships as:** template 1.7.1 (decision 1).

**Principle for every item:** an application adds a module by **adding files under `Modules/<M>/`, never by editing a template file**, and anything it leaves out fails at startup or in a shipped test, naming what is missing. **(E2)** The same holds for the application's own code that belongs to no module, which lives under `AppLevel/` (§3.6).

## 1. What is wrong today (verified in code)

### Item 1: a generated project with a module fails its own tests

- **T20 asserts that the real model contains nothing but template entities.** `ModuleGuardTests.T20_TheTemplatesOwnModel_HasNoModuleProblems` (`tests/Application.UnitTests/DataFoundations/ModuleGuardTests.cs:183-190`) checks two things:
  - `FindProblems` is empty. This is correct.
  - every non-owned entity of `ApplicationDbContext` passes `TemplateEntities.Contains` ("the template ships no module entity"). This fails as soon as the application has its first `[Module<>]` entity.
- **It is not the only test of that kind.** `RequestAuthorizationRegistryTests.TheApplicationDeclaresTheExpectedNumberOfRequestTypes` pins `ExpectedRequestTypeCount = 32` over the **whole** Application assembly (`RequestAuthorizationRegistryTests.cs:42, 52-59`). A module's first command makes it 33, and the test fails. The comment at `:44-50` says the count is meant to force a conscious update. In a generated project, that means editing a template test for every request.
- **The hole that hid both.** `tooling/smoke-generate.ps1` generates a project, builds it and runs the four suites (`:530-620`). However, it runs them on the project **as generated**, which has no module. No release has ever run the suites on a project that has a module. Pass 60's own samples (`GuardAlphaModule`, `SampleModule`, `tests/ArchitectureSamples`) live in **test** contexts and assemblies, never in the real `ApplicationDbContext` or the real `src` assemblies.
- **A module also has to edit a template file to compile its permissions.** Every `Permissions.*` constant must be in `AdministratorPermissionRegistry.Granted` (`:67`) or `Excluded` (`:212`), or `AssertNoDivergence` fails startup (`:249-258`). README step 4 of the module checklist says this. Both lists live in one template file, so the smoke's sample module could not be added without patching it (§7.2).

### Item 2: no module service-registration hook

- **Each layer registers its own services in one shared method.** These are `AddApplication` (`Application/DependencyInjection.cs:14-71`), `AddInfrastructure` (`Infrastructure/DependencyInjection.cs:48-60`, business services `:360-380`) and `AddServerUI` (`Server.UI/DependencyInjection.cs:57-…`).
- **What is already found by scan:**
  - posting-event handlers, in the Application assembly only (`AddPostingEventHandlers`, `PostingEventDispatcher.cs:49-63`, called at `Application/DependencyInjection.cs:56`);
  - validators (`AddValidatorsFromAssembly`, `:18`);
  - Mediator handlers;
  - `IDataSourceService<>`, in Infrastructure only (`Infrastructure/DependencyInjection.cs:415-431`);
  - menu contributors (`Server.UI/DependencyInjection.cs:46-56`, called at `:126`).
- **What is not:**
  - **A module's `Contracts` implementations.** The README puts their interfaces in `Application/Modules/<M>/Contracts` and their implementations in `Infrastructure/Modules/<M>/`, but nothing registers them. An unregistered one fails only when it is first resolved.
  - **Seeders.** `ITenantSeeder` is "registered with `services.AddScoped<ITenantSeeder, MySeeder>()`" and run "in registration order" (`ITenantSeeder.cs:6-7`, `TenantSeedRunner.cs:6`). The only place to call that is the shared `AddBusinessServices` (`Infrastructure/DependencyInjection.cs:366-367`).
  - Any other service a module owns.
- **Layer order differs between the app and the tests.** `Program.cs:17-19` calls Application, then Infrastructure, then Server.UI. `Application.IntegrationTests/Testing.cs:101-102` calls Infrastructure first, then Application, and never calls Server.UI. A hook must not depend on the order of the layers.
- **A module must never add an `ISaveChangesInterceptor`.** `InterceptorOrderingTests` (pinned) asserts exactly two (`:63`).

### Item 3: no application role catalogue

- `Roles` has two constants, `Admin` and `Basic` (`Application/Common/Constants/Roles.cs:18-22`). Its doc comment says to keep it "in step" with `EnsureRolesAsync`.
- `EnsureRolesAsync` hard-codes both roles (`ApplicationDbContextInitializer.cs:168-180`). Admin gets `AdministratorPermissionRegistry.Granted`, and Basic gets `BasicPermissions` (`:129-133`).
- `EnsureRoleAsync` already reconciles the way the brief asks: it creates a missing role, grants missing claims, never revokes and logs only on change (`:185-229`). **But when a role cannot be created, it logs and returns** (`:200-206`). That conflicts with "an omission fails loudly" (§7.3).
- **Pass 60's approval rules that a catalogue must respect:**
  - Approval permissions are found by `[ApprovalPermission]` (`ApprovalContracts.cs:35-67`).
  - The registry fails startup if one is granted to the administrator (`AdministratorPermissionRegistry.ValidateApprovals`, `:261-282`).
  - `AdministratorProtectionService.EnsureApprovalSeparationAsync` refuses a role set that makes an `Admin` member hold an approval permission (`:98-125`). It runs only on the UI's role-assignment path, not at provisioning.
  - The send-time check in `ApprovalBehaviour` is the guarantee.
- **Tests and the app reach roles through `ProvisionAsync`** (`TenantSeederTests.cs:36-39`, `SystemContextTests.cs:38`). A catalogue reconciled there reaches both.

### Item 4: branch extension points

- **`AddEditBranchCommand` is not transactional and raises no events** (`Application/Features/Branches/Branches.cs:57-118`):
  - It is `IRequest<Result<Saved<int>>>, IVersionedEdit` only, and saves once.
  - It calls `userContextInvalidator.InvalidateAll()` after the save when `IsActive` changed (`:105, 115`).
  - It checks the code for duplicates by query (`:89-90`), and `BranchConfiguration` backs that check with a unique index (`:13`).
- **Pass 58's event mechanism fits the brief, with one problem: timing.**
  - A `PostingEvent` added with `AddDomainEvent` is dispatched inside the transaction, and a handler vetoes by throwing `PostingRejectedException` (`PostingEvent.cs:3-25`, `IPostingEventHandler.cs:10-12`).
  - `Branch` derives from `BaseVersionedEntity` and so can raise events (`Domain/Entities/Branch.cs:14`).
  - **The timing problem:** the events are dispatched by `TransactionBehaviour.DrainPostingEventsAsync` **after the handler has returned** (`TransactionBehaviour.cs:21-22, 146, 190-210`). Anything the handler scopes with `using` has ended by then.
- **The pass 59 grant cannot cover a new branch today:**
  - `BranchGrant.Begin(id)` is opened only by `BranchAccessBehaviour`, which is constrained to `IBranchCommand` and refuses `BranchId <= 0` (`BranchAccessBehaviour.cs:22-23, 49-63`).
  - `BranchGrant.Extend` "adds branches to the innermost active grant. **No-op without one**" (`BranchContracts.cs:64-71`).
  - `AddEditBranchCommand` names no branch. When it creates one, the new id does not exist until the save.
  - So a branch-scoped row written for the new branch, by the handler or by an event handler, fails at save with "saved outside a branch command" (`DataRules.cs:283, 313-315`).
  - Nested commands push their own grant node, popped when they end (`BranchContracts.cs:28-30`).
- **Tightening the rules already works on the server. It has no test or documentation.**
  - `ValidationBehavior` runs **every** `IValidator<TRequest>` registered (`ValidationBehavior.cs:13, 32-38`).
  - `AddValidatorsFromAssembly` scans the Application assembly, which holds `Application/Modules/<M>/`.
  - So a module's own `AbstractValidator<AddEditBranchCommand>` already runs beside the template's (`Branches.cs:70-77`).
  - **The dialog does not see it.** `BranchFormDialog` validates only `Required`/`MaxLength="20"` attributes on its fields (`BranchFormDialog.razor:7-8`). A module's rule therefore shows only as the error snackbar after submit (`:46-49`).
  - A unique **name** also needs an index. `Branch` is a template entity configured in a template file.
- **The screens are fixed:**
  - `Pages/Branches/Branches.razor` is routed at `/system/branches` (`:1`) and opens `BranchFormDialog` by type (`:77`).
  - The menu entry is hard-coded in `MenuService` (`:124-129`).
  - Blazor refuses two components with one route (an ambiguous-route error at run time), so an application cannot simply add its own page at `/system/branches`.
- **The template has exactly one writer of `Branch`:** this command. The initializer seeds no branch, and nothing else adds one.

### Item 5: a user can have branches and no default

- **The server allows it.** `SetUserBranchesCommandValidator` allows `DefaultBranchId == null` with any `BranchIds` (`Branches.cs:137-139`), and the handler then saves every assignment with `IsDefault = false` (`:158-161`).
- **Only the UI prevents it.** `UserBranchesSection` sends the lowest-id branch when none was starred (`UserBranchesSection.razor:82`).
- **Nothing in the database prevents it either.** `BranchConfiguration.cs:31-34` says "one default per user" is kept by the command, not by a filtered unique index, because no boolean filter suited all three providers.
- **Readers already cope with no default.** `UserBranchDirectory.DefaultBranchIdAsync` (`:46`) and `UserContextLoader` (`:203`) return null, and `ActiveBranchState` falls back to the first writable branch (`:92, 127`). The rule is about data integrity, not a crash.

## 2. Target behaviour

1. **Proof on every release.** The smoke test adds a sample module to the generated project: an entity, a configuration, a permission, a role, a menu contributor, a posting-event handler, a `Contracts` interface with its implementation, and its own tests. It then runs all four suites green. The template's tests assert "the template's own part is as expected" and never "nothing else exists".
2. **Module services.** Each module registers its services in its own folder, once per layer. Template services cannot be replaced silently. An unregistered `Contracts` interface fails startup.
3. **Roles.** A module declares roles and grants in its own files. Startup reconciles them (grant missing, never revoke). Startup fails on an unknown permission, an entry naming the administrator role, or an approval permission reaching an administrator.
4. **Branches.**
   - Saving a branch is one unit of work that raises `BranchCreated`, `BranchChanged` and `BranchDeactivating`. Any handler can veto.
   - Handlers of the command that saves a branch may write that branch's rows.
   - A module's validators tighten the rules, and the dialog shows them on the field.
   - A module can replace the page, the dialog and the menu entry, and the template's page then redirects to the replacement.
5. **Default branch.** A user with at least one assignment has exactly one default, enforced by the command, backed by a PostgreSQL partial unique index, and repaired once at startup for existing rows.
6. **(E2) Application-level code.** Code that belongs to no module has one home per layer (`AppLevel/`). It decides its permissions by attribute and registers its services through the same hook, with the same failures. It may send module commands and use module `Contracts`; no module may reference it; it holds no entities.

## 3. Components

**(P60b) Additions made during implementation, collected here** (each is also in the pass report):

1. **`BranchCreatedEvent` reads its id from the branch.** The event is raised before the save, when the identity value does not exist yet, so `BranchId` reads `Branch.Id` when a handler asks.
2. **`ModuleServiceLedger`.** A singleton recording what each owner registered and replaced. Startup logs the replacements from it; `ModuleServicesResolveTests` resolves from it. A module's collection also refuses `Remove`, `Clear` and overwriting a registration: replacing is `Replace<,>` only.
3. **`AddApplication(IConfiguration?)`.** The Application layer had no configuration to hand to its modules; `Program` now passes it. Existing callers are unchanged.
4. **`AdministratorPermissionRegistry.EffectiveGranted`** (the list plus `[AdministratorGrant]`) is what the administrator role and the system account receive. Three template tests compared against `Granted` and were changed to it (§7.1, sweep).
5. **The approval-separation log covers every role and direct claims**, not only catalogue roles (§3.3).
6. **Rule 1 for the application level.** "Send module commands" is read as: a Mediator request type and the types nested in it. A module sending another module's command is still a violation (a new wrong sample proves it).
7. **The guard's rule 3 sees what was registered before the module ran.** A test host that adds Infrastructure before Application (`Testing.cs`) cannot detect an Infrastructure module re-registering an Application template service; `Program`'s order (Application first) can. Recorded, not changed.
8. **The users' contexts are dropped after the commit by the background publisher**, so a few milliseconds later than in 1.7.0. Writes never depended on it (`BranchAccessBehaviour` reads fresh).
9. **Test seams.** A test module validator registered in `ModelHarness` behind an off-by-default switch; a branch-event probe handler that only records unless a test arms it; `AddModuleServices` over explicit types.
10. **Role names compare case-insensitively** in the catalogue, as Identity normalises them, and `Basic` may receive grants but not be declared.
11. **The smoke's sample migration has a fixed id** (`20990101000000_SampleModule`). The generated project's test databases are kept between smoke runs, and their harness refuses a history row the assembly does not define, so a fresh timestamp each release would fail the next run.

### 3.1 Item 1: T20, the count test and the smoke sample module

**T20, rewritten** (`ModuleGuardTests.cs:183-190`). It is not a pinned suite.

- Over the real `ApplicationDbContext` model, assert two things:
  - `DataModelGuard.FindProblems` is empty;
  - every non-owned, non-property-bag entity is either `TemplateEntities.Contains` or has `ModuleDefinition.Of(clr) is not null`.
- Add a third check: every type in `TemplateEntities.Types` is in the model, so the list cannot rot. On the bare template the test means exactly what it meant before. In a generated project it holds as long as the modules are sound.
- Rename it `T20_TheRealModel_HasNoModuleProblems_AndEveryEntityIsTemplateOrModule`.

**`TheApplicationDeclaresTheExpectedNumberOfRequestTypes`**, the same treatment. It counts only request types **outside** `*.Modules.*` namespaces, and stays at 32. A second test asserts that every request type inside a `Modules.<M>` namespace is marked. That second check is already enforced at startup by `AssertAllRequestsAreMarked`, so the template's count keeps its purpose: forcing a conscious update for **template** requests.

**Sweep.** Before writing the sample, search the four suites for other assertions about the whole model, the whole assembly or the whole menu (`OnlyContain`, `HaveCount`, `const int Expected`, equality with a template list). The first smoke run with the sample is the real detector. Each failure it finds is fixed by the same rule and listed in the pass report.

**The sample module** lives in `tooling/sample-module/`, a template-repository file tree. The `tooling/` folder is already excluded from the package. The smoke copies the tree into the generated project and replaces the namespace token with the project's name. It holds one module, `Sample` (schema `smp`):

| Layer | File | Proves |
|---|---|---|
| Domain | `Modules/Sample/SampleModule.cs`, `Entities/SampleBin.cs` (`[Module<SampleModule>]`, `IMustHaveBranch`, `BaseVersionedEntity<Guid>`), `Contracts/SampleBinCreated.cs` (a `PostingEvent`) | module entity, schema, branch scope |
| Application | `Modules/Sample/SamplePermissions.cs` (nested `Permissions.SampleBins`, with module-level registry decisions, §3.2), `Features/SampleBins/` (a transactional `IBranchCommand` that creates a bin and raises the event, and a query), `Contracts/ISampleBinLookup.cs`, `SampleServices.cs` (§3.2), `SampleRoles.cs` (§3.3), `SampleBranchValidator.cs` (§3.4) | permissions without the template list, the hook, roles, tightening |
| Infrastructure | `Modules/Sample/Configurations/SampleBinConfiguration.cs`, `SampleBinLookup.cs` (implements the contract), `SampleServices.cs`, `SampleTenantSeeder.cs` | configuration under a module, a `Contracts` implementation, a seeder through the hook |
| Application (handler) | `Modules/Sample/EventHandlers/OpenBinForNewBranch.cs`: `IPostingEventHandler<BranchCreatedEvent>`, which creates a default bin in the new branch | §3.4's grant, in a real project |
| Server.UI | `Modules/Sample/SampleMenu.cs` (`IMenuContributor`), `Pages/SampleBins/SampleBins.razor` | menu, module page |
| **(E2)** App level | `Application/AppLevel/SampleToolsPermissions.cs` (`Permissions.SampleTools.Use`, decided by `[AdministratorExcluded]`), `Server.UI/AppLevel/Pages/SampleTools.razor` (outside the module, gated on that permission, sends the sample module's create command), `Application/AppLevel/AppServices.cs` (grants `SampleTools.Use` to the sample role) | an app-level permission and page, decided and registered without editing a template file |
| Migrator | generated by the smoke: `dotnet ef migrations add SampleModule` for every migrator in the generated project | `EnsureSchema("smp")`; `ModelMatchesMigrationsTests` green |
| Tests | `tests/Application.IntegrationTests/Modules/SampleModuleTests.cs`, `tests/Server.UI.IntegrationTests/Modules/SampleModuleUiTests.cs` | each hook works end to end (§5, rows S1 to S8) |

**The smoke, new step 4b** (between "the generated solution builds" and the suites):

1. Copy the sample in and rewrite its namespace.
2. Add the migration with `dotnet ef migrations add SampleModule`, using the design-time `DatabaseSettings__*` variables, as the README's "Getting started" section already gives them. It fails the smoke if `dotnet-ef` is missing.
3. Assert that the migration contains `EnsureSchema(name: "smp")`.
4. Rebuild with 0 errors.
5. The existing suite step then runs on the project **with** the module.
6. Add one count check: `SampleModuleTests` passed alone and is named, as for the image upload tests. A module test that silently did not run therefore fails the smoke.

Keep the current run on the bare project as well? No. It doubles the slowest step, and the bare template is already proved by the repository's own suites. §8.12 confirms.

### 3.2 Item 2: module service registration

**Contract** (Application, `Common/Modules/ModuleServices.cs`, visible to all three layers):

```csharp
/// One per module per layer, in that layer's Modules/<M>/ folder. Found by scan; never called by hand.
public abstract class ModuleServices<TModule> where TModule : ModuleDefinition, new()
{
    public abstract void Register(IModuleServiceCollection services, IConfiguration configuration);
}

public interface IModuleServiceCollection : IServiceCollection
{
    /// The one sanctioned way to replace a template service. Logged at startup, naming the module.
    IModuleServiceCollection Replace<TService, TImplementation>(ServiceLifetime lifetime)
        where TService : class where TImplementation : class, TService;
}
```

**Discovery and order:**

- At the end of `AddApplication`, `AddInfrastructure` and `AddServerUI`, each layer scans **its own assembly** for `ModuleServices<>` subclasses with `AddModuleServices(Assembly, configuration)`. A layer never scans another layer's assembly, so the order of the layers does not matter.
- `AddApplication` has no `IConfiguration` today. Its overload gains an optional one, and it passes an empty configuration when none is given. The existing call sites do not change.
- **Registration order:**
  1. the template's own services first, as today;
  2. then the modules, ordered by `ModuleDefinition.Order` (new: `public virtual int Order => 0`), then by `Name` ordinal (§8.4).
- Order matters only for multi-registered services: seeders (run in registration order), posting handlers of one event, and menu sections (which already have their own `Order`). For seeders, this gives "Catalogue's seeders run before Inventory's" by giving Catalogue the lower `Order`. A module does not choose its position any other way.

**Lifetimes:**

- The module author chooses the lifetime for each call, as everywhere in .NET DI. The README states the rule:
  - **scoped by default**: anything that touches `IApplicationDbContextFactory`, the user context or Mediator;
  - **singleton** only for stateless services with singleton dependencies;
  - **transient** almost never.
- A wrong choice fails loudly through the resolve test below.
- Hook-registered posting handlers follow `AddPostingEventHandlers`: scoped.

**Fail-loud rules.** `ModuleServiceGuard` runs when each layer's scan completes. It builds no provider.

1. A `ModuleServices<TModule>` whose namespace is not `…Modules.<TModule.Name>` (architecture rule 8, applied to registration classes).
2. Two `ModuleServices<TModule>` for one module in one layer.
3. A descriptor the module adds whose `ServiceType` the template already registered, unless:
   - it is on the multi-service list: `ITenantSeeder`, `IMenuContributor`, `IPostingEventHandler<>`, `INotificationHandler<>`, `IValidator<>`, `IHostedService` and `ApplicationRoleDefinition` (§3.3); or
   - it came through `Replace`.

   Without this rule, MS DI's "last registration wins" would let a module replace `IFileStorage` without a word.
4. Any `ISaveChangesInterceptor` or `IInterceptor` from a module. The pinned ordering test asserts exactly two.
5. **The `Contracts` check, after all layers.** Every public interface in a `…Modules.<M>.Contracts` namespace of the Domain or Application assembly must be registered exactly once. `AddServerUI` runs it at its end, because in `Program.cs` it runs last. A Server.UI integration test runs it on the `GxWebApplicationFactory` host, so an unregistered contract fails a shipped test even in a project whose tests never call `AddServerUI`.
   - **(E1) A contract that is not a service.** A `Contracts` interface may be a shape that other modules implement on their own types, or a marker, and is never resolved from the container. It is declared so with `[NotAService("reason")]` (Domain, `Common/Modules`), and rule 5 exempts it. The reason is required and must not be blank. Nothing is inferred from the interface's members: an interface with no members is still checked unless marked.
   - **(E1)** A marked interface that is nevertheless registered fails startup, naming it, because the marking would then be wrong.
   - An unmarked, unregistered interface still fails startup, naming it.
6. **(E2)** The same rules apply to `AppLevelServices` (§3.6): rule 1 checks for an `AppLevel` namespace instead of a module's, rule 2 allows one per layer, and rules 3 and 4 are unchanged.

**Shipped test** `ModuleServicesResolveTests` (Server.UI.IntegrationTests), which every generated project runs:

- every service a module registered resolves, in a scope, from a provider built with `ValidateScopes = true`. This catches a singleton that captures a scoped service;
- the `Contracts` check passes.

On the bare template both are vacuous, and they say so: the test asserts that it found zero modules and passes. The smoke's sample makes them real.

**What a module writes:**

```csharp
// src/Infrastructure/Modules/Inventory/InventoryServices.cs
public sealed class InventoryServices : ModuleServices<InventoryModule>
{
    public override void Register(IModuleServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IStockLevels, StockLevels>();            // its Contracts implementation
        services.AddScoped<ITenantSeeder, InventoryTenantSeeder>(); // runs after lower-Order modules' seeders
    }
}
```

**Scope boundary.** Posting handlers in **Infrastructure** are not scanned today; `AddPostingEventHandlers` covers Application only. The README places them in Application, so that does not change. A module that puts one in Infrastructure registers it through the hook (`AddPostingEventHandlers(typeof(X).Assembly)` is callable from it).

### 3.3 Item 3: the application role catalogue

**Declared through the hook (§3.2),** so that there is one mechanism and not two:

```csharp
// src/Application/Modules/Inventory/InventoryServices.cs
services.AddApplicationRole("Storekeeper", "Receives and issues stock in assigned branches.",
        Permissions.StockItems.View, Permissions.StockMovements.Post)
    .GrantToRole("Branch Manager", Permissions.StockItems.View);   // a role another module declares
```

- `AddApplicationRole` adds a singleton `ApplicationRoleDefinition(Name, Description, Permissions, DeclaredBy)`.
- `GrantToRole` adds an `ApplicationRoleGrant(Role, Permissions, DeclaredBy)`.
- Roles cross modules: "Branch Manager" receives grants from Inventory, Sales and Purchasing. So **one** module declares the role, with its description, and the others only grant to it.

**Validation** (`RoleCatalogue.Validate`, a pure function, unit-tested with planted inputs, and called at the start of `EnsureRolesAsync`):

1. A role declared twice: fail, naming both modules.
2. A grant to a role nobody declares: fail. The template's `Basic` counts as declared (§8.7).
3. An entry or grant naming `Roles.Admin`: **fail, whatever the permission.** The administrator's grant is `AdministratorPermissionRegistry`, its single source. This is a stricter form of the brief's rule, and §8.7 decides it.
4. A permission that is not a `Permissions.*` constant (`AdministratorPermissionRegistry.DiscoverAllPermissions`): fail. This catches a typo or a deleted constant.
5. An empty or whitespace name, or a name longer than Identity's 256: fail.

**Reconcile** in `ProvisionAsync`, inside `EnsureRolesAsync`, after `Admin` and `Basic`:

- For each catalogue role, call `EnsureRoleAsync(name, description, permissions ∪ grants)`. That is pass 59's grant-only, log-on-insert method, unchanged in behaviour for `Admin`/`Basic`.
- **A catalogue role that cannot be created throws** (§7.3). The template's own two keep their log-and-return.
- A description edited later by an operator is never overwritten. Neither the description nor the claims are ever revoked.

**Approval rule at startup.** ~~Startup fails if any member of that role is also a member of `Admin`.~~ **Decided otherwise (decision 8):** after the reconcile, on **every** startup, each user who is a member of `Admin` and also holds an `[ApprovalPermission]` through another role is **logged at Error**, naming the user, the role and the permission. Startup then continues.
- The send-time refusal in `ApprovalBehaviour` remains the guarantee: that user's approval is refused when sent.
- Reason (owner): one user's role membership must not stop the application for every branch. This state can arise from an ordinary release that adds an approve permission to a role an administrator already holds.
- **(P60b)** The check covers every non-administrator role that holds an approval permission, not only catalogue roles, because a role created in the UI reaches the same state. It also covers an approval permission held as a direct user claim. It reuses `ApprovalPermissions.DiscoverLoaded()`.

**`Roles.cs` is not edited.** A module that needs its role names in code declares its own constants class (`InventoryRoles.Storekeeper`) beside its services class.

### 3.4 Item 4: branch extension points

**(a) Unit of work and in-transaction events.**

- `AddEditBranchCommand` becomes `ITransactionalRequest`. It is not idempotent: it returns `Result<Saved<int>>`, and a double click is already guarded by the version and by the code's unique index.
- New events in `Domain/Events/Branches/`. They are template events, so any module may name them without breaking rule 1:
  - `BranchCreatedEvent(BranchId, Code, Name, IsActive)`, on insert;
  - `BranchChangedEvent(BranchId, Code, Name, OldCode, OldName, IsActive, WasActive)`, when the code or name changes, or on reactivation;
  - `BranchDeactivatingEvent(BranchId, Code)`, when `IsActive` goes from true to false.
  - All three derive from `PostingEvent`.
  - The handler adds them with `branch.AddDomainEvent(…)` before its one save. An edit that changes nothing raises nothing.
- **Veto:** a handler throws `PostingRejectedException("Branch ABJ still has open stock counts.")`. Pass 58 rolls everything back, and the user sees that message in the dialog's snackbar. No new mechanism is needed.
- **Moved:** `userContextInvalidator.InvalidateAll()` must not run before the commit (§7.5). It moves to an ordinary after-commit `BranchActivationChangedEvent` (a `DomainEvent`, published after the commit by pass 58) with a template `INotificationHandler` that invalidates.

**(b) The grant for the saved branch.**

- New marker `IBranchDefiningCommand`, and a constrained behaviour `BranchDefinitionGrantBehaviour<,>`, registered immediately after `BranchAccessBehaviour`.
  - It sits **outside** `TransactionBehaviour`, so its grant node spans the handler, every retry and every posting-event round.
  - It opens an **empty** grant node, `BranchGrant.BeginEmpty()`. If an `IBranchDefiningCommand` node is already open further out, it joins that node instead, so a module command that nests `AddEditBranchCommand` keeps the grant for its own writes after the nested send returns.
- The handler, after its save (when the id exists), calls `BranchGrant.Extend([branch.Id])`.
- `Extend` without an open node becomes a **throw** for this caller (`BranchGrant.ExtendDefining`), never a no-op.
- From then on, until the command ends, `DataRules.CheckBranch` accepts that branch's rows, from the handler or from any posting handler.
- **Which saves get it** (§8.9): recommended, every save of a branch (create, change and deactivation), and always only that one branch.
  - A deactivation handler that closes the branch's open rows needs it.
  - The brief names only creation. The command already holds `Branches.Create` or `Branches.Edit` over that branch.
- **Bounded.** The grant is for one branch, inside one command, and ends with it. `BranchGrant.Current` returns an empty set (not null) before the save, so a branch-scoped write before the save fails with an explicit "no branch granted yet" message.
- **A retried attempt** may leave a rolled-back id in the node. That is harmless: identity values are not reused. The test asserts it.
- **Pinned pipeline.** Pass 58's `TransactionPipelineTests` T20 asserts the order for a probe request. The new behaviour is constrained to `IBranchDefiningCommand`, so the probe's order is unchanged, the same argument pass 60 used for `ApprovalBehaviour`.

**(c) Tightening the rules.** No new mechanism for the server: a module adds `AbstractValidator<AddEditBranchCommand>` in `Application/Modules/<M>/`, and `ValidationBehavior` runs it with the template's. The pass adds:

- a test that proves it (row 13);
- the README text;
- **field-level display.** `BranchFormDialog` (and the dialog contract in (d)) gets `Validation="@ValidationService.ValidateValue<AddEditBranchCommand>()"`, using the existing `IValidationService` that runs every registered validator for one property. A module's "code must be 3 letters" then shows on the field, not only after submit.
- **Unique name:** the validator gives the message. For the index, a module adds an `IEntityTypeConfiguration<Branch>` in `Infrastructure/Modules/<M>/Configurations` that adds only `HasIndex(b => b.Name).IsUnique()`, with its migration.
  - Verify in the pass that two configurations of one type both apply, and that `DataModelGuard` and the "every configuration is applied" test accept a module configuration of a template entity.
  - If either refuses, the fallback is a `BranchOptions.UniqueName` switch in the template's configuration (§7.7).

**(d) Replacing the screens.** These are options, set through the hook (§3.2):

```csharp
// src/Server.UI/Modules/Organisation/OrganisationServices.cs
services.Configure<BranchScreenOptions>(o =>
{
    o.FormDialog = typeof(OrgBranchDialog);   // a component with [Parameter] AddEditBranchCommand Model
    o.PageRoute  = "/org/branches";           // the module's own page
});
```

- **The template page** opens `BranchScreenOptions.FormDialog` by type with `DialogService.ShowAsync(Type, …)`. When `PageRoute` is set, the template page redirects to it (`NavigateTo(PageRoute, replace: true)`), and the menu entry's `Href` uses it.
- **Old links** to `/system/branches` therefore land on the application's screen. No template screen offers a path that skips the module's UI.
- **The server-side guarantee is (a) and (c).** Every save goes through `AddEditBranchCommand`, its validators and its event handlers, whichever screen sent it.
- **Startup fails** (`BranchScreenOptions` validation, `ValidateOnStart`) if either of these is wrong:
  - `FormDialog` is not a component with a public `[Parameter]` named `Model` whose type is `AddEditBranchCommand` or derives from it;
  - `PageRoute` matches no `[Route]` in the Server.UI assembly.
- **Extending, not replacing:** a module dialog may wrap the template's fields. `BranchFormFields.razor` is extracted from `BranchFormDialog` for this, and the module adds its own. Extra data goes to a module command that nests `AddEditBranchCommand` (it joins the unit of work, and the grant per (b)), then writes its own rows in the same transaction.

### 3.5 Item 5: exactly one default

- **The rule, in the validator:**
  - `BranchIds` non-empty requires `DefaultBranchId` (message: "Choose the user's default branch.") and requires it to be one of `BranchIds`;
  - `BranchIds` empty requires `DefaultBranchId` null.
  - It refuses rather than auto-picking (§8.11). The UI already always sends one (`UserBranchesSection.razor:82`), so nothing a user does changes.
- **The handler** asserts the result before saving (exactly one `IsDefault` when any row remains), as a backstop for a future edit.
- **The database:**
  - a PostgreSQL partial unique index `IX_UserBranches_UserId_Default ON "UserBranches" ("UserId") WHERE "IsDefault"` (at most one), added in `OnModelCreating` only when `Database.IsNpgsql()`, under `#if (UsePostgreSql)`;
  - the other providers' snapshots do not change. The comment at `BranchConfiguration.cs:31-34` is rewritten.
  - "At least one" stays the command's job. A constraint for it would be a deferred trigger, out of proportion here.
- **Existing rows.** The 1.6.0 and 1.7.0 databases can hold users with assignments and no default, through any direct send. `ProvisionAsync` gains `EnsureOneDefaultBranchAsync`: for each such user, it sets the lowest-id **active** assigned branch (or the lowest-id one if none is active) as the default, and logs each repair. It is idempotent and uses LINQ only, so it adds no raw SQL and no entry to rule 5's list. It runs **before** the migration's unique index can matter: the index allows zero defaults, so ordering is not a risk.

### 3.6 (E2) Application-level code, and permission decisions by attribute

**The home.** Code that belongs to no module (for example a Development-only page that sends commands of several modules) lives in one folder per layer:

| Layer | Folder (namespace segment `AppLevel`) | Holds |
|---|---|---|
| Domain | none | No entities and no events. Domain code is either template or module code. |
| Application | `src/Application/AppLevel/` | requests, validators, `AppServices.cs`, permission classes |
| Infrastructure | `src/Infrastructure/AppLevel/` | services, `AppServices.cs` |
| Server.UI | `src/Server.UI/AppLevel/` | `Pages/`, `Components/`, a menu contributor, `AppServices.cs` |

**How the architecture rules treat it** (rule 1, `ModuleBoundaryRule`, and rule 8, `ModuleDeclarationRules`):

- **It may send module commands and use module `Contracts`.** An `AppLevel` type may reference a module type only if that type is published (`Contracts`) or is a Mediator request (implements `IMessage`), including the request's nested types. Any other module type (an entity, a handler, a DTO, a page) is a violation, reported as `AppLevel -> <M>`.
- **No module may reference it.** A reference from a module type to an `AppLevel` type is a violation, reported as `<M> -> AppLevel`.
- **It holds no entities.** An entity under an `AppLevel` namespace fails rule 8, naming it. (`DataModelGuard` also refuses it at startup, because it declares no module.)
- A type is `AppLevel` when its namespace has the segment `AppLevel` and no `Modules` segment.

**Services.** `AppLevelServices` (non-generic, abstract, with the same `Register(IModuleServiceCollection, IConfiguration)`) is found by the same scan in each layer. At most one per layer, and its namespace must contain `AppLevel`. It runs **after** every module in that layer. Guard rules 3 and 4 apply to it unchanged, so an omission or a silent replacement fails in the same way. Roles it declares carry `DeclaredBy = "AppLevel"`.

**Permission decisions by attribute (decision 2).** Two attributes in Application, `Common/Security`:
- `[AdministratorGrant]` on a permission constant grants it to the administrator role;
- `[AdministratorExcluded("reason")]` excludes it, with a non-blank reason.

They decide **any** `Permissions.*` constant that is not in the template's own lists, wherever the application declares it: in a module, in `AppLevel`, or elsewhere. `Permissions` is a partial class, so a module's or app-level permission class is a nested class of it whatever folder its file is in. `AssertNoDivergence` reads the two lists and the attributes together. Startup fails, naming the constant, for:
- a constant decided by neither (as today);
- a constant in a template list that also carries an attribute (two sources for one decision);
- a constant carrying both attributes;
- an `[ApprovalPermission]` constant carrying `[AdministratorGrant]`, or not excluded (as today for the lists).

`AdministratorPermissionRegistry.EffectiveGranted` (the list plus `[AdministratorGrant]`) is what the initializer gives the administrator role and the system account. `Granted` keeps meaning the template's own list.

**What app-level code writes:**

```csharp
// src/Application/AppLevel/DevToolsPermissions.cs   (namespace …Application.Common.Security, as every Permissions part)
public static partial class Permissions
{
    public static class DevTools
    {
        [AdministratorExcluded("Development only; granted to developers' role by the catalogue.")]
        [Description("Allows using the development tools page")]
        public const string Use = "Permissions.DevTools.Use";
    }
}
```

## 4. Order of work

0. **Start.** Branch from `main` at `f9f37ae0`. Prove the four suites green at pass 60's counts (601+14, 239, 136, 348).
1. **Item 5:** validator, handler backstop, index, migration (all three providers; only PostgreSQL's changes), the reconcile, tests. **S, 0.5 day.**
2. **Item 2:** `ModuleServices<>`, `IModuleServiceCollection`, `ModuleDefinition.Order`, the three scan calls, the guard's five rules with planted samples, `ModuleServicesResolveTests`, README. **M, 1.5 days.**
   **(E1)** `[NotAService]` and its two rules are part of this step. **(E2)** `AppLevelServices` too.
3. **§8.2, permission decisions by attribute** (decided), needed before any module permission compiles without editing the registry. **(E2)** It covers any constant outside the template lists, and this step also extends rules 1 and 8 for `AppLevel` (§3.6). **S, 0.75 day.**
4. **Item 3:** definitions and grants, `RoleCatalogue.Validate`, reconcile, the approval check at provisioning, tests, README. **S/M, 1 day.**
5. **Item 4:**
   - (a) unit of work, events, invalidation moved: **0.75 day**;
   - (b) behaviour, empty node, join rule, `ExtendDefining`: **0.75 day**;
   - (c) dialog field validation, the two-configuration check, README: **0.5 day**;
   - (d) options, redirect, menu, fields component, startup validation, bUnit tests: **1 day**.
   - **M, 3 days.**
6. **Item 1:**
   - T20 and the count test, and the sweep: **0.5 day**;
   - the sample module with its two test files: **1 day**;
   - smoke step 4b: **0.5 day**.
   - **M, 2 days.** This comes last because the sample uses every hook above.
7. **Docs:** README "Modules and schemas" (checklist steps 4 to 6 rewritten for the hook, roles and registry attributes), "Branches" (events, grant, screens, default), "Architecture tests" (the guard rules); the release notes. **0.5 day.**
8. **Release 1.7.1** (or per §8.1). Set `AppConfigurationSettings.Version`. **Commit, then** pack, install, run `tooling/smoke-generate.ps1 -UseInstalled -WorkRoot C:\gx60b`, and tag `gx-v1.7.1`. If auto mode refuses the smoke, the fallback is to run its steps by hand, as in pass 57. **0.25 day.**

**Total: about 9 days.**

## 5. Tests (Application.IntegrationTests on `GX_TEST_PG` unless noted)

| # | Test |
|---|---|
| 1 | **T20 rewritten:** passes on the template; passes on a test context holding a sound module entity; fails naming an entity that is neither template nor module (unit). Every `TemplateEntities` type is in the model (unit). |
| 2 | The request count is 32 over template namespaces. A planted module request type does not change it (unit). |
| 3 | **Hook:** a `ModuleServices<>` registers a scoped service, a seeder and a posting handler, and each resolves. Seeders of two test modules run in `Order`, then name order (integration). |
| 4 | **Hook fails loud** (unit, one planted sample each): wrong namespace; two in one layer; a module silently re-registering `IFileStorage` (`Replace` passes and logs); a module `ISaveChangesInterceptor`; an unregistered `Contracts` interface, named. `InterceptorOrderingTests` unchanged and green. |
| 5 | `ModuleServicesResolveTests` passes vacuously on the template and says so; on a test host with a singleton capturing a scoped service it fails, naming both (Server.UI). |
| 6 | **Roles:** a catalogue role is created with its description and permissions; a later run adds a new grant and logs it; an operator's extra claim and edited description survive; a removed catalogue permission is not revoked. |
| 7 | **Roles fail loud** (unit): a role declared twice; a grant to an undeclared role; any entry naming `Admin`; an unknown permission; a role that Identity refuses to create (integration, throws). |
| 8 | **Approval at provisioning (decision 8):** a catalogue gives `Approve` to "Budget Holder", whose members include an `Admin` member. **Startup succeeds**, an Error is logged naming the user, the role and the permission, it is logged again on the next startup, and that user's approval is refused at send time by `ApprovalBehaviour`. Without the `Admin` member nothing is logged. Pass 60 rows 4 and 11 unchanged. |
| 9 | **Branch events:** create raises Created; renaming raises Changed (old and new); deactivating raises Deactivating; reactivating raises Changed; saving unchanged raises nothing. Each is handled in the transaction. |
| 10 | **Veto:** a Deactivating handler throws `PostingRejectedException`. The branch stays active, nothing is saved, the message reaches the result, and the user contexts are not invalidated. |
| 11 | **Grant:** a Created handler inserts a `TestBranchRow` for the new branch. It commits, under a user with no assignment to it (the administrator). A handler writing a row for **another** branch fails at save, naming both. A write before the branch's save fails with the "no branch granted yet" message. After a retried attempt (pass 58 T11's deadlock injector) the row exists once. |
| 12 | **Nested:** a test module command (`IBranchDefiningCommand`, transactional) sends `AddEditBranchCommand` and then writes the new branch's row itself. It commits. The same command without the marker fails at save. |
| 13 | **Tightening:** a test module's `AbstractValidator<AddEditBranchCommand>` (code exactly 3 letters, name unique) refuses through `Mediator.Send`; the template's validator still applies. The dialog shows the module's message on the field (Server.UI, bUnit). A second `IEntityTypeConfiguration<Branch>` adding a unique index on Name is applied (unit). |
| 14 | **Screens** (Server.UI): with options set, the menu entry points to `PageRoute`, `/system/branches` redirects there, and the template page opens the configured dialog. Startup fails for a dialog with no `Model` parameter and for a route that matches no page. Without options, everything is as in 1.7.0. |
| 15 | Pass 59's branch tests, the version conflict on a branch edit included (`PicklistVersionTests`), pass unchanged now that the command is transactional. |
| 16 | **Default branch:** branches without a default are refused; a default outside the set is refused; empty branches with a default are refused; one branch with it as the default is accepted. The PostgreSQL index refuses a second `IsDefault` row written directly. The reconcile repairs a user with no default, picking the lowest-id active branch, logs it, and does nothing on the next run. |
| 17 | Unchanged: the pinned suites, pass 58's transaction tests (T20's order included), pass 59's rows 1 to 35 and pass 60's rows 1 to 46. |
| 18 | **(E1)** An unmarked, unregistered `Contracts` interface fails the check, naming it. The same interface marked `[NotAService("…")]` passes. A marked interface that is registered fails, naming it. A blank reason fails (unit). |
| 19 | **(E2)** An application-level permission decided by `[AdministratorGrant]` or `[AdministratorExcluded]` passes the registry check, and a granted one reaches the administrator role. The same constant with no decision fails startup, naming it. A constant both in a template list and attributed, one with both attributes, and an approval permission with `[AdministratorGrant]` each fail (unit). |
| 20 | **(E2)** A module type referencing an `AppLevel` type fails the boundary rule (`<M> -> AppLevel`). An `AppLevel` type sending a module's command and using its `Contracts` passes. An `AppLevel` type using a module's entity fails (`AppLevel -> <M>`). An entity in an `AppLevel` namespace fails rule 8 (unit, samples in `tests/ArchitectureSamples`). |
| 21 | **(E2)** `AppLevelServices` registers a service that resolves, after the modules; one in a non-`AppLevel` namespace and two in one layer each fail (unit). |
| S1–S8 | **In the generated project only** (the smoke's sample module): S1 `smp."TBL_SAMPLE_BIN"` exists; S2 the sample command creates a bin in branch 1 and a user of branch 2 cannot read it; S3 `ISampleBinLookup` resolves to the Infrastructure implementation; S4 the sample seeder ran; S5 the "Bin Keeper" catalogue role exists with its permissions; S6 creating a branch creates its default bin through `OpenBinForNewBranch`; S7 the sample menu section appears between "Application" and "MANAGEMENT" (Server.UI); S8 the sample's branch validator refuses a two-letter code. **(E2)** S9: the app-level `SampleTools` page is routed, gated on `Permissions.SampleTools.Use`, and its command creates a bin; its permission is decided by attribute and granted to the sample role by `AppServices`. |

## 6. Effort summary

| Item | Effort |
|---|---|
| 1. T20, count test, sweep, sample module, smoke step | M, 2 days |
| 2. Module service hook | M, 1.5 days |
| 3. Role catalogue | S/M, 1 day |
| 4. Branch extension points: events 0.75, grant 0.75, validators 0.5, screens 1 | M, 3 days |
| 5. Exactly one default | S, 0.5 day |
| §8.2 permission decisions by attribute | S, 0.5 day |
| Docs and release | 0.75 day |
| **Total** | **About 9 days** |

If time is short, 4(d) (screens) can follow alone. The server-side guarantees of 4(a) to 4(c) already keep any screen within the application's rules.

## 7. Conflicts with the brief or with the code, and what I recommend

1. **"T20 … fails in a generated project".** It is true, and **it is not the only test of that kind.** `TheApplicationDeclaresTheExpectedNumberOfRequestTypes` fails on the first module command (§1).
   - **Recommend:** fix both by the same rule, and let the smoke's sample find any others. The report lists each one.
   - **(P60b)** The sweep found three more, all about the administrator's grant once a module decides a constant by attribute: `AdministratorPermissionRegistryTests.TheTwoListsCoverEveryConstantBetweenThem`, and `ProvisioningTests.TheAdministratorRole_HoldsExactlyTheExplicitlyGrantedPermissions` and `TheSystemAccount_HoldsTheAdministratorRegistryAsUserClaims`. They now compare against the lists plus the attributes. The sample module, run in the repository and in the generated project, found no further one.
2. **A module cannot add a permission without editing a template file.** `AdministratorPermissionRegistry`'s two lists must decide every constant. That contradicts the aim of items 2 and 3, and the smoke would have to patch a template file, so it would prove the wrong thing.
   - **Recommend (§8.2):** `[AdministratorGrant]` and `[AdministratorExcluded("reason")]` on a module's constants, read by `AssertNoDivergence` beside the lists. The template's own constants stay in the lists.
   - An `[ApprovalPermission]` constant carrying `[AdministratorGrant]` fails startup, as an approval permission in `Granted` does today.
3. **"Omission fails loudly" versus `EnsureRoleAsync`'s log-and-return** when a role cannot be created (`ApplicationDbContextInitializer.cs:200-206`).
   - **Recommend:** throw for catalogue roles, and keep the template's two roles as they are, so that passes 58 to 60 behave as before.
4. **The new-branch grant against pass 59's decision** that the administrator "writes only where it is assigned" and never holds `Branches.WriteAll`. The administrator creates branches (it holds `Branches.Create`). Under item 4, the handlers of its command write the new branch's rows.
   - **Recommend:** accept it, bounded. It covers one branch, the one being saved, for one command, and only rows that command or its event handlers write. The administrator gains no lasting write right, and `BranchAccessBehaviour` is unchanged.
   - It is an explicit exception to a decided rule, so §8.9 asks.
5. **Making `AddEditBranchCommand` transactional moves its `InvalidateAll` inside the transaction.** Today it runs after the save. Inside a unit of work it would run before the commit, so another circuit could reload and cache the branch as still active, and keep it until the next invalidation.
   - **Recommend:** move it to an after-commit `DomainEvent` handler (§3.4a).
6. **"The rest of the command and its handlers can write the new branch's rows."** Posting handlers run **after** the command's handler returns (`TransactionBehaviour.cs:190-210`), and a nested command's grant is popped when it ends. A grant opened in the handler would therefore not reach them.
   - **Recommend:** the behaviour outside the transaction with an empty node, plus the join rule for nesting (§3.4b).
7. **"Tighten the branch rules without editing the template's validator"** already works on the server (`ValidationBehavior` runs every validator).
   - **Recommend:** a test, field-level display and docs, not a new mechanism.
   - A unique **name** needs an index on a template table. Plan a module configuration of `Branch`. If the pass finds that a guard refuses a second configuration of a template entity, fall back to a template option. The report says which.
8. **"Replace the Branches page."** Blazor refuses two components with one route, so a module page cannot take `/system/branches`.
   - **Recommend:** options with a redirect and the menu entry, validated at startup (§3.4d).
9. **"Exactly one default"** cannot be a plain index.
   - **Recommend:** the command enforces "exactly one", a PostgreSQL partial unique index enforces "at most one", and a startup repair fixes existing rows. The configuration's "three providers" comment predates "PostgreSQL only" and is rewritten.
10. **"Stays green on every release"** requires the smoke to generate a migration, so it now needs `dotnet-ef` (10.0.11 is installed globally on this machine) and the design-time variables. Auto mode refused the smoke script in pass 57.
    - **Recommend:** the smoke checks for `dotnet-ef` and fails naming it. The pass report says whether the script or the manual fallback ran.
11. **"Ships as 1.7.1".** This pass adds public contracts (`ModuleServices<>`, the role catalogue, branch events, options). It also tightens two existing behaviours: `SetUserBranchesCommand` refuses a missing default, and `AddEditBranchCommand` becomes transactional. Semantic versioning calls that a minor release.
    - ~~Recommend: 1.8.0.~~ **Decided: 1.7.1, as briefed** (decision 1).

## 8. Decisions for the owner

All decided by the owner on 2026-11-11.

1. **Version: decided, not as recommended.** 1.7.1, as briefed.
2. **Module permissions decided by attribute: decided.** Yes, `[AdministratorGrant]` and `[AdministratorExcluded("reason")]`. **(E2)** They decide any constant outside the template's own lists, wherever the application declares it (§3.6).
3. **Module service hook shape: decided.** `ModuleServices<TModule>` per module per layer, found by scan. **(E2)** `AppLevelServices` for code that belongs to no module.
4. **Module order: decided.** `ModuleDefinition.Order` (virtual, default 0), ties broken by name.
5. **Replacing a template service: decided.** Refused unless done through `Replace<,>`, which is logged.
6. **Role catalogue declaration: decided.** Through the hook (`AddApplicationRole` / `GrantToRole`): one declaring module per role, grants from others.
7. **Template roles in the catalogue: decided.** `Admin` may never be named; `Basic` may receive grants, never revoked.
8. **An approval permission reaching an `Admin` member at provisioning: decided, not as recommended.** Startup does **not** fail. It logs at Error on every startup, naming the user, the role and the permission, and continues. The send-time refusal in `ApprovalBehaviour` remains the guarantee. Reason: one user's role membership must not stop the application for every branch, and this state can arise from an ordinary release that adds an approve permission to a role an administrator already holds (§3.3, row 8).
9. **The branch grant: decided.** The saved branch, for create, change and deactivation, bounded to the command; a bounded exception to pass 59's "administrator writes only where assigned".
10. **Branch screens: decided.** `BranchScreenOptions` (dialog type, page route) with a redirect from `/system/branches`.
11. **Missing default branch: decided.** Refused by `SetUserBranchesCommand`; a PostgreSQL partial unique index; a one-time startup repair.
12. **The smoke's sample module: decided.** In `tooling/sample-module/` with its own tests, run on every release, with no separate run on the bare project; never shipped in the package.
