# Pass 43 — The Validator That Defends Against What It Permits

**Nature:** editing pass, four items, all four completed. **No git actions.**
**Date:** 2026-09-06.

**Result in one line:** the validation gap and the circular test Pass 42 found from either end are
**closed together, by making them the same object** — `UseDatabase` now dispatches from a table and
`DatabaseSettings` validates against that table's keys, so a provider cannot be validatable without
being dispatchable. Three mutations proved it: the pre-fix code was reproduced and **`"oracle"`
passed validation**; the fixed code rejects it; and a fourth *dispatchable* provider now reddens the
test that used to stay green. `NoHubMethodTakesATenantFromTheClient` gained a behavioural sibling
that reddens on a rewrite it cannot see.

---

## 1. Start state

| | |
|---|---|
| HEAD | `a538e1b0` — *"Pass40-ClearTheBoard"* |
| Working tree | **not clean** — Pass 41 and Pass 42 both uncommitted (see below) |
| Build | 0 errors, **19 warnings** (clean `--no-incremental` rebuild), across 10 distinct source locations plus `NETSDK1206` |
| Tests | 229 + 12 + 512 + 251 = **1004 passed, 12 skipped, 0 failed** |

**Actual counts per suite, measured, not asserted by the brief:**

| Suite | Passed | Skipped |
|---|---|---|
| `Infrastructure.UnitTests` | 229 | 0 |
| `Application.IntegrationTests` | 12 | 0 |
| `Application.UnitTests` | 512 | 12 |
| `Server.UI.IntegrationTests` | 251 | 0 |
| **Total** | **1004** | **12** |

Identical to Pass 42's start state, which is the comparison the brief asked for; Pass 42 changed
nothing, so the two agreeing is the expected result and the check that says so.

**The precondition did not hold, for the third pass running.** The tree carried Pass 41's six
entries *and* `pass42-report.md`. Recorded and proceeded, as Passes 38 and 42 did for the same
situation. The cost is the same as Pass 42's: mutation restores are verified by **SHA-256** rather
than by a clean `git status`, and every one below is.

Probe work ran from `C:\gxp43`, a short path (Pass 40 A3).

---

## 2. §A — The provider set

### 2.1 The mechanism chosen, and why

The brief offered three: a single collection the switch dispatches from; a test asserting the two
agree; the validator consulting the same construct the switch uses. **The first and third are the
same move, and that is what was built** — one dispatch table, written once, dispatched from by
`UseDatabase` and read by `DatabaseSettings`:

```csharp
private static readonly IReadOnlyDictionary<string, Func<DbContextOptionsBuilder, string, bool, DbContextOptionsBuilder>>
    DatabaseProviders = new Dictionary<...>(StringComparer.Ordinal)
    {
        [DbProviderKeys.Npgsql]    = (builder, connectionString, snakeCaseNaming) => { ... },
        [DbProviderKeys.SqlServer] = (builder, connectionString, _) => builder.UseSqlServer(...),
        [DbProviderKeys.SqLite]    = (builder, connectionString, _) => builder.UseSqlite(...),
    };

internal static readonly IReadOnlyList<string> SupportedDatabaseProviders = DatabaseProviders.Keys.ToArray();
```

```csharp
// UseDatabase, in full
if (!DatabaseProviders.TryGetValue(dbProvider.ToLowerInvariant(), out var configure))
    throw new InvalidOperationException($"DB Provider {dbProvider} is not supported.");

return configure(builder, connectionString, snakeCaseNaming);
```

```csharp
// DatabaseSettings
private static readonly string[] SupportedProviders = DependencyInjection.SupportedDatabaseProviders.ToArray();
```

**Why not the second option — a test asserting the two agree.** It detects the drift; it does not
prevent it, and it costs a test that has to enumerate the switch's arms by reading source or by
hand. The requirement the brief actually stated is structural: *a provider cannot be validatable
without being dispatchable*. With one table that is not a rule to be enforced but an identity — the
set the validator accepts **is** the set of arms. A test asserting an identity would itself be the
shape this pass exists to remove.

