# Pass 48 — No call-outs: the Seq sink, and every other default outbound address

**Suites (template solution, each project alone, foreground, PostgreSQL 15.7 at 127.0.0.1:5432):**
Application.UnitTests **531 passed + 14 skipped** · Infrastructure.UnitTests **221** ·
Application.IntegrationTests **42** · Server.UI.IntegrationTests **261** — 0 failed.

**Date:** 2026-10-09. **Start:** HEAD `6ebe4004` ("pass47"), clean tree.
**Commit:** one squashed commit, `pass48`. Not pushed.
**Template version:** 1.0.0 → **1.1.0**.
**Origin:** the MNEFleets finding of 2026-10-09: the upstream starter's Serilog setup ships every event to
`https://seq.blazorserver.com`.

---

## Summary

1. **The Seq sink is not in GXTemplate, and has not been since pass 7C (`c1fb1c37`, 2026-08-25).** Pass
   7C deleted `WriteToSeq`, its call and the `Serilog.Sinks.Seq` package. It also deleted upstream's
   Google Analytics beacon. Pass 7-3 (`de15b3e6`) had already removed the seeded Gravatar pictures.
   **Upstream HEAD still has the sink**, hard-coded (Disproven premise 1).
2. **Nothing in the template as it stands sent logs off the server.** The sweep (Report 3) still found
   defaults that call out, or would the moment someone used them. **Removed:**
   - the **MaxMind GeoIP web-service client**: registered with an `HttpClient`, never used, configured
     for `geolite.info`;
   - the **Google Fonts** link on every page, for a font the theme does not use;
   - the **cdnjs `qrcodejs`** script on every page, which nothing calls;
   - five unreferenced components or scripts that loaded Swiper, Fancybox, OpenSeadragon and fabric.js
     from unpkg, jsdelivr and cdnjs.
3. **`OutboundAddressTests`** ships in `tests/Application.UnitTests/Configurations` and so reaches every
   generated project.
   - It fails on any `http(s)://`, `ws(s)://` or `ftp://` address in `src/` (C#, `appsettings*.json`,
     `web*.config`, `.csproj`) that is not on its allowed list.
   - Each entry on that list names its file and its reason. Three entries: the two Mailgun API hosts,
     and one documentation link in a comment.
   - Stale entries also fail.
4. **Mutations:** all 6 went red, each on the test it targets. Every file was restored and checked
   with `cmp`:
   - the Seq sink put back;
   - a new address in C#, in `appsettings.json` (upstream's own 2024 Seq form) and in a `.csproj`;
   - the seeder writing a Gravatar address;
   - a stale allowed entry.
5. **Controls:**
   - The smoke **passed**: pack 1.1.0, install into a throwaway hive, `dotnet new gxblazor --Database
     postgresql`, build with 0 errors, the guard alone 3/3 green, and all four generated suites green.
   - The generated app was **booted on a fresh database**, signed in, and taken through 10 pages for
     3 minutes. Its TCP connections were sampled every 5 s.
   - **The only remote address was 127.0.0.1.** The browser requested nothing but the app itself.
6. **No NoTrackingTests-style guard over pages and scripts exists in the template.** None was built
   (as instructed). One page script still loads from a CDN (Found but not fixed 1).

---

## Disproven premises

| # | Premise | What was found |
|---|---|---|
| 1 | The Seq sink is in GXTemplate, to be found and removed | Absent at HEAD. `git grep` for `WriteTo.Seq`, `Serilog.Sinks.Seq`, `seq.`, `blazorserver` and `WriteToSeq` finds only attribution text. Removed in pass 7C (`c1fb1c37`); its report is `../GXTemplate-passes/pass7c-report.md` §A. The installed template package (`%USERPROFILE%\.templateengine\packages\GX.Blazor.Template.1.0.0.nupkg`, 2026-09-26, byte-identical to the one at the repository root) has no Seq sink and no Gravatar address either. So **every project generated from GXTemplate since 2026-08-25 is clean of both**. A project generated earlier, or straight from upstream, is not: MNEFleets' `src/Infrastructure/Extensions/SerilogExtensions.cs` is the upstream code. |
| 2 | "The seeder writes Gravatar pictures" | It has not since pass 7-3 (`de15b3e6`). Measured on the boot database: `Administrator\|<null>`. With no picture, every avatar shows the app's own placeholder, the user's initial (`UserInfoCard.razor:21-24`, `UserInfoColumn.razor`, `OnlineUsersTracker.razor`). The screenshot showed "A". |
| 3 | `Bootstrap:AdministratorPassword`, or a random password when it is empty | GXTemplate has no such setting: `git grep AdministratorPassword` is empty. The password is **always** generated (`ApplicationDbContextInitializer.cs:238`, `GenerateCompliantPassword`), and logged once through Serilog (Report 2.4). |
| 4 | "web.config" in `src/` | There is none in the repository or in the template content. On a server, web.config is written at deployment. The guard still covers `web*.config`, so one added under `src/` is scanned. |

