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
dotnet new install GX.Blazor.Template.1.0.0.nupkg
```

### 2. Generate a project

```
dotnet new gxblazor -n IMS -o IMS
```

`-n` sets the project name; every assembly, namespace and the solution file take it.

### 3. Set the connection string

Open `src/Server.UI/appsettings.json` and set `DatabaseSettings:ConnectionString` to a database you
can actually reach. The wizard writes a placeholder of the right shape for the provider you chose
and names it after your project, but it cannot know your host or credentials.

There are **two** connection strings. `LogConnectionString` names a second database, on the same
server, that Serilog writes to and the SystemLogs page reads from — so log volume stays out of the
business database's backups and can be retained under its own policy. Leave the setting empty and
the application still runs, logging to console and file only, and says so at startup.

**You do not have to create the log database first.** The application creates it on startup when it
is absent and the login is allowed to, exactly as EF's `Migrate()` has always created the business
database. On SQLite both are files and nothing is needed at all.

If the login may not create databases, that is not a failure: the application logs one error naming
the database, the login and the grant it would need, then runs and audits normally with the
SystemLogs page reporting the log database unavailable.

**For production, prefer creating the log database in advance.** `CREATEDB` / `dbcreator` is
**unnecessary**, not merely harmless — with the database already there the application finds it,
issues nothing, and never touches the create path on any start:

```sql
-- PostgreSQL, once, as an administrator
CREATE DATABASE "IMS_Logs";
CREATE ROLE ims_log LOGIN PASSWORD '…' NOCREATEDB;
GRANT CONNECT ON DATABASE "IMS_Logs" TO ims_log;
-- then either create the table yourself, or let the application do it on one start with CREATE TABLE
```

The catalogue checks the application uses — `pg_database` and `DB_ID()` — need no privilege beyond
connecting, which is what lets a least-privileged login start it silently every time.

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

Four options, and they are the whole option surface. Each one is exercised by the template's own
verification, which is why there are four of them and not thirty.

| Option | CLI | Values | Default | Effect |
|---|---|---|---|---|
| Database provider | `--Database` | `postgresql`, `mssql`, `sqlite` | `postgresql` | Writes `DatabaseSettings:DBProvider` and a connection string of the right shape into `appsettings.json`. |
| Database name | `--DatabaseName` | any name | the project name | Names both databases: `<name>` and `<name>_Logs`. |
| Default time zone | `--DefaultTimeZone` | any time zone id | `UTC` | Writes `AppConfigurationSettings:DefaultTimeZone`, the zone a newly provisioned account gets when nobody has chosen one. |
| Allow self-registration | `--AllowSelfRegistration` | `true`, `false` | `true` | Writes `AppConfigurationSettings:AllowSelfRegistration`. When `false`, the self-service account-creation surface returns 404. |

**`--DatabaseName`** defaults to the project name, so `dotnet new gxblazor -n IMS` produces databases
called `IMS` and `IMS_Logs` rather than the `GXApplication` every generated project used to share —
two projects on one server no longer collide.

The name is **sanitised**: anything outside letters, digits and underscore is stripped, so
`My DB"; DROP` becomes `MyDBDROP`, and a project named `Acme.Ims` yields `AcmeIms`. The template
engine offers no way to *reject* a malformed value for a free-text parameter, so it corrects one
instead — a database name containing a space or a quote produces a connection string that fails
obscurely at runtime, and silently fixing it beats shipping it. If you want an exact name, pass one
that is already valid.

Two things worth knowing about `--Database`:

- **All three providers and all three migration projects ship regardless of your choice.** The
  option selects configuration, not content. That keeps generated projects mergeable against future
  template versions, and it means switching provider later is a configuration change plus a
  migration regeneration, not a regeneration of the project.
