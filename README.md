# GX Blazor Server Solution Template

A Blazor Server solution template for .NET 10, laid out in Clean Architecture layers, with
multi-tenant ASP.NET Core Identity, deny-by-default request authorization, transactional audit
trails, and pluggable file storage.

This README ships with the template and with every project generated from it, so parts of it are
addressed to whoever is generating a project and parts to whoever is maintaining the generated one.

---

## Lineage and licence

This is a **GX-maintained derivative** of
[neozhu/CleanArchitectureWithBlazorServer](https://github.com/neozhu/CleanArchitectureWithBlazorServer),
which is itself in the lineage of Jason Taylor's Clean Architecture solution template. It is
distributed under the **MIT licence**, and the copyright notice in `LICENSE` retains the original
authors alongside GX Informatics Limited.

The GX fork differs from upstream in ways that matter for a line-of-business application:

- demo features (Products, Contacts, the chatbot, document OCR) have been removed;
- every Mediator request must declare the permission it requires, and the application refuses to
  start if one does not;
- audit rows are written in the same database transaction as the change they describe;
- cached responses are separated per principal structurally, rather than by hand in each query;
- file storage is an abstraction with a disk provider and an Azure Blob provider, served through an
  authenticated endpoint;
- upstream's branding, analytics beacon, log shipping and third-party asset URLs are gone.

---

## Getting started

### 1. Install the template

From a local clone:

```
dotnet new install .
```

Or from a package:

```
dotnet new install GX.Blazor.Template.0.1.0-preview.1.nupkg
```

### 2. Generate a project

```
dotnet new gxblazor -n IMS -o IMS
```

`-n` sets the project name; every assembly, namespace and the solution file take it.

### 3. Set the connection string

Open `src/Server.UI/appsettings.json` and set `DatabaseSettings:ConnectionString` to a database you
can actually reach. The wizard writes a placeholder of the right shape for the provider you chose,
but it cannot know your host or credentials.

For anything beyond local development, keep secrets out of the file — every setting can be supplied
as an environment variable using the standard double-underscore form:

```
DatabaseSettings__ConnectionString="Host=db;Port=5432;Database=ims;Username=ims;Password=..."
```

### 4. Run

```
dotnet run --project src/Server.UI
```

The application creates and migrates its own schema on startup. There is no separate
`dotnet ef database update` step.

### 5. First sign-in

On a database with no administrator, the bootstrap creates one and writes its password to the
console, **once**:

```
================ ADMINISTRATOR ACCOUNT CREATED ================
  Username: Administrator
  Password: <generated>
This password was generated for this installation and is shown ONCE, here, now.
It cannot be read back from the application. Copy it before this process exits.
You will be required to change it the first time you sign in.
===============================================================
```

Three things follow from that, and all three are deliberate:

- **The password is generated per installation.** There is no default password, so an unattended
  deployment does not come up with a known one.
- **It is written to the console only.** It is filtered out of the file and database log sinks, so
  it does not persist anywhere. If you lose it before signing in, delete the administrator row (or
  the database) and start the application again.
- **The account is flagged `MustChangePassword`.** Signing in lands you on
  `/account/change-password` and nothing else is reachable until you change it. This is enforced
  both over HTTP and inside a live Blazor circuit.

The bootstrap runs in every environment. In Development it additionally seeds sample data (a second
organisation and picklist values); Production gets roles, one organisation and the administrator,
and nothing else.

---

## The wizard options

Three options, and they are the whole option surface. Each one is exercised by the template's own
verification, which is why there are three of them and not thirty.

| Option | CLI | Values | Default | Effect |
|---|---|---|---|---|
| Database provider | `--Database` | `postgresql`, `mssql`, `sqlite` | `postgresql` | Writes `DatabaseSettings:DBProvider` and a connection string of the right shape into `appsettings.json`. |
| Default time zone | `--DefaultTimeZone` | any time zone id | `UTC` | Writes `AppConfigurationSettings:DefaultTimeZone`, the zone a newly provisioned account gets when nobody has chosen one. |
| Allow self-registration | `--AllowSelfRegistration` | `true`, `false` | `true` | Writes `AppConfigurationSettings:AllowSelfRegistration`. When `false`, the self-service account-creation surface returns 404. |

Two things worth knowing about `--Database`:

- **All three providers and all three migration projects ship regardless of your choice.** The
  option selects configuration, not content. That keeps generated projects mergeable against future
  template versions, and it means switching provider later is a configuration change plus a
  migration regeneration, not a regeneration of the project.
- The migration for each provider already exists under `src/Migrators/`. If you change the model,
  regenerate with, for example:
  ```
  DatabaseSettings__DBProvider=postgresql dotnet ef migrations add <Name> \
    --project src/Migrators/Migrators.PostgreSQL --startup-project src/Server.UI
  ```

`--DefaultTimeZone` and `--AllowSelfRegistration` are written as **configuration**, not compiled in.
A generated project can change its mind about either without regenerating from the template.

---

## Configuration reference

All settings live in `src/Server.UI/appsettings.json` and can be overridden by environment variables
(`Section__Key=value`). Three sections are validated when the application starts: a bad value fails
the process immediately, naming the offending value, rather than surfacing later as an obscure
runtime error.

### `DatabaseSettings` — validated at startup

| Key | Notes |
|---|---|
| `DBProvider` | `postgresql`, `mssql` or `sqlite`. Anything else fails startup, naming the supported set. |
| `ConnectionString` | Required. |

### `Storage` — validated at startup

| Key | Notes |
|---|---|
| `Provider` | `disk` (default) or `azureblob`. Anything else fails startup, naming the supported set. |
| `RootPath` | Disk provider only. Directory for stored files; relative paths resolve against the content root. Default `Files`. |
| `ConnectionString` | Azure Blob only, and **required** when the provider is `azureblob` — a missing one fails startup rather than the first upload. |
| `ContainerName` | Azure Blob only, and required on the same terms. One container; the storage key is the blob name. |
| `CacheControlMaxAgeSeconds` | How long a browser may cache a file served by `/files`. Default 3600. |

Stored files are addressed by a provider-opaque **storage key** of the shape
`{UploadType}/{Folder?}/{FileName}`, and served by an authenticated endpoint at `/files/{key}` under
**both** providers. There is no anonymous static-file route for uploaded content, and the Azure
container is private.

### `AppConfigurationSettings` — validated at startup

| Key | Notes |
|---|---|
| `AppName` | Shown in the title, the navigation shell and the page metadata. |
| `ApplicationUrl` | Used in the Open Graph `og:url` tag. Set it or the social preview points at `example.com`. |
| `Company`, `Copyright`, `Version` | Displayed in the shell. |
| `DefaultTimeZone` | Must be a time zone id this system recognises, or startup fails naming the value. |
| `AllowSelfRegistration` | See below. |

### `SmtpClientOptions`

| Key | Notes |
|---|---|
| `Host`, `Port`, `UserName`, `Password`, `UseSsl`, `RequireCredentials` | Standard SMTP settings. Mail is disabled until `Host` is set. |
| `DefaultFromEmail` | Defaults to `noreply@example.com`, an IANA-reserved domain that can never route. **Set this** before enabling mail, or your messages claim to come from a placeholder. |

### `AllowSelfRegistration`

When `false`, the following return **404** — not 403, because with registration disabled the feature
does not exist and a 403 would confirm the endpoint is there:

- `/account/register` and `/account/registerconfirmation`
- `/account/linkexternallogin` and `/pages/authentication/performlinkexternallogin`

The last two matter. There are **two** self-service doors, not one: besides the registration form,
the external-login callback creates a brand-new account for any external identity it does not
recognise. Closing only the first would make the flag untrue. Signing in with an external identity
that **already** has an account is unaffected either way.

---

## The GX standards

These are the properties a GX application is expected to have. They are stated here as contracts
because each one is enforced by something that will fail loudly if you break it.

### Deny-by-default authorization

**Contract: every Mediator request declares the permission it requires, and one that does not is
denied.**

- Every `IRequest` must carry a `RequestAuthorizeAttribute`. `AuthorizationBehaviour` denies any
  request that does not, and logs why.
- `RequestAuthorizationRegistry.AssertAllRequestsAreMarked` runs at **startup** and throws, naming
  the offending request types, if any request in the Application assembly is unmarked. You cannot
  ship a request that silently runs unauthorized; you get a failed start instead.
- A request also needs an ambient principal. `AuthorizationBehaviour` fails closed when there is
  none, which is why prerendering is off — a prerendered first render has no circuit and therefore
  no principal.
- At the HTTP layer the same posture applies: the authorization **fallback policy** requires an
  authenticated user, and the anonymous surface (login, health, framework assets, the Blazor
  circuit) is opted back in explicitly, one endpoint at a time.

When you add a feature, add the attribute. The startup assertion is the reminder.

### Transactional audit

**Contract: an audit row and the change it describes commit together, or neither does.**

`AuditableEntityInterceptor` opens a transaction in `SavingChanges` when there are audit rows to
write and holds it across the save, committing both together and rolling back both on failure. A
change is never recorded as having happened when it did not, and — the direction people forget — a
change never happens without its audit row: **if the audit write fails, the business operation is
rolled back with it**.

Interceptor **registration order is load-bearing** and is asserted by a test.
`AuditableEntityInterceptor` must come before `DispatchDomainEventsInterceptor`, so domain events are
never published for a change the audit write subsequently rolled back.

Audited entities are those implementing `IAuditable`. **Identity entities are not audited** — see
Known limitations.

### `CacheScope`

**Contract: declaring a scope guarantees that entries are separated as declared. It does not
guarantee the declaration is right.**

A cacheable request declares `CacheScope.Global`, `PerUser`, `PerTenant` or `PerUserAndTenant`, and
the caching pipeline folds the matching identity components of the **ambient** principal into the
key. Scoping is therefore structural, not re-implemented in each query's `ToString()`.

What it does **not** do is work out what your query depends on. Declaring `Global` for a query whose
results vary by user still leaks between users — it just does so at a declaration you can review
rather than through a key someone forgot to extend. **`PerUserAndTenant` is the right default when
in doubt.**

### Escalation guards

Administration operations that could lock everyone out, or let someone grant themselves more than
they hold, are guarded:

- `AdministratorProtectionService` refuses an operation that would remove the last administrator, and
  refuses a role rewrite that would leave the installation without one.
- `PermissionAssignmentService` mediates permission changes rather than letting the UI write claims
  directly.
- `AdministratorPermissionRegistry` is an **explicit list** of what the administrator role is granted,
  not a reflection sweep over every permission constant, and `AssertNoDivergence` fails the start if
  the list and the constants drift apart. A new permission is therefore not silently granted to
  administrators by existing.
- Changing a user's roles or password refreshes their security stamp, so existing sessions do not
  keep the old permissions.

---

## Known limitations

Stated plainly, because finding these out later is worse than reading them now.

- **Role and user administration bypasses Mediator.** Those pages call `UserManager` and
  `RoleManager` directly, so deny-by-default request authorization does **not** cover them. They are
  protected by page-level `[Authorize]` and permission checks, which is a weaker guarantee than the
  one the rest of the application has.
- **Granular Create/Delete permission constants gate rendering only.** Some fine-grained permission
  constants control whether a button is shown, not whether the underlying operation is permitted.
  Treat the coarse feature permission as the real boundary.
- **Identity entities are not audited.** `ApplicationUser`, `ApplicationRole` and their claim tables
  do not implement `IAuditable`, so user and role changes do not produce audit rows. Login events
  are recorded separately; permission and role changes are not.
- **`FileUploadZone` ships unwired.** The component is present, migrated and tested, but nothing
  currently renders it. It is intended as the upload component for features you add.
- **The Azure Blob provider is exercised against Azurite, not a real storage account.** The full
  storage contract passes against the emulator. Authentication with a real account key, network
  failure behaviour, and throttling responses are untested.
- **The PostgreSQL migration is reviewed, not applied, in the template's own verification.** It is
  generated and its SQL is read; there is no PostgreSQL server in the build environment.
- **The `docker-compose.dcproj` references `.github/copilot-instructions.md`**, which is excluded
  from generation. Harmless — it is a `<None Include>` — but Visual Studio may show it as missing.

---

## Solution layout

```
src/
  Domain/           entities, enums, domain events - no dependencies
  Application/      CQRS requests, handlers, pipeline behaviours, interfaces
  Infrastructure/   EF Core, Identity, caching, storage providers, mail, logging
  Server.UI/        Blazor Server components, pages, endpoints, middleware
  Migrators/        one EF Core migration project per provider
tests/
  Application.UnitTests/         handlers, pipeline, security, storage, configuration
  Application.IntegrationTests/  handlers against a real SQL Server database
  Server.UI.IntegrationTests/    the real HTTP pipeline: cookie login, authorization matrices
  Infrastructure.UnitTests/      infrastructure services
```

### The HTTP integration harness

`tests/Server.UI.IntegrationTests` boots the **real application** with
`WebApplicationFactory<Program>` over a throwaway SQLite database and storage root, and drives a
**real cookie sign-in**. It holds the authorization matrices that used to be re-measured by hand:
which paths challenge an anonymous caller, which are deliberately anonymous, how `/files` responds
to authorized, unauthorized and anonymous callers, and how the forced-password-change gate behaves.

If you add an endpoint, add its row. That file is the regression net for the security posture above.

---

## Running the tests

```
dotnet test
```

Twelve tests exercise the Azure Blob provider against the [Azurite](https://github.com/Azure/Azurite)
emulator and are **skipped** — not failed — when it is not running. To include them:

```
npx azurite --silent --location <a temp dir>
```
