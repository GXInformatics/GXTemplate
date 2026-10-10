# Pass 55 — Base entity keys, context contract, request registry

**Origin:** InventoryMS `docs/passes/ims-p0-template-audit.md`:

- §3: `BaseEntity` fixes `int Id`, and entities that cannot use it lose domain-event dispatch and
  audit stamping.
- §2.3: the request-authorization registry.
- The T1 carry-overs: the naming-convention gaps from the handover.

All fixed in the template so every GX product inherits them.

**Name:** the next free number after the tenancy pass (54). The planned passes 49, 50, 51 and 53
are untouched.

**Outcome:**

- All four items are done.
- 0 errors, and the warning set is identical to Pass 54's.
- All four suites are green on local PostgreSQL 18 (port 5434).
- Mutations 1 and 3 fail against unfixed code, and each mutated file was restored byte for byte.
- `dotnet pack` and `tooling/smoke-generate.ps1` both pass.

---

## 1. What each item delivered

| # | Item | Delivered |
|---|---|---|
| 1 | Generic base entity | `BaseEntity<TKey>` (`TKey : IEquatable<TKey>`) carries `Id` and the domain-event list, and implements the new `IHasDomainEvents` (`DomainEvents`, `AddDomainEvent`, `ClearDomainEvents`). The same generic treatment goes to `BaseAuditableEntity<TKey>` and `BaseAuditableSoftDeleteEntity<TKey>`. `BaseEntity`, `BaseAuditableEntity` and `BaseAuditableSoftDeleteEntity` are now the `<int>` forms. `DispatchDomainEventsInterceptor` iterates `Entries<IHasDomainEvents>()`. No existing entity changes its declared base or key type, and the model snapshot is unchanged (`ModelMatchesMigrationsTests` green; no migration). |
| 2 | Context contract | `IApplicationDbContext` exposes `DatabaseFacade Database { get; }`, satisfied by `DbContext.Database` itself. A handler-shaped class begins and commits a transaction through the interface. A rollback control proves the transaction is real: both saves and their audit rows are undone. |
| 3 | Request registry | `IsRequest` keys off Mediator's `IBaseRequest`, `IBaseCommand` and `IBaseQuery`. Between them those cover `IRequest`, `IRequest<T>`, `ICommand`, `ICommand<T>` and `IQuery<T>`; Mediator 3 has no non-generic `IQuery`, as reflection over the package confirms. New overload: `AssertAllRequestsAreMarked(IEnumerable<Type>, string)`, which the assembly overload now runs. **Addition:** value-type requests are refused at startup (§3.2). |
| 4 | Naming gaps | `[Table]` (DataAnnotation) is honoured like `ToTable`. TPT derived types get their own `TBL_` name. In TPC, every concrete type is named and an abstract root is skipped. Implicit many-to-many joins are named `TBL_<UPPER_SNAKE of EF's join name>` in `core` when either side is in `core`, and a hand-named join is left alone. `EveryTableOutsideCore_IsOneOfTheTemplates` now runs over a project-shaped model with a TPT pair, an abstract-root TPC hierarchy and two joins. |

---

## 2. File-by-file changes

### Domain

| File | Change |
|---|---|
| `Common/Entities/BaseEntity.cs` | `BaseEntity<TKey> : IEntity<TKey>, IBusinessEntity, IHasDomainEvents`. `BaseEntity : BaseEntity<int>`. |
| `Common/Entities/IHasDomainEvents.cs` | **New.** |
| `Common/Entities/BaseAuditableEntity.cs` | `BaseAuditableEntity<TKey> : BaseEntity<TKey>, IAuditableEntity`. `BaseAuditableEntity : BaseAuditableEntity<int>`. `IAuditableEntity` is unchanged. |
| `Common/Entities/BaseAuditableSoftDeleteEntity.cs` | `BaseAuditableSoftDeleteEntity<TKey>`, and its `<int>` form. |

### Application

| File | Change |
|---|---|
| `Common/Interfaces/IApplicationDbContext.cs` | `DatabaseFacade Database { get; }`. The doc says what it is for (handler-owned transactions) and what it is not for (raw SQL). |
| `Common/Security/RequestAuthorizationRegistry.cs` | `FindRequestTypes(IEnumerable<Type>)` considers classes and value types. `AssertAllRequestsAreMarked(IEnumerable<Type>, string)` adds the struct refusal. `IsRequest` uses the three base interfaces. |