**Why the table lives in `DependencyInjection` and not in a new file.** `UseDatabase`'s arms carry
~20 lines of load-bearing remarks (the `EnableLegacyTimestampBehavior` history, the
`snakeCaseNaming` prohibition) that would have to move with them, and the three
`*_MIGRATIONS_ASSEMBLY` constants with them again. `DatabaseSettings` already named
`DependencyInjection.UseDatabase` in its own comment, so the conceptual dependency was already
declared; this makes it a compile-time one. **No signature or DI change crossed a project boundary,
so the brief's stop condition was not reached** — the migrator projects are untouched.

**What the table does NOT govern, stated in its own remarks so the next reader is not misled.**
There are two other provider switches — `UseExceptionProcessor` in the same file, and the sink
selection in `SerilogExtensions`. A fourth provider would need an arm in both. Neither is consulted
by validation, so **neither can produce the "validates, then throws at first use" shape**; they
throw at the same point they always did. Left alone deliberately, and named in the new test's
comment as the thing to check when it reddens.

### 2.2 The test

`TheSupportedSetIsReadFromDbProviderKeys_NotAHandWrittenList` →
**`TheSupportedSetIsExactlyTheThreeProvidersUseDatabaseCanDispatch`**. It parses the validator's own
failure message and asserts the listed set:

```csharp
listed.Should().BeEquivalentTo(new[] { "postgresql", "mssql", "sqlite" }, ...);
```

The old comment claimed *"if a fourth key is added to `DbProviderKeys` the validator picks it up
automatically, and this test says so"*. That is now **false in its premise** (the validator does not
read `DbProviderKeys`) and was false in its claim (§2.3 M43b). The replacement says what is true:

> *A hand-written list, deliberately. The validator now reads its set from the dispatch table
> `DependencyInjection.UseDatabase` resolves an arm from, so a test that read that same table would
> move with it and prove nothing — which is exactly what this test used to do against
> `DbProviderKeys`. […] Naming the three here makes this the second, independent source. Adding or
> removing a provider arm reddens it, and that is the prompt to check the two provider switches
> validation does NOT see — `UseExceptionProcessor`, and `SerilogExtensions`' sink selection.*

A second, unplanned improvement falls out: `AnUnsupportedProvider_FailsValidationNamingTheValueAndTheSupportedSet`
asserts the message contains `DbProviderKeys.SqLite`, `.SqlServer` and `.Npgsql`. Those constants
are no longer the validator's source, so **that assertion is now cross-source too** — it fails if a
declared constant loses its arm.

### 2.3 Mutation evidence

Three mutations, each applied to files copied outside the repository first and restored from that
copy, every restore SHA-256-verified.

| # | Mutation | Expected | Observed |
|---|---|---|---|
| **M43a** | fourth key `Oracle = "oracle"` added to `DbProviderKeys`, **with the fix in place**; `AnUnsupportedProvider` temporarily repointed from `"mysql"` to `"oracle"` | `"oracle"` still rejected | **10 of 10 green** |
| **M43a-control** | same, but `DatabaseSettings.SupportedProviders` temporarily reverted to the pre-fix reflection over `DbProviderKeys` | the Pass 42 §6.1 gap reproduces | **2 of 10 red** — see below |
| **M43b** | fourth *dispatchable* arm `["oracle"]` added to the table, fix in place | the new test reddens | **1 of 10 red** |

**M43a-control is the important one**, because it is the only evidence that the gap was real rather
than argued:

```
Failed AnUnsupportedProvider_FailsValidationNamingTheValueAndTheSupportedSet
  Expected a <OptionsValidationException> to be thrown, but no exception was thrown.
```

`DBProvider = "oracle"` **passed validation** — a provider `UseDatabase` has no arm for, admitted by
the check that exists to reject it. Pass 42's M7 established that no test noticed; this establishes
what was actually wrong underneath. The same run also reddened the *new* test:

```
Failed TheSupportedSetIsExactlyTheThreeProvidersUseDatabaseCanDispatch
  Expected listed to contain exactly 3 items in any order ..., but found one extraneous item at index 3: "oracle"
```

which is the direct comparison the brief asked for: **the same mutation, on the same code, that left
all ten green in Pass 42, now reddens.**

**M43b** is the scenario the old comment named — a fourth provider genuinely added — and it reddens
identically. So the test is sensitive to a provider arriving whether or not the validator is wired
correctly, which is what "not circular" means here.

**Restores.** Four files were mutated across the three runs. Pre-mutation and post-restore hashes:

| File | SHA-256 | Restored |
|---|---|---|
| `src/Application/Common/Constants/DbProviderKeys.cs` | `7a65bf0a...97adf` | identical |
| `src/Infrastructure/DependencyInjection.cs` | `62d74986...28b5c` | identical |
| `src/Infrastructure/Configurations/DatabaseSettings.cs` | `b4cae77f...92851` | identical |
| `tests/.../DatabaseSettingsValidationTests.cs` | `bf77368e...8779f` | identical |

Hashes are of the **post-fix** files; the mutations were applied on top of this pass's edits and
rolled back to them, not to HEAD.

### 2.4 The reverse control — the three still validate *and* still dispatch

A change that made everything invalid would satisfy every negative above. Run as a temporary probe
(`Pass43DispatchProbe`, six cases, **all green**, deleted before the pass ended — §10):

| Case | Asserted | Result |
|---|---|---|
| `postgresql` | `UseDatabase` produces options carrying an `Npgsql...` extension | pass |
| `mssql` | ... a `SqlServer...` extension | pass |
| `sqlite` | ... a `Sqlite...` extension | pass |
| `SQLite` | same as `sqlite` — the case-insensitivity the settings comment promises | pass |
| the set | `SupportedDatabaseProviders` is exactly the three | pass |
| `oracle` | still throws `DB Provider oracle is not supported.` from `UseDatabase` | pass |

Permanent cover for the validating half is `EverySupportedProviderKey_Validates` (3 `TestCase`s,
green in the full run). The dispatching half needs no permanent test *because it is the same set* —
the probe exists to show that identity holds at runtime and not only on paper.

---

## 3. §B — The hub-parameter test

### 3.1 What was added

`NoClientSuppliedArgumentMovesAConnectionIntoAnotherTenantsAudience`, in
`ServerHubTenantIsolationTests`. It drives **every public declared method on `ServerHub`** with every
string argument set to the *other* tenant's id, then asserts all three of Pass 30B's surfaces:

```csharp
alice.GroupsJoined.Should().AllBe(GroupA, ...);      // which group the connection is IN
alice.GroupsAddressed.Should().AllBe(GroupA, ...);   // which group a broadcast is addressed TO
snapshot.Select(u => u.UserName).Should().NotContain("carol", ...);  // what a return value carries
```

Two recording helpers were added to the existing `Connection` harness: `GroupsAddressed` (every name
passed to `Clients.Group`, captured in the strict mock's callback) and `GroupsJoined` (every
`Groups.AddToGroupAsync` target). Nothing else in the harness changed.

Today `GetOnlineUsers()` takes no arguments, so the loop supplies none — the test currently asserts
that the connect-time audience survives being driven. **The point is that it does not stay that
way:** a method added tomorrow with a client-supplied scope argument is driven with tenant B's id the
moment it exists, with no edit to this test.

### 3.2 Red before, green after

The property is currently guarded only by spelling, so there was no fix to revert. **A temporary
mutation was used instead**, and it is deliberately the one Pass 42 §4.5 named:

```csharp
public async Task<List<UserContext>> GetOnlineUsers(string? organisationId = null)
{
    var callerGroup = GroupFor(organisationId ?? Context.GetUserContext()?.TenantId);
```

| | result |
|---|---|
| `NoHubMethodTakesATenantFromTheClient` (structural) | **green** — the parameter is not spelled "tenant" |
| `NoClientSuppliedArgumentMovesAConnectionIntoAnotherTenantsAudience` (behavioural) | **red** |
| suite | 18 of 19 passed |

```
Expected snapshot.Select(u => u.UserName) {"carol"} to not contain "carol" because GetOnlineUsers
returned another tenant's roster when asked for it by argument ...
```

`src/Server.UI/Hubs/ServerHub.cs` restored byte-identically, SHA-256 `8cc36119...1d8b03`, and
`git diff` on it is empty. With the mutation reverted, 19 of 19 pass.

### 3.3 Which test carries the weight

**The behavioural one.** Stated in the report because the brief asked, and stated **in the source**
because that is where someone deleting a test will be. The structural test's summary now reads:

> *The structural tripwire. It is fast and it fails on the obvious mistake, but it checks PARAMETER
> SPELLING — so a method taking `string organisationId` and grouping by it passes, and a rename that
> preserves the property reddens it. **The property itself is carried by
> `NoClientSuppliedArgumentMovesAConnectionIntoAnotherTenantsAudience`, below.** Keep both: this one
> names the rule in a form a reader can act on, that one is what actually holds it. Pass 42 §4.5
> recorded the wrong sensitivity in both directions.*