---

## Report

### 1. Is it here

- **In GXTemplate:** no (Disproven premise 1). It was last present at `fe2ef014` (Pass7-2) as
  `SerilogExtensions.WriteToSeq`, inside the sub-logger that pass 7B gave the bootstrap-banner filter.
- **Upstream history** (the fetch-only `template` remote, fetched this pass):
  - **`66c855b5`** (2024-10-01, "add Serilog.Sinks.Seq") added the package (8.0.0). It also added a
    `Serilog:WriteTo` entry `{"Name": "Seq", "Args": {"serverUrl": "http://10.33.1.150:8082"}}` in
    `appsettings.json`: a private address, read through `ReadFrom.Configuration`, and so replaceable
    by configuration.
  - **`5329057c`** (2025-11-11, "Update packages and refactor Serilog configuration") moved it into code
    as `WriteToSeq`:
    - `serverUrl = "https://seq.blazorserver.com"`, `apiKey = "none"`, `restrictedToMinimumLevel =
      "Verbose"`;
    - the `configuration` parameter is accepted and never read, and the appsettings entry was deleted.

    **This is the commit that made it phone home with no way to switch it off.** Its own
    `docs/Serilog-Configuration-Migration.md` describes a configurable `SerilogSeq` section, which the
    code does not read.
- **Upstream HEAD** (`55e50694`, 2026-10-05): **still present**:
  `src/Infrastructure/Extensions/SerilogExtensions.cs:57` (URL), `:67` (`WriteTo.Seq`), and
  `src/Infrastructure/Infrastructure.csproj:30` (`Serilog.Sinks.Seq` 9.1.0).

### 2. What it could send

From code and configuration only. This is what a log event carries in a project generated today. The
Seq sink would have received all of it from `Debug` up: `Verbose` was its own floor, and the
configured default level is `Debug`. The database sink receives the same from `Information` up.

**2.1 Properties on every event**

| Property | Source |
|---|---|
| Timestamp, Level, MessageTemplate, rendered Message, Exception with full stack trace | Serilog itself |
| `SourceContext` (logger category), `EventId` | the Microsoft.Extensions.Logging bridge (`Serilog.AspNetCore`) |
| `TimeStamp` (UTC) | `UtcTimestampEnricher`, `SerilogExtensions.cs` |
| `UserName` | `UserInfoEnricher`: `HttpContext.User.Identity.Name` |
| `ClientIP` | `UserInfoEnricher`: the first `X-Forwarded-For` value (client-supplied, spoofable), else `Connection.RemoteIpAddress` |
| `ClientAgent` | `UserInfoEnricher`: the `User-Agent` header |
| `TenantId` | `UserInfoEnricher`: the ambient `IUserContextAccessor` |
| `RequestId`, `RequestPath` (path only, no query), `ConnectionId`, `TraceId`, `SpanId`, `ParentId` | ASP.NET Core hosting's per-request scope, surfaced by `Enrich.FromLogContext()` |
| `BootstrapSecret`, `LogDatabaseDiagnostic` | `BeginScope` markers (`ApplicationDbContextInitializer.cs:274`, `LogDatabaseStartupCheck.cs:132`, `Program.cs:101`) |
| `Application` ("GX Application"), `Environment`, `TargetFramework` | `Serilog:Properties` in `appsettings*.json` |

