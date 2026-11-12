# GXTemplate: working rules

This repository is the GX Blazor template (`GX.Blazor.Template`, Clean Architecture on Blazor Server, EF Core,
PostgreSQL). Applications are generated from it with `dotnet new`. This file is the template repository's own;
it is excluded from the package, so a generated project keeps its own `CLAUDE.md`.

## Read first

1. `docs/gx-conventions.md`: the GX conventions as MNEFleets practises them. The template follows them.
2. The README sections that describe the mechanisms: "Database naming", "Modules", "Posting transactions",
   "Money and quantities", "Concurrency", "Posted records", "Branches", "Approvals", "Architecture tests".
3. The latest pass report in `docs/passes/` (pass 54 onwards; earlier passes are in `GXTemplate-passes/`).

## How a pass works

- **Plan first.** Write `docs/passes/passNN-<name>-plan.md`. It covers what is wrong today (verified in code, with
  file and line), the target behaviour, the components, the order of work, the tests, the conflicts, and the
  decisions for the owner. Print the decisions and stop. Implement only after the owner returns the plan, with
  any amendments.
- **Every plan has a section "GX conventions touched"** (owner decision D7, pass 60c). It cites
  `docs/gx-conventions.md` and has one table row per convention the pass affects. Each row ends in Conforms,
  Departs, Not decided or No conflict.
  - **A departure needs an owner decision that names it as a departure from a GX convention,** and the row
    names that decision (`D3`, `Q-F`, "owner decision …").
  - A plan that changes a convention without saying so is not ready for the owner.
  - `PassPlanRulesTests` checks every plan from `docs/passes/.gx-plan-rules` onwards.
- **Then the report,** `docs/passes/passNN.md`: suites before and after, what was built, additions marked (PNN)
  that the plan did not say, what was not done, the commit hash and tag.
- **One local commit per pass.** Do not push unless the owner asks.
- **Pack only after the commit.** `build/pack.csproj` stamps `git rev-parse HEAD`.
- **Release tags are `gx-vX.Y.Z`.** Push a tag by name (`git push origin gx-v1.8.0`), never `git push --tags`: the
  repository holds about 881 upstream template tags.

## Rules for the code

- **PostgreSQL only.** Npgsql-only code goes under `#if (UsePostgreSql)`; the other providers must still build.
- **The pinned suites pass unmodified, every pass:** `SaveChangesInterceptorRegressionTests`,
  `InterceptorOrderingTests` (exactly two `ISaveChangesInterceptor`s), `TransactionalAuditTests`, and the publisher
  suites. New save-time rules go in `ApplicationDbContext.SaveChanges` (`DataRules`), not in a third interceptor.
- **Database naming** (`GxNamingConventions`, `GxSnakeCaseConvention`):
  - business tables are `core."TBL_<PLURAL>"`, with snake_case columns;
  - lookups are `TBL_LK_` on `BaseLkEntity`;
  - keys are `int` for master data and lookups, `long` for documents, lines and ledgers, and never a Guid;
  - template tables keep their names in `public`;
  - `tests/Application.UnitTests/Model/tables.approved.txt` lists every table, and changes in review.
- **An entity a test declares is never a plain business noun.** Prefix it with what it tests (`GuardProbeWarehouse`,
  `TestLedgerRow`, `SampleWidget`). A test model built on the real context also holds the application's
  entities (T11).
- **Template tests create branches with `TestBranches`.** Use floor-safe codes and unique names, and insert fixtures
  directly. An application's tightened branch rules must not fail them (T9).

## This machine

- **Test database:** `GX_TEST_PG=Host=127.0.0.1;Port=5432;Username=postgres`. Set it per command. Never drop a
  database.
- **Line endings:** many `src` files are CRLF. Use the Edit tool rather than multi-line `sed`/`perl` patterns, and
  grep after every scripted edit.
- **Memory:** run `dotnet build-server shutdown` between heavy steps.
- **The smoke test** is `powershell -NoProfile -ExecutionPolicy Bypass -File tooling/smoke-generate.ps1 -WorkRoot C:\gx…`.
  It needs a short work path.
