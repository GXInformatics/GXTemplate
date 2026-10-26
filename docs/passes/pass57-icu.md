# Pass 57 — App-local ICU, and 1.4.0

**Scope:** the handoff `docs/HANDOFF-icu-timezones.md`, the same fix as FMS pass 17c. Apps generated from
the template carry their own ICU, so the IANA ids they use for time zones (`DefaultTimeZone` =
`Africa/Lagos`) resolve on Windows Server 2019 and older. Before this, such a server answered IIS
500.30 after creating the log database and before creating the business database.

**Name:** the handoff asked for `GXTemplate-passes/pass53-icu.md` ("the next number after the last
report; use the next free one if 53 is taken"). Passes 54–56 write their reports to `docs/passes/`. Pass 56 records
49, 50, 51 and 53 as planned passes not yet run, and the last report is 56. So this is
**Pass 57**, in `docs/passes/` beside 54–56. If you want it under `GXTemplate-passes/` or numbered 53, it
is a rename.

**Outcome:**

- `Microsoft.ICU.ICU4C.Runtime` **72.1.0.3**, the latest stable on nuget.org, so no version change.
- Build: 0 errors. The warning set is **identical to HEAD's**, 91 unique warnings, diffed line by line against
  a `--no-incremental` build of `5a3c1144` in a worktree.
- Every run gives the startup line `ICU app-local 72.1.0.3; default time zone Africa/Lagos resolves: yes`: the template
  in Development, its published Release exe, and the published exe of an app generated from 1.4.0.
- The publish puts `icuuc72.dll`, `icuin72.dll` and `icudt72.dll` beside the exe. The running process
  **loads them from there**, as its module list shows.
- With ICU disabled (NLS forced, as on Server 2019), the FMS fault reproduces exactly, and the new refusal text
  names the three DLLs (§3).
- All four suites pass, each run alone, both in the template and in the generated project.
- 1.4.0 packed: `GX.Blazor.Template.1.4.0.nupkg`, 879 entries. The pack's own assertion had a bug that
  rejected the clean package. It is fixed (§2, item 6).
- Nothing committed.

---

## 1. Steps

| # | Handoff step | Result |
|---|---|---|
| 1 | Version | nuget.org lists 67.1.0.4 … **72.1.0.3**, the last and the latest stable. Unchanged in `Server.UI.csproj` (both numbers) and in the refusal's DLL names. |
| 2 | Build `CleanArchitecture.Blazor.slnx` | 0 errors; warning set identical to HEAD (91 unique, `diff` empty). No compile fixes needed, and no test asserts the exact process-wide-state line. |
| 3 | Run locally | `dotnet run`, Development, scratch databases on local PG 15 (5432): startup line below. |
| 4 | Publish `src/Server.UI -c Release` | ICU DLLs present, runtimeconfig carries the option, published exe started from a console in Production against a scratch database; `GET /` → 302 to login, `GET /account/login` → 200. |
| 5 | Four test projects, each alone | All green (§4). |
| 6 | 1.3.0 → 1.4.0 | nuspec `<version>` and release-notes entry (the handoff's wording), `AppConfigurationSettings.Version`, README deployment section. Packed. |
| 7 | Generate from the 1.4.0 package | Installed into a throwaway hive (`--debug:custom-hive`, so the machine's store still has 1.3.0), `dotnet new gxblazor -n IcuApp`, built, published, ran: same DLLs, same startup line, its four suites green. |
| 8 | Report | This file. |

The startup line, identical in all three runs:

```
Process-wide state: database provider postgresql; Npgsql.EnableLegacyTimestampBehavior not set; timestamptz in force: yes; QuestPDF licence Community; ICU app-local 72.1.0.3; default time zone Africa/Lagos resolves: yes.
```

The publish (template; the generated app's `C:\gxp57\genpub` has the same three files):

```
icudt72.dll   31,093,280
icuin72.dll    2,893,864
icuuc72.dll    2,212,280

runtimeconfig.json configProperties:
  "System.Globalization.AppLocalIcu": "72.1.0.3",
```

Modules loaded by the running published exe:

```
icuuc72.dll  C:\gxp57\pub\icuuc72.dll
icudt72.dll  C:\gxp57\pub\icudt72.dll
icuin72.dll  C:\gxp57\pub\icuin72.dll
```

---

## 2. File-by-file changes

| # | File | Change |
|---|---|---|
| 1 | `src/Server.UI/Server.UI.csproj` | (handoff, already done) `Microsoft.ICU.ICU4C.Runtime` 72.1.0.3 + `RuntimeHostConfigurationOption System.Globalization.AppLocalIcu` 72.1.0.3. |
| 2 | `src/Server.UI/Program.cs` | (handoff, already done) the process-wide-state line adds `ICU {Icu}` and `default time zone {DefaultTimeZone} resolves: {ZoneResolves}`. |
| 3 | `src/Infrastructure/Configurations/AppConfigurationSettings.cs` | (handoff, already done) the `DefaultTimeZone` refusal names the missing-ICU cause and the three DLLs. **This pass:** `Version` default `1.3.0` → `1.4.0`. |
| 4 | `tests/Application.UnitTests/Configurations/AppLocalIcuTests.cs` | (handoff, already done) new, untracked. |
| 5 | `GX.Blazor.Template.nuspec` | `<version>1.4.0</version>`; the 1.4.0 release-notes entry at the top. |
| 6 | `build/pack.csproj` | **Not in the handoff; fix for a pack-assertion bug (§3.1).** `AssertTemplatePackageContents` reads the version from the nuspec (`XmlPeek`) and extracts only `GX.Blazor.Template.<version>.nupkg`, not every `GX.Blazor.Template.*.nupkg` in the output folder. |
| 7 | `README.md` | Deployment section (after the IIS settings): why the app carries ICU, the two numbers that must match, never `UseNls`, the startup line to look for, and what the refusal means. |

`docs/HANDOFF-icu-timezones.md` is still untracked, as I found it. Delete it or commit it as you see fit.

---

## 3. Findings — read these

### 3.1 The pack assertion checked every package in the folder, not the one just packed

The first 1.4.0 pack failed with `The package CONTAINS content/.claude/`. The 1.4.0 package itself
has no `.claude` entry (checked with `System.IO.Compression`). The extraction folder did, with files dated 26 Sep
and 10 Oct. The cause is `_ProducedPackage Include="$(_PackageOutDir)GX.Blazor.Template.*.nupkg"`, which
matched 1.0.0, 1.1.0, 1.2.0 and 1.4.0 in the repository root. `Unzip` then merged all four into one folder.
1.0.0–1.2.0 each carry `content/.claude/`, so the merged folder failed.

The same merge could also have let a broken package pass. A 1.4.0 missing `template.json` or
`ide.host.json` would have found an older package's copy in the merged folder. Pass 56 packed 1.3.0 with 1.0.0–1.2.0
already present. So its first pack's `.claude` failure (Pass 56 §3.2) may have been this merge, at least in part. Its
final 1.3.0 is clean either way: it has no `.claude` entry.

Fixed by naming the exact file (id + nuspec version). Verified three ways:

- The real pack of 1.4.0 passes with 1.0.0–1.3.0 still in the root.
- 1.2.0 copied alone into a scratch folder as `GX.Blazor.Template.1.4.0.nupkg` → **fails** on `content/.claude/`.
- The real 1.4.0 beside 1.2.0 in another scratch folder passes the `.claude` check. Its only error is the
  commit check, which is expected when the target runs alone without `StampRepositoryCommit`.

### 3.2 NLS mode reproduces the FMS fault, and the new message reads as intended

Published exe with `DOTNET_SYSTEM_GLOBALIZATION_USENLS=true`, which turns ICU off the way a server without
ICU does, against fresh `gx_p57_icu_nls*` databases:

```
Process-wide state: …; ICU app-local 72.1.0.3; default time zone Africa/Lagos resolves: NO.
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException: DataAnnotation validation failed
for 'AppConfigurationSettings' members: 'DefaultTimeZone' with the error: 'AppConfigurationSettings.DefaultTimeZone
'Africa/Lagos' is not a time zone this system recognises; use an IANA id such as 'UTC', 'Africa/Lagos' or
'Europe/London'. If the id is right, this process has no ICU to resolve it: Windows Server 2019 and older ship
none, and the app brings its own (Server.UI.csproj, System.Globalization.AppLocalIcu) - check that icuuc72.dll,
icuin72.dll and icudt72.dll were published beside the app'.
```

`gx_p57_icu_nls_logs` was created and `gx_p57_icu_nls` was not, which is the FMS staging picture.

### 3.3 The `ICU` field reports the configured option, not what is in force

In the NLS run above, the field still says `ICU app-local 72.1.0.3`, because it reads
`AppContext.GetData("System.Globalization.AppLocalIcu")`. That is the runtimeconfig value, which `UseNls` overrides.
`resolves: NO` beside it still tells the operator the truth. And if the DLLs are missing, the runtime itself fails at startup
("Failed to load app-local ICU") before this line. So the gap is narrow: only `UseNls` set from outside, by
environment or runtimeconfig. **Not changed** here, because the handoff fixed the line's shape. If you want the field to
report the mode actually in force, the documented check is the sort version:
`CultureInfo.InvariantCulture.CompareInfo.Version`, where ICU gives `SortId`'s first four bytes ==
`FullVersion` ≠ 0 and NLS does not. Print "NLS" in that case.

### 3.4 `AppConfigurationSettings.Version` is not what an app displays

Bumped to 1.4.0 as asked. But `appsettings.json` sets `"Version": "1.0.0"`, which overrides the default, and
history shows the property's `1.3.0` came from upstream, not from template releases. A generated app still
shows 1.0.0, its own version, which is presumably right. Left `appsettings.json` alone.

### 3.5 The package records HEAD, not this working tree

The 1.4.0 nupkg's `<repository commit>` is `5a3c11440d70ee6d0da8adb4fd18b77799d4bd9d` (Pass 56), while its
content is this uncommitted working tree. The README warns about this. **After you commit, repack 1.4.0 from the clean tree**, as
Pass 56 §3.4 did, before installing or distributing it. It was **not** installed into the machine's template
store: that store still holds 1.3.0. The generation check used a throwaway hive.

### 3.6 The smoke script was not run

`tooling\smoke-generate.ps1` needs `powershell -ExecutionPolicy Bypass`, which this session's permission mode
refused. Step 7 was done by hand instead: install into a custom hive, generate, build, publish, run, and the
four suites each alone. That covers what the handoff asks. It does not cover the smoke's 107 content checks. Run
`powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1` yourself (with `GX_TEST_PG` set)
before release.

---

## 4. Tests

`GX_TEST_PG=Host=127.0.0.1;Port=5432;Username=postgres` (local PostgreSQL 15.7) for the run only. Each
project was run alone, with `dotnet build-server shutdown` between them. Pass 56 used PG 18 on 5434, which now refuses
a password-less login, so 5432 was used.

| Suite | Template (Pass 56 → Pass 57) | Generated from 1.4.0 (`IcuApp`) |
|---|---|---|
| Application.UnitTests | 567 + 14 skipped → **568 + 14 skipped** (+`AppLocalIcuTests`) | **568 + 14 skipped** |
| Infrastructure.UnitTests | 227 → **227** | **206** (the 21 provider-conditional tests, as in Pass 56) |
| Application.IntegrationTests | 60 → **60** | **60** |
| Server.UI.IntegrationTests | 283 → **283** | **283** |
| **Total** | **1,138 passed, 14 skipped** | **1,117 passed, 14 skipped** |

`AppLocalIcuTests` fails when it should. With the option set to `72.1.0.2` and the package left at `72.1.0.3`: `Expected … "72.1.0.3" because the
runtime loads exactly the ICU version the option names … but "72.1.0.2" differs`. The csproj was restored from a copy
afterwards; the diff above is the restored file.

---

## 5. Left behind on this machine

- Scratch databases on local PG 15 (5432). None dropped, per house rule: `gx_p57_icu`, `gx_p57_icu_logs`,
  `gx_p57_icu_pub`, `gx_p57_icu_pub_logs`, `gx_p57_icu_nls_logs`, `gx_p57_icuapp`, `gx_p57_icuapp_logs`,
  plus the suites' own `gx_test_*` databases.
- `C:\gxp57\`: the template's publish (`pub`), the custom hive (`hive`), the generated `gen\IcuApp`, its
  publish (`genpub`), the negative-check folders (`neg`, `mix`), and the run logs. Safe to delete.
- `GX.Blazor.Template.1.4.0.nupkg` in the repository root (gitignored, like 1.0.0–1.3.0).

---

## 6. Commit

None, as asked. The intended commit is the six modified files, plus `AppLocalIcuTests.cs` and this report.
After it, repack 1.4.0 (§3.5).
