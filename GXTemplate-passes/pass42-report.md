# Pass 42 — The Circularity Sweep

**Nature:** survey. Nothing changed; seven mutations applied and all seven restored byte-identically.
**Date:** 2026-09-06.

**Result in one line:** **one confirmed circular test**, found by mutation and not by reading —
`TheSupportedSetIsReadFromDbProviderKeys_NotAHandWrittenList`, which names in its own comment the
exact scenario it cannot detect. **Six authorization guards were removed one at a time and every one
of them reddened a test.** The suite's known weak spot is not its guards; it is its *fixtures*, where
41 of 45 hand-built `UserContext`s are a shape only a different code path can produce.

---

## 1. Start state

| | |
|---|---|
| HEAD | `a538e1b0` — *"Pass40-ClearTheBoard"* |
| Working tree | **not clean** — see below |
| Build | 0 errors |
| Tests | 229 + 12 + 512 + 251 = **1004 passed, 12 skipped, 0 failed** |

**The precondition did not hold: Pass 41 is uncommitted.** The tree carries its six entries
(`ReconnectModal.razor`, `boot.js` deleted, the test csproj, plus `pass41-report.md`,
`gxReconnect.js` and `ReconnectUiTests.cs`). Recorded and proceeded, as Pass 38's brief established
for the same situation — this pass changes nothing, so an uncommitted predecessor costs only that
the mutation restores had to be verified by **file hash** rather than by a clean `git status`. Each
was, and §3.1 records the hashes' agreement.

The 1004 figure matches Pass 41 §6.5 exactly. Probe work ran from `C:\gxp42`.

---

## 2. Coverage — what this sweep actually looked at

Stated first, because §A.4's error is the one this pass is most at risk of committing about itself.

| Population | Size | Examined | Method |
|---|---|---|---|
| Tests in the suite | 1016 (1004 + 12 skipped) | — | — |
| Test files | 126 | ~40 read in part or whole | targeted |
| **Reflection-over-the-subject sites** (§A.1) | **29, in 16 files** | **29 — complete** | grep-enumerated on `GetProperties\|GetFields\|GetMembers\|GetMethods\|typeof(X).Get`, every hit read |
| **Authorization decision points** (§A.2) | **16** — 13 `throw new ForbiddenAccessException` + 3 bool-returning `May*`/`IsAllowed*` helpers | **6 mutated** | see §3 |
| **Tests driven under mutation** | — | **118 distinct** | exact, measured |
| **`new UserContext(...)` in tests** (§A.3) | **45** | **45 — complete** | grep-enumerated, each classified |
| **Claim-type uses in tests** (§A.3) | **83 sites in 39 files**, 6 distinct claim types | all 6 types traced to their production writer; the 3 `TenantId` sites read individually | grep-enumerated by type |
| Named surveys (§A.4) | 4 | 4 | as listed in the brief |

**What this sweep did NOT cover, and therefore says nothing about:**

- **Identity comparisons that do not use reflection** — "both sides call the same production helper".
  This is the larger and less greppable half of §A.1. Sampled incidentally, never enumerated. The
  single confirmed instance was found in the reflection half; **the non-reflection half remains
  unmeasured.**
- **10 of the 16 authorization decision points**, listed in §3.3.
- Everything in `Application.IntegrationTests` beyond `Testing.cs` (12 tests), and the bulk of the
  512 `Application.UnitTests`.

**The honest rate is therefore a rate over what was examined, not over the suite.** See §7.

---

## 3. §A.2 — The mutation log

The only reliable detector for "right outcome, wrong reason" is removal. Seven mutations; each
applied to exactly one production file, verified unique before applying, and restored from a copy
taken outside the repository.

### 3.1 Results