### Infrastructure

| File | Change |
|---|---|
| `Persistence/Interceptors/DispatchDomainEventsInterceptor.cs` | `Entries<IHasDomainEvents>()`. |
| `Persistence/Extensions/GxNamingConventions.cs` | Two passes: entities, then joins. New helpers: `HasOwnTable` (TPH/TPT/TPC), `IsNamedByHand` (Explicit or DataAnnotation), `IsJoinEntity`, `SkipNavigationsUsing`. |

### README

- **Authorization contract:** commands and queries are covered, and requests must be classes, not
  `record struct`s.
- **Database naming:** a new "Hierarchies, joins and hand-named tables" table, and a "Keys, events
  and transactions" subsection.
- **Known limitations:** the old "`BaseEntity` is `IEntity<int>`, with no `long` variant" entry is
  removed, because it is fixed.

### Tests

**New:**

| File | Tests |
|---|---|
| `Application.UnitTests/Persistence/GenericKeyEntityTests.cs` | 4. `KeyedProbe : BaseEntity<long>, IAuditableEntity` is saved through both real interceptors over a probe table in its own `gx_probe_key` schema. Its event reaches a real `INotificationHandler`, and it is stamped and committed. The bases are the `<int>` forms. The template's entities keep their declared base and `int` key. A `BaseAuditableEntity<Guid>` maps a `Guid` key and does not map `DomainEvents`. |
| `Application.IntegrationTests/Persistence/HandlerOwnedTransactionTests.cs` | 3. The interface exposes `DatabaseFacade`; a handler commits through it; without the commit, both saves and their audit rows roll back. |

**Changed:**

| File | Change |
|---|---|
| `Application.UnitTests/Security/RequestAuthorizationRegistryTests.cs` | +5 tests: an unmarked `ICommand<T>` fails startup by name; marking it passes; every shape is recognised and nothing else (notification, stream query, abstract, plain record); the assembly scan finds commands and queries; a struct request is refused. +9 probe types. |
| `Infrastructure.UnitTests/Persistence/GxTableNamingTests.cs` | Sample model: `[Table]` entity, TPT pair, abstract-root TPC pair, two many-to-many joins (`SampleShapes.Configure`). +6 tests. Changes to existing tests, below. |

The existing-test changes in `GxTableNamingTests.cs`:

- `ApplyingTheConventionTwice_ChangesNothing` is extended to TPT, TPC and the join.
- `BusinessContext()` is now a `ProjectShapedContext : ApplicationDbContext`.
- `EveryTableOutsideCore_IsOneOfTheTemplates` now counts tables (it skips types without one) and
  reports a join by its EF name.
- `TheProjectShapedModel_ReallyContainsATptPairAndAJoin` keeps that test from passing vacuously.

---

## 3. Decisions — read these

### 3.1 `BaseAuditableEntity` is now `BaseEntity<int>`, not `BaseEntity`

The task asks that `BaseEntity : BaseEntity<int>`, that `BaseAuditableEntity` get the same generic
treatment, and that no existing entity change type. Single inheritance allows those only one way:
`BaseAuditableEntity : BaseAuditableEntity<int> : BaseEntity<int>`. `Document`, `PicklistSet` and
`SecurityPolicy` still declare `BaseAuditableEntity` with an `int` key (a test pins this). But
`BaseAuditableEntity` is no longer assignable to the non-generic `BaseEntity`.

The only code in the template that relied on that was the dispatcher, which this pass moved to
`IHasDomainEvents`. **Mutation 1 shows what that reliance would have done:** with the old
`Entries<BaseEntity>()` line, every existing auditable entity's events stop being published too
(§5.1). A project that wrote `is BaseEntity` to mean "any entity" must switch to an interface; the
README says so.

The alternative was to keep `BaseAuditableEntity : BaseEntity` and duplicate the four audit
properties into a separate `BaseAuditableEntity<TKey>`. That keeps the old assignability but gives
two audit bases that can drift. I chose the single chain.

### 3.2 The registry refuses struct requests (an addition)

While extending `IsRequest` I found a hole beside it. A `record struct` request can never be
authorized:

- `RequestAuthorizeAttribute` is class-only, so it cannot be marked.
- `AuthorizationBehaviour` is constrained to `class`, and the source generator silently gives a
  message type no behaviour when it does not satisfy the constraint. The behaviour's own remarks
  document that silence.
- `FindRequestTypes` considered classes only, so the startup check never saw one either.

A struct command would therefore run **with no authorization at all**. The registry now finds value
types and refuses them at startup with their own message ("value types … would run unauthorized"),
not "unmarked", because adding the attribute is not a fix that exists. The template has no struct
requests; this guards projects.

### 3.3 Stream messages are still outside the registry (not done)

`IStreamRequest<T>`, `IStreamCommand<T>` and `IStreamQuery<T>` derive from `IStreamMessage`, not
`IMessage`, so `AuthorizationBehaviour` never sees them either. They are a separate pipeline that
would need its own behaviour. The registry deliberately does not count them as requests: refusing
them would block a feature, not close a hole, until a stream behaviour exists. The template has none.
Noted as a follow-up.

### 3.4 Mutation 1's handler is real, but reached through a mocked `IMediator`

The test project has no Mediator source generator, so a handler declared in a test cannot be
discovered by a real `IMediator`. The mock routes exactly what the interceptor publishes to a real
`INotificationHandler<KeyedProbeCreated>`. The assertion is that the handler ran, with this entity's
event, after the save.

### 3.5 Naming rules, and one pre-existing test weakness fixed

- **Joins** are named when **either** side is in `core`. A join between a project entity and a
  template entity (say, `ApplicationUser`) is project data. It is named
  `TBL_ + ToUpperSnake(EF's join name)`, so `SampleCourseSampleStudent` becomes
  `TBL_SAMPLE_COURSE_SAMPLE_STUDENT`. A hand-named join (`UsingEntity(..., j => j.ToTable(...))`)
  is left alone.
- **`ApplyingTheConventionTwice_ChangesNothing` compared a model with itself.** EF caches one model
  per context **type**, so the "apply twice" context reused the "apply once" model. The test's
  `SampleContext` now replaces `IModelCacheKeyFactory` so the key includes `ApplyCount`, and both
  models really are built. The test still passes: the convention's own `SetTableName` records
  Explicit, which the second pass yields to.

---

## 4. Mutation checks

Protocol: copy the file aside and record a SHA-256 prefix; apply the mutation (diff below); build;
run the named tests; copy the file back and compare hashes. After the last restore, `grep MUTATION`
over `src` and `tests` returns 0.

### 4.1 Mutation 1: dispatch reverted to `Entries<BaseEntity>()`

```diff
         var entities = context.ChangeTracker
-            .Entries<IHasDomainEvents>()
+            .Entries<BaseEntity>() // MUTATION 1
```

`GenericKeyEntityTests` plus `Interceptors` (30 tests): **5 fail.**

```
ALongKeyedEntity_RaisingAnEventOnSave_ReachesItsHandler_AndIsStamped:
  Expected handler.Handled to contain a single item, but the collection is empty.
```

The other 4 are the existing `PicklistSet` dispatch tests:
`DispatchDomainEventsInterceptor_ShouldPublishAndClearDomainEvents`, `…Updated…`, `…Deleted…` and
`BothInterceptorsTogetherCompleteASaveWithoutTransactionConflict`. Their message is "Expected
invocation on the mock once, but was 0 times". `PicklistSet` is a `BaseAuditableEntity`, which is no
longer a `BaseEntity` (§3.1).

Restored: `a7dfda8117b50c80` → `a7dfda8117b50c80`.

**Variant 1b, to isolate the key-type claim.** `Entries<BaseEntity<int>>()` keeps every int-keyed
entity dispatching:

```diff
-            .Entries<IHasDomainEvents>()
+            .Entries<BaseEntity<int>>() // MUTATION 1b
```

**1 of 30 fails**, and it is exactly `ALongKeyedEntity_…` (handler never called). Restored, hash
identical.

### 4.2 Mutation 3: `IsRequest` reverted to its pre-pass body

```diff
-        typeof(IBaseRequest).IsAssignableFrom(type)
-        || typeof(IBaseCommand).IsAssignableFrom(type)
-        || typeof(IBaseQuery).IsAssignableFrom(type);
+        // MUTATION 3: the pre-Pass-55 body
+        type.GetInterfaces().Any(i =>
+            i == typeof(IRequest) ||
+            (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>)));
```

