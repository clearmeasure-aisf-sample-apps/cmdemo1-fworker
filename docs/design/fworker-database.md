# The schema of the gateway's database: DbUp and plain .sql files

Design of 2026-10-29. Decision of the owner: build the fworker database schema with DbUp and plain .sql migration files, "just like
cmdemo1-workorders does it", instead of EF Core migrations and instead of Wolverine building its own tables at every start. "When the gateway
starts, we should know the schema is good."

## 1. What was wrong

- The gateway's tables (schema `factory`) were built by 8 EF Core migrations that `WolverineHostService` applied at start, and three tables were
  created by DDL that the stores ran on first use (`IdempotencyLedger`, `CopilotReservations`, `IngressDedup`).
- Wolverine's own tables (schema `wolverine`) were built and diffed by Weasel at every start (`AutoCreate.CreateOrUpdate`). On the tdd Azure SQL S0
  database that diff took about 80 s and needed a 300 s command timeout (#533).
- Nothing said, at start, whether the database was what the code expects.

## 2. The design

The schema is built by DbUp from plain .sql files, numbered and journaled the way cmdemo1-workorders does it. EF Core migrations are gone, and so
is Wolverine building its tables. The gateway applies the scripts at start, before Wolverine. It then checks that the catalog matches what the
scripts build, and `/ready` stays 503 until that check passes.

### Taken from workorders

- DbUp with dbup-sqlserver.
- Scripts in `scripts/Update/NNN_PascalDescription.sql`, run in ordinal file-name order.
- The journal `dbo.SchemaVersions`, set explicitly with `JournalToSqlTable("dbo", "SchemaVersions")`. The fworker database is dedicated, so dbo is free.
- DbUp's default of no transaction: each script owns its transaction (`BEGIN TRY / BEGIN TRANSACTION ... COMMIT / CATCH ROLLBACK; THROW`).
- A `QuietLog` that keeps only the information lines naming a `.sql` script, warnings and errors.
- `EnsureDatabase.For.SqlDatabase`, used only where creating the database is allowed (tests).

### Different, each for a reason

1. The scripts are embedded in the assembly, not read from the file system. The gateway is one container and no package folder exists at run time.
   The resources get `LogicalName="%(Filename)%(Extension)"`, so the journal stores bare file names, as workorders' file-system provider does.
2. DbUp runs inside the gateway at start, not as a separate Octopus "Migrate database" step. The owner asked for the start to know the schema, and
   the gateway's managed identity already has `db_ddladmin`. An Octopus step would need kit and infra work in cmdemo1-system. `FactorySchema` is a
   public library API, so such a step can be added later without a code change.
3. Every script is idempotent: guarded CREATE and ALTER, no DROP, TRUNCATE, DELETE or UPDATE of data. The same scripts run as no-ops on the live tdd
   database (built by EF and Weasel) and build an empty database in CI. Workorders relies on the journal alone.
4. Script numbers are unique. Workorders tolerates duplicates; a test here forbids them.
5. No Spectre console and no positional password. The gateway is the runner and tests call the library.
6. A process-wide lock (`sp_getapplock`, session owner, 60 s) is taken around the upgrade. At every deploy two gateway processes overlap during the
   lease hand-over, and DbUp has no lock of its own (EF's MigrateAsync had one).

## 3. Project layout

`src/Fworker.Database` (class library, net10.0, no project reference; packages dbup-sqlserver 6.0.16, Microsoft.Data.SqlClient 6.1.7 pinned to what
EF Core 10.0.14 brings, System.Security.Cryptography.Pkcs 10.0.14 for the advisories SqlClient 6.1.7 carries). The old `DbUp` 5.x meta-package of
workorders is not added: dbup-sqlserver 6.x is the provider.

| File | Content |
|---|---|
| `scripts/Update/001_...sql` to `013_...sql`, `100_...sql` | The schema |
| `schema-manifest.json` | What the scripts build, read from the catalog (embedded) |
| `SchemaNames.cs` | `factory`, `wolverine`, `dbo.SchemaVersions`, the lock resource |
| `FactorySchema.cs` | `Scripts`, `LatestVersion`, `UpgradeAsync`, `VerifyAsync`, `UpgradeAndVerifyAsync` |
| `SchemaManifest.cs` | The catalog reader and the comparer |
| `SchemaReport.cs` | Options, report, the exceptions |
| `QuietLog.cs` | The DbUp log, JSON lines, never the connection string |

Nothing in `src/` references it except the host (`gateway/Gateway.csproj`); the architecture test says so.

## 4. The scripts

Common rules: UTF-8 without BOM; every CREATE TABLE under `IF OBJECT_ID(...) IS NULL`, every CREATE INDEX under `IF NOT EXISTS (... sys.indexes ...)`,
every added column under `IF COL_LENGTH(...) IS NULL`, every schema created only if missing, procedures `CREATE OR ALTER`.

| Script | Content |
|---|---|
| 001 CreateFactorySchema | Schema `factory`; CatalogCopies, Correlations, ItemHistory, OpenInstances, ProjectionVersions, SessionMailbox (EF InitialCreate) |
| 002 AddDeliveredToAndMergedItems | DeliveredTo, MergedItems (EF DeliveryTables) |
| 003 AddDeliveryTrains | DeliveryTrains (EF DeliveryTrain) |
| 004 AddDeliveryShadowComparisons | DeliveryShadowComparisons (EF DeliveryShadow) |
| 005 AddReplayToDeliveryShadowComparisons | Column Replay with an unnamed default, as EF left it (EF DeliveryShadowReplay) |
| 006 AddWorkItems | WorkItems (EF WorkItemSaga) |
| 007 AddCapacityLedgers | CapacityLedgers (EF CapacityLedger) |
| 008 AddMergeTrains | MergeTrains (EF MergeTrain) |
| 009 AddIdempotencyLedgerAndCopilotReservations | The DDL `SqlCommandSchema` ran on first use |
| 010 AddIngressDedup | The DDL `SqlIngressDedup` ran on first use |
| 011 CreateWolverineMessageStore | Wolverine's tables, indexes, foreign key, table type and 5 procedures, generated, never written by hand |
| 012 AddQcEnvironments | QcEnvironments (W9.0 for W9.1: the QC gate per repository and pull request) |
| 013 AddTranscripts | Transcripts (W9.0 for W9.8: runtime session transcripts) |
| 100 AddIncidents | Incidents (W8.0 for W8.1: the incident saga of one application and environment; the timeline is in ItemHistory) |
| 140 AddIdempotencyClaimsAndReservationMode | IdempotencyClaims (W9.10c: the pending claim of a command key) and the column Mode of CopilotReservations (launch, resume). Numbers 130 to 169 are reserved per work item of W9.10 (the plan), so the numbers are ascending, not contiguous |

The DDL of 001 to 008 is what `dotnet ef migrations script <previous> <this>` printed, with the history inserts removed and guards added. Column types,
lengths, nullability, identity, key names (`PK_<Table>`), index names, filters and the default are exactly EF's, so the tables EF built on tdd match.
That SQL is kept as a test fixture (`tests/fixtures/ef-migrations-2026-10-29/ef-migrations.sql`).

Script 011 is `ToDatabaseScript()` of Wolverine's message store, built (not started) from `WolverineHostService.BuildHostForSchemaExport`, which is
`BuildHost` with a dummy connection string, so the exported DDL always comes from the gateway's real options. It has no TRY/CATCH, because
`CREATE OR ALTER PROCEDURE` must start its batch. Idempotence replaces the transaction: a script is journaled only after it succeeded, so a partial run
is completed by the next one. The text of the procedures contains DELETE and UPDATE (Wolverine's own statements); no script changes a row.

Future scripts: `014_` onward, the next free number, except for the blocks reserved for the parallel waves of the implementation plan (W8.1: 100-109, W8.2: 110-119, W8.3: 120-129; `DatabaseScriptsTests` requires 001-099 to be contiguous and every number to be unique). DbUp runs them in ordinal order of the name, so 013 runs before 100. Every future script is expand-only (add tables, add nullable or defaulted columns, add indexes), so
that the previous gateway process, still running during the lease hand-over, keeps working. A contraction (dropping a column or table) is a later
script, shipped once no running image uses the object.

## 5. EF Core is a query mapper

`FactoryDbContext` and `Entities.cs` stay with the same Fluent model; the stores read and write through it and `Revision` stays the optimistic
concurrency token. The 8 migrations, the snapshot, the design-time factory and the `Microsoft.EntityFrameworkCore.Design` package are deleted. Nothing
calls `Database.MigrateAsync`, `EnsureCreated` (tests only) or `GetMigrations`. A model change now means a new `NNN_` script, and CI fails until the script
matches (`SchemaMatchesEfModelTests`).

The runtime DDL of `SqlCommandSchema` and `SqlIngressDedup` is removed, not kept as no-ops: one owner per table. Callers that can run before the
Wolverine host is up wait for the schema: `CopilotReconcileService` skips its pass, the saga's views answer an empty set, and the shadow run's
housekeeping skips, until `FactorySchemaStatus.IsGood`. Handlers run only inside the host, which starts after the schema.

## 6. Wolverine never builds or diffs its tables

In `WolverineHostService.BuildHost`:

- `w.AutoBuildMessageStorageOnStartup = AutoCreate.None` makes `WolverineRuntime.tryMigrateStorage` skip the Weasel diff (GH-4166).
- `PersistMessagesWithSqlServer(cs, "wolverine").OverrideAutoCreateResources(AutoCreate.None)` stops the on-demand paths (`IMessageStoreAdmin.MigrateAsync`,
  storage clear-handled).
- What Wolverine 6.56 still runs at start is `AssertStorageProvisionedAsync`: four `select 1 from wolverine.<table> where 1 = 0` probes, in milliseconds.
  It is an existence check, not a build.
- `IHost.SetupResources()` and `ResetResourceState()` are never called: they always migrate with CreateOrUpdate.
- The 300 s command timeout of #533 is removed; the message store gets the connect timeout only.
- `WolverineHostOptions.Schema` is the constant `SchemaNames.Wolverine`: the scripts write `[wolverine]` literally.

## 7. Where it runs, and what "the schema is good" means

`WolverineHostService.RunAsync`, once per start attempt and again whenever the node begins anew after re-taking the lease:

1. `FactorySchemaStatus.Upgrading(attempt)`.
2. Only where `FACTORY_DB_CREATE=1` (tests only): `EnsureDatabase`.
3. The application lock `fworker-schema-upgrade` on a connection of its own.
4. DbUp `PerformUpgrade` over the embedded scripts (with nothing pending, one journal SELECT).
5. `VerifyAsync` on a new connection.
6. The lock is released.
7. Good: `FactorySchemaStatus.Good`, then the Wolverine host is built and started with `AutoCreate.None`.
8. Not good: `SchemaNotGoodException`, handled like any start failure (the retry loop and backoff are unchanged).

The schema is good when:

- (a) the journal holds every embedded script name (an entry this image does not ship, after a rollback to an older image, is reported as `ahead`, not a failure);
- (b) the catalog matches `schema-manifest.json`. One batch reads `sys.schemas`, `sys.tables`, `sys.columns`, `sys.types`, `sys.default_constraints`,
  `sys.indexes`, `sys.index_columns` and `sys.sql_modules` for the schemas `factory` and `wolverine` (about 250 rows, one round trip). Compared per table:
  presence; per column the type, max length, precision, scale, nullability, identity and default expression (whitespace and parentheses normalised, the
  constraint name ignored); per index or key the name, uniqueness, primary key, ordered key columns, included columns and filter; per procedure the
  SHA-256 of its definition (line endings normalised). Something missing or different is a `SchemaDifference` (an object name, never a value) and fails
  the check. Extra tables, columns, indexes or procedures (for example `__EFMigrationsHistory`, or objects of a newer image) are counted in `extra` and do not fail.

The manifest is generated from a database built by the scripts (`FWORKER_UPDATE_SCHEMA_MANIFEST=1`), so it is by construction what the scripts produce.

### Reporting

- `/ready`, order of checks: replaced, prompts, revocations, then schema. Where `FactorySchemaStatus.Required` is true (`FACTORY_DB_CONNECTION` is set and
  **v2 acts** in the environment, `Engine.V2Acts`: the v2 CI gateways, and tdd after the cutover; W9.10o narrowed it from "the environment runs the Wolverine host", so
  in shadow, where v1 acts and needs no table, a database that is down, slow to start or failing a v2-only script does not keep `/ready` at 503 and no release is mistaken for
  a failed one; the schema then shows in `/api/v1/status` only) and the schema has not been verified good by this process, `/ready` answers
  503 `{"ready":false,"waitingFor":"schema","state":"upgrading"|"failed","attempts":N,"since":"...","failedScript":...,"lastFailure":...}`. Where it is not
  required (uat, prod, image-start, every test without a database), `/ready` is exactly what it was. After the first good verification `/ready` no longer
  depends on the database for the life of the process. This deliberately reverses the W2.2 rule "/ready does not depend on the database", for the schema
  only and as the owner asked. Container Apps has no probe on `/ready`, so traffic is unaffected; the release rule (`/ready` 200) now proves the schema.
- `/api/v1/status`: a block `schema` next to `wolverine`, where the host runs (shadow or v2): `state`, `journal`, `shipped`, `latest`, `applied`, `pending`, `ahead`,
  `differences` (at most 20), `extra`, `attempts`, `since`, `verifiedAt`, `durationMs`, `failedScript`, `lastFailure`.
- `/_build`: `schemaVersion` (`"013"` today), the highest number the image ships, like workorders' `databaseVersion`.
- A failure is a type, a SQL error number and a script name, never a message. One JSON line is logged: `{"gateway":"schema","event":"failed","detail":"<type> <script>"}`.
- The Wolverine host is never started on a bad schema, so v2 and shadow work waits. The v1 gateway (sweep, workers, plant) keeps running. A difference
  does not heal on retry: it stays failed and visible, which the release rule treats as an outage (find the missing script, or revert the pull request).

## 8. The live tdd database: no baseline step

The scripts run on tdd as no-ops and DbUp journals them. Nothing is marked as run (no workorders-style `baseline` command), and nobody does anything by
hand:

- tdd is a shadow environment where v2 never acted, so `IdempotencyLedger` and `CopilotReservations` (009) are probably missing and `IngressDedup` (010)
  exists only if a webhook was claimed. Marking scripts as run would journal 009 and 010 without creating the tables, and the check would then fail.
- Only the gateway's managed identity can reach the database from a session.
- Running the guarded scripts proves the guards against the real database on the first start; `LegacyDatabaseUpgradeTests` proves them in CI first.

First start of the DbUp image on tdd: the lock; `dbo.SchemaVersions` is created (`db_ddladmin` covers it); 001 to 008 find every EF table and index and
create nothing; 005 finds Replay; 009 and 010 create what is missing; 011 finds every Wolverine table, index and foreign key and rewrites the 5
procedures in place with `CREATE OR ALTER` (their text is the export Weasel would have written, so behaviour is unchanged; the old process tolerates the
short schema lock); all 11 names are journaled; the manifest check runs.

`__EFMigrationsHistory` is kept for now, untouched and read by nothing, so the rollback of CLAUDE.md ("revert the pull request") works: the reverted
EF image finds all 8 migrations applied and applies nothing, and its Weasel diff finds the Wolverine schema it expects. `dbo.SchemaVersions` stays
behind harmlessly. Dropping the history is a later expand/contract step (`0NN_DropEfMigrationsHistory.sql`) at least one tdd release later and with the
owner's word. A database built from nothing by DbUp never has it.

## 9. Tests

All fresh databases of the SQL tests are built by the scripts (`TestDatabase.CreateAsync`, one helper per test project), and the Wolverine hosts of the
tests run with `AutoCreate.None`, so every test runs on tables the scripts built.

| Test | Proves |
|---|---|
| `DatabaseScriptsTests` (no SQL) | Names, unique and ascending numbers; the embedded set is the files; no DROP or TRUNCATE; DELETE or UPDATE only in procedure bodies; every CREATE and ALTER guarded; procedures `CREATE OR ALTER`; UTF-8 without BOM |
| `SchemaMatchesEfModelTests` | A database built by the scripts equals the one `EnsureCreated` builds from `FactoryDbContext` (columns, keys, indexes); every other table is one of the four raw-SQL tables |
| `SchemaManifestTests` | The manifest is what the scripts build; the scripts run twice with no error and no change; drift (dropped column, index, table, altered procedure, removed journal row) is found, an extra table is tolerated; two or three processes in parallel journal each script once; a failing script is not journaled; the log never has the connection string; the database is created only on request |
| `LegacyDatabaseUpgradeTests` | The rehearsal of tdd: EF migrations as they ran, the old runtime DDL of one table, Wolverine's tables built by Weasel, with rows; the scripts succeed, journal all, create the two missing tables, keep every row and leave a schema equal to the fresh one |
| `WolverineStorageScriptTests` | `TheExportMatchesTheGolden` (no SQL): the export for the solo and balanced profiles, with and without the runtime commands, is the same and equals `tests/Gateway.Tests/golden/wolverine-storage.sql`; script 011 is that golden with its header; `TheScriptsBuildWolverinesStorage` (SQL): Weasel's full diff of its model against a database built by the scripts is empty |
| `WolverineHostSqlServerTests`, `WolverineHostTests` | The journal holds exactly the scripts; no `__EFMigrationsHistory`; the 18 factory tables and Wolverine's; `/ready`, `/api/v1/status` and `/_build` as above; a blocking schema keeps `/ready` at 503 `upgrading`, a failing one gives `failed` with the script and the host is never built; the default command timeout |
| `ArchitectureTests` | The project table; `SchemaIsBuiltOnlyByTheScripts`; `WolverineNeverBuildsItsStorage` |

Rewrite switches: `FWORKER_WRITE_WOLVERINE_SQL=1` (golden and script 011), `FWORKER_UPDATE_SCHEMA_MANIFEST=1` (manifest).

The v2 Tier 1 job (`tests/scenarios/run.ps1 -Engine v2`) sets `FACTORY_DB_CREATE=1` next to `FACTORY_DB_CONNECTION`, so the gateway's DbUp creates the per-process
database and applies the scripts. The variable is read only from the environment and never set in configuration or overrides (a test asserts it): tdd's
identity cannot CREATE DATABASE, and bicep creates the database.

## 10. How a Wolverine upgrade adds a script

1. Bump every `WolverineFx*` package to one version (`WolverinePackagesShareOneVersion` enforces it).
2. Run Gateway.Tests. If the storage DDL changed, `TheExportMatchesTheGolden` fails and shows the difference.
3. Generate the delta against a database built by the current scripts: `FWORKER_WRITE_WOLVERINE_DELTA=<file> dotnet test tests/Gateway.Tests --filter WriteWolverineDelta` (needs `FACTORY_TEST_SQL`).
4. Save it as `scripts/Update/0NN_UpgradeWolverineTo<Version>.sql`. Add guards to any unguarded ALTER (Weasel's delta does not guard `ALTER TABLE ADD`). Keep it expand-only.
5. Rewrite the golden (`FWORKER_WRITE_WOLVERINE_SQL=1`; it rewrites script 011 only while 011 is the only Wolverine script) and the manifest.
6. `TheScriptsBuildWolverinesStorage`, `TheScriptsAreIdempotent` and `DatabaseScriptsTests` must pass.
7. Read the Wolverine release notes for column-default changes: Weasel does not compare defaults, but the manifest does.

An EF model change follows the same steps: write `0NN_<Change>.sql` by hand, then rewrite the manifest. `SchemaMatchesEfModelTests` fails until the script matches.

## 11. Risks

- `/ready` waits for the schema where a database is configured **and v2 acts** (W9.10o; before it, also in shadow). Where v2 acts a database outage during a start keeps `/ready` at 503
  with `waitingFor: schema`: that calls for `FACTORY_V2_OFF` or a database fix, not for a revert of an unrelated pull request. A `FACTORY_DB_CONNECTION` that is set and is not a
  connection string is treated as not configured (`/api/v1/status` `database.malformed`).
- The first tdd start depends on the guards matching what EF and Weasel built there; nobody has read tdd's catalog for this design. The rehearsal is in CI. If tdd differs
  anyway, the check reports it in `/api/v1/status` `schema.differences` and `/ready` stays 503; roll back by reverting the pull request.
- The manifest compares default expressions and procedure hashes. Script 011 rewrites the 5 procedures on tdd, so their hashes become the scripts'.
- Wolverine with `AutoCreate.None` still runs four existence probes at start; its columns, indexes and procedure bodies are checked only by the manifest at start and by Weasel's assert in CI.
- The DDL depends on configuration: a Durability option enabled later (message deduplication, stale-time columns, dead-letter expiration, recurring messages) changes the export.
  The golden test catches it only because it builds the gateway's real options.
- Weasel's delta for a future upgrade does not guard `ALTER TABLE ADD`; the idempotence test catches an unguarded script.
- Every future script must be expand-only, because the previous process keeps running on the same database during the lease hand-over.
- dbup-sqlserver 6.0.16 with Microsoft.Data.SqlClient pinned to 6.1.7: check `dotnet list package --include-transitive` after a change of either.

## 12. Deviations from workorders, for the owner

Scripts are embedded rather than read from a package folder; the migration runs at gateway start rather than as an Octopus step; scripts are idempotent
rather than journal-only; there is no console tool. Each is explained above.