| # | Guard removed | Target suites | Tests red | Verdict |
|---|---|---|---|---|
| **M1** | `RoleDefinitionWrite.MayDefineRolesAsync` → `true` | `RoleDefinitionRightTests` | **8 of 16** | real cover |
| **M2** | `SharedPicklistWrite.MayManageSharedAsync` → `true` | `SharedPicklistWriteTests`, `SharedPicklistCreationTests` | **7 of 26** | real cover |
| **M3** | `InstallationPolicyWrite.IsAllowedAsync` → `true` | `InstallationPolicyWriteTests` | **4 of 12** | real cover |
| **M4** | `AdministratorProtectionService.EnsureRoleCanBeDeleted` → no-op | role suites, both projects | **3 of 43** | real cover |
| **M5** | `PermissionAssignmentService.EnsureActorHolds` → no-op | `PermissionAssignmentGuardTests` | **4 of 13** | real cover |
| **M6** | `AuthorizationBehaviour` deny-by-default → never fires | `AuthorizationBehaviourTests` | **2 of 14** | real cover |
| **M7** | a **fourth key** added to `DbProviderKeys` | `DatabaseSettingsValidationTests` | **0 of 10** | **CIRCULAR — §4.1** |

Every restore verified by SHA-256 against the pre-mutation hash; all seven reported
`RESTORED byte-identical`. `git status` at the end of the pass shows exactly Pass 41's six entries
and nothing else, and `git diff --stat` over `src/Application`, `src/Infrastructure` and
`src/Server.UI/Hubs` is empty.

### 3.2 The reddened tests, named

Because "some tests went red" is not the same as "the right tests went red".

**M1** — `ANonHolderCannotRePermissionARoleThroughTheService`, **`ANonHolderCannotRePermissionARoleInBulk`**,
`ANonHolderCannotREVOKEAPermissionEither`, `AnUnassignedGrantIsNotAGrant`,
`AnUnrelatedPermissionIsNotThisOne`, `TheDefinitionGuardIsAskedBeforeTheActorIsBuilt`,
`TheRefusalSaysWhatIsStillAllowed`, `ANonHolderCanStillAssignAUserToAnExistingRole`.

**`ANonHolderCannotRePermissionARoleInBulk` is the test Pass 33 A2 found circular.** It now fails
when the guard it names is removed. **Pass 33's repair holds** — which is the single most valuable
result in this table, because it is the only one of the four historic shapes that could be re-tested
directly.

**M2** — `ANonHolderCannotEditASharedRow`, `ANonHolderCannotDeleteASharedRow`,
`ATenantlessNonHolderCannotCREATEASharedRow`, `ATenantScopedNonHolderAskingForSharedIsRefusedAndWritesNothing`,
`AMixedDeleteIsRefusedWholesaleRatherThanPartiallyApplied`,
`TheFlagGrantsNothing_ARefusedCallerNeverReachesTheInterceptor`, `TheRuleFailsClosedWithNoPrincipal`.

**M3** — `ANonHolderCannotSaveThePolicy`, `ANonHolderIsRefusedEvenWithAPrincipal`,
`ARefusedSaveDoesNotInvalidateTheCache`, `ARefusedSaveDoesNotSeedARowOnAFreshDatabase`.

**M4** — `TheAdministratorRoleCannotBeDeleted`, `TheAdministratorRoleCheckIsCaseInsensitive`,
**`AHolderIsStillRefusedOnTheProtectedAdministratorRole`**. The third is the test
`RoleDefinitionRightTests`' own remarks say exists "so a future pass cannot satisfy one by deleting
the other". It does exactly that.

**M5** — `AGranterWhoDoesNotHoldThePermission_IsDenied_AndNothingIsWritten`,
`RevokingAPermissionTheActorDoesNotHold_IsAlsoDenied`,
`ABulkGrantIsRejectedWholesaleIfAnySingleClaimIsNotHeld`, `OnlyPermissionClaimsCanBeAssigned`.
Notable because `EnsureNotTargetingSelf` and `EnsureNotTargetingAHeldRole` sit alongside
`EnsureActorHolds` and are exactly the "second, earlier check" that would have masked it. They do
not.

**M6** — `AnUnmarkedRequest_IsDenied_WithTheUnmarkedMessage`,
`TheUnmarkedMessageIsDistinctFromTheFailedCheckMessage`.

### 3.3 What was NOT mutated

Ten of the sixteen decision points, so this section's coverage is **37.5%**:

- `AuthorizationBehaviour` — the other two throws (no ambient principal; policy check failed).
- `AdministratorProtectionService` — `EnsureRolePermissionsCanBeModified`,
  `EnsureNotRemovingLastAdministratorAsync`, `EnsureRoleRewriteKeepsAnAdministratorAsync`.
- `PermissionAssignmentService` — the other five throws, including `EnsureNotTargetingSelf` and
  `EnsureNotTargetingAHeldRole`.
- `FileEndpoints`' fail-closed guard (Pass 38) — not mutated; it is the newest and best-covered.

Selection criterion, stated so the gap is legible: **the six chosen were the ones where a second,
earlier check plausibly existed** — the three Pass 33/37/40 "scope rights" that sit behind a per-verb
permission, the administrator protection that sits behind the definition right, grant-what-you-hold
that sits behind two adjacent self-targeting guards, and deny-by-default which everything else sits
behind. The ten unmutated are mostly *first* checks with nothing upstream of them, which is precisely
where this shape does not occur.

---

## 4. Findings by shape

### 4.1 §A.1 — Identity comparison: 1 confirmed

| | |
|---|---|
| **Test** | `DatabaseSettingsValidationTests.TheSupportedSetIsReadFromDbProviderKeys_NotAHandWrittenList` |
| **Claims to prove** | its own comment: *"if a fourth key is added to `DbProviderKeys` the validator picks it up automatically, and this test says so rather than the set silently drifting"* |
| **Actually proves** | that `DatabaseSettings.Validate`'s message mentions every key — which both sides read from the same type, by the same reflection |

Production:

```csharp
private static readonly string[] SupportedProviders = typeof(DbProviderKeys)
    .GetFields(Public | Static | FlattenHierarchy)
    .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
    .Select(f => (string)f.GetRawConstantValue()!)…
```

Test:

```csharp
var declared = typeof(DbProviderKeys)
    .GetFields(Public | Static)
    .Where(f => f.IsLiteral && f.FieldType == typeof(string))
    .Select(f => (string)f.GetRawConstantValue()!);
```

**Confirmed by mutation (M7).** A fourth key — `public const string Oracle = "oracle";` — was added to
`DbProviderKeys`. **All 10 tests stayed green.** The scenario the comment names is the one scenario
the test is structurally incapable of noticing, because adding a key moves both sides at once.

It is not *wholly* inert: it would redden if `SupportedProviders` were replaced by a hand-written
literal that omitted a key, which is what the test's *name* claims. **The name is defensible and the
comment is false**, and it is the comment that tells the next reader what is covered.

### 4.2 §A.2 — Right outcome, wrong reason: 0 found in 6 guards

No instance. Every guard mutated produced red tests, and in each case the red tests were the negative
tests that name that guard, not incidental collateral.

Two mutation artifacts, recorded so the numbers are not read as stronger than they are:

- **`TheRuleFailsClosedWithNoPrincipal` stayed green under M1** and reddened under M2. The mutation
  inserted `return true;` *after* the `if (string.IsNullOrEmpty(userId)) return false;` line, so the
  null-principal path was untouched in M1. That is the mutation being partial, not the test being
  weak.
- **`ANonHolderCanStillAssignAUserToAnExistingRole` reddened under M1**, which looks backwards for a
  positive test. Its last line asserts `MayDefineRolesAsync(...)` is false directly, so it reddens for
  the correct reason.

### 4.3 §A.3 — Unreachable inputs: 1 systemic, latent

**The `TenantId` claim — the Pass 38 A1 archetype — is fully remediated.** Three uses remain in the
whole test tree, and all three are deliberate:

| Site | |
|---|---|
| `FileEndpointsAuthorizationTests.Principal` | carries **no** tenant claim, and line 257 asserts it is null |
| `FileEndpointsAuthorizationTests.PrincipalWithTenantClaim` | *"the population for whom the old code was correct. Used only by the regression control"* |
| `ServerHubTenantIsolationTests.ConnectAsync(claimedTenantId:)` | a forged claim, used to prove it is **ignored** |

**The live finding is `UserContext`.** `UserContextLoader` — the only writer of the ambient context —
always populates `AllowedTenantIds`, and includes `user.TenantId` in it whenever that is non-empty:

```csharp
var allowedTenantIds = string.IsNullOrEmpty(user.TenantId)
    ? memberships.Distinct().ToList()
    : memberships.Append(user.TenantId).Distinct().ToList();
```

**Production invariant: `AllowedTenantIds` is never null on an ambient context, and contains
`TenantId` when there is one.**

**41 of the 45 `new UserContext(...)` in tests violate it** — typically
`new UserContext("u", "u", TenantId: "tenant-A")`, leaving `AllowedTenantIds` at its default `null`,
and then pushing that into `IUserContextAccessor`. Only four set it:
`TenantVisibilityTests`, `UserVisibilityTests`, `UserTenantScopeComponentTests` and
`UserDeactivationPermissionComponentTests` — and all four use `TenantId: allowed.FirstOrDefault()`,
so they honour the invariant exactly.

**Severity today: low. Severity by construction: exactly Pass 38 A1's.** The 41 are harmless because
their subjects (interceptors, query filters, log stamping, the publisher) read `TenantId` and never
`AllowedTenantIds`. The four suites that *do* exercise `AllowedTenantIds` are the four that populate
it — the discipline is real, it is just undeclared. The trap is that
`TenantDataSourceService` already contains

```csharp
var allowed = user.AllowedTenantIds?.ToArray() ?? Array.Empty<string>();
```

whose `?? Array.Empty<string>()` branch **is unreachable from production and reachable only from a
fixture.** The day a subject in one of the other 41 suites starts reading `AllowedTenantIds`, those
fixtures will silently exercise the defensive branch instead of the real one and will keep passing.

### 4.4 §A.4 — Surveys that read as exhaustive

| Survey | Enumerated or accumulated |
|---|---|
| **The `Clients.All` prohibition** (`ServerHub.cs:19` — *"Every recipient set in this file is a tenant group"*) | **Enumerated, and tested.** `ServerHubContainsNoBroadcastToEveryClient` reads `ServerHub.cs`'s **source text**, strips comment lines, and asserts the literal is absent. It scans the whole file, so a *newly added* method is covered — not just the sites that existed when the claim was written |
| **The administrator permission census** (`AdministratorPermissionRegistryTests`) | **Enumerated.** `covered.Should().BeEquivalentTo(all)` where `all` is every `Permissions.*` constant by reflection and `covered` is `Granted ∪ Excluded`, hand-maintained. Two independent sources, exhaustive comparison. A new constant in neither list reddens it |
| **`ISignalRHub`'s surface** (`TheChatAndPageComponentSurfacesNoLongerExist`) | **Mixed, and sound overall.** `hubMethods.Should().NotContain([five names])` is accumulated, but the same test's `typeof(ISignalRHub).GetMethods().Should().BeEquivalentTo([three names])` is exhaustive and carries the weight |
| **`VisibleDocumentSpecification`'s consumers** (*"two of its four list views"*) | **Accumulated, prose only, untested.** There are four real consumers today — `AddEditDocumentCommand`, `DeleteDocumentCommand`, `GetFileStreamQuery`, `AdvancedDocumentsSpecification` — plus `FileEndpoints`. Nothing enumerates them; a fifth list view that restated the clause by hand would contradict the remark and fail nothing |
| **`IUserContextAccessor`'s remarks** | **No consumer list found there.** The brief named it as a target; the file carries no enumeration to audit. Recorded as "nothing to check", not as "checked and clean" |

**The `Clients.All` scan deserves a note against Pass 38.** That pass's §4.3 concluded a source-text
guard was "not cheaply expressible here", partly because a scan hard-coding paths would *fail open* in
a generated project. This one had already solved that: it walks up from the test assembly looking for
`src/Server.UI/Hubs/ServerHub.cs`, is anchored on the folder layout (which generation preserves)
rather than on a namespace (which it renames), and **throws `FileNotFoundException` rather than
passing** if it cannot find the file. Pass 41's generation probe ran all 251 `Server.UI` tests green
in a generated project, so it does find it there. The technique Pass 38 wanted existed three passes
earlier, in a file that pass did not read.