**2.2 Message content written by template code**
- **Usernames:**
  - sign-in and sign-out (`IdentityComponentsEndpointRouteBuilderExtensions.cs:273,280,546`);
  - external sign-in, lockout and not-allowed (`:410-420`);
  - user deleted and activated (`Users.razor:591,641,999`).
- **Email addresses:**
  - every mail sent or failed (`SendMailCommand.cs:79,83`, `MailgunMailService.cs:69,75`, the
    reset, welcome and activation notifications);
  - `ConfirmEmail.razor:68`, which logs `user.Email` under `{UserName}`.
- **Identifiers:** user IDs, tenant IDs, and a passkey credential ID (`:741`; a public identifier,
  not a secret).
- **External sign-in:**
  - the `ReturnUrl` (`:369`), a local URL that may carry a query string;
  - the provider's `RemoteError` text (`:384`).
- **Mailgun's response body** when it refuses a message (`MailgunMailService.cs:75`).
- **Identity error descriptions** on a failed reset or confirmation (`ResetPassword.razor:110,138`).
- **The page path on an unhandled UI error.** The query string is stripped
  (`CustomError.razor:118-126`), which keeps reset tokens out.
- **Every exception's message and stack trace.**

**2.3 EF Core, request bodies, tokens and passwords**
- **EF Core SQL:**
  - Every `Microsoft*` category is clamped to `Error` in code (`SerilogExtensions.cs`, the
    `MinimumLevel.Override("Microsoft", Error)` after `ReadFrom.Configuration`), so normal commands are
    not logged.
  - A **failed** command is logged at Error with its SQL text. Parameter values show as `?`:
    `EnableSensitiveDataLogging` appears nowhere in `src/`.
  - On PostgreSQL, Npgsql redacts a server error's `Detail` (the row values in a constraint violation),
    because no connection string sets `Include Error Detail`. That covers `src/`, `.template.config`
    (including the generated `appsettings.Development.json`) and `tests/TestSupport`.
  - *Caveat, mssql choice only:* SQL Server's own messages carry the duplicate key value.
- **Request bodies and form values:** no path.
  - There is no `UseSerilogRequestLogging`, `UseHttpLogging` or `AddHttpLogging`.
  - The Mediator pipeline logs the request **type name** only (`ResultExceptionBehavior.cs:55`;
    `FusionCacheBehaviour` and `CacheInvalidationBehaviour` at Trace, type and cache key).
  - No `{@Request}`-style destructuring exists in `src/`.
- **Tokens:** no antiforgery token, cookie or `Authorization` header is logged. Reset and confirmation
  tokens travel in query strings, which no log call records.
- **Passwords:** the sign-in form's password is never logged. The remaining routes by which a value
  could reach a log line are **exception messages that embed values**, the external sign-in
  `ReturnUrl`, and the Mailgun refusal body.

**2.4 The first-run administrator password goes through Serilog.**
- It is logged once at Warning (`ApplicationDbContextInitializer.cs:283-289`) inside a `BootstrapSecret`
  scope.
- The file sink drops it (`IsExcludedFromFileSink`), and so does the database sink
  (`IsExcludedFromDatabaseSink`). The console keeps it, by design.
- **Measured in the boot control:** console 1 banner, `log/log-*.txt` 0, `system_logs` 0 of 12 rows.
- **The filter is opt-out, per sink.** A sink added through `ReadFrom.Configuration` (`Serilog:WriteTo`
  in any appsettings file, or `Serilog__WriteTo__…` environment variables) or through `ReadFrom.Services`
  sits outside both filtered sub-loggers and **would receive the password**. The upstream Seq sink in
  GX between passes 7B and 7C sat inside the filter, so it did not. Upstream's sits outside any filter,
  but upstream prints no generated password. Not changed here (Found but not fixed 3).

### 3. Every other way the server talks out

Swept: every `.cs`, `.razor`, `.js`, `.css`, `.csproj`, `appsettings*.json`, `launchSettings.json`, `.resx`
and `.svg` under `src/`, `.template.config/`, the nuspec, `build/pack.csproj` and `Directory.Build.props`.
Also every package reference, every `HttpClient` registration, and the authentication, cache and job
registrations.