`RequestAuthorizationRegistryTests` (11): **5 fail.**

```
AnUnmarkedCommand_FailsTheStartupAssertion_ByName:
  Expected the exception message to match the equivalent of "*UnmarkedCommandProbe*" …
MarkingIt_Passes:
  Did not expect any exception, but found System.InvalidOperationException: Authorization registry
  found no Mediator request types in 'command probe'. …
```

To the old code, the unmarked command is invisible: the check reports "no request types" instead of
naming it. The marked one cannot pass either. Also failing:
`EveryRequestShape_IsRecognised_AndNothingElse`, `TheAssemblyScan_FindsCommandsAndQueries` and
`AStructRequest_IsRefused_ItCanNeverBeAuthorized`. The 6 pre-existing tests pass, as they should.

Restored: `c4bf8add514a879c` → `c4bf8add514a879c`.

### 4.3 Extra: the naming tests against the pre-pass convention (not requested)

With `GxNamingConventions.cs` at HEAD, **22 of 27** naming tests fail, and the reason is stronger
than the individual gaps. **The old convention cannot build any model containing an abstract TPC
root**:

```
System.InvalidOperationException : The entity type 'SampleShape' cannot be instantiated because its
corresponding CLR type is abstract, but the entity type was mapped to 'core.TBL_SAMPLE_SHAPE' using
the 'TPC' mapping strategy.
```

Every test whose model includes the shared shapes therefore fails at model building. The 5 that pass
are the `ToUpperSnake` cases, which build no model. In a real project this meant that adding an
abstract TPC root broke the context outright.

Restored: `4897dd019d7e62e6` → `4897dd019d7e62e6`.

---

## 5. Test counts

Same machine and server: PostgreSQL 18.0, localhost:5434. `GX_TEST_PG` was set for the run only.
`GX_TEST_CREATE_DATABASES` was not set, and Azurite was not running.

**Before** is Pass 54's final full run, which was on `bbd6ed6b`, the exact tree this pass started
from. A post-edit run of the old tests also matched those numbers.

| Suite | Before (`bbd6ed6b`) | After | Δ |
|---|---|---|---|
| Application.UnitTests | 558 passed, 14 skipped (572) | **567 passed, 14 skipped (581)** | +9 |
| Infrastructure.UnitTests | 221 (221) | **227 (227)** | +6 |
| Application.IntegrationTests | 57 (57) | **60 (60)** | +3 |
| Server.UI.IntegrationTests | 283 (283) | **283 (283)** | 0 |
| **Total** | **1,119 passed, 14 skipped** | **1,137 passed, 14 skipped** | **+18** |

No test was removed or renamed. The skips are the same 14: 12 Azurite tests and 2
`GX_TEST_CREATE_DATABASES` tests.

---

## 6. Standing controls

- **Build** (`--no-incremental`): 0 errors. The warning set (file, line, code) is **identical to
  Pass 54's: 50 unique, nothing added or removed.** The MSBuild counter reads 59, the same
  non-incremental repeat count as Pass 54.
- **Migrations:** none. The generic base classes change no column, and `ModelMatchesMigrationsTests`
  is green. The template model contains no TPT, TPC, join or `[Table]` entity, so the naming changes
  move nothing in it.
- **Pack and smoke:** `tooling/smoke-generate.ps1` with `GX_TEST_PG` set gives **SMOKE PASSED
  (postgresql)**, with 106 checks `ok`. The generated project's suites:

| Generated suite | Passed | Skipped |
|---|---|---|
| Application.UnitTests | 567 | 14 |
| Infrastructure.UnitTests | 206 | 0 |
| Application.IntegrationTests | 60 | 0 |
| Server.UI.IntegrationTests | 283 | 0 |

  Infrastructure.UnitTests was 200 in Pass 54. The +6 are the naming tests, which survive the
  provider stripping.

---

## 7. Not done, and follow-ups

- **Stream requests are not authorized by anything** (§3.3). They need a stream pipeline behaviour
  before the registry should count them.
- **InventoryMS is not updated to this pass yet.** Once this commit is pushed,
  the project update follows the same two-generation route as Pass 54's.

## 8. Commit

One commit on `main`, `Pass55-EntityKeysContextRegistry`, containing everything above, including
this report. Not pushed.
