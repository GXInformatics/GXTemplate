# Pass 56 — Hygiene and 1.3.0

**Scope:** deliberately minimal. This is the last template pass before InventoryMS is regenerated
from the packed template. Five items. Anything else found along the way is recorded in §7, not fixed.

**Name:** the next free number in the pass plan (Passes 54 and 55 took 54 and 55). The planned
passes 49, 50, 51 and 53 are untouched.

**Outcome:**

- All five items are done.
- 0 errors, and the warning set is identical to Pass 55's.
- All four suites are green on local PostgreSQL 18 (port 5434).
- 1.3.0 is packed and installed in this machine's template store as the only GX.Blazor.Template.
- The smoke test passes **against the installed package** (107 checks).

---

## 1. Items

| # | Item | Delivered |
|---|---|---|
| 1 | No editor/agent state in the package | The nuspec excludes `.claude`, `.vscode`, `.idea` and `.cursor`, **each listed twice**: `**\X\**` excludes the files and `**\X` excludes the folder itself. It also excludes `.DS_Store`, `Thumbs.db` and `*.swp`. `build/pack.csproj` now fails the pack if `content/.claude/` is present. Verified by listing the nupkg (§4): no entry for any of them, with the folders empty and with them populated. |
| 2 | `<repository commit>` | The nuspec carries `<repository type="git" url="https://github.com/GXInformatics/GXTemplate.git" commit="$commit$" />`. A new target, `StampRepositoryCommit` (before `GenerateNuspec`), fills `commit` from `git rev-parse HEAD`. It refuses to pack if git does not return a 40-hex SHA. The pack assertion reads the commit back out of the packed nuspec. Verified by reading the nuspec out of the nupkg (§5). |
| 3 | Infrastructure tests absent from generation | The Pass 55 follow-up found **none lost**. All 21 are deliberately excluded by provider conditionals (`UseSqlServer`). Left as is, and the list is now named in the README under *Packaging the template → What the template's suites run that a generated project does not*: the 10 whole tests and 11 `mssql` theory rows, file by file. |
| 4 | Version 1.3.0, reinstall | The nuspec is at 1.3.0 with a 1.3.0 release-notes entry. Packed, `dotnet new uninstall GX.Blazor.Template` (it removed 1.2.0), then installed 1.3.0. `dotnet new uninstall` lists **one** GX.Blazor.Template, at 1.3.0 (§6). See §3.1 on "and nothing else". |
| 5 | Smoke on the installed 1.3.0 | `tooling/smoke-generate.ps1 -UseInstalled` does not pack. It checks that `dotnet new` reports exactly one GX.Blazor.Template at the nuspec's version, reads its content checks from `%USERPROFILE%\.templateengine\packages\GX.Blazor.Template.1.3.0.nupkg`, and generates from the machine's own store. Result: **SMOKE PASSED** (§8). |

---

## 2. File-by-file changes