- The migration for each provider already exists under `src/Migrators/`. If you change the model,
  regenerate with, for example:
  ```
  DatabaseSettings__DBProvider=postgresql dotnet ef migrations add <Name> \
    --project src/Migrators/Migrators.PostgreSQL --startup-project src/Server.UI \
    --context ApplicationDbContext
  ```
  `--context` is required: the application registers two contexts, and `LogDbContext` deliberately
  has no migrations of its own. `DatabaseSettings__DBProvider` alone is enough only when the
  configured connection strings already suit that provider; regenerating for a provider other than
  the configured one needs `DatabaseSettings__ConnectionString` and
  `DatabaseSettings__LogConnectionString` overridden to match, because the design-time host builds
  the real service provider and a PostgreSQL connection string fails to parse as a SQLite one.
  Regenerate all three providers together so the chains stay in step.

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
| `ConnectionString` | Required. The business database. |
| `LogConnectionString` | The **separate** database Serilog writes to and the SystemLogs page reads from — same server, same provider. **The application creates it when it is absent and the login may**; if not, it logs one error naming the database, the login and the required grant (`CREATEDB` / `dbcreator`) and carries on. Creating it in advance makes that grant unnecessary — see [Set the connection string](#3-set-the-connection-string). Absent is supported: the application runs, logs to console and file only, and says so loudly at startup. It never falls back to `ConnectionString`. |

#### Every persisted `DateTime` is UTC

This is a rule, not a convention, and on PostgreSQL it is enforced by the database.

- **On PostgreSQL every date column is `timestamptz`** (`timestamp with time zone`). That is Npgsql's
  default mapping for `DateTime`, and this template does not override it: it neither sets
  `Npgsql.EnableLegacyTimestampBehavior` nor declares column types by hand. A `timestamptz` parameter
  **accepts `DateTimeKind.Utc` and rejects `Unspecified` and `Local`** — so a local-time value is not
  a subtly wrong row, it is a failed write with a clear message. On SQL Server (`datetime2`) and
  SQLite (`TEXT`) the types are unchanged and the same UTC rule applies by discipline rather than by
  rejection.
- **Get the clock from `IDateTime.UtcNow`**, injected, rather than from `DateTime.Now` or
  `DateTime.UtcNow` directly. That is how handlers and interceptors should obtain the current time:
  it keeps the Kind correct by construction and lets tests pin an exact instant. `DateTime.Now`
  anywhere near a persisted value is a bug on PostgreSQL.
- **Date ranges keep the caller's Kind.** `DateTimeExtensions.GetDateRange` re-specifies both ends to
  the input's Kind at its single exit, so every keyword — including ones nothing calls yet — returns
  a value a `timestamptz` parameter will accept.
- Two tests keep this from drifting: `TimestamptzModelInvariantTests` asserts every `DateTime` in the
  Npgsql model maps to `timestamp with time zone` and that no source file outside `Program.cs` sets an
  `AppContext` switch; `ProcessWideStateTests` asserts the legacy switch is unset after a real boot.
  A startup line at `Information` names the effective state, to console and file.
- Process-wide decisions belong in `ConfigureProcessWideState()` at the top of `src/Server.UI/Program.cs`,
  which runs before `WebApplication.CreateBuilder`. It is where the QuestPDF licence is set, and where
  anything else global to the process should go.

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

### `SecuritySettings:IdleTimeout` — validated at startup

Bounds and bootstrap defaults only; the policy in force is administered at runtime. See
[Idle timeout and auto-logout](#idle-timeout-and-auto-logout).

| Key | Notes |
|---|---|
| `Enabled` | `false` makes the feature inert end to end — no JS module fetched, no principal check, the security-settings route answers 404 and its menu entry and the profile Security tab are both absent, and the cookie falls back to a fixed 8 hours. |
| `DefaultIdleTimeoutMinutes` | Seeded into a fresh database on first read. Must lie within `[Min, Max]`. |
| `DefaultCountdownSeconds` | Seeded likewise. Between 10 and 600, and **must not exceed** `MinIdleTimeoutMinutes` in seconds — equal is allowed, which is what the shipped 1-minute minimum and 60-second countdown rely on. |
| `MinIdleTimeoutMinutes` | Floor for every policy, administered or per-user. At least 1. |
| `MaxIdleTimeoutMinutes` | Ceiling, **and the only value that sizes the authentication cookie** (max + countdown + grace). A deployment decision rather than an administrator's, because a cookie is issued once and cannot be shortened afterwards. At most 480. |
| `AllowUserOverride` | When false the Profile → Security field is absent, not disabled, and an existing preference is ignored rather than honoured. |
| `KeepAlivePingEnabled` | The browser ping that renews the sliding cookie inside a long-lived circuit. Turning it off reintroduces the trap described in the standards section. |
| `CookieGraceMinutes` | Slack on the cookie's lifetime so it never lapses marginally before the enforcement meant to end the session. At least 1. |

### `AppConfigurationSettings` — validated at startup

| Key | Notes |
|---|---|
| `AppName` | Shown in the title, the navigation shell and the page metadata. |
| `ApplicationUrl` | Used in the Open Graph `og:url` tag. Set it or the social preview points at `example.com`. |
| `Company`, `Copyright`, `Version` | Displayed in the shell. |
| `DefaultTimeZone` | Must be a time zone id this system recognises, or startup fails naming the value. |
| `AllowSelfRegistration` | See below. |

### `Mail`

Mail goes out through the **Mailgun HTTP API**. There is no SMTP option.

| Key | Notes |
|---|---|
| `Region` | `US` or `EU`, matching where the sending domain is provisioned. Anything else fails startup naming the value. The endpoint URL is composed from this and `Domain`; it is never stored, so the two cannot disagree. |
| `Domain` | The Mailgun sending domain, e.g. `mg.example.com`. |
| `FromAddress` | Defaults to `noreply@example.com`, an IANA-reserved domain that can never route. **Set this**, or your messages claim to come from a placeholder. A malformed address fails startup. |
| `FromName` | Display name shown beside the address. |
| `Delivery` | `Sink`, `Mailgun`, or empty. **Leave it empty** — see below. |
| `SinkPath` | Where the sink writes. Defaults to `mail`, which is gitignored. |
| `TimeoutSeconds` | Defaults to 10. An administrator waits on this synchronously when resending a verification email; `HttpClient`'s 100-second default is indistinguishable from a hang. |

#### The API key is environment-only

`Mail__ApiKey`, from the environment. **It is not in `appsettings.json` and must not be put there.**
Everything else in the block is environment-true rather than secret — which domain, which address,
which region — and belongs in committed configuration where a reviewer can see it.

#### The development sink is on by default

With `Delivery` empty, mail goes to the **sink** in Development and to **Mailgun** everywhere else.
The sink renders each message to `./mail/` and logs a line naming the recipient, subject and path;
it makes no network call. A developer machine therefore cannot email a real customer by accident,
and the decision needs no `appsettings.Development.json` — there is none, it is gitignored.

The sink renders through the same renderer as Mailgun, so the file is what would have been sent.

#### Sending for real, including against Mailgun's sandbox

Set `Mail:Delivery` to `Mailgun`, `Mail:Domain` to your sandbox domain
(`sandboxXXXX.mailgun.org`), and `Mail__ApiKey` in the environment. Mailgun's sandbox only delivers
to **authorised recipients** you add in their dashboard, so it is the safe way to see a real message
arrive. Everything else — templates, tokens, the from-address — behaves exactly as in production.

#### Templates

Three Scriban templates ship, in `src/Infrastructure/Resources/EmailTemplates/`:
`recovery-password.sbn`, `user-activation.sbn`, `welcome.sbn`. Four tokens — `user_name`,
`app_name`, `company`, `base_url` — are supplied to every template automatically; anything a caller
sets explicitly wins. `user_name` falls back from display name to user name to `there`, so an email
never opens "Hi ,".

At startup every template is checked for presence, valid UTF-8, absence of replacement characters,
and parseability. Outside Development a failure stops the application: a missing template is a broken
deployment, not a configuration choice.

**Adding a template:** drop the `.sbn` file in that directory and add a `const` to `MailTemplates`.
The csproj picks it up by wildcard; the startup guard will then require it.

**Note:** `.sbn` files are deliberately kept out of the `Content` item group so the Web SDK cannot
publish them as static web assets. Do not "fix" this by moving them to `Content` — it would serve
your email templates over HTTP.

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

### Idle timeout and auto-logout

**Contract: the browser shows a warning and counts down; the server ends the session, and would do
so if the browser never ran at all.**

A JavaScript timer is not security. It can be disabled, paused on a breakpoint, or stopped when the
Blazor circuit drops, and while the authentication cookie is valid the user is still authenticated —
a modal covering the UI has signed nobody out. So the feature has two halves that read one policy:

| Concern | Mechanism |
|---|---|
| Detect inactivity, warn, count down | `wwwroot/js/gxIdleTimeout.js` + `IdleTimeoutMonitor.razor` |
| End the session gracefully | the template's existing sign-out endpoint |
| End it even if the circuit is dead | the JS deadline is absolute, and fires without .NET |
| Guarantee it ends regardless of the browser | `IdleSessionEnforcer`, in `OnValidatePrincipal` |
| Absolute upper bound on any session | `ExpireTimeSpan` = max window + countdown + grace |

`IdleSessionEnforcer` runs on **every authenticated HTTP request** and reads the policy in force at
that moment — so an administrator shortening the window takes effect on sessions already open, which
is the whole point of making it administrable. The cookie's own expiry is only the outer bound: a
cookie is issued once and cannot be shortened afterwards, which is why it is sized from
`MaxIdleTimeoutMinutes` rather than from the current policy.

**Three levels, and only one of them is the user's.**

| Level | Who | Where |
|---|---|---|
| Bounds and seed defaults | deployment | `SecuritySettings:IdleTimeout` in `appsettings.json` |
| Policy in force | administrator | System → Security Settings (`Permissions.SecuritySettings.Edit`) |
| Preference | user | Profile → Security — may only **shorten** |

The user level is tighten-only because an idle timeout is a control against unattended workstations.
If a user could raise their own, the first person to find it inconvenient sets it to eight hours and
the control is gone — the same argument that keeps password policy out of a user profile. Shortening
is safe and genuinely useful: someone on a shared shop-floor terminal can pick five minutes. The
narrowing is applied at **read time** in `IdleTimeoutPolicyProvider`, not only in the screen's
validator, so a value forced into the database directly is still clamped.

`Enabled: false` makes the feature inert end to end: no module is fetched, no principal check runs,
the cookie falls back to a fixed eight hours, and **both screens go away rather than emptying out** —
`/system/security-settings` answers 404 through `SecuritySettingsPageMiddleware` (the same shape and
the same 404-not-403 reasoning the self-registration surface uses), its navigation entry is dropped,
and `Profile.razor` omits the Security tab panel entirely. `AllowUserOverride: false` removes that
tab too. Absent, never disabled: an empty tab invites a support call asking what belongs in it.

**The policy is installation-wide, not per-tenant.** `SecurityPolicies` holds a single row and the
cache key is a constant, so every tenant in a multi-tenant deployment shares one idle window. This is
a deliberate starting point rather than an oversight — every reader goes through
`IIdleTimeoutPolicyProvider` precisely so that adding a tenant column and keying the cache by tenant
is a migration plus one cache key, not a redesign — but today one tenant's administrator sets the
policy for all of them.

**The row is seeded lazily, on first read.** A freshly provisioned database has an **empty**
`SecurityPolicies` table until something asks for the policy; until then the configured
`DefaultIdleTimeoutMinutes` / `DefaultCountdownSeconds` are what is in force. That is what lets the
feature work on a database provisioned before it existed, with no data migration — but do not read an
empty table as "the feature is not configured".

**The per-user preference is deliberately not audited.** Changing the administered policy writes an
audit row, because `SecurityPolicy` implements `IAuditable`. A user shortening their own window does
not, because it lives on `ApplicationUser` and Identity entities are deliberately outside the audit
trail (see [Transactional audit](#transactional-audit)). Auditing it would mean auditing Identity,
which is a larger decision than this feature should make on its own.

**Two failure modes this is built around, both invisible until they bite:**

1. **The Blazor Server sliding-cookie trap.** Cookie `SlidingExpiration` renews on HTTP requests, and
   a user working inside one long-lived SignalR circuit makes almost none — so somebody actively
   working for two hours can have their cookie expire underneath them, and the next real request (a
   download, a refresh, an export) bounces them to the login page mid-task. The keep-alive ping at
   `/account/keep-alive` exists solely to make that request. It is also why **Stay Logged In** calls
   the endpoint rather than only resetting a timer. Any existing GX app on this stack without one is
   worth checking: work past the cookie lifetime without a full page load, then refresh.

2. **Multi-tab false logouts.** Activity is shared across tabs through `localStorage`, and every tab
   measures idleness against the most recent activity in *any* tab. With one deliberate asymmetry:
   activity in **another** tab cancels a countdown (the user is demonstrably working), while activity
   in the tab **showing** the countdown does not (a stray mouse movement must not silently extend a
   session). Dismissal there requires the button.

**Sign-out is simultaneous across every tab**, by two mechanisms on purpose. The tab that ends the
session writes a `gx:idle:signedOut` record and every other tab leaves on the `storage` event; and
any tab regaining focus re-pings the server, so a tab that was throttled or asleep through the whole
countdown gets a 401 and redirects. The second is the robust one — it depends on no message being
received, and it covers sign-out from *any* cause, not just idle: an explicit logout elsewhere, an
administrator disabling the account, or the cookie simply expiring.

**Four things here are load-bearing in ways that are easy to undo:**

- **The cookie event is chained, never replaced.** `OnValidatePrincipal` is Identity's security-stamp
  validator — the mechanism behind "changing a user's roles or password signs their existing sessions
  out". Assigning over it would delete that guarantee silently: everything would still compile, boot
  and pass its own tests. `IdleTimeoutWiringTests` drives the real delegate and fails if the stamp
  validator stops running.
- **Only the keep-alive path counts as activity.** If any authenticated request renewed the window,
  an unattended workstation would keep itself signed in through whatever its browser happened to
  fetch.
- **The keep-alive endpoint is origin-checked.** It returns nothing and changes no business data, but
  it does renew a session, and this application sets `SameSite=None` — so an unchecked endpoint would
  let any page the user has open hold their session open indefinitely, defeating the control it
  serves.
- **The keep-alive answers with status codes, not redirects.** It is the one endpoint on this surface
  that does, and it is `AllowAnonymous` with the check stated in the handler for exactly that reason:
  the fallback policy's challenge fires before any handler and redirects to the login page, and the
  browser's `fetch` follows redirects, so an expired session read as `200` and every client-side
  check for a dead session was inert. It answers `401` unauthenticated and `403` origin-refused, and
  the JSON bodies keep `UseStatusCodePagesWithReExecute` from rewriting a bodiless error into the
  not-found page. Do **not** "fix" this by teaching the cookie handler's `OnRedirectToLogin` about
  the path — that event is shared by every page in the application.

The user preference is read from the database behind a per-user cache rather than carried as a claim
(the way `MustChangePassword` is). A claim only changes when the cookie is reissued, and
`SignInManager.RefreshSignInAsync` cannot run inside a Blazor circuit — the response has already
started — so a claim would take effect at the user's *next sign-in*, which is not what the screen
says it does.

**Testing it by hand.** Set the policy to 1 minute with a 15-second countdown, then check the
multi-tab and circuit-drop behaviour: three idle tabs must all land on the login page when the
countdown expires, including one backgrounded throughout; killing the SignalR connection during the
countdown must still sign you out at the deadline; and signing in again must not be bounced straight
out by the stale `gx:idle:signedOut` key. None of that is reachable from the automated suite.

### Database naming

**Contract: your business models live in the `core` schema as `TBL_UPPER_SNAKE`, and you get that by
deriving from `BaseEntity` — there is nothing to remember per entity.**

| Rule | Value |
|---|---|
| Schema for business models | `core` |
| Table naming | `TBL_UPPER_SNAKE` — `core."TBL_STOCK_MOVEMENT"` |
| Lookup tables | `TBL_LK_UPPER_SNAKE` — `core."TBL_LK_ADJUSTMENT_REASON"` |
| Column naming | PascalCase, quoted — EF's default, **no snake_case plugin** |
| Applied by | one convention loop in `OnModelCreating`, not `[Table]` per entity |

`BaseEntity` implements `IBusinessEntity`, so every entity you derive from it is picked up by
`GxNamingConventions.ApplyGxTableNaming()`, which runs last in `ApplicationDbContext.OnModelCreating`.
Mark a table as a lookup by implementing `ILookupEntity` as well.

**What counts as a lookup.** One question: *does any code branch on this row's value?*

- **No** → it is a lookup (`TBL_LK_`). The application treats every row identically; it is code plus
  description and nothing else, and a client can safely add rows without a developer.
- **Yes** → it is not a lookup (`TBL_`), however small or dropdown-ish it looks. A tax code carrying
  rates and GL mappings is `TBL_`; so is an account that posting rules resolve to.

Deliberately a two-way split. Resist a third category for "master data with behaviour": the boundary
between a master and a transactional document is a spectrum (versioned price lists, BOMs with
lifecycle states), so a third category produces per-entity judgement calls and therefore
inconsistency. Having **no** lookup tables at all is a normal outcome — a status the code switches on
belongs in a C# enum stored as a string, not in a table.

**The convention ships dormant.** The template defines no entity that reaches it — the only three
deriving from `BaseAuditableEntity` are pinned below — so a freshly generated project has **no `core`
schema and no `TBL_` table at all**, and its migration contains no `EnsureSchema("core")`. The schema
appears the moment you add your first entity deriving from `BaseEntity` / `BaseAuditableEntity`. If
you generate a project, go looking for `core`, and find nothing, that is why.

**The template's own tables stay out of `core`.** Identity, `Tenants`, `AuditTrails`, `Documents`,
`PicklistSets`, `SecurityPolicies`, `DataProtectionKeys` and `__EFMigrationsHistory` keep their names
in the default schema, so opening pgAdmin shows a visible line between the framework's tables and
your business's, and so a template upgrade never hands an existing project a rename migration.
`Documents`, `PicklistSets` and `SecurityPolicies` derive from `BaseAuditableEntity` like any entity
of yours, and stay put only because their configurations call `ToTable(...)` explicitly — which the
convention yields to, on schema as well as name.

`GxTableNamingTests` and `TemplateTablesStayOutOfCoreTests` assert all of the above, including the
acronym handling (`UomConversion` → `TBL_UOM_CONVERSION`, not `TBL_U_OM_CONVERSION`) and that a
second `dotnet ef migrations add` produces an *empty* migration.

Four consequences, because they are not obvious and they bite late:

1. **Hand-written SQL must quote its identifiers.** PostgreSQL folds unquoted names to lowercase, so
   check constraints, index filters, triggers and plpgsql functions must be written
   `core."TBL_JOURNAL_ENTRY"`, `"Status"`, `"Debit"`. A single unquoted identifier is rejected at
   `database update`, so it fails loudly — but it fails *after* the model looks fine.

2. **Never apply `UseSnakeCaseNamingConvention()` to `ApplicationDbContext`.** `UseDatabase` takes a
   `snakeCaseNaming` flag and only `LogDbContext` sets it, because Serilog's PostgreSQL sink owns
   that table and writes snake_case columns. On the business context the plugin also rewrites EF's
   *migration history* model, creating `__EFMigrationsHistory` with `migration_id` /
   `product_version`; the day it is removed, EF queries `"MigrationId"` and fails with
   `42703: column "MigrationId" does not exist` — a database that can be neither migrated forward nor
   inspected, recoverable only by hand-altering EF's own bookkeeping table.

3. **`ApplyConfigurationsFromAssembly`'s namespace filter is case-sensitive.** Both contexts filter
   by `t.Namespace == …Namespace`, computed from a `typeof(...)` rather than a string literal, for
   exactly this reason: a hard-coded namespace whose casing does not match (`Ims.` vs `IMS.`) drops
   **every** configuration silently — no check constraints, no unique indexes, no row versions — and
   the application still builds and boots. Keep it type-derived, or assert the configuration count
   in a test.

4. **Seeders must be idempotent per item, never per "has anything been seeded".** This is the
   template's own seeding pattern, in `ApplicationDbContextInitializer.EnsureRoleAsync`: each role is
   reconciled by name and each permission by its natural key, so a grant added to
   `AdministratorPermissionRegistry` in a later release reaches databases provisioned before it. The
   shape to avoid is `if (await _roleManager.RoleExistsAsync(Roles.Admin)) return;` — correct on a
   fresh database, and thereafter silently delivering nothing new. `AssertNoDivergence` does not
   catch it: it compares the registry to the permission constants, not to what a database holds.
   Grants are added, never revoked, so a permission an operator granted at runtime survives a
   deployment; and a start that changes nothing logs nothing, so a line in the log means a grant
   genuinely appeared. `ProvisioningTests` covers the provision → revoke behind the initializer's
   back → provision → restored cycle, on both roles, plus a deleted role and an operator's extra
   grant. Follow the same pattern for anything you seed.

**On SQLite the schema is ignored, not honoured.** The provider drops it and creates a bare
`TBL_STOCK_MOVEMENT`; PostgreSQL and SQL Server both emit `EnsureSchema("core")` and a qualified
name. That is benign — but two same-named tables in different schemas would collide on SQLite.

#### Upgrading an existing PostgreSQL project

Adopting this standard renames every table and every column on a project that was previously
snake_cased — and those databases also carry a snake_cased `__EFMigrationsHistory`, which blocks EF
before it can apply anything at all. **This is a deliberate, tested exercise per project, not a
template bump.**

If the project has **no deployed database**, do not migrate: delete its migrations and regenerate a
single clean initial. Far better than layering hundreds of renames into the first page of its
history.

Otherwise:

1. Take a backup, and confirm it restores. Not "confirm it exists".
2. Rename EF's own bookkeeping first, or nothing else can run:
   ```sql
   ALTER TABLE "__EFMigrationsHistory" RENAME COLUMN migration_id  TO "MigrationId";
   ALTER TABLE "__EFMigrationsHistory" RENAME COLUMN product_version TO "ProductVersion";
   ```
3. Generate the rename migration against the new conventions and **read every operation** — confirm
   they are `RenameTable` / `RenameColumn`, never drop-and-create. EF will happily generate the
   latter, and it takes the data with it.
4. Re-point any hand-written SQL — views, functions, triggers, reports, scripts outside EF — at the
   new quoted identifiers. EF does not find these for you, and they fail at the point of use.
5. Rehearse the whole sequence on a restored copy of production before touching production.

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
- **Generating into a deep directory fails with MSB3021.** Windows' 260-character path limit is
  reached easily here: the solution nests `src/Migrators/Migrators.PostgreSQL/Migrations/` under your
  chosen name, and the build then writes longer paths still under `obj/`. The failure names a file
  copy rather than the real cause, so it reads as a mysterious build break. Generate into a short
  path — `C:\src\IMS` rather than a nested folder under `Documents` — or enable long paths
  (`git config --global core.longpaths true` plus the `LongPathsEnabled` registry setting).
- **`tests/Application.IntegrationTests` is pinned to SQL Server LocalDB, whatever `--Database` you
  chose.** Its own `appsettings.json` sets `mssql` and a `(localdb)\mssqllocaldb` connection string,
  and the wizard does not rewrite it. On a machine without LocalDB those 9 tests **fail** — they do
  not skip — while the rest of the suite passes. This is deliberate: they assert handler behaviour
  against a real SQL Server, and repointing them at whatever the wizard chose would quietly change
  what they prove. It is also why the newer HTTP harness, `tests/Server.UI.IntegrationTests`,
  defaults to SQLite: that one needs a database, not a particular one. To run these, install
  LocalDB, or point that file at any SQL Server you can reach.
- **`BaseEntity` is `IEntity<int>`, with no `long` variant.** A project with high-volume tables — a
  ledger, a movement history — cannot use the template base and must carry its own, which then has to
  implement `IEntity<T>` by hand before the pagination and specification helpers will accept it. It
  also puts that entity outside `IBusinessEntity`, so the GX naming convention skips it unless the
  project's own base implements the marker too.

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

Nine tests in `tests/Application.IntegrationTests` need SQL Server LocalDB and **fail** rather than
skip without it, whatever `--Database` you generated with. See Known limitations for why.

---

## Packaging the template

This section is for whoever maintains the template, not for a generated project.

The distributable is built from `GX.Blazor.Template.nuspec` by a project that compiles nothing and
exists only to carry three MSBuild properties:

```
dotnet pack build/pack.csproj -o .
```

That writes `GX.Blazor.Template.1.0.0.nupkg` to the repository root — around 745 entries and 1.1 MB.
No `nuget.exe` is needed and none is committed; `.gitignore` refuses one at the root.

Two things about that command are load-bearing.

**`dotnet pack GX.Blazor.Template.nuspec` also runs, and is wrong.** NuGet drops every file whose
name begins with a dot unless it is given `-NoDefaultExcludes`, and the `.nuspec` form of
`dotnet pack` has no way to accept that option: it is not MSBuild-driven, so
`-p:NoDefaultExcludes=true` is ignored, and a bare `-NoDefaultExcludes` is read as a second
`.nuspec` to pack. The package it produces is missing `.editorconfig`, `.gitignore`,
`.gitattributes` and `.dockerignore`, so every project generated from it loses its formatting rules
and its ignore list. Nothing fails, and nothing warns you at install time. `build/pack.csproj` sets
`NoDefaultExcludes` as an ordinary property, which is the only way to set it short of the full
`nuget.exe`.

**Every exclude pattern in the nuspec must begin with `**\`.** NuGet matches them against each
file's resolved *absolute* path, so a root-anchored pattern such as `docs\**` is compared against
`C:\...\GXTemplate\docs\...` and never matches. Such a pattern excludes nothing and reports nothing.

To install the built package locally and generate from it:

```
dotnet new install ./GX.Blazor.Template.1.0.0.nupkg
dotnet new gxblazor -n IMS -o IMS
dotnet new uninstall GX.Blazor.Template
```

**NuGet caches by id and version.** Re-packing after a change without bumping `<version>` and then
reinstalling gives you the *old* package back. Uninstall first, or bump the version.

**Pack asserts its own output.** After packing, `build/pack.csproj` extracts the `.nupkg` and fails
the build unless `content/.template.config/` contains `template.json`, `ide.host.json` and
`icon.png`. That check exists because "the package lacks what the repository has" has bitten three
times, and each file fails differently and silently: without `template.json` the package installs
and offers no template; without `ide.host.json` the template appears in Visual Studio with **no
parameter page**, because VS hides every symbol unless a host file says otherwise — while the CLI
shows them regardless, so CLI testing cannot catch it.

### Installing for Visual Studio

`dotnet new install .` from a clone registers the template as a **folder**, which is convenient for
CLI work but is not how Visual Studio expects to consume one — every other template it lists is a
versioned package. For Visual Studio, install the package:

```
dotnet new uninstall <path-to-clone>      # drop the folder registration first
dotnet pack build/pack.csproj -o .
dotnet new install ./GX.Blazor.Template.1.0.0.nupkg
```

Then restart Visual Studio. Its template cache is separate from the CLI's and is only rebuilt when
it notices a change, so a template installed or changed while VS was open may not be picked up until
it restarts.
