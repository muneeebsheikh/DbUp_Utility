using System.Configuration;

namespace DBMigragtionTool;

internal sealed class CliOptions
{
    public string? ConnectionString { get; init; }
    public string? ScriptsPath { get; init; }
    public string? DboRoot { get; init; }
    public string? StoredProceduresRoot { get; init; }
    public string? StageDir { get; init; }
    public string? LogFilePath { get; init; }
    public IReadOnlyList<string> Sprints { get; init; } = [];
    public IReadOnlyList<int> PbiIds { get; init; } = [];
    public bool IncludePriorUnreleased { get; init; }
    public bool PackMode { get; init; }
    public bool StageOnly { get; init; }
    public bool NonInteractive { get; init; }
    public bool ShowHelp { get; init; }

    public static CliOptions Parse(string[] args)
    {
        string? connectionString = null;
        string? scriptsPath = null;
        string? dboRoot = null;
        string? storedProceduresRoot = null;
        string? stageDir = null;
        string? logFilePath = null;
        var sprints = new List<string>();
        var pbiIds = new List<int>();
        var includePrior = false;
        var packMode = false;
        var stageOnly = false;
        var nonInteractive = false;
        var showHelp = false;
        string? positionalConnection = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (IsFlag(arg, "--help", "-h", "/?"))
            {
                showHelp = true;
                continue;
            }

            if (IsFlag(arg, "--yes", "-y", "--non-interactive", "--noninteractive"))
            {
                nonInteractive = true;
                continue;
            }

            if (IsFlag(arg, "--pack"))
            {
                packMode = true;
                continue;
            }

            if (IsFlag(arg, "--stageOnly", "--stage-only"))
            {
                stageOnly = true;
                packMode = true;
                continue;
            }

            if (IsFlag(arg, "--includePriorUnreleased", "--include-prior-unreleased"))
            {
                includePrior = true;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var conn, "--connectionString", "--connection-string", "--connection"))
            {
                connectionString = conn;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var scripts, "--scriptsPath", "--scripts-path", "--scriptsRoot", "--scripts-root"))
            {
                scriptsPath = scripts;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var dbo, "--dboRoot", "--dbo-root"))
            {
                dboRoot = dbo;
                packMode = true;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var spRoot, "--storedProceduresRoot", "--stored-procedures-root", "--spRoot", "--sp-root"))
            {
                storedProceduresRoot = spRoot;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var stage, "--stageDir", "--stage-dir"))
            {
                stageDir = stage;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var logFile, "--logFile", "--log-file", "--logPath", "--log-path"))
            {
                logFilePath = logFile;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var sprintVal, "--sprint"))
            {
                sprints.AddRange(SplitList(sprintVal));
                packMode = true;
                continue;
            }

            if (TryGetValue(args, ref i, arg, out var pbiVal, "--pbi"))
            {
                foreach (var part in SplitList(pbiVal))
                {
                    var digits = new string(part.Where(char.IsDigit).ToArray());
                    if (!int.TryParse(digits, out var id))
                    {
                        throw new ArgumentException($"Invalid --pbi value: {part}");
                    }

                    pbiIds.Add(id);
                }

                packMode = true;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                throw new ArgumentException($"Unknown argument: {arg}. Use --help for usage.");
            }

            positionalConnection ??= arg;
        }

        connectionString ??= positionalConnection;
        connectionString ??= GetEnv("DBUP_CONNECTION_STRING");
        scriptsPath ??= GetEnv("DBUP_SCRIPTS_PATH");
        dboRoot ??= GetEnv("DBUP_DBO_ROOT");
        storedProceduresRoot ??= GetEnv("DBUP_SP_ROOT");
        stageDir ??= GetEnv("DBUP_STAGE_DIR");
        logFilePath ??= GetEnv("DBUP_LOG_FILE") ?? GetEnv("DBUP_LOG_PATH");

        if (!string.IsNullOrWhiteSpace(dboRoot))
        {
            packMode = true;
        }

        if (IsTruthyEnv("CI") || IsTruthyEnv("DBUP_NON_INTERACTIVE") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))
        {
            nonInteractive = true;
        }

        return new CliOptions
        {
            ConnectionString = connectionString,
            ScriptsPath = scriptsPath,
            DboRoot = dboRoot,
            StoredProceduresRoot = storedProceduresRoot,
            StageDir = stageDir,
            LogFilePath = logFilePath,
            Sprints = sprints,
            PbiIds = pbiIds,
            IncludePriorUnreleased = includePrior,
            PackMode = packMode,
            StageOnly = stageOnly,
            NonInteractive = nonInteractive,
            ShowHelp = showHelp
        };
    }

    public string ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return ConnectionString;
        }

        var fromConfig = ConfigurationManager.AppSettings["ConnectionString"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }

        if (NonInteractive)
        {
            throw new InvalidOperationException(
                "Connection string required in non-interactive mode. Pass --connectionString or set DBUP_CONNECTION_STRING.");
        }

        return "Data Source=localhost;Initial Catalog=DevcaswellTest;Integrated Security=True;Encrypt=True;Trust Server Certificate=True";
    }

    public string ResolveScriptsPathSetting()
    {
        if (!string.IsNullOrWhiteSpace(ScriptsPath))
        {
            return ScriptsPath;
        }

        if (!string.IsNullOrWhiteSpace(DboRoot))
        {
            return Path.Combine(DboRoot, "Scripts");
        }

        var fromConfig = ConfigurationManager.AppSettings["ScriptsPath"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }

        throw new InvalidOperationException(
            NonInteractive
                ? "ScriptsPath required in non-interactive mode. Pass --scriptsPath / --dboRoot or set DBUP_SCRIPTS_PATH."
                : "ScriptsPath is not configured in App.config.");
    }

    public string ResolveStoredProceduresRoot()
    {
        if (!string.IsNullOrWhiteSpace(StoredProceduresRoot))
        {
            return StoredProceduresRoot;
        }

        if (!string.IsNullOrWhiteSpace(DboRoot))
        {
            return Path.Combine(DboRoot, "Stored Procedures");
        }

        throw new InvalidOperationException(
            "Stored Procedures root required for pack mode. Pass --storedProceduresRoot or --dboRoot.");
    }

    public string ResolveStageDir()
    {
        if (!string.IsNullOrWhiteSpace(StageDir))
        {
            return StageDir;
        }

        return Path.Combine(Path.GetTempPath(), "DBMigragtionTool-stage", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
    }

    public string ResolveLogFilePath()
    {
        string? configured = LogFilePath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = ConfigurationManager.AppSettings["LogFilePath"]
                ?? ConfigurationManager.AppSettings["LogPath"];
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
            return Path.Combine(logsDir, $"dbup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }

        // Directory-only config: create timestamped file inside it
        var trimmed = configured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var looksLikeDir = !Path.HasExtension(trimmed) || Directory.Exists(configured);
        if (looksLikeDir && (Directory.Exists(configured) || !Path.HasExtension(trimmed)))
        {
            var dir = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"dbup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            DBMigragtionTool — DbUp SQL Server runner

            Legacy (folder of .sql files):
              DBMigragtionTool --connectionString <cs> --scriptsPath <paths> --yes

            CUSTOMERINSIGHT Deploy packs:
              DBMigragtionTool --connectionString <cs> --dboRoot <dbo> --sprint 2026.19.00 --yes
              DBMigragtionTool --dboRoot <dbo> --sprint 2026.20.00 --includePriorUnreleased --pbi 820932 --yes

            Options:
              --connectionString          SQL connection string
              --scriptsPath/--scriptsRoot Script folder(s); separate with ; or ,
              --dboRoot                   CUSTOMERINSIGHT dbo root (Scripts + Stored Procedures)
              --storedProceduresRoot      Override SP folder
              --sprint                    Sprint folder name(s)
              --pbi                       PBI id(s), e.g. 820932 or PBI-820932
              --includePriorUnreleased    With --sprint, also include earlier sprint folders
              --stageDir                  Where to write expanded pack scripts
              --logFile/--logPath          Log file or directory (also App.config LogFilePath)
              --pack                      Force Deploy-pack mode
              --stageOnly                 Expand packs to stageDir only (no DB)
              --yes/--non-interactive     Skip Press Enter (Release/CI)
              --help                      Show this help

            Environment:
              DBUP_CONNECTION_STRING, DBUP_SCRIPTS_PATH, DBUP_DBO_ROOT, DBUP_SP_ROOT,
              DBUP_STAGE_DIR, DBUP_LOG_FILE, DBUP_NON_INTERACTIVE=true, CI=true / TF_BUILD

            Pack mode: expands Created/Altered SP names from Deploy_*.sql, runs SPs as RunAlways
            (CREATE OR ALTER), then Scripts-section SQL as RunOnce. Does not journal PRINT-only Deploys.

            Exit codes: 0 success, non-zero failure.
            """);
    }

    static IEnumerable<string> SplitList(string value) =>
        value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool IsFlag(string arg, params string[] names) =>
        names.Any(n => string.Equals(arg, n, StringComparison.OrdinalIgnoreCase));

    static bool TryGetValue(string[] args, ref int i, string arg, out string value, params string[] names)
    {
        value = "";
        foreach (var name in names)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {name}");
                }

                value = args[++i];
                return true;
            }

            var prefix = name + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = arg[prefix.Length..];
                return true;
            }
        }

        return false;
    }

    static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    static bool IsTruthyEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || value == "1"
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