| File | Change |
|---|---|
| `GX.Blazor.Template.nuspec` | `<version>1.3.0</version>`; a 1.3.0 `<releaseNotes>` entry; `<repository … commit="$commit$" />`; excludes for `.claude`, `.vscode`, `.idea` and `.cursor` (folder and contents), plus `.DS_Store`, `Thumbs.db` and `*.swp`. Comments explain why each folder is listed twice. |
| `build/pack.csproj` | New `StampRepositoryCommit` target. `AssertTemplatePackageContents` now fails if `content/.claude/` is packed, and fails unless the packed nuspec contains `commit="<HEAD>"`. Its success message names the commit. |
| `tooling/smoke-generate.ps1` | New `-UseInstalled` and `-ExpectedVersion` options (the version defaults to the nuspec's). Hive selection goes through `$hiveArgs`, so both generations use the chosen store. In `-UseInstalled` mode the content root is the installed package. |
| `README.md` | Packaging section: the commit stamp, editor/agent-state exclusion, `-UseInstalled`, and the list of 21 provider-conditional Infrastructure tests (item 3). |

No source or test file changed, so the suites' counts are unchanged.

---

## 3. Decisions and findings — read these

### 3.1 "…and nothing else": the other ten template packages were kept, at your instruction

This machine's store holds ten packages unrelated to GX: MudBlazor.Templates,
BlazorHero.CleanArchitecture, Blazorise.Templates, OrchardCore.ProjectTemplates,
JasonTaylorDev.RapidBlazor, the two FullStackHero boilerplates, Ardalis.CleanArchitecture.Template,
Clean.Architecture.Solution.Template and CleanArchitecture.Blazor.Solution.Template.

Meeting "lists GX.Blazor.Template 1.3.0 and nothing else" literally would mean uninstalling them, so
I asked, and you chose to keep them. What is verified instead: the listing contains **exactly one**
`GX.Blazor.Template` entry, at **Version 1.3.0**, and no 1.2.0 remains. The smoke test asserts the
same thing (`exactly one GX.Blazor.Template is installed`, `its version is 1.3.0`). The full output
is in §6.

### 3.2 An empty `.claude` folder is still packed unless the folder itself is excluded

The first real pack of this pass **failed on the new assertion** ("The package CONTAINS
content/.claude/"), even with `**\.claude\**` in place. `**\X\**` excludes the files **in** a folder,
but NuGet writes a `_._` placeholder for an **empty** directory matched by `.\**`.

`.claude` is empty between Claude Code sessions, which is exactly how 1.2.0 came to ship
`content/.claude/_._`, giving every generated project an empty `.claude` folder. The earlier probe
pack had passed only because the probe file made the folder non-empty.

The fix lists each folder a second time without the trailing `\**`. Verified both ways: empty folders
give no entries, and populated folders (a probe file in `.claude`, empty `.vscode` and `.idea`) give
no entries. A mutation that removes the `.claude` excludes makes the pack fail with the assertion's
message.

### 3.3 The smoke test caught a README line

The first `-UseInstalled` smoke failed one check: "unprocessed marker in README.md:1348". The item-3
README text I had written contained the literal `#if (UseSqlServer)`. Markdown is not processed for
C# conditionals, so the literal reached the generated README and looked like a conditional that
survived generation. I reworded it ("the `UseSqlServer` conditional blocks"), then repacked,
reinstalled and re-ran the smoke, which passed. The check did its job.

### 3.4 Which commit the package records, and the order things were done in

The nuspec records **HEAD**, while the content comes from the **working tree**. A pack from a dirty
tree therefore names the parent commit. The evidence in §4 to §6 and §8 comes from a **verification
pack** made before this pass was committed, so its nuspec says
`commit="7745291d1afdb187d5cce6f27cac235ebbbc720b"` (Pass 55).

`docs/` is excluded from the package, so this report does not change the package content. After
committing, the release package was repacked from the **clean pass commit**, checked against the
verification pack (identical content, nuspec differing only in `commit`), reinstalled, and smoked
again. A commit cannot contain its own SHA, so the final package's commit is this report's own commit
(`git log -1 --format=%H -- docs/passes/pass56.md`). The chat reply for this pass quotes it.

---

## 4. The packed nupkg

**Verification package** `GX.Blazor.Template.1.3.0.nupkg`: 878 entries, 5,478,855 bytes
uncompressed. Pack output:

```
Template package contents verified: template.json, ide.host.json, icon.png and local-settings/appsettings.Development.json are present under content/.template.config/, no local src/Server.UI/appsettings.Development.json and no .claude/ were packed, and the nuspec records commit 7745291d1afdb187d5cce6f27cac235ebbbc720b.
```

**Against 1.2.0 (855 entries):** 878 = 855 − 3 + 26.

- **Removed (3):** `content/.claude/_._`,
  `content/src/Application/Features/Tenants/Commands/AddEdit/AddEditTenantCommand.cs` and
  `…/AddEditTenantCommandValidator.cs`.
- **Added (26):** the sources, migrations and tests of Passes 54 and 55. No other differences.
- **Root entries** (all expected): `GX.Blazor.Template.nuspec`, `README.md`, `icon.png`,
  `[Content_Types].xml`, `_rels/.rels` and `package/services/metadata/core-properties/nuget.psmdcp`.
- **No `.claude`, `.vscode`, `.idea`, `.cursor`, `.DS_Store`, `Thumbs.db` or `*.swp` entry.**

The complete sorted listing is in the appendix.

---

## 5. The nuspec as packed

Read out of the verification nupkg with `unzip -p GX.Blazor.Template.1.3.0.nupkg
GX.Blazor.Template.nuspec`. NuGet rewrites the namespace and inlines the license URL. The released
package differs only in the `commit` value (§3.4).

```xml
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
  <metadata>
    <id>GX.Blazor.Template</id>
    <version>1.3.0</version>
    <title>GX Blazor Server Solution Template</title>
    <authors>GX Informatics Limited</authors>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <license type="expression">MIT</license>
    <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
    <icon>icon.png</icon>
    <readme>README.md</readme>
    <description>A Blazor Server solution template for .NET 10, laid out in Clean Architecture layers.
            Ships ASP.NET Core Identity with permission claims over a multi-tenant DATA MODEL,
            deny-by-default authorization on every request, transactional audit trails, structured
            logging into a separate log database, and Documents and PicklistSets as worked examples.
            Tenant isolation is enforced for Documents, the Users area, audit trails, online presence
            and picklists - the first three with a named cross-tenant right, presence and picklists
            deliberately with none; system logs remain installation-wide. Roles stay installation-wide
            by design, but DEFINING one needs a named, revocable right. Picklists are shared reference
            data plus per-tenant additions, and creating a shared value needs a named right too. See
            the Tenancy section of the README before relying on it.</description>
    <summary>Blazor Server solution template for .NET 10 in Clean Architecture layers, with a
            multi-tenant data model, deny-by-default authorization and transactional auditing.</summary>
    <releaseNotes>1.3.0
            - Tenancy by marker: every IMustHaveTenant / IMayHaveTenant entity is tenant-filtered and
              stamped from its marker, and startup fails if a marked entity ends up unfiltered.
              Documents are now filtered too: a principal with no tenant sees only tenantless ones.
              ITenantSeeder seeds each tenant on creation and reconciles every tenant on start;
              ISystemContext runs background work as the gx-system account inside one tenant.
              TenantUsers gets a unique (TenantId, UserId) index; tenant Create and Update are
              separate commands with separate rights.
            - BaseEntity&lt;TKey&gt; and BaseAuditableEntity&lt;TKey&gt;: any key type, with domain events and
              audit stamping kept. IApplicationDbContext.Database lets a handler own a transaction.
              The startup authorization check covers commands and queries and refuses struct
              requests. Table naming handles TPT, TPC, many-to-many joins and [Table].
            - The package carries no editor or agent state (.claude, .vscode, .idea, .cursor), and
              records the git commit it was packed from in its repository element.

            1.2.0
            - The theme drawer is gone. One light/dark toggle sits in the app bar; with no saved choice
              the device's preference decides, and no page paints in the wrong theme first. The drawer's
              colour picker, right-to-left toggle and online-users roster went with it.
            - GxSubmitButton: every button that submits or does work shows its progress (spinner, busy
              label, aria-busy) and cannot run twice; never the disabled attribute. gx-busy-button.js
              covers real form posts. MudLoadingButton and the hand-made busy flags are removed, and
              SubmitButtonGuardTests keeps a plain submit MudButton out.
            - Dialog actions run inside their dialog: user and role deletes, clearing the logs and
              resetting a password show progress there, and a refusal stays in the dialog instead of a
              snackbar after it closed.

            1.1.0
            - Nothing calls out by default. Removed: the unused MaxMind GeoIP web-service client, the
              Google Fonts link, the qrcodejs CDN script loaded on every page, and the unreferenced
              components and scripts that loaded Swiper, Fancybox, fabric.js and a second copy of
              OpenSeadragon from CDNs. The document image viewer still loads OpenSeadragon from unpkg.
              The upstream Seq sink and seeded Gravatar pictures were already removed.
            - OutboundAddressTests: a hard-coded http(s) address in src/ fails the tests unless it is
              on the test's allowed list with its reason.
            - Every pass since 1.0.0 (test infrastructure on PostgreSQL, configuration layout, mail
              over the Mailgun API); see the repository's pass reports.

            1.0.0
            - First GX release, derived from CleanArchitectureWithBlazorServer.
            - Deny-by-default request authorization with a startup assertion.
            - Audit rows written in the same transaction as the change they describe.
            - Structural per-principal cache scoping.
            - Demo features (Products, Contacts, chatbot, document OCR) removed.</releaseNotes>
    <copyright>Copyright (c) 2019 Jason Taylor and contributors; portions copyright (c) GX Informatics Limited</copyright>
    <tags>blazor blazor-server clean-architecture multi-tenant dotnet10 template csharp</tags>
    <packageTypes>
      <packageType name="Template" />
    </packageTypes>
    <repository type="git" url="https://github.com/GXInformatics/GXTemplate.git" commit="7745291d1afdb187d5cce6f27cac235ebbbc720b" />
  </metadata>
</package>
```

---

## 6. `dotnet new uninstall` output

After `dotnet new uninstall GX.Blazor.Template`, which printed `Success: GX.Blazor.Template@1.2.0 was
uninstalled.`, and `dotnet new install` of the 1.3.0 nupkg. Complete and unedited. The GX entry is
last; the ten above it are the unrelated packages kept at your instruction (§3.1).

```
Currently installed items:
   MudBlazor.Templates
      Version: 0.6.3
      Details:
         Author: MudBlazor Team
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         MudBlazor Templates (mudblazor) C#
      Uninstall Command:
         dotnet new uninstall MudBlazor.Templates

   BlazorHero.CleanArchitecture
      Version: 2.2.0
      Details:
         Author: Mukesh Murugan
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         Blazor Hero - Clean Architecture Template (BlazorHero.CleanArchitecture) C#
      Uninstall Command:
         dotnet new uninstall BlazorHero.CleanArchitecture

   Blazorise.Templates
      Version: 1.2.0
      Details:
         Author: Megabit
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         Blazorise App (blazorise) C#
      Uninstall Command:
         dotnet new uninstall Blazorise.Templates

   OrchardCore.ProjectTemplates
      Version: 1.6.0
      Details:
         Author: Orchard Core Community and Contributors
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         Orchard Core Cms Module (ocmodulecms) C#
         Orchard Core Cms Web App (occms) C#
         Orchard Core Mvc Module (ocmodulemvc) C#
         Orchard Core Mvc Web App (ocmvc) C#
         Orchard Core Theme (octheme) C#
      Uninstall Command:
         dotnet new uninstall OrchardCore.ProjectTemplates

   JasonTaylorDev.RapidBlazor
      Version: 7.0.3
      Details:
         Author: JasonTaylorDev
         Owners: JasonTaylorDev
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         Rapid Blazor App (rapid-blazor-sln) C#
      Uninstall Command:
         dotnet new uninstall JasonTaylorDev.RapidBlazor

   FullStackHero.WebAPI.Boilerplate
      Version: 1.0.0
      Details:
         Author: Mukesh Murugan
         Owners: iammukeshm
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         FullStackHero WebAPI Boilerplate (fsh-api) C#
      Uninstall Command:
         dotnet new uninstall FullStackHero.WebAPI.Boilerplate

   FullStackHero.BlazorWebAssembly.Boilerplate
      Version: 0.0.1-rc
      Details:
         Author: Mukesh Murugan
         Owners: iammukeshm
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         Blazor WebAssembly Boilerplate - FullStackHero (fsh-blazor) C#
      Uninstall Command:
         dotnet new uninstall FullStackHero.BlazorWebAssembly.Boilerplate

   Ardalis.CleanArchitecture.Template
      Version: 9.0.1
      Details:
         Author: Steve Smith
         Owners: ardalis
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         ASP.NET Clean Architecture Solution (clean-arch) C#
      Uninstall Command:
         dotnet new uninstall Ardalis.CleanArchitecture.Template

   Clean.Architecture.Solution.Template
      Version: 10.0.0
      Details:
         Author: JasonTaylorDev
         Owners: JasonTaylorDev
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         Clean Architecture Solution (ca-sln) C#
         Clean Architecture Solution Use Case (ca-usecase) C#
      Uninstall Command:
         dotnet new uninstall Clean.Architecture.Solution.Template

   CleanArchitecture.Blazor.Solution.Template
      Version: 1.30.3
      Details:
         Author: hl.z
         Owners: neozhu
         Reserved: ✘
         NuGetSource: https://api.nuget.org/v3/index.json
      Templates:
         Clean Architecture for Blazor Server Solution (ca-blazorserver-sln) C#
      Uninstall Command:
         dotnet new uninstall CleanArchitecture.Blazor.Solution.Template

   GX.Blazor.Template
      Version: 1.3.0
      Details:
         Author: GX Informatics Limited
         Reserved: ✘
      Templates:
         GX Blazor Server Solution (Clean Architecture) (gxblazor) C#
      Uninstall Command:
         dotnet new uninstall GX.Blazor.Template

```

---

## 7. Recorded for the plan, not fixed

Found during this pass, outside its five items, and left alone on purpose:

1. **The README's install and packaging commands name `GX.Blazor.Template.1.0.0.nupkg`.** That
   appears in *Getting started* and in *Packaging the template*, and there were 1.1.0, 1.2.0 and now
   1.3.0 since. The same section says the package is "around 745 entries and 1.1 MB" (it is 878
   entries and 1.7 MB), and that the smoke test runs "three non-database test suites" (it runs all
   four, against PostgreSQL). Suggested for pass 53 (tooling, README).
2. **The nuspec `<description>` is stale.** It lists the tenant-isolated surfaces as they were before
   Pass 54, and does not mention marker-based filtering, the system context or generic keys. The
   release notes are current. Pass 53.
3. **A dirty-tree pack records a commit that does not contain the content** (§3.4). A guard, either
   refusing a release pack from a dirty tree or recording `-dirty`, would make the traceability
   unconditional. Pass 53 or pass 51 (deployment).
4. **The generated README still carries the maintainer-only *Packaging the template* section.**
   This was already planned for pass 53 (its `<!--#if (false)-->` guard).
5. **Stream requests** (`IStreamRequest`/`IStreamCommand`/`IStreamQuery`) **pass through no
   authorization behaviour.** Carried from Pass 55, §3.3.
6. **InventoryMS has an empty `.claude` folder** from 1.2.0. It disappears when InventoryMS is
   regenerated from 1.3.0, which is the plan.

---

## 8. Smoke on the installed 1.3.0, and test counts

`powershell -ExecutionPolicy Bypass -File tooling\smoke-generate.ps1 -UseInstalled`, with
`GX_TEST_PG` pointed at localhost:5434: **SMOKE PASSED (postgresql), 107 checks `ok`, 0 `FAIL`.**
That is the 106 checks of a source-tree smoke plus the installed-version check.

```
== the installed template is GX.Blazor.Template 1.3.0
  ok    exactly one GX.Blazor.Template is installed
  ok    its version is 1.3.0
  ok    content checks read %USERPROFILE%\.templateengine\packages\GX.Blazor.Template.1.3.0.nupkg
```

| Suite | Template repository (Pass 55 → Pass 56) | Project generated from the installed 1.3.0 |
|---|---|---|
| Application.UnitTests | 567 passed, 14 skipped → **567 passed, 14 skipped** | **567 passed, 14 skipped** |
| Infrastructure.UnitTests | 227 → **227** | **206**, the 21 provider-conditional tests listed in the README (item 3) |
| Application.IntegrationTests | 60 → **60** | **60** |
| Server.UI.IntegrationTests | 283 → **283** | **283** |
| **Total** | **1,137 passed, 14 skipped**, unchanged | **1,116 passed, 14 skipped** |

- **Template build** (`--no-incremental`): 0 errors. The warning set is identical to Pass 55's:
  50 unique, nothing added or removed.
- **Template suites:** run on PostgreSQL 18.0 with `GX_TEST_PG` set for the run only. No source or
  test file changed in this pass, so the counts are Pass 55's.
- **Skips:** the same 14 throughout: 12 Azurite tests and 2 `GX_TEST_CREATE_DATABASES` tests.

---

## 9. Commit

One commit on `main`, `Pass56-HygieneAndRelease1.3.0`, pushed. The release package was then repacked
from that commit and reinstalled (§3.4).

---

## Appendix: the complete nupkg listing (878 entries, sorted)

```
GX.Blazor.Template.nuspec
README.md
[Content_Types].xml
_rels/.rels
content/.editorconfig
content/.gitattributes
content/.gitignore
content/.template.config/icon.png
content/.template.config/ide.host.json
content/.template.config/local-settings/appsettings.Development.json
content/.template.config/template.json
content/CleanArchitecture.Blazor.slnx
content/LICENSE
content/README.md
content/src/Application/Application.csproj
content/src/Application/Common/Constants/AppStrings.cs
content/src/Application/Common/Constants/ApplicationClaimTypes.cs
content/src/Application/Common/Constants/DbProviderKeys.cs
content/src/Application/Common/Constants/GlobalVariables.cs
content/src/Application/Common/Constants/LocalizationConstants.cs
content/src/Application/Common/Constants/MailTemplates.cs
content/src/Application/Common/Constants/QueryFilters.cs
content/src/Application/Common/Constants/Roles.cs
content/src/Application/Common/Constants/StorageProviderKeys.cs
content/src/Application/Common/Constants/UserCacheKeys.cs
content/src/Application/Common/Constants/Users.cs
content/src/Application/Common/ExceptionHandlers/DbExceptionHandler.cs
content/src/Application/Common/ExceptionHandlers/FallbackExceptionHandler.cs
content/src/Application/Common/ExceptionHandlers/ForbiddenAccessException.cs
content/src/Application/Common/ExceptionHandlers/LogDatabaseNotConfiguredException.cs
content/src/Application/Common/ExceptionHandlers/NotFoundException.cs
content/src/Application/Common/ExceptionHandlers/NotFoundExceptionHandler.cs
content/src/Application/Common/ExceptionHandlers/RequestExceptionHandlerState.cs
content/src/Application/Common/ExceptionHandlers/ResultFailureFactory.cs
content/src/Application/Common/ExceptionHandlers/ValidationExceptionHandler.cs
content/src/Application/Common/Extensions/DataRowExtensions.cs
content/src/Application/Common/Extensions/DateTimeExtensions.cs
content/src/Application/Common/Extensions/DescriptionAttributeExtensions.cs
content/src/Application/Common/Extensions/EnumExtensions.cs
content/src/Application/Common/Extensions/IdentityResultExtensions.cs
content/src/Application/Common/Extensions/QueryableExtensions.cs
content/src/Application/Common/Extensions/SpecificationBuilderExtensions.cs
content/src/Application/Common/Extensions/ValidationExtensions.cs
content/src/Application/Common/Interfaces/Caching/CacheScope.cs
content/src/Application/Common/Interfaces/Caching/CacheScopeKey.cs
content/src/Application/Common/Interfaces/Caching/IAppCache.cs
content/src/Application/Common/Interfaces/Caching/ICacheInvalidatorRequest.cs
content/src/Application/Common/Interfaces/Caching/ICacheableRequest.cs
content/src/Application/Common/Interfaces/IApplicationDbContext.cs
content/src/Application/Common/Interfaces/IApplicationDbContextFactory.cs
content/src/Application/Common/Interfaces/IApplicationSettings.cs
content/src/Application/Common/Interfaces/IDataSourceService.cs
content/src/Application/Common/Interfaces/IDateTime.cs
content/src/Application/Common/Interfaces/IExcelService.cs
content/src/Application/Common/Interfaces/IIdleTimeoutPolicyProvider.cs
content/src/Application/Common/Interfaces/IIdleTimeoutSettings.cs
content/src/Application/Common/Interfaces/ILogDbContext.cs
content/src/Application/Common/Interfaces/ILogDbContextFactory.cs
content/src/Application/Common/Interfaces/IMailService.cs
content/src/Application/Common/Interfaces/IObjectMapper.cs
content/src/Application/Common/Interfaces/IPDFService.cs
content/src/Application/Common/Interfaces/IPermissionQueryService.cs
content/src/Application/Common/Interfaces/IPermissionService.cs
content/src/Application/Common/Interfaces/IResult.cs
content/src/Application/Common/Interfaces/ITenantSwitchService.cs
content/src/Application/Common/Interfaces/IValidationService.cs
content/src/Application/Common/Interfaces/Identity/IClientInfoAccessor.cs
content/src/Application/Common/Interfaces/Identity/IIdentityService.cs
content/src/Application/Common/Interfaces/Identity/IIdentitySettings.cs
content/src/Application/Common/Interfaces/Identity/ISystemContext.cs
content/src/Application/Common/Interfaces/Identity/IUserContextAccessor.cs
content/src/Application/Common/Interfaces/Identity/IUserProfileState.cs
content/src/Application/Common/Interfaces/Identity/UserContext.cs
content/src/Application/Common/Interfaces/MultiTenant/ITenantSeeder.cs
content/src/Application/Common/Interfaces/Storage/IFileStorage.cs
content/src/Application/Common/Interfaces/Storage/StoredFile.cs
content/src/Application/Common/Interfaces/Storage/StoredFileContent.cs
content/src/Application/Common/Mappings/MapsterConfiguration.cs
content/src/Application/Common/Mappings/MapsterObjectMapper.cs
content/src/Application/Common/Models/ClientInfo.cs
content/src/Application/Common/Models/FileUploadRequest.cs
content/src/Application/Common/Models/MailRecipient.cs
content/src/Application/Common/Models/PaginatedData.cs
content/src/Application/Common/Models/PaginationFilter.cs
content/src/Application/Common/Models/PaginationRequest.cs
content/src/Application/Common/Models/Result.cs
content/src/Application/Common/PublishStrategies/ChannelBasedNoWaitPublisher.cs
content/src/Application/Common/Security/AdministratorPermissionRegistry.cs
content/src/Application/Common/Security/ModuleInfo.cs
content/src/Application/Common/Security/Permissions.cs
content/src/Application/Common/Security/Permissions/Dashboards.cs
content/src/Application/Common/Security/Permissions/Roles.cs
content/src/Application/Common/Security/Permissions/Users.cs
content/src/Application/Common/Security/PermissionsModel.cs
content/src/Application/Common/Security/RequestAuthorizationRegistry.cs
content/src/Application/Common/Security/RequestAuthorizeAttribute.cs
content/src/Application/Common/Security/UserProfile.cs
content/src/Application/DependencyInjection.cs
content/src/Application/Features/AuditTrails/AuditTrailTenantScope.cs
content/src/Application/Features/AuditTrails/Caching/AuditTrailsCacheKey.cs
content/src/Application/Features/AuditTrails/DTOs/AuditTrailDto.cs
content/src/Application/Features/AuditTrails/Queries/Export/ExportAuditTrailsQuery.cs
content/src/Application/Features/AuditTrails/Queries/PaginationQuery/AuditTrailsWithPaginationQuery.cs
content/src/Application/Features/AuditTrails/Security/AuditTrailsPermissions.cs
content/src/Application/Features/AuditTrails/Specifications/AuditTrailAdvancedFilter.cs
content/src/Application/Features/AuditTrails/Specifications/AuditTrailAdvancedSpecification.cs
content/src/Application/Features/Documents/Caching/DocumentCacheKey.cs
content/src/Application/Features/Documents/Commands/AddEdit/AddEditDocumentCommand.cs
content/src/Application/Features/Documents/Commands/AddEdit/AddEditDocumentCommandValidator.cs
content/src/Application/Features/Documents/Commands/Delete/DeleteDocumentCommand.cs
content/src/Application/Features/Documents/Commands/Delete/DeleteDocumentCommandValidator.cs
content/src/Application/Features/Documents/Commands/Upload/UploadDocumentCommand.cs
content/src/Application/Features/Documents/Commands/Upload/UploadDocumentCommandValidator.cs
content/src/Application/Features/Documents/DTOs/DocumentDto.cs
content/src/Application/Features/Documents/EventHandlers/DocumentCreatedEventHandler.cs
content/src/Application/Features/Documents/EventHandlers/DocumentDeletedEventHandler.cs
content/src/Application/Features/Documents/Queries/GetFileStream/GetFileStreamQuery.cs
content/src/Application/Features/Documents/Queries/PaginationQuery/DocumentsWithPaginationQuery.cs
content/src/Application/Features/Documents/Security/DocumentsPermissions.cs
content/src/Application/Features/Documents/Specifications/AdvancedDocumentsFilter.cs
content/src/Application/Features/Documents/Specifications/AdvancedDocumentsSpecification.cs
content/src/Application/Features/Documents/Specifications/VisibleDocumentSpecification.cs
content/src/Application/Features/Identity/DTOs/ApplicationRoleDto.cs
content/src/Application/Features/Identity/DTOs/ApplicationUserDto.cs
content/src/Application/Features/Identity/DTOs/UserBriefDto.cs
content/src/Application/Features/Identity/Notifications/ResetPassword/ResetPasswordCommand.cs
content/src/Application/Features/Identity/Notifications/SendMail/SendMailCommand.cs
content/src/Application/Features/Identity/Notifications/SendWelcome/SendWelcomeCommand.cs
content/src/Application/Features/Identity/Notifications/UserActivation/UserActivationCommand.cs
content/src/Application/Features/Identity/RoleDefinitionWrite.cs
content/src/Application/Features/Identity/UserTenantVisibility.cs
content/src/Application/Features/PicklistSets/Caching/PicklistSetCacheKey.cs
content/src/Application/Features/PicklistSets/Commands/AddEdit/AddEditPicklistSetCommand.cs
content/src/Application/Features/PicklistSets/Commands/AddEdit/AddEditPicklistSetCommandValidator.cs
content/src/Application/Features/PicklistSets/Commands/Delete/DeletePicklistSetCommand.cs
content/src/Application/Features/PicklistSets/Commands/Delete/DeletePicklistSetCommandValidator.cs
content/src/Application/Features/PicklistSets/Commands/Import/ImportPicklistSetsCommand.cs
content/src/Application/Features/PicklistSets/Commands/Import/ImportPicklistSetsCommandValidator.cs
content/src/Application/Features/PicklistSets/DTOs/PicklistSetDto.cs
content/src/Application/Features/PicklistSets/EventHandlers/PicklistSetChangedEventHandler.cs
content/src/Application/Features/PicklistSets/Queries/ByName/PicklistSetsQueryByName.cs
content/src/Application/Features/PicklistSets/Queries/Export/ExportPicklistSetsQuery.cs
content/src/Application/Features/PicklistSets/Queries/GetAll/GetAllPicklistSetsQuery.cs
content/src/Application/Features/PicklistSets/Queries/PaginationQuery/PicklistSetsWithPaginationQuery.cs
content/src/Application/Features/PicklistSets/Security/PicklistSetsPermissions.cs
content/src/Application/Features/PicklistSets/SharedPicklistWrite.cs
content/src/Application/Features/PicklistSets/Specifications/PicklistSetAdvancedFilter.cs
content/src/Application/Features/PicklistSets/Specifications/PicklistSetAdvancedSpecification.cs
content/src/Application/Features/SecuritySettings/Commands/UpdateSecurityPolicyCommand.cs
content/src/Application/Features/SecuritySettings/Commands/UpdateSecurityPolicyCommandValidator.cs
content/src/Application/Features/SecuritySettings/InstallationPolicyWrite.cs
content/src/Application/Features/SecuritySettings/Queries/GetSecurityPolicyQuery.cs
content/src/Application/Features/SecuritySettings/Security/SecuritySettingsPermissions.cs
content/src/Application/Features/SystemLogs/Caching/LogCacheKey.cs
content/src/Application/Features/SystemLogs/Commands/Clear/ClearSystemLogsCommand.cs
content/src/Application/Features/SystemLogs/DTOs/SystemLogDto.cs
content/src/Application/Features/SystemLogs/DTOs/SystemLogTimeLineDto.cs
content/src/Application/Features/SystemLogs/Queries/ChatData/SystemLogsChatDataQuery.cs
content/src/Application/Features/SystemLogs/Queries/PaginationQuery/SystemLogsWithPaginationQuery.cs
content/src/Application/Features/SystemLogs/Security/LogsPermissions.cs
content/src/Application/Features/SystemLogs/Specifications/SystemLogAdvancedFilter.cs
content/src/Application/Features/SystemLogs/Specifications/SystemLogAdvancedSpecification.cs
content/src/Application/Features/Tenants/Caching/TenantCacheKey.cs
content/src/Application/Features/Tenants/Commands/Create/CreateTenantCommand.cs
content/src/Application/Features/Tenants/Commands/Delete/DeleteTenantCommand.cs
content/src/Application/Features/Tenants/Commands/Delete/DeleteTenantCommandValidator.cs
content/src/Application/Features/Tenants/Commands/TenantForm.cs
content/src/Application/Features/Tenants/Commands/Update/UpdateTenantCommand.cs
content/src/Application/Features/Tenants/DTOs/TenantDto.cs
content/src/Application/Features/Tenants/PrimaryTenantRule.cs
content/src/Application/Features/Tenants/Queries/GetAll/GetAllTenantsQuery.cs
content/src/Application/Features/Tenants/Queries/Pagination/TenantsPaginationQuery.cs
content/src/Application/Features/Tenants/Security/TenantsPermissions.cs
content/src/Application/Pipeline/AuthorizationBehaviour.cs
content/src/Application/Pipeline/CacheInvalidationBehaviour.cs
content/src/Application/Pipeline/FusionCacheBehaviour.cs
content/src/Application/Pipeline/PerformanceBehaviour.cs
content/src/Application/Pipeline/ResultExceptionBehavior.cs
content/src/Application/Pipeline/ValidationBehavior.cs
content/src/Application/Resources/Constants/AppStrings.Designer.cs
content/src/Application/Resources/Constants/AppStrings.de-DE.resx
content/src/Application/Resources/Constants/AppStrings.en.resx
content/src/Application/Resources/Constants/AppStrings.resx
content/src/Application/Resources/Constants/AppStrings.zh-CN.resx
content/src/Application/Resources/Features/AuditTrails/Queries/Export/ExportAuditTrailsQueryHandler.de-DE.resx
content/src/Application/Resources/Features/AuditTrails/Queries/Export/ExportAuditTrailsQueryHandler.en.resx
content/src/Application/Resources/Features/AuditTrails/Queries/Export/ExportAuditTrailsQueryHandler.resx
content/src/Application/Resources/Features/AuditTrails/Queries/Export/ExportAuditTrailsQueryHandler.zh-CN.resx
content/src/Application/Resources/Features/DocumentTypes/Commands/Import/ImportDocumentTypesCommandHandler.Designer.cs
content/src/Application/Resources/Features/DocumentTypes/Commands/Import/ImportDocumentTypesCommandHandler.de-DE.resx
content/src/Application/Resources/Features/DocumentTypes/Commands/Import/ImportDocumentTypesCommandHandler.en.resx
content/src/Application/Resources/Features/DocumentTypes/Commands/Import/ImportDocumentTypesCommandHandler.resx
content/src/Application/Resources/Features/DocumentTypes/Commands/Import/ImportDocumentTypesCommandHandler.zh-CN.resx
content/src/Application/Resources/Features/DocumentTypes/Queries/Export/ExportDocumentTypesQueryHandler.Designer.cs
content/src/Application/Resources/Features/DocumentTypes/Queries/Export/ExportDocumentTypesQueryHandler.de-DE.resx
content/src/Application/Resources/Features/DocumentTypes/Queries/Export/ExportDocumentTypesQueryHandler.en.resx
content/src/Application/Resources/Features/DocumentTypes/Queries/Export/ExportDocumentTypesQueryHandler.resx
content/src/Application/Resources/Features/DocumentTypes/Queries/Export/ExportDocumentTypesQueryHandler.zh-CN.resx
content/src/Application/Resources/Features/Documents/Queries/Export/ExportDocumentsQueryHandler.Designer.cs
content/src/Application/Resources/Features/Documents/Queries/Export/ExportDocumentsQueryHandler.de-DE.resx
content/src/Application/Resources/Features/Documents/Queries/Export/ExportDocumentsQueryHandler.en.resx
content/src/Application/Resources/Features/Documents/Queries/Export/ExportDocumentsQueryHandler.resx
content/src/Application/Resources/Features/Documents/Queries/Export/ExportDocumentsQueryHandler.zh-CN.resx
content/src/Application/Resources/Features/Identity/Commands/ResetPassword/ResetPasswordCommandHandler.de-DE.resx
content/src/Application/Resources/Features/Identity/Commands/ResetPassword/ResetPasswordCommandHandler.en.resx
content/src/Application/Resources/Features/Identity/Commands/ResetPassword/ResetPasswordCommandHandler.resx
content/src/Application/Resources/Features/Identity/Commands/ResetPassword/ResetPasswordCommandHandler.zh-CN.resx
content/src/Application/Resources/Features/Identity/DTOs/ApplicationUserDtoValidator.Designer.cs
content/src/Application/Resources/Features/Identity/DTOs/ApplicationUserDtoValidator.de-DE.resx
content/src/Application/Resources/Features/Identity/DTOs/ApplicationUserDtoValidator.en.resx
content/src/Application/Resources/Features/Identity/DTOs/ApplicationUserDtoValidator.resx
content/src/Application/Resources/Features/Identity/DTOs/ApplicationUserDtoValidator.zh-CN.resx
content/src/Application/Resources/Features/Identity/Notifications/ResetPassword/ResetPasswordNotificationHandler.resx
content/src/Application/Resources/Features/Identity/Notifications/SendFactorCode/SendFactorCodeNotificationHandler.resx
content/src/Application/Resources/Features/Identity/Notifications/SendWelcome/SendWelcomeNotificationHandler.resx
content/src/Application/Resources/Features/Identity/Notifications/UserActivation/UserActivationNotificationHandler.resx
content/src/Application/Resources/Features/KeyValues/Commands/Import/ImportKeyValuesCommandHandler.Designer.cs
content/src/Application/Resources/Features/KeyValues/Commands/Import/ImportKeyValuesCommandHandler.de-DE.resx
content/src/Application/Resources/Features/KeyValues/Commands/Import/ImportKeyValuesCommandHandler.en.resx
content/src/Application/Resources/Features/KeyValues/Commands/Import/ImportKeyValuesCommandHandler.resx
content/src/Application/Resources/Features/KeyValues/Commands/Import/ImportKeyValuesCommandHandler.zh-CN.resx
content/src/Application/Resources/Features/KeyValues/Queries/Export/ExportKeyValuesQueryHandler.Designer.cs
content/src/Application/Resources/Features/KeyValues/Queries/Export/ExportKeyValuesQueryHandler.de-DE.resx
content/src/Application/Resources/Features/KeyValues/Queries/Export/ExportKeyValuesQueryHandler.en.resx
content/src/Application/Resources/Features/KeyValues/Queries/Export/ExportKeyValuesQueryHandler.resx
content/src/Application/Resources/Features/KeyValues/Queries/Export/ExportKeyValuesQueryHandler.zh-CN.resx
content/src/Application/Resources/Features/Loggers/Queries/Export/ExportLogsQueryHandler.Designer.cs
content/src/Application/Resources/Features/Loggers/Queries/Export/ExportLogsQueryHandler.de-DE.resx
content/src/Application/Resources/Features/Loggers/Queries/Export/ExportLogsQueryHandler.en.resx
content/src/Application/Resources/Features/Loggers/Queries/Export/ExportLogsQueryHandler.resx
content/src/Application/Resources/Features/Loggers/Queries/Export/ExportLogsQueryHandler.zh-CN.resx
content/src/Application/Resources/Features/PicklistSets/Commands/Import/ImportPicklistSetsCommandHandler.resx
content/src/Application/Resources/Features/PicklistSets/Queries/Export/ExportPicklistSetsQueryHandler.resx
content/src/Application/Resources/Features/PicklistSets/Queries/Export/ExportPicklistSetsQueryHandler.zh-CN.resx
content/src/Application/Resources/Features/SystemLogs/Queries/ChatData/SystemLogsChatDataQueryHandler.resx
content/src/Application/Resources/Features/SystemLogs/Queries/Export/ExportSystemLogsQueryHandler.de-DE.resx
content/src/Application/Resources/Features/SystemLogs/Queries/Export/ExportSystemLogsQueryHandler.en.resx
content/src/Application/Resources/Features/SystemLogs/Queries/Export/ExportSystemLogsQueryHandler.resx
content/src/Application/Resources/Features/SystemLogs/Queries/Export/ExportSystemLogsQueryHandler.zh-CN.resx
content/src/Application/_Imports.cs
content/src/Domain/Common/DomainEvent.cs
content/src/Domain/Common/Entities/BaseAuditableEntity.cs
content/src/Domain/Common/Entities/BaseAuditableSoftDeleteEntity.cs
content/src/Domain/Common/Entities/BaseEntity.cs
content/src/Domain/Common/Entities/IAuditable.cs
content/src/Domain/Common/Entities/IBusinessEntity.cs
content/src/Domain/Common/Entities/IEntity.cs
content/src/Domain/Common/Entities/IHasDomainEvents.cs
content/src/Domain/Common/Entities/IMayBeShared.cs
content/src/Domain/Common/Entities/IMustHaveTenant.cs
content/src/Domain/Common/Entities/ISoftDelete.cs
content/src/Domain/Common/Enums/ExportType.cs
content/src/Domain/Common/Enums/PartnerType.cs
content/src/Domain/Common/Enums/TrackingState.cs
content/src/Domain/Common/Enums/UploadType.cs
content/src/Domain/Domain.csproj
content/src/Domain/Entities/AuditTrail.cs
content/src/Domain/Entities/Document.cs
content/src/Domain/Entities/PicklistSet.cs
content/src/Domain/Entities/SecurityPolicy.cs
content/src/Domain/Entities/SystemLog.cs
content/src/Domain/Entities/Tenant.cs
content/src/Domain/Entities/TenantUser.cs
content/src/Domain/Enums/SecurityRiskLevel.cs
content/src/Domain/Enums/SecurityThreatType.cs
content/src/Domain/Events/DocumentCreatedEvent.cs
content/src/Domain/Events/DocumentDeletedEvent.cs
content/src/Domain/Events/PicklistSetCreatedEvent.cs
content/src/Domain/Events/PicklistSetDeletedEvent.cs
content/src/Domain/Events/PicklistSetUpdatedEvent.cs
content/src/Domain/Identity/ApplicationRole.cs
content/src/Domain/Identity/ApplicationRoleClaim.cs
content/src/Domain/Identity/ApplicationUser.cs
content/src/Domain/Identity/ApplicationUserClaim.cs
content/src/Domain/Identity/ApplicationUserLogin.cs
content/src/Domain/Identity/ApplicationUserRole.cs
content/src/Domain/Identity/ApplicationUserToken.cs
content/src/Domain/_Imports.cs
content/src/Infrastructure/Configurations/AppConfigurationSettings.cs
content/src/Infrastructure/Configurations/DatabaseSettings.cs
content/src/Infrastructure/Configurations/IdentitySettings.cs
content/src/Infrastructure/Configurations/IdleTimeoutSettings.cs
content/src/Infrastructure/Configurations/MailSettings.cs
content/src/Infrastructure/Configurations/StorageSettings.cs
content/src/Infrastructure/DependencyInjection.cs
content/src/Infrastructure/Extensions/HostExtensions.cs
content/src/Infrastructure/Extensions/HttpContextExtensions.cs
content/src/Infrastructure/Extensions/SerilogExtensions.cs
content/src/Infrastructure/Infrastructure.csproj
content/src/Infrastructure/Persistence/ApplicationDbContext.cs
content/src/Infrastructure/Persistence/ApplicationDbContextFactory.cs
content/src/Infrastructure/Persistence/ApplicationDbContextInitializer.cs
content/src/Infrastructure/Persistence/Configurations/AuditTrailConfiguration.cs
content/src/Infrastructure/Persistence/Configurations/DataProtectionKeyConfiguration.cs
content/src/Infrastructure/Persistence/Configurations/DocumentConfiguration.cs
content/src/Infrastructure/Persistence/Configurations/IdentityUserConfiguration.cs
content/src/Infrastructure/Persistence/Configurations/PicklistSetConfiguration.cs
content/src/Infrastructure/Persistence/Configurations/SecurityPolicyConfiguration.cs
content/src/Infrastructure/Persistence/Configurations/TenantConfiguration.cs
content/src/Infrastructure/Persistence/Configurations/TenantUserConfiguration.cs
content/src/Infrastructure/Persistence/Conversions/ValueConversionExtensions.cs
content/src/Infrastructure/Persistence/Extensions/GxNamingConventions.cs
content/src/Infrastructure/Persistence/Extensions/ModelBuilderExtensions.cs
content/src/Infrastructure/Persistence/Interceptors/AuditWriteScope.cs
content/src/Infrastructure/Persistence/Interceptors/AuditableEntityInterceptor.cs
content/src/Infrastructure/Persistence/Interceptors/DispatchDomainEventsInterceptor.cs
content/src/Infrastructure/Persistence/Logging/Configurations/SystemLogConfiguration.cs
content/src/Infrastructure/Persistence/Logging/LogDatabaseDdl.cs
content/src/Infrastructure/Persistence/Logging/LogDatabaseStartupCheck.cs
content/src/Infrastructure/Persistence/Logging/LogDbContext.cs
content/src/Infrastructure/Persistence/Logging/LogDbContextFactory.cs
content/src/Infrastructure/Persistence/Logging/LogTableDdl.cs
content/src/Infrastructure/Persistence/TenantFilterGuard.cs
content/src/Infrastructure/Resources/EmailTemplates/recovery-password.sbn
content/src/Infrastructure/Resources/EmailTemplates/user-activation.sbn
content/src/Infrastructure/Resources/EmailTemplates/welcome.sbn
content/src/Infrastructure/Resources/Services/SecurityAnalysisService.de-DE.resx
content/src/Infrastructure/Resources/Services/SecurityAnalysisService.en-US.resx
content/src/Infrastructure/Resources/Services/SecurityAnalysisService.resx
content/src/Infrastructure/Resources/Services/SecurityAnalysisService.zh-CN.resx
content/src/Infrastructure/Services/Caching/FusionAppCache.cs
content/src/Infrastructure/Services/DataSourceServiceBase.cs
content/src/Infrastructure/Services/DateTimeService.cs
content/src/Infrastructure/Services/ExcelService.cs
content/src/Infrastructure/Services/Identity/AdministratorProtectionService.cs
content/src/Infrastructure/Services/Identity/ApplicationUserClaimsPrincipalFactory.cs
content/src/Infrastructure/Services/Identity/AuditSignInManager.cs
content/src/Infrastructure/Services/Identity/ClientInfoAccessor.cs
content/src/Infrastructure/Services/Identity/HubUserContext.cs
content/src/Infrastructure/Services/Identity/IUserContextLoader.cs
content/src/Infrastructure/Services/Identity/IdentityService.cs
content/src/Infrastructure/Services/Identity/PermissionAssignmentService.cs
content/src/Infrastructure/Services/Identity/PermissionQueryService.cs
content/src/Infrastructure/Services/Identity/RoleDataSourceService.cs
content/src/Infrastructure/Services/Identity/SystemContext.cs
content/src/Infrastructure/Services/Identity/UserContextAccessor.cs
content/src/Infrastructure/Services/Identity/UserContextHubFilter.cs
content/src/Infrastructure/Services/Identity/UserContextLoader.cs
content/src/Infrastructure/Services/Identity/UserDataSourceService.cs
content/src/Infrastructure/Services/Identity/UserProfileState.cs
content/src/Infrastructure/Services/InMemoryTicketStore.cs
content/src/Infrastructure/Services/Mail/MailStartupCheck.cs
content/src/Infrastructure/Services/Mail/MailTemplateGuard.cs
content/src/Infrastructure/Services/Mail/MailTemplateRenderer.cs
content/src/Infrastructure/Services/Mail/MailgunMailService.cs
content/src/Infrastructure/Services/Mail/SinkMailService.cs
content/src/Infrastructure/Services/MultiTenant/TenantDataSourceService.cs
content/src/Infrastructure/Services/MultiTenant/TenantSeedRunner.cs
content/src/Infrastructure/Services/PDFService.cs
content/src/Infrastructure/Services/PermissionService.cs
content/src/Infrastructure/Services/PicklistDataSourceService.cs
content/src/Infrastructure/Services/Security/IdleSessionEnforcer.cs
content/src/Infrastructure/Services/Security/IdleTimeoutPolicyProvider.cs
content/src/Infrastructure/Services/Storage/AzureBlobFileStorage.cs
content/src/Infrastructure/Services/Storage/LocalDiskFileStorage.cs
content/src/Infrastructure/Services/Storage/StorageKeys.cs
content/src/Infrastructure/Services/TenantSwitchService.cs
content/src/Infrastructure/Services/ValidationService.cs
content/src/Infrastructure/_Imports.cs
content/src/Migrators/Migrators.MSSQL/Migrations/20260906061143_InitialCreate.Designer.cs
content/src/Migrators/Migrators.MSSQL/Migrations/20260906061143_InitialCreate.cs
content/src/Migrators/Migrators.MSSQL/Migrations/20261010123836_TenantUserUniqueMembership.Designer.cs
content/src/Migrators/Migrators.MSSQL/Migrations/20261010123836_TenantUserUniqueMembership.cs
content/src/Migrators/Migrators.MSSQL/Migrations/ApplicationDbContextModelSnapshot.cs
content/src/Migrators/Migrators.MSSQL/Migrators.MSSQL.csproj
content/src/Migrators/Migrators.PostgreSQL/Migrations/20260906061136_InitialCreate.Designer.cs
content/src/Migrators/Migrators.PostgreSQL/Migrations/20260906061136_InitialCreate.cs
content/src/Migrators/Migrators.PostgreSQL/Migrations/20261010123810_TenantUserUniqueMembership.Designer.cs
content/src/Migrators/Migrators.PostgreSQL/Migrations/20261010123810_TenantUserUniqueMembership.cs
content/src/Migrators/Migrators.PostgreSQL/Migrations/ApplicationDbContextModelSnapshot.cs
content/src/Migrators/Migrators.PostgreSQL/Migrators.PostgreSQL.csproj
content/src/Migrators/Migrators.SqLite/Migrations/20260906061121_InitialCreate.Designer.cs
content/src/Migrators/Migrators.SqLite/Migrations/20260906061121_InitialCreate.cs
content/src/Migrators/Migrators.SqLite/Migrations/20261010123839_TenantUserUniqueMembership.Designer.cs
content/src/Migrators/Migrators.SqLite/Migrations/20261010123839_TenantUserUniqueMembership.cs
content/src/Migrators/Migrators.SqLite/Migrations/ApplicationDbContextModelSnapshot.cs
content/src/Migrators/Migrators.SqLite/Migrators.SqLite.csproj
content/src/Server.UI/App.razor
content/src/Server.UI/Components/AppShell/HeaderMenu.razor
content/src/Server.UI/Components/AppShell/NavigationMenu.razor
content/src/Server.UI/Components/AppShell/NotificationMenu.razor
content/src/Server.UI/Components/AppShell/SearchDialog.razor
content/src/Server.UI/Components/AppShell/TenantSelector.razor
content/src/Server.UI/Components/AppShell/UserInfoCard.razor
content/src/Server.UI/Components/Branding/Logo.razor
content/src/Server.UI/Components/Common/GxSubmitButton.razor
content/src/Server.UI/Components/Dialogs/ConfirmationDialog.razor
content/src/Server.UI/Components/Dialogs/DeleteConfirmation.razor
content/src/Server.UI/Components/Dialogs/DialogAction.cs
content/src/Server.UI/Components/Errors/CustomError.razor
content/src/Server.UI/Components/Errors/ErrorPageComponent.razor
content/src/Server.UI/Components/Feedback/MudDataGridStatus.razor
content/src/Server.UI/Components/Feedback/ReconnectModal.razor
content/src/Server.UI/Components/Feedback/StatusMessage.razor
content/src/Server.UI/Components/Identity/UserActionInfo.razor
content/src/Server.UI/Components/Identity/UserInfoColumn.razor
content/src/Server.UI/Components/Identity/UserLoginState.razor
content/src/Server.UI/Components/Inputs/Autocomplete/LanguageAutocomplete.cs
content/src/Server.UI/Components/Inputs/Autocomplete/PickSuperiorAutocomplete.razor.cs
content/src/Server.UI/Components/Inputs/Autocomplete/PicklistAutocomplete.razor.cs
content/src/Server.UI/Components/Inputs/Autocomplete/TimeZoneAutocomplete.cs
content/src/Server.UI/Components/Inputs/Display/ReadOnlyField.razor
content/src/Server.UI/Components/Inputs/Select/MudDateTimeField.razor
content/src/Server.UI/Components/Inputs/Select/MudEnumSelect.razor.cs
content/src/Server.UI/Components/Inputs/Select/TenantSelect.razor
content/src/Server.UI/Components/Localization/LanguageSelector.razor
content/src/Server.UI/Components/Navigation/Breadcrumbs.razor
content/src/Server.UI/Components/Routing/ForcePasswordChangeGuard.razor
content/src/Server.UI/Components/Routing/RedirectToLogin.razor
content/src/Server.UI/Components/Security/IdleTimeoutMonitor.razor
content/src/Server.UI/Components/Theming/ThemeToggle.razor
content/src/Server.UI/Components/Time/UtcToLocal.razor
content/src/Server.UI/DependencyInjection.cs
content/src/Server.UI/Endpoints/FileEndpoints.cs
content/src/Server.UI/Extensions/ClaimsPrincipalExtensions.cs
content/src/Server.UI/Extensions/DataGridExtensions.cs
content/src/Server.UI/Extensions/NumericExtensions.cs
content/src/Server.UI/Extensions/StaleCookieMiddlewareExtensions.cs
content/src/Server.UI/Hubs/HubClient.cs
content/src/Server.UI/Hubs/IHubConnectionFactory.cs
content/src/Server.UI/Hubs/ISignalRHub.cs
content/src/Server.UI/Hubs/ServerHub.cs
content/src/Server.UI/Layouts/AppLayout.razor
content/src/Server.UI/Layouts/AuthLayout.razor
content/src/Server.UI/Layouts/MainLayout.razor
content/src/Server.UI/Middlewares/ForcePasswordChangeMiddleware.cs
content/src/Server.UI/Middlewares/GlobalExceptionHandler.cs
content/src/Server.UI/Middlewares/HangfireDashboardAuthorizationFilter.cs
content/src/Server.UI/Middlewares/LocalizationCookiesMiddleware.cs
content/src/Server.UI/Middlewares/SecuritySettingsPageMiddleware.cs
content/src/Server.UI/Middlewares/SelfRegistrationMiddleware.cs
content/src/Server.UI/Middlewares/StaleCookieMiddleware.cs
content/src/Server.UI/Models/NavigationMenu/MenuSectionItemModel.cs
content/src/Server.UI/Models/NavigationMenu/MenuSectionModel.cs
content/src/Server.UI/Models/NavigationMenu/MenuSectionSubItemModel.cs
content/src/Server.UI/Models/NavigationMenu/PageStatus.cs
content/src/Server.UI/Models/Notification/NotificationModel.cs
content/src/Server.UI/Models/Notification/NotificationTypes.cs
content/src/Server.UI/Models/SharedResource.cs
content/src/Server.UI/Pages/Dashboard/Dashboard.razor
content/src/Server.UI/Pages/Documents/Components/DocumentFormDialog.razor
content/src/Server.UI/Pages/Documents/Components/UploadFilesFormDialog.razor
content/src/Server.UI/Pages/Documents/Documents.razor
content/src/Server.UI/Pages/Error.razor
content/src/Server.UI/Pages/Identity/Forgot/Forgot.razor
content/src/Server.UI/Pages/Identity/Forgot/ForgotPasswordConfirmation.razor
content/src/Server.UI/Pages/Identity/Forgot/ResetPassword.razor
content/src/Server.UI/Pages/Identity/Forgot/ResetPasswordConfirmation.razor
content/src/Server.UI/Pages/Identity/Forgot/_Imports.razor
content/src/Server.UI/Pages/Identity/Login/ChangePassword.razor
content/src/Server.UI/Pages/Identity/Login/ExternalLoginPicker.razor
content/src/Server.UI/Pages/Identity/Login/InvalidUser.razor
content/src/Server.UI/Pages/Identity/Login/LinkExternalLogin.razor
content/src/Server.UI/Pages/Identity/Login/Lockout.razor
content/src/Server.UI/Pages/Identity/Login/Login.razor
content/src/Server.UI/Pages/Identity/Login/LoginWith2fa.razor
content/src/Server.UI/Pages/Identity/Login/LoginWithRecoveryCode.razor
content/src/Server.UI/Pages/Identity/Login/_Imports.razor
content/src/Server.UI/Pages/Identity/Register/ConfirmEmail.razor
content/src/Server.UI/Pages/Identity/Register/Register.razor
content/src/Server.UI/Pages/Identity/Register/RegisterConfirmation.razor
content/src/Server.UI/Pages/Identity/Register/_Imports.razor
content/src/Server.UI/Pages/Identity/Roles/Components/PermissionsDrawer.razor
content/src/Server.UI/Pages/Identity/Roles/Components/RoleFormDialog.razor
content/src/Server.UI/Pages/Identity/Roles/Roles.razor
content/src/Server.UI/Pages/Identity/Users/Components/ChangePasswordTab.razor
content/src/Server.UI/Pages/Identity/Users/Components/ProfileInformationTab.razor
content/src/Server.UI/Pages/Identity/Users/Components/ResetPasswordDialog.razor
content/src/Server.UI/Pages/Identity/Users/Components/SecurityTab.razor
content/src/Server.UI/Pages/Identity/Users/Components/UserFormDialog.razor
content/src/Server.UI/Pages/Identity/Users/Components/_Imports.razor
content/src/Server.UI/Pages/Identity/Users/Profile.razor
content/src/Server.UI/Pages/Identity/Users/Users.razor
content/src/Server.UI/Pages/NotFound.razor
content/src/Server.UI/Pages/PicklistSets/Components/CreatePicklistDialog.razor
content/src/Server.UI/Pages/PicklistSets/PicklistSets.razor
content/src/Server.UI/Pages/SystemManagement/AuditTrails.razor
content/src/Server.UI/Pages/SystemManagement/Components/LogsLineCharts.razor
content/src/Server.UI/Pages/SystemManagement/LogDatabaseState.cs
content/src/Server.UI/Pages/SystemManagement/SecuritySettings.razor
content/src/Server.UI/Pages/SystemManagement/SystemLogs.razor
content/src/Server.UI/Pages/Tenants/TenantFormDialog.razor
content/src/Server.UI/Pages/Tenants/Tenants.razor
content/src/Server.UI/Pages/_Imports.razor
content/src/Server.UI/Program.cs
content/src/Server.UI/Resources/Components/AppShell/HeaderMenu.de-DE.resx
content/src/Server.UI/Resources/Components/AppShell/HeaderMenu.en.resx
content/src/Server.UI/Resources/Components/AppShell/HeaderMenu.resx
content/src/Server.UI/Resources/Components/AppShell/HeaderMenu.zh-CN.resx
content/src/Server.UI/Resources/Components/AppShell/NavigationMenu.de-DE.resx
content/src/Server.UI/Resources/Components/AppShell/NavigationMenu.en.resx
content/src/Server.UI/Resources/Components/AppShell/NavigationMenu.resx
content/src/Server.UI/Resources/Components/AppShell/NavigationMenu.zh-CN.resx
content/src/Server.UI/Resources/Components/AppShell/NotificationMenu.de-DE.resx
content/src/Server.UI/Resources/Components/AppShell/NotificationMenu.en.resx
content/src/Server.UI/Resources/Components/AppShell/NotificationMenu.resx
content/src/Server.UI/Resources/Components/AppShell/NotificationMenu.zh-CN.resx
content/src/Server.UI/Resources/Components/AppShell/SearchDialog.de-DE.resx
content/src/Server.UI/Resources/Components/AppShell/SearchDialog.en.resx
content/src/Server.UI/Resources/Components/AppShell/SearchDialog.resx
content/src/Server.UI/Resources/Components/AppShell/SearchDialog.zh-CN.resx
content/src/Server.UI/Resources/Components/AppShell/TenantSelector.de-DE.resx
content/src/Server.UI/Resources/Components/AppShell/TenantSelector.resx
content/src/Server.UI/Resources/Components/AppShell/TenantSelector.zh-CN.resx
content/src/Server.UI/Resources/Components/AppShell/UserInfoCard.de-DE.resx
content/src/Server.UI/Resources/Components/AppShell/UserInfoCard.en.resx
content/src/Server.UI/Resources/Components/AppShell/UserInfoCard.resx
content/src/Server.UI/Resources/Components/AppShell/UserInfoCard.zh-CN.resx
content/src/Server.UI/Resources/Components/Errors/CustomError.de-DE.resx
content/src/Server.UI/Resources/Components/Errors/CustomError.en.resx
content/src/Server.UI/Resources/Components/Errors/CustomError.resx
content/src/Server.UI/Resources/Components/Errors/CustomError.zh-CN.resx
content/src/Server.UI/Resources/Components/Errors/ErrorPageComponent.de-DE.resx
content/src/Server.UI/Resources/Components/Errors/ErrorPageComponent.en.resx
content/src/Server.UI/Resources/Components/Errors/ErrorPageComponent.resx
content/src/Server.UI/Resources/Components/Errors/ErrorPageComponent.zh-CN.resx
content/src/Server.UI/Resources/Components/Identity/UserActionInfo.de-DE.resx
content/src/Server.UI/Resources/Components/Identity/UserActionInfo.en.resx
content/src/Server.UI/Resources/Components/Identity/UserActionInfo.resx
content/src/Server.UI/Resources/Components/Identity/UserActionInfo.zh-CN.resx
content/src/Server.UI/Resources/Components/Identity/UserLoginState.de-DE.resx
content/src/Server.UI/Resources/Components/Identity/UserLoginState.en.resx
content/src/Server.UI/Resources/Components/Identity/UserLoginState.resx
content/src/Server.UI/Resources/Components/Identity/UserLoginState.zh-CN.resx
content/src/Server.UI/Resources/Components/Localization/LanguageSelector.de-DE.resx
content/src/Server.UI/Resources/Components/Localization/LanguageSelector.en.resx
content/src/Server.UI/Resources/Components/Localization/LanguageSelector.resx
content/src/Server.UI/Resources/Components/Localization/LanguageSelector.zh-CN.resx
content/src/Server.UI/Resources/Components/Presence/_._
content/src/Server.UI/Resources/Components/Security/SecurityRiskDashboard.de-DE.resx
content/src/Server.UI/Resources/Components/Security/SecurityRiskDashboard.en.resx
content/src/Server.UI/Resources/Components/Security/SecurityRiskDashboard.resx
content/src/Server.UI/Resources/Components/Security/SecurityRiskDashboard.zh-CN.resx
content/src/Server.UI/Resources/Components/Security/UserLoginRiskSummaryIndicator.de-DE.resx
content/src/Server.UI/Resources/Components/Security/UserLoginRiskSummaryIndicator.en.resx
content/src/Server.UI/Resources/Components/Security/UserLoginRiskSummaryIndicator.resx
content/src/Server.UI/Resources/Components/Security/UserLoginRiskSummaryIndicator.zh-CN.resx
content/src/Server.UI/Resources/Components/Theming/PrimaryColorPicker.zh-CN.resx
content/src/Server.UI/Resources/Components/Theming/ThemeToggle.de-DE.resx
content/src/Server.UI/Resources/Components/Theming/ThemeToggle.resx
content/src/Server.UI/Resources/Components/Theming/ThemeToggle.zh-CN.resx
content/src/Server.UI/Resources/Layouts/AuthLayout.de-DE.resx
content/src/Server.UI/Resources/Layouts/AuthLayout.en.resx
content/src/Server.UI/Resources/Layouts/AuthLayout.resx
content/src/Server.UI/Resources/Layouts/AuthLayout.zh-CN.resx
content/src/Server.UI/Resources/Pages/Dashboard/Dashboard.de-DE.resx
content/src/Server.UI/Resources/Pages/Dashboard/Dashboard.en.resx
content/src/Server.UI/Resources/Pages/Dashboard/Dashboard.resx
content/src/Server.UI/Resources/Pages/Dashboard/Dashboard.zh-CN.resx
content/src/Server.UI/Resources/Pages/Documents/Documents.de-DE.resx
content/src/Server.UI/Resources/Pages/Documents/Documents.en.resx
content/src/Server.UI/Resources/Pages/Documents/Documents.resx
content/src/Server.UI/Resources/Pages/Documents/Documents.zh-CN.resx
content/src/Server.UI/Resources/Pages/Error/Error.de-DE.resx
content/src/Server.UI/Resources/Pages/Error/Error.en.resx
content/src/Server.UI/Resources/Pages/Error/Error.resx
content/src/Server.UI/Resources/Pages/Error/Error.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/Forgot.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/Forgot.en.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/Forgot.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/Forgot.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ForgotPasswordConfirmation.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ForgotPasswordConfirmation.en.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ForgotPasswordConfirmation.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ForgotPasswordConfirmation.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPassword.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPassword.en.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPassword.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPassword.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPasswordConfirmation.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPasswordConfirmation.en.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPasswordConfirmation.resx
content/src/Server.UI/Resources/Pages/Identity/Forgot/ResetPasswordConfirmation.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Login/ExternalLoginPicker.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Login/ExternalLoginPicker.en.resx
content/src/Server.UI/Resources/Pages/Identity/Login/ExternalLoginPicker.resx
content/src/Server.UI/Resources/Pages/Identity/Login/ExternalLoginPicker.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Login/InvalidUser.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Login/InvalidUser.en.resx
content/src/Server.UI/Resources/Pages/Identity/Login/InvalidUser.resx
content/src/Server.UI/Resources/Pages/Identity/Login/InvalidUser.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LinkExternalLogin.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LinkExternalLogin.en.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LinkExternalLogin.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LinkExternalLogin.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Lockout.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Lockout.en.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Lockout.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Lockout.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Login.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Login.en.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Login.resx
content/src/Server.UI/Resources/Pages/Identity/Login/Login.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWith2fa.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWith2fa.en.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWith2fa.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWith2fa.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWithRecoveryCode.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWithRecoveryCode.en.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWithRecoveryCode.resx
content/src/Server.UI/Resources/Pages/Identity/Login/LoginWithRecoveryCode.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/LoginAudits/LoginAudits.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/LoginAudits/LoginAudits.en.resx
content/src/Server.UI/Resources/Pages/Identity/LoginAudits/LoginAudits.resx
content/src/Server.UI/Resources/Pages/Identity/LoginAudits/LoginAudits.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Register/ConfirmEmail.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Register/ConfirmEmail.en.resx
content/src/Server.UI/Resources/Pages/Identity/Register/ConfirmEmail.resx
content/src/Server.UI/Resources/Pages/Identity/Register/ConfirmEmail.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Register/Register.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Register/Register.en.resx
content/src/Server.UI/Resources/Pages/Identity/Register/Register.resx
content/src/Server.UI/Resources/Pages/Identity/Register/Register.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Register/RegisterConfirmation.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Register/RegisterConfirmation.en.resx
content/src/Server.UI/Resources/Pages/Identity/Register/RegisterConfirmation.resx
content/src/Server.UI/Resources/Pages/Identity/Register/RegisterConfirmation.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Components/PermissionsDrawer.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Components/PermissionsDrawer.en.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Components/PermissionsDrawer.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Components/PermissionsDrawer.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Roles.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Roles.en.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Roles.resx
content/src/Server.UI/Resources/Pages/Identity/Roles/Roles.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Components/LoginHistoryTab.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Components/LoginHistoryTab.en.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Components/LoginHistoryTab.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Components/LoginHistoryTab.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Components/PasskeysTab.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Components/PasskeysTab.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Components/PasskeysTab.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Profile.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Profile.en.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Profile.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Profile.zh-CN.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Users.de-DE.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Users.en.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Users.resx
content/src/Server.UI/Resources/Pages/Identity/Users/Users.zh-CN.resx
content/src/Server.UI/Resources/Pages/PicklistSets/PicklistSets.de-DE.resx
content/src/Server.UI/Resources/Pages/PicklistSets/PicklistSets.en.resx
content/src/Server.UI/Resources/Pages/PicklistSets/PicklistSets.resx
content/src/Server.UI/Resources/Pages/PicklistSets/PicklistSets.zh-CN.resx
content/src/Server.UI/Resources/Pages/SystemManagement/AuditTrails.de-DE.resx
content/src/Server.UI/Resources/Pages/SystemManagement/AuditTrails.en.resx
content/src/Server.UI/Resources/Pages/SystemManagement/AuditTrails.resx
content/src/Server.UI/Resources/Pages/SystemManagement/AuditTrails.zh-CN.resx
content/src/Server.UI/Resources/Pages/SystemManagement/SystemLogs.de-DE.resx
content/src/Server.UI/Resources/Pages/SystemManagement/SystemLogs.en.resx
content/src/Server.UI/Resources/Pages/SystemManagement/SystemLogs.resx
content/src/Server.UI/Resources/Pages/SystemManagement/SystemLogs.zh-CN.resx
content/src/Server.UI/Resources/Pages/Tenants/Tenants.de-DE.resx
content/src/Server.UI/Resources/Pages/Tenants/Tenants.en.resx
content/src/Server.UI/Resources/Pages/Tenants/Tenants.resx
content/src/Server.UI/Resources/Pages/Tenants/Tenants.zh-CN.resx
content/src/Server.UI/Resources/ValidationMessages/ValidationMessages.Designer.cs
content/src/Server.UI/Resources/ValidationMessages/ValidationMessages.de-DE.resx
content/src/Server.UI/Resources/ValidationMessages/ValidationMessages.en.resx
content/src/Server.UI/Resources/ValidationMessages/ValidationMessages.resx
content/src/Server.UI/Resources/ValidationMessages/ValidationMessages.zh-CN.resx
content/src/Server.UI/Routes.razor
content/src/Server.UI/Server.UI.csproj
content/src/Server.UI/Services/DialogServiceHelper.cs
content/src/Server.UI/Services/IdentityComponentsEndpointRouteBuilderExtensions.cs
content/src/Server.UI/Services/IdentityRevalidatingAuthenticationStateProvider.cs
content/src/Server.UI/Services/ImageProcessor.cs
content/src/Server.UI/Services/JsInterop/BlazorDownloadFileService.cs
content/src/Server.UI/Services/JsInterop/HistoryGo.cs
content/src/Server.UI/Services/JsInterop/InputClear.cs
content/src/Server.UI/Services/JsInterop/JSInteropConstants.cs
content/src/Server.UI/Services/JsInterop/LocalTimezoneOffset.cs
content/src/Server.UI/Services/JsInterop/OpenSeadragon.cs
content/src/Server.UI/Services/Layout/LayoutService.cs
content/src/Server.UI/Services/Navigation/IMenuService.cs
content/src/Server.UI/Services/Navigation/MenuService.cs
content/src/Server.UI/Services/Notifications/INotificationService.cs
content/src/Server.UI/Services/Notifications/InMemoryNotificationService.cs
content/src/Server.UI/Services/Notifications/NotificationMessages.cs
content/src/Server.UI/Services/UserPreferences/UserPreference.cs
content/src/Server.UI/Services/UserPreferences/UserPreferencesService.cs
content/src/Server.UI/Themes/Theme.cs
content/src/Server.UI/_Imports.cs
content/src/Server.UI/_Imports.razor
content/src/Server.UI/appsettings.Production.json
content/src/Server.UI/appsettings.Staging.json
content/src/Server.UI/appsettings.json
content/src/Server.UI/wwwroot/brand.svg
content/src/Server.UI/wwwroot/css/app.css
content/src/Server.UI/wwwroot/dark-brand.svg
content/src/Server.UI/wwwroot/favicon-152x152.png
content/src/Server.UI/wwwroot/favicon-96x96.png
content/src/Server.UI/wwwroot/favicon.ico
content/src/Server.UI/wwwroot/img/avatar.png
content/src/Server.UI/wwwroot/img/lines-alt.svg
content/src/Server.UI/wwwroot/img/lines.svg
content/src/Server.UI/wwwroot/img/pattern.svg
content/src/Server.UI/wwwroot/js/auth.js
content/src/Server.UI/wwwroot/js/clearinput.js
content/src/Server.UI/wwwroot/js/downloadFile.js
content/src/Server.UI/wwwroot/js/gettimezoneoffset.js
content/src/Server.UI/wwwroot/js/gx-busy-button.js
content/src/Server.UI/wwwroot/js/gxIdleTimeout.js
content/src/Server.UI/wwwroot/js/gxReconnect.js
content/src/Server.UI/wwwroot/js/historygo.js
content/src/Server.UI/wwwroot/js/openseadragon.js
content/src/Server.UI/wwwroot/js/passkeys.js
content/src/Server.UI/wwwroot/robots.txt
content/tests/Application.IntegrationTests/Application.IntegrationTests.csproj
content/tests/Application.IntegrationTests/AssemblyInfo.cs
content/tests/Application.IntegrationTests/Harness/PostgresTestDatabaseConfigurationTests.cs
content/tests/Application.IntegrationTests/Harness/ResetOrderTests.cs
content/tests/Application.IntegrationTests/Harness/SharedDatabaseTests.cs
content/tests/Application.IntegrationTests/Harness/TestClockTests.cs
content/tests/Application.IntegrationTests/Harness/TestDatabaseGuardTests.cs
content/tests/Application.IntegrationTests/HarnessPrincipalTests.cs
content/tests/Application.IntegrationTests/MultiTenant/RecordingTenantSeeder.cs
content/tests/Application.IntegrationTests/MultiTenant/SystemContextTests.cs
content/tests/Application.IntegrationTests/MultiTenant/TenantCommandTests.cs
content/tests/Application.IntegrationTests/MultiTenant/TenantSeederTests.cs
content/tests/Application.IntegrationTests/Persistence/HandlerOwnedTransactionTests.cs
content/tests/Application.IntegrationTests/Picklist/Commands/AddEditPicklistCommandTests.cs
content/tests/Application.IntegrationTests/Picklist/Commands/DeletePicklistTests.cs
content/tests/Application.IntegrationTests/PostgreSqlCanaryTests.cs
content/tests/Application.IntegrationTests/Services/PicklistServiceTests.cs
content/tests/Application.IntegrationTests/Services/TenantsServiceTests.cs
content/tests/Application.IntegrationTests/TestBase.cs
content/tests/Application.IntegrationTests/TestClock.cs
content/tests/Application.IntegrationTests/Testing.cs
content/tests/Application.IntegrationTests/appsettings.json
content/tests/Application.UnitTests/Application.UnitTests.csproj
content/tests/Application.UnitTests/Common/ExceptionHandlers/DbExceptionHandlerTests.cs
content/tests/Application.UnitTests/Common/Exceptions/ValidationExceptionTests.cs
content/tests/Application.UnitTests/Common/Extensions/GetDateRangeKindTests.cs
content/tests/Application.UnitTests/Common/Interceptors/InterceptorOrderingTests.cs
content/tests/Application.UnitTests/Common/Interceptors/SaveChangesInterceptorRegressionTests.cs
content/tests/Application.UnitTests/Common/Interceptors/TenantStampingTests.cs
content/tests/Application.UnitTests/Common/Interceptors/TransactionalAuditTests.cs
content/tests/Application.UnitTests/Common/Mappings/ApplicationUserProjectionTests.cs
content/tests/Application.UnitTests/Common/Mappings/MappingTests.cs
content/tests/Application.UnitTests/Common/PublishStrategies/ChannelBasedNoWaitPublisherTests.cs
content/tests/Application.UnitTests/Common/PublishStrategies/PublisherAmbientContextTests.cs
content/tests/Application.UnitTests/Common/PublishStrategies/PublisherDisposalTests.cs
content/tests/Application.UnitTests/Components/CustomErrorEnvironmentGatingTests.cs
content/tests/Application.UnitTests/Configurations/AppConfigurationSettingsValidationTests.cs
content/tests/Application.UnitTests/Configurations/CommittedAppSettingsTests.cs
content/tests/Application.UnitTests/Configurations/DatabaseSettingsValidationTests.cs
content/tests/Application.UnitTests/Configurations/OutboundAddressTests.cs
content/tests/Application.UnitTests/Configurations/ServerUiPublishTests.cs
content/tests/Application.UnitTests/Configurations/StorageSettingsValidationTests.cs
content/tests/Application.UnitTests/Endpoints/FileEndpointsAuthorizationTests.cs
content/tests/Application.UnitTests/Features/AuditTrails/AuditTrailTenantFilterTests.cs
content/tests/Application.UnitTests/Features/Documents/DocumentTenantIsolationTests.cs
content/tests/Application.UnitTests/Features/Documents/EventHandlers/DocumentDeletedEventHandlerTests.cs
content/tests/Application.UnitTests/Features/Documents/Queries/GetFileStreamQueryHandlerTests.cs
content/tests/Application.UnitTests/Features/Notifications/RemainingNotificationMediatorSmokeTests.cs
content/tests/Application.UnitTests/Features/PicklistSets/PicklistSetTenantFilterTests.cs
content/tests/Application.UnitTests/Features/PicklistSets/PicklistTenantUniquenessTests.cs
content/tests/Application.UnitTests/Features/PicklistSets/SharedPicklistCreationTests.cs
content/tests/Application.UnitTests/Features/PicklistSets/SharedPicklistWriteTests.cs
content/tests/Application.UnitTests/Features/SecuritySettings/InstallationPolicyWriteTests.cs
content/tests/Application.UnitTests/Features/SystemLogs/Queries/SystemLogsWithPaginationQueryCacheKeyTests.cs
content/tests/Application.UnitTests/Features/Tenants/PrimaryTenantRuleTests.cs
content/tests/Application.UnitTests/Identity/AdministratorProtectionTests.cs
content/tests/Application.UnitTests/Identity/LogoutReturnUrlTests.cs
content/tests/Application.UnitTests/Identity/MustChangePasswordTests.cs
content/tests/Application.UnitTests/Identity/PermissionAssignmentGuardTests.cs
content/tests/Application.UnitTests/Identity/Provisioning/NewUserTimeZoneDefaultTests.cs
content/tests/Application.UnitTests/Identity/Roles/RoleDefinitionRightTests.cs
content/tests/Application.UnitTests/Identity/Roles/RoleImportPlanTests.cs
content/tests/Application.UnitTests/Identity/UserContextAllowedTenantsTests.cs
content/tests/Application.UnitTests/Identity/UserContextLoaderTests.cs
content/tests/Application.UnitTests/Identity/Users/SwitchableTenantsTests.cs
content/tests/Application.UnitTests/Identity/Users/TenantSwitchAuthorizationTests.cs
content/tests/Application.UnitTests/Identity/Users/UserRoleChangeSecurityStampTests.cs
content/tests/Application.UnitTests/Identity/Users/UserTenantConsistencyTests.cs
content/tests/Application.UnitTests/Infrastructure/TicketStoreFailSafeTests.cs
content/tests/Application.UnitTests/Logging/LogDatabaseCreationAcceptanceTests.cs
content/tests/Application.UnitTests/Logging/SinkTimestampAcceptanceTests.cs
content/tests/Application.UnitTests/Middlewares/ForcePasswordChangeMiddlewareTests.cs
content/tests/Application.UnitTests/Middlewares/HangfireDashboardAuthorizationFilterTests.cs
content/tests/Application.UnitTests/Middlewares/SecuritySettingsPageMiddlewareTests.cs
content/tests/Application.UnitTests/Middlewares/SelfRegistrationMiddlewareTests.cs
content/tests/Application.UnitTests/MultiTenant/TenantFilterGuardTests.cs
content/tests/Application.UnitTests/MultiTenant/TenantFilterTests.cs
content/tests/Application.UnitTests/MultiTenant/TenantMembershipUniquenessTests.cs
content/tests/Application.UnitTests/MultiTenant/TenantProbe.cs
content/tests/Application.UnitTests/MultiTenant/TenantStampingByMarkerTests.cs
content/tests/Application.UnitTests/Persistence/GenericKeyEntityTests.cs
content/tests/Application.UnitTests/Persistence/ProvisioningTests.cs
content/tests/Application.UnitTests/Persistence/TimestamptzModelInvariantTests.cs
content/tests/Application.UnitTests/Pipeline/AuthorizationBehaviourTests.cs
content/tests/Application.UnitTests/Pipeline/CacheInvalidationScopeTests.cs
content/tests/Application.UnitTests/Pipeline/CacheScopeBehaviourTests.cs
content/tests/Application.UnitTests/Security/AdministratorPermissionRegistryTests.cs
content/tests/Application.UnitTests/Security/LogPermissionScopeTests.cs
content/tests/Application.UnitTests/Security/RequestAuthorizationRegistryTests.cs
content/tests/Application.UnitTests/Storage/AzureBlobFileStorageTests.cs
content/tests/Application.UnitTests/Storage/FileStorageContractTests.cs
content/tests/Application.UnitTests/Storage/LocalDiskFileStorageTests.cs
content/tests/Application.UnitTests/UnitTestDatabase.cs
content/tests/Infrastructure.UnitTests/InfraTestDatabase.cs
content/tests/Infrastructure.UnitTests/Infrastructure.UnitTests.csproj
content/tests/Infrastructure.UnitTests/Logging/LogDatabaseDdlTests.cs
content/tests/Infrastructure.UnitTests/Logging/LogDatabaseDiagnosticRoutingTests.cs
content/tests/Infrastructure.UnitTests/Logging/LogTableDdlTests.cs
content/tests/Infrastructure.UnitTests/Logging/LogTablePostgresTests.cs
content/tests/Infrastructure.UnitTests/Logging/LogTenantStampingTests.cs
content/tests/Infrastructure.UnitTests/Logging/PublishedNotificationLogTenantTests.cs
content/tests/Infrastructure.UnitTests/Logging/SinkColumnDriftTests.cs
content/tests/Infrastructure.UnitTests/Logging/SinkTimestampTests.cs
content/tests/Infrastructure.UnitTests/Mail/MailDeliveryTests.cs
content/tests/Infrastructure.UnitTests/Mail/MailTemplateGuardTests.cs
content/tests/Infrastructure.UnitTests/Mail/MailTokenInjectionTests.cs
content/tests/Infrastructure.UnitTests/Mail/TemplateFileCollection.cs
content/tests/Infrastructure.UnitTests/Persistence/GxTableNamingTests.cs
content/tests/Infrastructure.UnitTests/Persistence/LogDbContextSurfaceTests.cs
content/tests/Infrastructure.UnitTests/Persistence/LogModelSeparationTests.cs
content/tests/Infrastructure.UnitTests/Persistence/ModelMatchesMigrationsTests.cs
content/tests/Infrastructure.UnitTests/Security/IdleTimeoutPolicyTests.cs
content/tests/Infrastructure.UnitTests/Services/DataSourceScopeTests.cs
content/tests/Infrastructure.UnitTests/Services/PicklistDataSourceScopeTests.cs
content/tests/Infrastructure.UnitTests/Services/TenantVisibilityTests.cs
content/tests/Infrastructure.UnitTests/Services/UserVisibilityTests.cs
content/tests/Server.UI.IntegrationTests/AnonymousMatrixTests.cs
content/tests/Server.UI.IntegrationTests/AuthLayoutComponentTests.cs
content/tests/Server.UI.IntegrationTests/ChangePasswordNavigationComponentTests.cs
content/tests/Server.UI.IntegrationTests/CookieLogin.cs
content/tests/Server.UI.IntegrationTests/CookieLoginTests.cs
content/tests/Server.UI.IntegrationTests/DialogActionComponentTests.cs
content/tests/Server.UI.IntegrationTests/FileEndpointMatrixTests.cs
content/tests/Server.UI.IntegrationTests/ForcedPasswordChangeTests.cs
content/tests/Server.UI.IntegrationTests/GxSubmitButtonComponentTests.cs
content/tests/Server.UI.IntegrationTests/GxWebApplicationFactory.cs
content/tests/Server.UI.IntegrationTests/HarnessTests.cs
content/tests/Server.UI.IntegrationTests/IdentityEnumerationComponentTests.cs
content/tests/Server.UI.IntegrationTests/IdentityLifecycleComponentTests.cs
content/tests/Server.UI.IntegrationTests/IdentityLifecyclePolicyTests.cs
content/tests/Server.UI.IntegrationTests/IdleTimeoutDialogComponentTests.cs
content/tests/Server.UI.IntegrationTests/IdleTimeoutWiringTests.cs
content/tests/Server.UI.IntegrationTests/InMemoryRoleStore.cs
content/tests/Server.UI.IntegrationTests/InMemoryUserStore.cs
content/tests/Server.UI.IntegrationTests/LogDatabaseSeparationTests.cs
content/tests/Server.UI.IntegrationTests/LogDatabaseStateComponentTests.cs
content/tests/Server.UI.IntegrationTests/LogPermissionDescriptionComponentTests.cs
content/tests/Server.UI.IntegrationTests/MailPipelineTests.cs
content/tests/Server.UI.IntegrationTests/PicklistSeedVisibilityTests.cs
content/tests/Server.UI.IntegrationTests/ProcessWideStateTests.cs
content/tests/Server.UI.IntegrationTests/ProfileDirectoryExposureComponentTests.cs
content/tests/Server.UI.IntegrationTests/ProfileSecurityTabComponentTests.cs
content/tests/Server.UI.IntegrationTests/ReconnectUiTests.cs
content/tests/Server.UI.IntegrationTests/ResetLinkDisclosureComponentTests.cs
content/tests/Server.UI.IntegrationTests/RoleDefinitionComponentTests.cs
content/tests/Server.UI.IntegrationTests/SearchPermissionComponentTests.cs
content/tests/Server.UI.IntegrationTests/SecuritySettingsPermissionComponentTests.cs
content/tests/Server.UI.IntegrationTests/SerilogPipelineCaptureTests.cs
content/tests/Server.UI.IntegrationTests/Server.UI.IntegrationTests.csproj
content/tests/Server.UI.IntegrationTests/ServerHubTenantIsolationTests.cs
content/tests/Server.UI.IntegrationTests/SharedPicklistGridComponentTests.cs
content/tests/Server.UI.IntegrationTests/SubmitButtonGuardTests.cs
content/tests/Server.UI.IntegrationTests/SuperiorAutocompleteScopeComponentTests.cs
content/tests/Server.UI.IntegrationTests/SuperiorBoundComponentTests.cs
content/tests/Server.UI.IntegrationTests/SystemLogsPageStateComponentTests.cs
content/tests/Server.UI.IntegrationTests/SystemMenuGateComponentTests.cs
content/tests/Server.UI.IntegrationTests/TenantSelectorComponentTests.cs
content/tests/Server.UI.IntegrationTests/ThemeToggleComponentTests.cs
content/tests/Server.UI.IntegrationTests/UiTestDatabase.cs
content/tests/Server.UI.IntegrationTests/UserDeactivationPermissionComponentTests.cs
content/tests/Server.UI.IntegrationTests/UserLoginStateComponentTests.cs
content/tests/Server.UI.IntegrationTests/UserTenantScopeComponentTests.cs
content/tests/TestSupport/ApplicationModel.cs
content/tests/TestSupport/PostgresTestDatabase.cs
content/tests/TestSupport/ResetOrder.cs
content/tests/TestSupport/TestDatabaseGuard.cs
content/tests/TestSupport/TestDatabaseNames.cs
content/tests/TestSupport/TestSupport.csproj
icon.png
package/services/metadata/core-properties/nuget.psmdcp
```
