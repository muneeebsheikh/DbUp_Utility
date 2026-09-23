# DB Migration Tool

A console app that uses [DbUp](https://dbup.readthedocs.io/) to run SQL scripts against SQL Server. Configuration lives in `App.config`.

## Release / CI usage (pipeline-safe)

Do **not** rely on interactive `Press Enter`. For Azure Pipelines Release (or any CI):

```bash
DBMigragtionTool.exe ^
  --connectionString "%DBUP_CONNECTION_STRING%" ^
  --scriptsPath "%DBUP_SCRIPTS_PATH%" ^
  --yes
```

Or set environment variables and run with `--yes`:

| Variable | Purpose |
| --- | --- |
| `DBUP_CONNECTION_STRING` | SQL connection (prefer Key Vault / variable group) |
| `DBUP_SCRIPTS_PATH` | One or more script folders (`;` or `,`) |
| `DBUP_NON_INTERACTIVE=true` | Force non-interactive |
| `CI=true` or `TF_BUILD` | Treated as non-interactive |

Flags:

- `--yes` / `--non-interactive` â€” skip Press Enter; never hang on redirected stdin
- `--connectionString` â€” overrides App.config / positional arg
- `--scriptsPath` / `--scriptsRoot` â€” overrides App.config `ScriptsPath`
- Positional first arg still accepted as connection string (local convenience)

Exit codes: `0` success, non-zero failure.

In non-interactive mode the tool **fails** if connection string or ScriptsPath is missing (no silent localhost default).

> Deploy-pack / SP name expansion (Created/Altered â†’ Stored Procedures) is planned next for CUSTOMERINSIGHT `dbo\Scripts\<sprint>\PBI-*\Deploy_*.sql`. Until then, point `ScriptsPath` at folders of real runnable `.sql` only.

## How to run

```bash
dotnet run
```

You can override the connection string from the command line:

```bash
dotnet run -- "Data Source=localhost;Initial Catalog=DevcaswellTest;Integrated Security=True;Encrypt=True;Trust Server Certificate=True"
```

The app lists the scripts it will run and waits for Enter before applying them.

## Configuration (`App.config`)

All settings are under `<appSettings>`:

```xml
<configuration>
  <appSettings>
    <add key="ConnectionString" value="Data Source=localhost;Initial Catalog=DevcaswellTest;Integrated Security=True;Encrypt=True;Trust Server Certificate=True" />
    <add key="ScriptsPath" value="D:\path\to\Tables;D:\path\to\Stored Procedures" />
    <add key="ScriptFiles" value="" />
    <add key="ExcludeFiles" value="" />
    <add key="IncludeSubDirectories" value="true" />
  </appSettings>
</configuration>
```

### `ConnectionString`

SQL Server connection string used when none is passed on the command line.

Priority:

1. First command-line argument
2. `ConnectionString` in `App.config`
3. Built-in localhost default

### `ScriptsPath`

One or more folders that contain `.sql` files. Separate paths with `;` or `,`.

Single folder:

```xml
<add key="ScriptsPath" value="D:\Repository\Git Repos\CI_Workspace\CI_OnePlatform\database\CUSTOMERINSIGHT\dbo\Tables" />
```

Multiple folders:

```xml
<add key="ScriptsPath" value="D:\...\dbo\Tables;D:\...\dbo\Stored Procedures" />
```

Relative paths are resolved from the output folder (`bin\Debug\net10.0`). Absolute paths are used as-is.

Every listed folder must exist. If any path is missing, the app stops.

### `ScriptFiles`

Optional list of specific SQL files. Separate names with `,` or `;`.

Empty — run **all** `.sql` files in the configured folders as `RunOnce` (skipped if already in `SchemaVersions`):

```xml
<add key="ScriptFiles" value="" />
```

Specific files — only these files are selected. They run in two steps:

1. **RunOnce** — executed and journaled in `SchemaVersions` if they have not run before
2. **Re-run at the end** — the same files run again (`RunAlways`), even if they are already in `SchemaVersions`

```xml
<add key="ScriptFiles" value="Table1.sql, Table2.sql" />
```

You can also use a relative path if the same file name exists in more than one subfolder:

```xml
<add key="ScriptFiles" value="dbo\Tables\Customer.sql" />
```

The re-run step requires scripts that are safe to execute twice (for example `IF NOT EXISTS` / `CREATE OR ALTER`).

If a name is in both `ScriptFiles` and `ExcludeFiles`, it is skipped.

### `ExcludeFiles`

Optional list of SQL files to skip. Separate names with `,` or `;`. Empty means nothing is excluded.

```xml
<add key="ExcludeFiles" value="" />
```

Skip specific files while running everything else:

```xml
<add key="ExcludeFiles" value="001_Activitylog.sql, SeedData.sql" />
```

Relative paths work the same as `ScriptFiles` when the same file name exists in more than one folder:

```xml
<add key="ExcludeFiles" value="dbo\Tables\Customer.sql" />
```

Excluded files are omitted from both the `RunOnce` pass and the end re-run.

### `IncludeSubDirectories`

- `true` — also scan subfolders under each `ScriptsPath`
- `false` — only the top-level folder

```xml
<add key="IncludeSubDirectories" value="true" />
```

## How scripts are tracked

DbUp records executed scripts in `[SchemaVersions]`.

| `ScriptFiles` | Behavior |
| --- | --- |
| Empty | `RunOnce` only. A script is skipped if it was already executed. |
| Has names | `RunOnce` first (new scripts are journaled). Then those same files are re-run at the end. |

Journal names use the assembly prefix, matching earlier embedded-resource runs:

```text
DBMigragtionTool.001_Activitylog.sql
```

The tool also treats a script as already run if the journaled name ends with the same file name (for example `DBMigragtionTool.001_Activitylog.sql` matches `001_Activitylog.sql`).

## Typical setups

**Apply every pending script once (prefer Deploy packs / Scripts folders for CUSTOMERINSIGHT — not full Tables+SP trees as the production model):**

```xml
<add key="ScriptsPath" value="D:\...\dbo\Tables;D:\...\dbo\Stored Procedures" />
<add key="ScriptFiles" value="" />
<add key="ExcludeFiles" value="SeedData.sql" />
```

**Run a stored procedure as RunOnce, then re-run it at the end:**

```xml
<add key="ScriptsPath" value="D:\...\dbo\Stored Procedures" />
<add key="ScriptFiles" value="usp_GetCustomer.sql" />
```

## Deploy-pack mode (CUSTOMERINSIGHT)

Point at the `dbo` root so the tool can find `Scripts` and `Stored Procedures`:

```bash
DBMigragtionTool.exe ^
  --connectionString "%DBUP_CONNECTION_STRING%" ^
  --dboRoot "D:\...\CUSTOMERINSIGHT\dbo" ^
  --sprint 2026.19.00 ^
  --yes
```

Useful flags:

- `--pbi 820932,813662` � limit to those PBI folders
- `--includePriorUnreleased` � with `--sprint`, also include earlier sprint folders (SchemaVersions still skips already-run Scripts)
- `--stageOnly` � expand packs to `--stageDir` without touching the database (safe for bots/review)
- `--storedProceduresRoot` � override SP folder if not under dbo

Behavior:

1. Parse each `Deploy_*.sql` Created/Altered comment lists
2. Resolve SP bodies under Stored Procedures and rewrite `CREATE PROCEDURE` to `CREATE OR ALTER PROCEDURE`
3. Per PBI: RunAlways expanded SPs, then RunOnce Scripts-section SQL (and any extra non-Deploy `.sql` in the PBI folder)
4. PRINT-only Deploy bodies are **not** journaled as success; SPs still apply when listed


## Logging

Console plus a log file. Path priority: --logFile / --logPath ? DBUP_LOG_FILE ? App.config LogFilePath ? default Logs\\dbup-yyyyMMdd-HHmmss.log next to the exe.

If LogFilePath is a directory (e.g. Logs), a timestamped file is created inside it.