Both are kept. The structural test is the faster, more legible statement of the rule; it is no
longer the only thing standing behind it.

---

## 4. §C — `VisibleDocumentSpecification`'s remark

### 4.1 The consumer list at HEAD — checked, not assumed

`grep` over `src/` for `VisibleDocumentSpecification|IsVisibleTo`, every hit read. **Five call sites,
exactly what the remark names:**

| Consumer | Site |
|---|---|
| `AddEditDocumentCommand` | `:63` |
| `DeleteDocumentCommand` | `:58` |
| `GetFileStreamQuery` | `:80` |
| `AdvancedDocumentsSpecification` | `:35` |
| `FileEndpoints` (`/files`) | `:157` |

Accurate at HEAD. Pass 38's addition is present; Passes 40 and 41 did not touch it.

### 4.2 The wording

New — the enumeration is now explicitly a snapshot, and the second paragraph says why the count does
not matter:

> *Extracted so there is exactly ONE definition of document visibility. A security rule with two
> copies is a security rule with one copy that is out of date.*
>
> ***Its consumers as of Pass 43, listed to orient a reader and not as a bound on how many there may
> be:** `GetFileStreamQueryHandler` for the download button, the `/files` streaming endpoint for
> anything rendered straight from a document's PublicUrl, `AdvancedDocumentsSpecification` for every
> listing, and the edit and delete commands before they touch a row. Nothing enumerates them and no
> test holds the list to those five, so read it as "at least these" and confirm with a find-usages
> rather than trusting the count — Pass 38 added the `/files` endpoint and this paragraph had to be
> edited by hand to say so.*
>
> ***What the list is for is the rule beside it, which does not change when the list does:** a new
> consumer applies `IsVisibleTo` rather than restating the clause. That is the whole reason the rule
> is a shared expression, and it is why a sixth consumer missing from the paragraph above is a stale
> comment and not a security defect — whereas a sixth consumer that wrote the clause out by hand
> would be the defect, and would contradict nothing here.*

No source-scan test, per Pass 42's recommendation.

### 4.3 A correction to Pass 42's citation

Pass 42 §4.4 and the brief both quote *"two of its four list views"* as the consumer enumeration. It
is not. That phrase is in a **different paragraph**, about Pass 24's finding that
`AdvancedDocumentsSpecification` had restated the clause in two of the four list views **it had at
the time** — a historical fact, and a true one. The accumulated survey is the *first* paragraph's
"It is enforced now by ...", which is what §4.2 rewords. The historical sentence is kept and given
"it had at the time" so it cannot be read as a present bound either. Recorded in §9 A2, because
acting on the quoted phrase rather than reading the file would have reworded the one sentence in
that remark that was already correct.

---

## 5. §D — `IUserContextAccessor`'s remarks

Pass 42 §4.4 was right that there is no consumer list here; the brief's question was whether there
*should* be something. There should, and it is not a list.

**Verified before writing it:** `grep` for `.Push(` across `src/` returns exactly one production
call site — `UserContextHubFilter.InvokeMethodAsync:52`. The claim is therefore checked, not
inherited.

The warning is now on `Current`, where someone reaching for it lands:

> ***This is `null` outside a SignalR hub method invocation, and that is most of the application.***
> *The sole writer is `UserContextHubFilter.InvokeMethodAsync`, which pushes an `AsyncLocal` for the
> duration of one hub method call. A Blazor Server circuit runs over SignalR, so component event
> handlers and the Mediator handlers they dispatch are inside that window and do see a value.*
>
> ***Everything else does not, and needs a different source.** Every HTTP request — minimal API
> endpoints, controllers, static file paths; the cookie handler's principal validation, where
> `IdleSessionEnforcer` runs; background and hosted work; and the hub's own `OnConnectedAsync` /
> `OnDisconnectedAsync`, because the filter's lifetime callbacks write `HubCallerContext.Items` and
> nothing else. Two replacements, and no third: inside a hub, `HubUserContext.GetUserContext()` off
> the `HubCallerContext`; on an HTTP path, `IUserContextLoader.LoadAsync`, which takes a
> `ClaimsPrincipal`, reads the tenant from the USER ROW, caches per user and is invalidated on tenant
> switch. Never the principal's tenant claim — `ApplicationUserClaimsPrincipalFactory` does not add
> one, so it is absent for every user who has never switched tenant.*
>
> ***Why this warning is here and not only where it was written.** The same three-way analysis lives
> in `HubUserContext`'s remarks, which is the right place for a hub author and no place at all for
> anyone else: Pass 38 A2 recorded that it sat away from the API it warned about and therefore had no
> readership. Pass 36 and Pass 38 both cost a pass to a call site that reached for an ambient value
> that was null there — the file-endpoint tenant read being the expensive one, since a null tenant
> makes `VisibleDocumentSpecification`'s no-tenant branch drop the tenant clause entirely and serve
> every public document in the installation. **A null here reads as "unconstrained" and must be
> treated as "unresolved": fail closed.***
>
> *No consumer list is kept here, deliberately. There are ~20 injection sites across Application and
> Infrastructure; an enumeration would be stale within a pass, and Pass 42 §A.4 found accumulated
> lists mislead precisely because absence and presence read identically in prose.*

The "~20" is measured: 20 `private readonly IUserContextAccessor` fields, 22 constructor parameters.

**Copied, not moved.** `HubUserContext`'s bullet is the right argument for a hub author choosing
between three sources, and it stays; it gains one sentence pointing at the general form:

> *... so `Context.Items` is the one source that works in both places. **The general form of this —
> where the ambient context IS and is not populated, and what to use instead — is now on
> `IUserContextAccessor.Current` itself, so that a caller outside a hub meets it too.***

The interface's own summary carries a one-line version so it is visible before the property is
expanded.

---

## 6. §E — Verification

| # | Check | Result |
|---|---|---|
| 1 | §A mutation evidence + reverse control | §2.3, §2.4. Three mutations, four files, **all restored SHA-256-identical**; three providers validate and dispatch, per provider, plus the case-insensitive form |
| 2 | §B red before / green after | §3.2. Red established by temporary mutation — there was no fix to revert, stated explicitly; the structural test stayed green through it, which is the finding |
| 3 | §C, §D by inspection, wording quoted | §4.2, §5 |
| 4 | Boundary suites green and byte-unmodified | below |
| 5 | Full suite; delta; warnings | below |
| 6 | Generation probe from a short path, uninstall before install | below |

**Boundary suites.** Green, and `git diff --name-only` over all of them is **empty** — none was
edited to make anything pass:

| Suite set | Tests | Result |
|---|---|---|
| Pass 15B / 11C database and log suites — `Infrastructure.UnitTests` `Logging` + `Persistence` | 131 | green |
| `Application.UnitTests` `Logging` + `Configurations` | 33 | green |
| Pass 30B presence suites — `ServerHubTenantIsolationTests`, `UserLoginStateComponentTests`, `LogDatabaseSeparationTests` | 34 | green |

`ServerHubTenantIsolationTests` is of course *not* byte-unmodified — it is §B's deliverable. The
other two presence-adjacent suites are.

**Full suite and delta.**

| Suite | Start | End | Δ |
|---|---|---|---|
| `Infrastructure.UnitTests` | 229 | 229 | 0 |
| `Application.IntegrationTests` | 12 | 12 | 0 |
| `Application.UnitTests` | 512 (+12 skipped) | 512 (+12 skipped) | 0 — one test **replaced**, not added |
| `Server.UI.IntegrationTests` | 251 | **252** | **+1** — §B's behavioural sibling |
| **Total** | **1004** | **1005** | **+1** |

0 failed, 12 skipped, unchanged.

**Warnings: unchanged.** Clean `--no-incremental` rebuild: **0 errors, 19 warnings**, the same count
Pass 41 recorded, across the same 10 distinct source locations (`DescriptionAttributeExtensions` ×4,
`MapsterConfiguration` ×2, `TenantSelect.razor`, `AuditTrails.razor`, `Dashboard.razor`,
`MudDateTimeField.razor`) plus `NETSDK1206`. **None is in a file this pass touched.**

**Generation probe.** From `C:\gxp43`, a short path. Uninstalled before installing, and the result
checked rather than assumed — `dotnet new uninstall GX.Blazor.Template` reported not-found (exit
103), so zero prior registrations of ours:

```
dotnet new uninstall GX.Blazor.Template  (not found - 0 registrations)
  -> dotnet pack build/pack.csproj -o C:\gxp43   (template.json, ide.host.json, icon.png verified)
  -> dotnet new install ...nupkg -> dotnet new gxblazor -n P43
  -> dotnet build P43.slnx : 0 Error(s), 19 Warning(s)
  -> dotnet test P43.slnx  : 229 + 12 + 512 + 252 = 1005 passed, 12 skipped, 0 failed
  -> dotnet new uninstall; C:\gxp43 removed
```

The generated project's counts match the repository exactly, including §B's new test — which matters
more than usual here, because that test's fixture and `ServerHubContainsNoBroadcastToEveryClient`
share the folder-anchored source reader that generation renames around.

---

## 7. File map — the four items, separable

| Item | File | Change |
|---|---|---|
| **§A** | `src/Infrastructure/DependencyInjection.cs` | `UseDatabase`'s `switch` → `TryGetValue` over a new `DatabaseProviders` table; new `internal SupportedDatabaseProviders`; the three arms become lambdas carrying their existing comments verbatim |
| **§A** | `src/Infrastructure/Configurations/DatabaseSettings.cs` | `SupportedProviders` now reads `DependencyInjection.SupportedDatabaseProviders`; reflection and `using System.Reflection` removed; remarks rewritten |
| **§A** | `tests/Application.UnitTests/Configurations/DatabaseSettingsValidationTests.cs` | one test replaced one-for-one; comment now true |
| **§B** | `tests/Server.UI.IntegrationTests/ServerHubTenantIsolationTests.cs` | new behavioural test; `GroupsAddressed` / `GroupsJoined` on the `Connection` harness; structural test gains a summary naming its sibling |
| **§C** | `src/Application/Features/Documents/Specifications/VisibleDocumentSpecification.cs` | remarks only |
| **§D** | `src/Application/Common/Interfaces/Identity/IUserContextAccessor.cs` | remarks only |
| **§D** | `src/Infrastructure/Services/Identity/HubUserContext.cs` | one sentence, cross-reference |

No item touched another's files. §A is the only one with production behaviour in it, and its
behaviour is unchanged for all three supported providers (§2.4).

**Diffstat** — this pass's seven files only; the tree also carries Pass 41's four entries and two
pass reports, untouched here:

```
 .../Interfaces/Identity/IUserContextAccessor.cs    |  44 ++++++--
 .../Specifications/VisibleDocumentSpecification.cs |  26 +++--
 .../Configurations/DatabaseSettings.cs             |  20 ++--
 src/Infrastructure/DependencyInjection.cs          | 115 ++++++++++++-------
 .../Services/Identity/HubUserContext.cs            |   4 +-
 .../DatabaseSettingsValidationTests.cs             |  28 +++--
 .../ServerHubTenantIsolationTests.cs               |  78 +++++++++++++-
 7 files changed, 248 insertions(+), 67 deletions(-)
```

**Edit fidelity.** Every comment moved into a lambda in `DependencyInjection` was moved verbatim,
with one deliberate edit: *"see the parameter's remarks"* became *"see `UseDatabase`'s
`snakeCaseNaming` remarks"*, because the text is no longer inside the method that declares the
parameter. Four mutated files restored SHA-256-identical; one temporary probe file created and
deleted (`tests/Application.UnitTests/Configurations/Pass43DispatchProbe.cs` — §2.4). `git status`
shows exactly the seven files above plus Pass 41's and Pass 42's pre-existing entries, and nothing
else.

---

## 8. What remains

| Item | Status |
|---|---|
| **The `DbProviderKeys` circular test and the validation gap** | **closed** — §2, one object, mutation-proved in both directions |
| **`NoHubMethodTakesATenantFromTheClient` as the only guard** | **closed** — §3; the structural test is kept as a tripwire and says so |
| **`UseExceptionProcessor` and `SerilogExtensions`' sink switch** | **open, and now documented.** A fourth provider needs an arm in each. Neither is consulted by validation, so neither has §A's shape; both would throw at first use. Not worth a table each until there is a fourth provider |
| **`UserContext` serves two jobs** (Pass 42 §6.2) | **open — its own pass.** The ambient context and `GetOnlineUsers`' presence DTO are the same type, so a partially-populated instance is producible as one and impossible as the other. That ambiguity is what makes Pass 42 §4.3's 41 fixtures arguable rather than wrong, and splitting the type would make the unproducible shape unconstructible |
| **The integration harness pinned to LocalDB** | open. `tests/Application.IntegrationTests` ignores `--Database`, so nine tests fail rather than skip without LocalDB |
| **The Respawn question** | open. Whether database reset between integration tests should move to Respawn, and what that costs on three providers |
| **Picklist duplicate reporting** (Pass 40 §5.6) | open. `AddEditPicklistSetCommand` has no duplicate check, so duplicates surface as `DbUpdateException` on both partitions |
| **The upstream contribution** | open. Which of these forty-three passes' findings belong back in `CleanArchitectureWithBlazorServer` |
| **Pass 42 §A.1's non-reflection half** | open. "Expected and actual both come from the same production helper" was never enumerated; it is the largest unmeasured population in that sweep |