### 4.5 §B — Structure where the property is behavioural

**One clear instance:**

| | |
|---|---|
| **Test** | `ServerHubTenantIsolationTests.NoHubMethodTakesATenantFromTheClient` |
| **Claims to prove** | *"A hub method parameter naming a tenant is a client-supplied claim"* |
| **Actually proves** | that no public declared method on `ServerHub` has a **parameter whose name contains "tenant"** |
| **Behavioural alternative** | available, and the file already contains the machinery: the send-site tests drive `OnConnectedAsync` through a mocked `HubCallerContext` and assert which group was reached. A method taking `string organisationId` and grouping by it would pass this test and fail a behavioural one |

**Three where structure is genuinely the only reachable surface**, which is a finding rather than an
excuse:

- **`LogDbContextSurfaceTests`** (5 tests) — "no handler can write to the log table through this
  contract" is a *compile-time* property. You cannot write a runtime test that fails to compile;
  reflection over `ILogDbContext` is the only proxy. Correct as built.
- **`TheChatAndPageComponentSurfacesNoLongerExist`** — asserting the absence of a method has no
  behavioural form.
- **`ServerHubContainsNoBroadcastToEveryClient`** — same, one level down: the prohibition is against
  code that does not exist yet.

**Four sites that look like §B and are not.** `RoleDefinitionComponentTests` (`Submit`,
`InvokePageAsync`, `_selectedRoles`), `UserTenantScopeComponentTests` (`ExportUsersAsync`) and
`UserLoginStateComponentTests` (`RaiseHubEvent`) all reach private members by reflection — but in the
**arrange and act**, to reach a handler a real user reaches by clicking. Every assertion that follows
is behavioural (rendered markup, exported rows, notifications raised). Reflection in the *act* is a
harness technique; reflection in the *assert* is the smell. Worth separating, because a grep for
`BindingFlags.NonPublic` finds both and they are not the same thing.

---

## 5. Severity ordering

| Rank | Finding | Why here |
|---|---|---|
| **1** | **§A.1 — `TheSupportedSetIsReadFromDbProviderKeys`** | The only *confirmed* circular test, and it is not cosmetic: it sits over a validation gap (§6.1) that lets a misconfigured provider through to a runtime throw the validator exists to pre-empt. The comment actively misinforms |
| **2** | **§A.3 — 41 partial `UserContext`s** | Latent, not live. But it is Pass 38 A1's exact shape, at 41× the scale, over the type Passes 28, 36 and 38 all turned on — and there is already one production line whose null branch only fixtures reach |
| **3** | **§B — `NoHubMethodTakesATenantFromTheClient`** | Guards a real tenancy boundary by checking *parameter spelling*. Cheap to strengthen; the file already has the machinery |
| **4** | **§A.4 — `VisibleDocumentSpecification`'s "four list views"** | Prose in a remark, contradicted silently by a fifth consumer. Low: the specification exists precisely so consumers do not restate the clause, and `AdvancedDocumentsSpecification` is the structural fix |
| — | **§A.2** | Nothing found in six guards |

---

## 6. Not circularity, reported separately

Per §D.3, so this pass's number means one thing.

### 6.1 `DatabaseSettings.Validate` accepts a provider the application cannot use

`SupportedProviders` is derived from `DbProviderKeys`, but the thing that must support a provider is
`UseDatabase`'s `switch`. Nothing ties the two together. Under M7, adding
`public const string Oracle = "oracle";` to `DbProviderKeys` made `DBProvider = "oracle"` **pass
validation** — and `DatabaseSettings.cs`'s own comment says why that matters:

> *"UseDatabase switches on DBProvider.ToLowerInvariant(), so match on the same value: anything else
> reaches its default arm and throws long after startup would have."*

The validator is defending against exactly the failure it would now permit. The correct source for
"supported" is the set of providers `UseDatabase` has arms for, not the set of names that exist. This
is a real defect found by a mutation aimed at something else; **it is a hypothetical today**, since
`DbProviderKeys` has exactly three keys and all three are wired.

### 6.2 `UserContext` serves two jobs and cannot tell you which one you hold