| # | What | Where (HEAD) | What it sends | GX needs it? | Disposition |
|---|---|---|---|---|---|
| 1 | Seq sink | — | — | — | gone since pass 7C |
| 2 | Gravatar seed pictures | — | — | — | gone since pass 7-3 |
| 3 | Google Analytics `gtag.js` (upstream's ID) | — | — | — | gone since pass 7C |
| 4 | **MaxMind GeoIP2 `WebServiceClient`** | `Infrastructure/DependencyInjection.cs:343-345`; `MaxMind` sections in all three appsettings (`Host: geolite.info`); package `MaxMind.GeoIP2` 6.1.0 | Each lookup sends a client IP to MaxMind. **Never resolved anywhere** (upstream's consumer is gone). It sends nothing today, but one constructor injection away it would. | No | **Removed**: registration, package, config sections, and the `MaxMind:LicenseKey` required-key entries in `CommittedAppSettingsTests` and the smoke script |
| 5 | **Google Fonts** stylesheet (Roboto) | `App.razor:17` | Every page load, from the visitor's browser: IP, User-Agent and Referer to Google | No. The theme's font stack is Inter, then system fonts (`Themes/Theme.cs:132-141`); Roboto appears only as a system-font fallback | **Removed** |
| 6 | **cdnjs `qrcodejs` 1.0.0** | `App.razor:56` | Every page load: the browser to Cloudflare | No. Its only caller, `GenerateQrCode`, is never used | **Removed**, with `GenerateQrCode.cs` and `generateQrCode.js` |
| 7 | **cdnjs OpenSeadragon 3.1.0** (static `import`) | `wwwroot/js/appInterop.js:3` | Would fetch on load | No. The file is never loaded | **Removed** |
| 8 | **unpkg fabric.js 5.2.1** | `Pages/Documents/Documents.razor.js` | Would fetch on load | No. Never imported | **Removed** |
| 9 | **unpkg Swiper 11.1.5** | `wwwroot/js/carousel.js` ← `Swiper.cs` ← `Carousel.razor` | Would fetch on use | No. `Carousel` is never rendered | **Removed** (all three) |
| 10 | **jsdelivr + unpkg Fancybox 6.1.7** | `wwwroot/js/fancybox.js` ← `Fancybox.cs` ← `FileUploadZone.razor` | Would fetch on use | No. `FileUploadZone` is never rendered | **Removed**, with its two helpers `Thumbnail.razor` and `FileSizeFormatter.razor` (used only by it), the `_Imports` using and the Fancybox CSS |
| 11 | **unpkg OpenSeadragon 5.0.1** | `wwwroot/js/openseadragon.js:30` ← `DocumentFormDialog.razor:71` | When a document with an image is opened: the browser to unpkg | The feature, yes; the CDN, no | **Kept, reported** (Found but not fixed 1) |
| 12 | Mailgun HTTP API | `MailSettings.cs:135` (`api.mailgun.net`, `api.eu.mailgun.net`) | The rendered mail, the recipient and the API key, only when `Mail:Delivery` resolves to Mailgun with a domain and key. The Development default is the file sink. | Yes: the configured mail transport | Kept; rewritten as two whole literals so the guard names them |
| 13 | Azure Blob Storage | `Storage:Provider = azureblob` + `Storage:ConnectionString` | Files | Yes: configured storage (default `disk`) | Kept |
| 14 | Databases | `DatabaseSettings` | — | Yes | Kept |
| 15 | Google and Microsoft external sign-in | `Infrastructure/DependencyInjection.cs:574-585` (packages `Microsoft.AspNetCore.Authentication.Google`, `.MicrosoftAccount`) | Only after a user clicks the provider button: a code exchange and a profile fetch with the configured client id and secret. Registered unconditionally, with placeholder ids. | Per project | Kept. Pass 46 R4 already schedules conditional registration (CO-10) |
| 16 | SignalR client | `Server.UI/Hubs/IHubConnectionFactory.cs:44` | The app's own hub, on its own base URL | Yes | Kept (seen in the boot as 127.0.0.1:5199) |
| 17 | `https://schema.org` (JSON-LD `@context`), `aka.ms` and `robotstxt.org` in comments, w3.org XML namespaces in `.resx` and `.svg`, `json.schemastore.org` in `template.json`/`ide.host.json`, the nuspec xmlns | — | Nothing: identifiers and comments, never fetched | — | Kept. `aka.ms` is on the guard's list as a comment |
| 18 | `launchSettings.json` localhost URLs | `Server.UI/Properties` | Loopback only; excluded from the package | — | Kept; not scanned |
| 19 | Hangfire (`InMemory`), FusionCache, QuestPDF, ClosedXML, Scriban, ImageSharp, MudBlazor, ApexCharts | packages | No network use: in-process; the JavaScript is served from `_content/` | — | Kept |

**Not the server, for completeness:** `dotnet restore` and NuGet audit contact nuget.org at **build** time,
and the .NET SDK sends CLI telemetry unless `DOTNET_CLI_TELEMETRY_OPTOUT=1`. Neither runs in a deployed
application.

---

## The changes

| File | Change |
|---|---|
| `src/Infrastructure/DependencyInjection.cs` | MaxMind `using`, `Configure<WebServiceClientOptions>` and `AddHttpClient<WebServiceClient>` removed (−9) |
| `src/Infrastructure/Infrastructure.csproj` | `MaxMind.GeoIP2` removed |
| `src/Server.UI/appsettings.json`, `.Staging.json`, `.Production.json` | `MaxMind` sections removed |
| `src/Server.UI/App.razor` | Google Fonts `<link>` and the cdnjs `qrcodejs` `<script>` removed |
| `src/Server.UI/_Imports.razor` | `@using …Components.Inputs.Upload` removed (the namespace is now empty) |
| `src/Server.UI/wwwroot/css/app.css` | Fancybox variable and two rules removed |
| deleted (12 files) | `Components/Inputs/Upload/{FileUploadZone,Thumbnail,FileSizeFormatter}.razor`, `Pages/Dashboard/Components/Carousel.razor`, `Pages/Documents/Documents.razor.js`, `Services/JsInterop/{Fancybox,GenerateQrCode,Swiper}.cs`, `wwwroot/js/{appInterop,carousel,fancybox,generateQrCode}.js` |
| `src/Infrastructure/Configurations/MailSettings.cs:128-135` | `ApiEndpoint` now picks between two whole host literals, so the guard can name them. The output is unchanged (`MailDeliveryTests.cs:223` still passes). |
| `tests/Application.UnitTests/Configurations/OutboundAddressTests.cs` (new) | the guard: 3 tests (below) |
| `tests/Application.UnitTests/Configurations/CommittedAppSettingsTests.cs` | `MaxMind:LicenseKey` dropped from `RequiredStructure` |
| `tooling/smoke-generate.ps1` | `MaxMind:LicenseKey` dropped; new step "nothing in the generated source calls out, and the guard ships"; new step "the outbound-address guard passes in the generated project"; header updated |
| `GX.Blazor.Template.nuspec` | version 1.1.0 and its release notes |
| `README.md` | new standard "Nothing calls out by default"; the `MaxMind:LicenseKey` mention removed |

**The guard, `OutboundAddressTests`:**
- **What it scans:** every `.cs`, `.csproj`, `appsettings*.json` and `web*.config` under `src/`,
  skipping `bin/` and `obj/`. It also skips `appsettings.Development.json`, which is gitignored,
  machine-local and never deployed. The README says so.
- **What counts as an address:** `\b(?:https?|wss?|ftp)://` plus the host, lower-cased. The path is
  not part of it.
- **The three tests:**
  - `EveryHardCodedAddressInSrc_IsOnTheAllowedList` reports every unlisted address as `file:line
    address`.
  - `EveryAllowedAddress_IsStillInItsFile` catches stale entries.
  - `TheScan_ReadsTheCSharpTheSettingsAndTheProjectFiles` guards against a vacuous scan.
- **The list:** each entry is `(file, address, reason)`:

| File | Address | Reason |
|---|---|---|
| `src/Infrastructure/Configurations/MailSettings.cs` | `https://api.mailgun.net` | Mailgun API, US region: the mail transport, only when configured |
| `src/Infrastructure/Configurations/MailSettings.cs` | `https://api.eu.mailgun.net` | Mailgun API, EU region |
| `src/Server.UI/DependencyInjection.cs` | `https://aka.ms` | a documentation link in the stock HSTS comment; never fetched |

---

## Verification

### Mutations (red first)

Method (`scratchpad/mutate.sh`):
1. Back up the file and apply one `perl` substitution, printing the diff.
2. Run `dotnet test tests/Application.UnitTests --no-build --filter OutboundAddressTests`.
3. Restore the file and `cmp` it against the backup.

`--no-build` is accurate here because the guard reads the source files at test time, not compiled code.

| # | Mutation | Red | Reported |
|---|---|---|---|
| M1 | Seq sink put back: upstream `5329057c`'s `WriteToSeq` and its call, into `SerilogExtensions.cs` | `EveryHardCodedAddressInSrc_IsOnTheAllowedList` | `src/Infrastructure/Extensions/SerilogExtensions.cs:130 https://seq.blazorserver.com` |
| M2a | new constant `"https://telemetry.example.com/v1/collect"` in `MailgunMailService.cs` | same | `…/MailgunMailService.cs:32 https://telemetry.example.com` |
| M2b | upstream `66c855b5`'s Seq entry in `appsettings.json` `Serilog:WriteTo` | same | `src/Server.UI/appsettings.json:120 http://10.33.1.150:8082` |
| M2c | `<PackageProjectUrl>https://updates.example.org/gx</PackageProjectUrl>` in `Server.UI.csproj` | same | `src/Server.UI/Server.UI.csproj:4 https://updates.example.org` |
| M3 | the seeder writing `ProfilePictureDataUrl = "https://s.gravatar.com/avatar/…?s=80"` (upstream's value) for the administrator | same | `…/ApplicationDbContextInitializer.cs:247 https://s.gravatar.com` |
| M4 | the `aka.ms` link removed from its comment (an allowed entry made stale) | `EveryAllowedAddress_IsStillInItsFile` | `src/Server.UI/DependencyInjection.cs https://aka.ms` |

Each mutation ended "restored … (cmp identical)". In M1 to M3, the other two tests stayed green.

### Build

`dotnet build CleanArchitecture.Blazor.slnx --no-incremental`: **0 errors**.
- The distinct warnings are pass 47's 19: 10 code warnings and 9 NETSDK1206.
- **New since pass 47: NuGet audit** reports `SixLabors.ImageSharp` 3.1.12 with **3 high** (GHSA-j3p4-wp97-rph4,
  GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w) and **2 moderate** (GHSA-gwg2-r3hj-4w44,
  GHSA-wmxv-xphr-5c9g) advisories, on every project that references it.
- The package was not touched here. The advisories are newly published (Found but not fixed 2).

### Suites

`GX_TEST_PG=Host=127.0.0.1;Port=5432;Username=postgres` (trust; the arrangement approved in pass 47).
Each suite was run with `dotnet test tests\<suite> --no-build`, in the foreground:

| Suite | Pass 47 final | Pass 48 | Wall-clock |
|---|---|---|---|
| Application.UnitTests | 528 + 14 skipped | **531 + 14 skipped** (+3 guard) | 30.3 s |
| Infrastructure.UnitTests | 221 | **221** | 6.5 s |
| Application.IntegrationTests | 42 | **42** | 16.7 s |
| Server.UI.IntegrationTests | 261 | **261** | 95.0 s |

The 14 skips are pass 47's 12 Azurite tests and 2 opt-in tests.

---

## Controls

**1. Pack, install, generate, build, test.**

Command:
`tooling\smoke-generate.ps1 -Database postgresql -ProjectName SmokeApp -KeepOutput -TestServer "Host=127.0.0.1;Port=5432;Username=postgres"`.

Result: **SMOKE PASSED (postgresql)**, exit 0.
- **The package:** `GX.Blazor.Template.1.1.0.nupkg` contains
  `content/tests/Application.UnitTests/Configurations/OutboundAddressTests.cs` and none of the 12 deleted
  files. Its `Infrastructure.csproj`, `App.razor`, `appsettings.json` and `MailSettings.cs` have 0 hits
  for MaxMind, `googleapis`, `qrcodejs`, `WriteTo.Seq`, `Sinks.Seq` and gravatar.
- **It installed into a custom hive** under the work folder. The machine's template store was not
  touched, and still holds 1.0.0.
- **The new step** passed: `OutboundAddressTests.cs is generated`, and each probe (Seq sink, Gravatar
  address, MaxMind GeoIP client, Google Fonts or qrcodejs) found nothing in `src` (425 files).
  - After the run, the probe names were reworded to fix "no a …" in the step's messages. Only that
    text changed, and the script was parse-checked afterwards (0 errors).
- **Build:** 0 errors.
- **The guard alone, without a server:** `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3`
  (`SmokeApp.Application.UnitTests.dll`).
- **Without `GX_TEST_PG`:** all four suites failed and named the variable (247/286/12 skipped, 34/166,
  23/19, 117/144).
- **Against the server:**
  - SmokeApp.Application.UnitTests **531 + 14 skipped**;
  - Infrastructure.UnitTests **200** (provider-specific tests are stripped for `postgresql`, as in
    pass 47's smoke);
  - Application.IntegrationTests **42**;
  - Server.UI.IntegrationTests **261**.
- `appsettings.Development.json` is gitignored in a fresh repository.

**2. Boot and sample** (`scratchpad/boot-sample.ps1` and `drive.js`).

How it was run:
- **The app:** the generated `SmokeApp.Server.UI.exe` in Development, on `http://127.0.0.1:5199`.
  The connection strings were overridden by environment variables to two **new** databases,
  `gx48_boot_smokeapp` and `gx48_boot_smokeapp_logs`, on 127.0.0.1:5432. Neither existed before.
- **The browser:** headless Chrome, driven by `playwright-core` 1.62.1. It was loaded read-only from
  MNEFleets' `tests/browser/node_modules`, and nothing was downloaded.
- **The session:**
  - signed in as `administrator` with the banner password, and was sent to `/account/change-password`;
  - changed the password, and landed on `/`;
  - then cycled through `/`, `/identity/users`, `/identity/roles`, `/system/logs`, `/system/audittrails`,
    `/pages/documents`, `/system/picklistset`, `/user/profile`, `/system/tenants` and
    `/system/security-settings`, 7 times. None of them redirected to sign-in.
  - The final screenshot shows the Roles page, signed in as "Administrator" with the initial-letter
    avatar.
- **The sampling:** `Get-NetTCPConnection -OwningProcess <app pid>` every 5 s for 180 s: **33 samples,
  746 rows**.

What was seen:

| Remote endpoint | What it is | Rows |
|---|---|---|
| `127.0.0.1:5432` | PostgreSQL: the business and log databases | 145 |
| `127.0.0.1:5199` | the app's own SignalR hub client, connecting to itself | 60 |
| `127.0.0.1:<ephemeral>` ×94 | the browser's connections to the app (local end `127.0.0.1:5199`) | 1–33 each |
| `0.0.0.0:0`, `:::0` | listening and bound sockets | 238 |

**Distinct remote addresses: `::`, `0.0.0.0`, `127.0.0.1`. Off-machine: none.**
- **Browser:** every request went to one origin, `http://127.0.0.1:5199` (740 requests). There were no
  font, CDN or analytics requests.
- **DNS:** lookups go through the Windows DNS Client service, not the app process, so per-process TCP
  sampling cannot see them. No connection to any non-loopback address appeared, so no lookup was
  followed by a connection.
- **Banner:** console 1, `log/log-*.txt` 0, `system_logs` 0 of 12.
- **Administrator's `ProfilePictureDataUrl`:** null.
- The console log kept in the scratchpad has the password replaced by `<redacted>`.

**Databases created on 127.0.0.1:5432** (none dropped):
- `gx48_boot_smokeapp` and `gx48_boot_smokeapp_logs` (boot);
- `gx_test_smokeapp_appint`, `_infra_logs`, `_ui` and `_ui_logs` (smoke);
- `gx_test_smokeapp_infra`, `_unit` and `_unit_logs` already existed from pass 47's interrupted smoke,
  and were reused.

**Temporary folders:** `%TEMP%\gxs48` (the smoke output) was removed.

---

## Also report

**Version.**
- `GX.Blazor.Template` **1.1.0** (was 1.0.0, unchanged since the "Final" commit). The release notes say
  what changed.
- The packed `GX.Blazor.Template.1.1.0.nupkg` carries the fix (Controls 1).
- The repository-root `GX.Blazor.Template.1.0.0.nupkg` (gitignored, 2026-09-26), and the 1.0.0 installed
  in this machine's template store, still carry the MaxMind client, the Google Fonts link and the
  qrcodejs script. They do not carry Seq or Gravatar. To replace the installed one:
  `dotnet pack build/pack.csproj -o .` then
  `dotnet new uninstall GX.Blazor.Template && dotnet new install .\GX.Blazor.Template.1.1.0.nupkg`.
  **This was not done**, because it changes this machine's template store.

**Could this go upstream?** Yes, as small, separate pull requests. Nothing was prepared.

- **PR 1, the Seq sink (the one that matters).** Upstream evidently wants its demo site's logs in its
  own Seq, so deleting the sink outright is unlikely to be accepted. A change that keeps it, **off by
  default**:
  - `WriteToSeq` reads `configuration["SerilogSeq:ServerUrl"]`, and returns without adding a sink
    when it is empty. `ApiKey` and `MinimumLevel` are read the same way. This is exactly what upstream's
    own `docs/Serilog-Configuration-Migration.md` already documents.
  - `appsettings.json` gets `"SerilogSeq": { "ServerUrl": "", "ApiKey": "", "MinimumLevel": "Information" }`.
  - Upstream's demo deployment sets `SerilogSeq__ServerUrl=https://seq.blazorserver.com` (and a real
    key) in its environment, and keeps its logging.
  - Alternatively, delete `WriteToSeq` and document a `Serilog:WriteTo` Seq entry for
    `ReadFrom.Configuration`, which is already wired up. This is 66c855b5's arrangement without a
    committed address.
  - The PR should also lower the default from Verbose, and add a short README note that the template
    ships no default log destination.
  - **The argument to make:** today every clone, fork and generated project ships logs to one server
    with no key, and no setting stops it. That includes usernames, client IPs, user agents, email
    addresses and exception text (Report 2).
- **PR 2:** drop the Gravatar seed URLs (`ApplicationDbContextInitializer.cs:150,172` upstream), since
  the UI already has an initial-letter placeholder.
- **PR 3:** the Google Analytics `gtag` with the author's measurement ID `G-W1TQBGC3QH`, still at upstream HEAD (`src/Server.UI/App.razor:82-87`).
  Removing it is the minimal change; making the ID configurable and empty by default is the
  alternative.
- **Optional:** the unused `WebServiceClient` registration, the unused `qrcodejs` tag and the dead CDN
  scripts.
- **Not upstreamable as is:** `OutboundAddressTests` is GX-specific in its allowed list, but the idea
  transfers.

---

## Found but not fixed

1. **The document image viewer loads OpenSeadragon 5.0.1 from unpkg.com** (`wwwroot/js/openseadragon.js:30`)
   when a document with an image is opened. That is a visitor's browser contacting a third party.
   - The fix is to vendor the file under `wwwroot/lib/openseadragon/` (BSD-3-Clause) and load it
     locally.
   - Not done, because pages and scripts were out of this pass's build scope. The template has **no
     NoTrackingTests-style guard over pages and scripts**. One would have caught this, and every item
     in Report 3 rows 5–11.
2. **`SixLabors.ImageSharp` 3.1.12 has 3 high and 2 moderate published advisories** (Build). Upgrade
   it in a dependency pass.
3. **The bootstrap-password filter is per sink, and opt-out** (Report 2.4). A sink configured through
   `Serilog:WriteTo` or environment variables, or registered through DI, receives the password.
   Applying `Filter.ByExcluding(CarriesBootstrapSecret)` at the root logger, and routing the banner to a
   console-only sub-logger, would close that.
4. **`appsettings.Development.json` is not scanned by the guard**, by design: it is local, gitignored and
   never deployed. A server's web.config is not in `src/` either. Both are configuration, which the
   guard does not police.
5. **Pass 48's previously planned scope** (pass 47 §5, 9: removing the SQLite and SQL Server options, and
   NETSDK1206) moves to the next pass, because this pass took the number.
6. **Reports folder.** Passes 19 to 47 keep their reports in the repository's `GXTemplate-passes/`, which
   is committed. The sibling `..\GXTemplate-passes\` holds passes up to 18b. This report follows the
   in-repository convention.