---

## 9. Anomalies

**A1 — the mutation the brief prescribed would have proved nothing, and the reason is the fix.**
§A.3 says *"add a fourth key temporarily, confirm the suite goes red"*. After the fix, adding a key
to `DbProviderKeys` is **inert**: the validator no longer reads that type, so the suite stays green —
correctly, because an unused constant is not a defect. Running the prescribed mutation and reporting
"green, therefore fine" would have been a non-result dressed as evidence. The mutation had to be
split into the two things it was standing for: **M43a-control**, which reproduces the pre-fix
derivation and shows `"oracle"` being *admitted* (the gap, demonstrated rather than argued), and
**M43b**, which adds a fourth *dispatchable* provider and shows the test *reddening* (the
circularity, gone). Recorded because the brief's instruction was written before the mechanism was
chosen, and a fix that changes what a mutation means also changes which mutation is evidence.

**A2 — the brief and Pass 42 both quoted the wrong sentence, and the quoted one was correct.**
§C names *"two of its four list views"* as the remark that reads as a bound. That sentence is Pass 24
history about `AdvancedDocumentsSpecification`'s own list views and is accurate; the accumulated
survey is a different paragraph in the same remark, "It is enforced now by ...". Reworded the one
that was actually wrong and left the history standing, with "it had at the time" added so neither can
be misread. Recorded because this is Pass 42 §11 A4's shape one level in: **a citation is not a
reading**, and two documents repeating the same quotation is not two confirmations of it.

**A3 — the first draft of the §A dispatch probe silently targeted the wrong type, and failed loudly
rather than passing wrongly.** `typeof(DependencyInjection)` inside
`namespace CleanArchitecture.Blazor.Application.UnitTests.Configurations` resolves to
**`CleanArchitecture.Blazor.Application.DependencyInjection`** — the enclosing namespace wins over
the `using` — so every reflection lookup returned null and six tests died on
`NullReferenceException`. Worth recording because `DatabaseSettings.cs` makes the *same* unqualified
reference and is correct there for the same rule: it sits in `...Infrastructure.Configurations`, so
the enclosing namespace resolves the other way. Two identical-looking lines, opposite meanings,
decided by which file they are in. Had the probe been written to tolerate a null lookup, it would
have asserted nothing and reported six passes.

**A4 — the precondition has now failed three passes running, and the cost is no longer only
bookkeeping.** Passes 41, 42 and 43 are all uncommitted in one tree. `git status` can no longer tell
this pass's work from its predecessors', which is why §7's diffstat had to be taken over an explicit
seven-file list and every mutation restore had to be hash-verified rather than confirmed by a clean
diff. That still worked. What it does not survive is a fourth pass touching a file one of the first
three already changed: there would be no baseline to restore to except the mutation backup, and no
way to attribute a regression. Recorded as a real risk rather than a formality.

---

## 10. Scratch probe disclosure

1. **`<scratchpad>/backup/`** — pre-mutation copies of the four §A files and `ServerHub.cs`, plus
   `hashes.txt`, used for restoration and verification. Outside the repository.
2. **`tests/Application.UnitTests/Configurations/Pass43DispatchProbe.cs`** — the §2.4 reverse
   control, six cases, all green. **Created and deleted inside this pass**; it is not in the tree.
3. **`C:\gxp43`** — the generation probe: nupkg, installed template, generated `P43`, built and
   tested. Template uninstalled, directory removed.

No external database was touched. No network beyond NuGet restore. The only writes inside the
repository were the seven deliverable files, the four restored mutations, and the deleted probe.