`UserContextLoader` builds the **ambient principal**, fully populated. `ServerHub.GetOnlineUsers`
builds a **presence DTO** with `AllowedTenantIds` and `Roles` deliberately left null. Both are
`UserContext`. So a partially-populated instance is producible *as a presence row* and unproducible
*as an ambient context*, and the type carries no way to say which it is. That is what makes §4.3's 41
fixtures arguable rather than plainly wrong — and it is the design fact underneath the finding.

---

## 7. The rate

**Of what was examined:**

| Population examined | Size | Circular | Rate |
|---|---|---|---|
| Reflection-over-subject sites (§A.1) — **complete enumeration** | 29 | **1** | **3.4%** |
| Authorization decision points (§A.2) — 37.5% sample, criterion stated | 6 | **0** | **0%** |
| Tests driven under mutation | **118** | **0** reddened wrongly; 28 reddened correctly | — |
| `new UserContext(...)` in tests (§A.3) — **complete enumeration** | 45 | **41 unproducible**, 0 currently misleading | 91% / **0% live** |
| Named surveys (§A.4) | 4 (+1 with nothing to audit) | **1 accumulated** | 25% |
| Structural-assertion sites (§B) | 12 | **1** wrong sensitivity; 3 correct; 4 misclassified by grep | 8.3% |

**Of the suite: unknown, and this pass cannot say.** 118 of 1016 tests were driven under mutation —
**11.6%** — and roughly 40 of 126 files were read. The largest unmeasured population is §A.1's
non-reflection half: "expected and actual both come from the same production helper" is not greppable
and was not enumerated. **Any statement of the form "the suite is N% circular" is unsupported by this
pass.**

The four historic instances were found at a rate of one per pass by passes that were not looking for
them. This sweep, looking for them deliberately across a targeted 12%, found one. That is consistent
with a low background rate and with the repairs holding — and it is not consistent with a claim that
the suite is clean, because the sample was chosen for suspicion, and the highest-yield shape (§A.1
non-reflection) was not sampled at all.

---

## 8. Where the suite survives mutation correctly

The brief asked for this, and it is the larger half of the measurement.

- **`ANonHolderCannotRePermissionARoleInBulk`** — Pass 33 A2's circular test, repaired, and **red
  under M1**. The single most direct evidence that a known instance stayed fixed.
- **`AHolderIsStillRefusedOnTheProtectedAdministratorRole`** — written specifically to stop a future
  pass satisfying one guard by deleting another. **Red under M4**, which is the scenario it names.
- **`AGranterWhoDoesNotHoldThePermission_IsDenied_AndNothingIsWritten`** — red under M5 despite two
  adjacent self-targeting guards that could plausibly have refused first. They do not.
- **`ARefusedSaveDoesNotSeedARowOnAFreshDatabase`** — red under M3. A refusal test that also checks
  nothing was *written*, which is the shape that survives a guard moving rather than being deleted.
- **`SinkColumnDriftTests`** — the Pass 24 archetype, repaired and now the best-defended cluster in
  the suite. Its SQLite column set is a hand-transcribed literal with the sink's own `INSERT`
  statement quoted in the remarks, and its own documentation explains the circularity it used to be.
  `LogTableDdlTests` and `SinkTimestampTests` compare `SystemLog`'s properties against columns read
  by `pragma_table_info` from a real database — and `LogTableDdl` holds its columns as literal data,
  not reflection, so those are genuinely two independent sources.
- **`ServerHubContainsNoBroadcastToEveryClient`** — strips comments before scanning, so the file's own
  prohibition is not mistaken for a violation, and **throws rather than passing** if it cannot find
  the source. A guard designed not to fail open.
- **`AdministratorPermissionRegistryTests`** — `covered.BeEquivalentTo(all)` over reflection versus a
  hand-maintained list. Exhaustive, two sources, and a new constant in neither reddens it.

---

## 9. Recommendation

| Finding | Recommendation |
|---|---|
| **§A.1 `TheSupportedSetIsReadFromDbProviderKeys`** | **Fix, with §6.1, in one small pass.** Derive `SupportedProviders` from what `UseDatabase` actually handles, and let the test assert against a literal three. That makes the test non-circular *and* closes the validation gap, because the two are the same defect seen from either end |
| **§6.1 the validation gap** | as above — same pass |
| **§A.3 41 partial `UserContext`s** | **Leave the fixtures; fix the type's ambiguity, or do nothing yet.** Rewriting 41 fixtures is a large diff for zero live defect. The cheaper and more durable move is §6.2's question — whether the presence DTO should be its own type — which would make the unproducible shape *unconstructible*. **Worth its own pass**, as a design question, not a test cleanup |
| **§B `NoHubMethodTakesATenantFromTheClient`** | **Fix, cheaply.** Add a behavioural sibling driving a hub method with a client-supplied tenant-ish argument and asserting the group is unchanged. Keep the structural test — it is a fast tripwire — but stop it being the only one |
| **§A.4 `VisibleDocumentSpecification`'s "four list views"** | **Leave, and reword.** Change the remark to say it enumerates today's consumers rather than implying a bound. A test enumerating consumers would be a source scan for modest value |
| **§A.2** | **Nothing to do.** Re-run this mutation set on the ten unmutated decision points if a future pass wants the other 62.5% |

**No fix was applied in this pass.**

---

## 10. Scratch probe disclosure

All under `C:\gxp42`, removed:

1. **`mutate.sh` / `mutate2.sh`** — the mutation harness: copy, verify-unique, apply, run, restore,
   hash-compare.
2. **`backup/`** — pre-mutation copies of the seven production files, used for restoration.

No database, no application run, no generation probe, no network. The only writes inside the
repository were the seven mutations, each restored and hash-verified; `git status` and
`git diff --stat` confirm the tree is exactly Pass 41's.

---

## 11. Anomalies

**A1 — the one circular test was invisible to reading and obvious to mutation, and I had already read
it and moved on.** On first pass I classified `TheSupportedSetIsReadFromDbProviderKeys` as *partially*
circular — reasoning, correctly, that it would still catch a hand-written list, and therefore had a
real failure mode. That reasoning is sound and it is the wrong test to apply. The question is not
"does this test have any failure mode" but **"does it have the failure mode it claims"** — and its
comment claims the one it cannot have. It took adding a fourth key and watching ten tests stay green
to settle it. Recorded because it is the same error the four historic instances share: each was read
by someone competent who concluded the assertion was reasonable, because the assertion *was*
reasonable — it just was not the assertion the surrounding prose promised.

**A2 — a guard's cover is not measured by how many tests redden, and one of these was nearly
misread.** M4 reddened 3 tests of 43; M1 reddened 8 of 16. The ratio says nothing: M4's filter
deliberately swept two whole role suites to see whether anything *unexpected* depended on
administrator protection, and nothing did — which is the good outcome and produces the low ratio.
Recorded because a mutation report that ranks guards by redden-count would rank M4 as the weakest,
and it is not.

**A3 — the brief named a survey that does not exist, and "checked and clean" would have been the easy
answer.** `IUserContextAccessor`'s remarks were listed as a §A.4 target; the file carries no consumer
enumeration to audit. The honest entry is "nothing to check" — which reads like a gap in the sweep and
is not one. Recorded because §A.4's whole subject is tables that present absence and presence
identically, and a survey row saying "clean" where the correct answer is "not applicable" commits the
error the section exists to catch.

**A4 — Pass 38 declared a technique infeasible that Pass 30 had already shipped.** Pass 38 §4.3
reasoned that a source-text guard could not be made safe here, because a scan anchored on paths or
namespaces would find nothing in a generated project and fail open. `ServerHubContainsNoBroadcastToEveryClient`
had solved precisely that — anchored on the folder layout, which generation preserves, and throwing
rather than passing when the file is not found. The reasoning in Pass 38 was correct in general and
wrong about this repository, and the counter-example was in a file that pass had no reason to open.
Recorded as the survey-shaped version of A1: **an argument about what is possible is not evidence
about what exists**, and this programme's own reports are now large enough that "we cannot do X" needs
the same grep that a claim about the code would get.
