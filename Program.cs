using System.Configuration;
using System.Reflection;
using System.Text;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Transactions;
using DbUp.ScriptProviders;
using DbUp.Support;
using DbUp.Engine.Output;
using DBMigragtionTool;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(ex.Message);
    Console.ResetColor();
    CliOptions.PrintHelp();
    return 1;
}

if (options.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

var nonInteractive = options.NonInteractive || Console.IsInputRedirected;

var logPath = options.ResolveLogFilePath();
using var fileLog = new FileUpgradeLog(logPath);
AppLog.Attach(fileLog);
AppLog.Info($"Writing log file: {fileLog.FilePath}");

string? connectionString = null;
if (!options.StageOnly)
{
    try
    {
        connectionString = options.ResolveConnectionString();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(ex.Message);
        Console.ResetColor();
        return 1;
    }

    AppLog.Info($"connectionString: {connectionString}");
}

if (nonInteractive)
{
    AppLog.Info("Running non-interactive (no Press Enter).");
}

if (options.PackMode)
{
    return RunPackMode(options, connectionString, nonInteractive, fileLog);
}

return RunLegacyMode(options, connectionString!, nonInteractive, fileLog);

static int RunPackMode(CliOptions options, string? connectionString, bool nonInteractive, FileUpgradeLog fileLog)
{
    string scriptsRoot;
    string spRoot;
    string stageRoot;
    try
    {
        scriptsRoot = options.ResolveScriptsPathSetting();
        if (!Path.IsPathRooted(scriptsRoot))
        {
            scriptsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, scriptsRoot));
        }

        // When --dboRoot is used, ScriptsPath resolves to dbo/Scripts (single root).
        // Legacy multi-path ScriptsPath is not used for pack discovery.
        if (scriptsRoot.Contains(';') || scriptsRoot.Contains(','))
        {
            scriptsRoot = scriptsRoot.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        spRoot = options.ResolveStoredProceduresRoot();
        stageRoot = options.ResolveStageDir();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(ex.Message);
        Console.ResetColor();
        return 1;
    }

    AppLog.Info($"Pack mode. Scripts root: {scriptsRoot}");
    AppLog.Info($"Stored Procedures root: {spRoot}");
    AppLog.Info($"Stage dir: {stageRoot}");

    IReadOnlyList<DeployPack> packs;
    try
    {
        packs = DeployPackager.DiscoverPacks(
            scriptsRoot,
            options.Sprints.Count > 0 ? options.Sprints : null,
            options.PbiIds.Count > 0 ? options.PbiIds : null,
            options.IncludePriorUnreleased);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(ex.Message);
        Console.ResetColor();
        return 1;
    }

    if (packs.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("No Deploy packs matched the sprint/PBI filters.");
        Console.ResetColor();
        return 1;
    }

    AppLog.Info($"Matched {packs.Count} Deploy pack(s):");
    foreach (var pack in packs)
    {
        AppLog.Info(
            $"  {pack.Sprint}/{pack.PbiFolder} created={pack.CreatedSpNames.Count} altered={pack.AlteredSpNames.Count} scripts={(pack.ScriptsIsPrintOnly ? "PRINT-only" : "SQL")}");
    }

    if (Directory.Exists(stageRoot))
    {
        Directory.Delete(stageRoot, recursive: true);
    }

    Directory.CreateDirectory(stageRoot);

    var assemblyName = Assembly.GetExecutingAssembly().GetName().Name!;
    var seq = 0;
    var staged = new List<string>();
    foreach (var pack in packs)
    {
        AppLog.Info($"Packaging {pack.Sprint}/{pack.PbiFolder}...");
        try
        {
            staged.Add(DeployPackager.StagePack(pack, spRoot, stageRoot, ++seq));
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(ex.Message);
            Console.ResetColor();
            return 1;
        }
    }

    if (options.StageOnly)
    {
        AppLog.Success($"Stage-only complete: {stageRoot}");
        AppLog.Info($"Log file: {fileLog.FilePath}");
        return 0;
    }

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Connection string required to apply packs.");
        Console.ResetColor();
        return 1;
    }

    if (!nonInteractive)
    {
        Console.WriteLine("Press Enter to proceed....");
        Console.ReadLine();
    }

    foreach (var packStage in staged)
    {
        var packName = Path.GetFileName(packStage);
        var spDir = Path.Combine(packStage, "sp");
        var scriptsDir = Path.Combine(packStage, "scripts");

        // Per PBI: SPs (RunAlways) then Scripts (RunOnce)
        if (Directory.Exists(spDir) && Directory.GetFiles(spDir, "*.sql").Length > 0)
        {
            AppLog.Info($"  RunAlways SPs for {packName}...");
            var spResult = BuildFolderUpgrader(connectionString, spDir, ScriptType.RunAlways, assemblyName, CreateUpgradeLog(fileLog)).PerformUpgrade();
            if (!spResult.Successful)
            {
                return WriteFailure(spResult, nonInteractive);
            }
        }

        if (Directory.Exists(scriptsDir) && Directory.GetFiles(scriptsDir, "*.sql").Length > 0)
        {
            AppLog.Info($"  RunOnce Scripts for {packName}...");
            var scriptsResult = BuildFolderUpgrader(connectionString, scriptsDir, ScriptType.RunOnce, assemblyName, CreateUpgradeLog(fileLog)).PerformUpgrade();
            if (!scriptsResult.Successful)
            {
                return WriteFailure(scriptsResult, nonInteractive);
            }
        }
    }

    AppLog.Success("Success!");
    AppLog.Info($"Log file: {fileLog.FilePath}");
    return 0;
}

static int RunLegacyMode(CliOptions options, string connectionString, bool nonInteractive, FileUpgradeLog fileLog)
{
    string scriptsPathSetting;
    try
    {
        scriptsPathSetting = options.ResolveScriptsPathSetting();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(ex.Message);
        Console.ResetColor();
        return 1;
    }

    var scriptsPaths = scriptsPathSetting
        .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(path => Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path)))
        .ToList();

    if (scriptsPaths.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("ScriptsPath is not configured.");
        Console.ResetColor();
        return 1;
    }

    var missingPaths = scriptsPaths.Where(path => !Directory.Exists(path)).ToList();
    if (missingPaths.Count > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        foreach (var path in missingPaths)
        {
            Console.WriteLine($"Scripts folder not found: {path}");
        }
        Console.ResetColor();
        return 1;
    }

    var includeSubDirectories = bool.TryParse(
        ConfigurationManager.AppSettings["IncludeSubDirectories"],
        out var include) && include;

    var scriptFiles = (ConfigurationManager.AppSettings["ScriptFiles"] ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var excludeFiles = (ConfigurationManager.AppSettings["ExcludeFiles"] ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    bool MatchesNames(string path, HashSet<string> names) =>
        names.Contains(Path.GetFileName(path))
        || names.Any(name => path.EndsWith(name, StringComparison.OrdinalIgnoreCase));

    bool ShouldIncludeScript(string path) =>
        !MatchesNames(path, excludeFiles)
        && (scriptFiles.Count == 0 || MatchesNames(path, scriptFiles));

    var fileOptions = new FileSystemScriptOptions
    {
        IncludeSubDirectories = includeSubDirectories,
        Extensions = ["*.sql"],
        Encoding = Encoding.UTF8,
        Filter = ShouldIncludeScript
    };

    Console.WriteLine("Using scripts from:");
    foreach (var path in scriptsPaths)
    {
        Console.WriteLine($"  {path}");
    }

    if (scriptFiles.Count == 0)
    {
        Console.WriteLine("ScriptFiles is empty; executing all .sql files as RunOnce.");
    }
    else
    {
        Console.WriteLine($"Executing specific scripts as RunOnce, then re-running them at the end: {string.Join(", ", scriptFiles)}");
    }

    var assemblyName = Assembly.GetExecutingAssembly().GetName().Name!;

    UpgradeEngine BuildUpgrader(ScriptType scriptType)
    {
        var sqlScriptOptions = new SqlScriptOptions { ScriptType = scriptType };
        var builder =
            DeployChanges.To
                .SqlDatabase(connectionString)
                .WithFilter(new FilenameMatchingScriptFilter())
                .LogTo(CreateUpgradeLog(fileLog));

        foreach (var scriptsPath in scriptsPaths)
        {
            builder.WithScripts(
                new AssemblyPrefixedFileSystemScriptProvider(scriptsPath, fileOptions, sqlScriptOptions, assemblyName));
        }

        return builder.Build();
    }

    var runOnceUpgrader = BuildUpgrader(ScriptType.RunOnce);
    var runOnceScripts = runOnceUpgrader.GetScriptsToExecute();
    Console.WriteLine($"RunOnce scripts to execute: {runOnceScripts.Count}");
    foreach (var script in runOnceScripts)
    {
        Console.WriteLine($"  pending: {script.Name}");
    }

    UpgradeEngine? rerunUpgrader = null;
    if (scriptFiles.Count > 0)
    {
        rerunUpgrader = BuildUpgrader(ScriptType.RunAlways);
        Console.WriteLine($"Scripts to re-run at the end: {rerunUpgrader.GetScriptsToExecute().Count}");
    }

    if (!nonInteractive)
    {
        Console.WriteLine("Press Enter to proceed....");
        Console.ReadLine();
    }

    var result = runOnceUpgrader.PerformUpgrade();
    if (!result.Successful)
    {
        return WriteFailure(result, nonInteractive);
    }

    if (rerunUpgrader is not null)
    {
        Console.WriteLine("Re-running mentioned scripts...");
        result = rerunUpgrader.PerformUpgrade();
        if (!result.Successful)
        {
            return WriteFailure(result, nonInteractive);
        }
    }

    AppLog.Success("Success!");
    AppLog.Info($"Log file: {fileLog.FilePath}");
    return 0;
}

static UpgradeEngine BuildFolderUpgrader(
    string connectionString,
    string folder,
    ScriptType scriptType,
    string assemblyName,
    IUpgradeLog upgradeLog)
{
    var sqlScriptOptions = new SqlScriptOptions { ScriptType = scriptType };
    var fileOptions = new FileSystemScriptOptions
    {
        IncludeSubDirectories = false,
        Extensions = ["*.sql"],
        Encoding = Encoding.UTF8
    };

    return DeployChanges.To
        .SqlDatabase(connectionString)
        .WithFilter(new FilenameMatchingScriptFilter())
        .WithScripts(new AssemblyPrefixedFileSystemScriptProvider(folder, fileOptions, sqlScriptOptions, assemblyName))
        .LogTo(upgradeLog)
        .Build();
}

static int WriteFailure(DatabaseUpgradeResult failedResult, bool nonInteractive)
{
    AppLog.Error(failedResult.Error?.ToString() ?? "Upgrade failed.");
#if DEBUG
    if (!nonInteractive)
    {
        Console.ReadLine();
    }
#endif
    return 1;
}

static IUpgradeLog CreateUpgradeLog(FileUpgradeLog fileLog)
{
    var aggregate = new AggregateLog();
    aggregate.AddLogger(new ConsoleUpgradeLog());
    aggregate.AddLogger(fileLog);
    return aggregate;
}

sealed class AssemblyPrefixedFileSystemScriptProvider : IScriptProvider
{
    private readonly FileSystemScriptProvider inner;
    private readonly string assemblyName;

    public AssemblyPrefixedFileSystemScriptProvider(
        string scriptsPath,
        FileSystemScriptOptions options,
        SqlScriptOptions sqlScriptOptions,
        string assemblyName)
    {
        inner = new FileSystemScriptProvider(scriptsPath, options, sqlScriptOptions);
        this.assemblyName = assemblyName;
    }

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager)
    {
        return inner.GetScripts(connectionManager).Select(script =>
        {
            var fileName = Path.GetFileName(script.Name);
            var journalName = $"{assemblyName}.{fileName}";
            return new SqlScript(journalName, script.Contents, script.SqlScriptOptions);
        });
    }
}

sealed class FilenameMatchingScriptFilter : IScriptFilter
{
    public IEnumerable<SqlScript> Filter(
        IEnumerable<SqlScript> sorted,
        HashSet<string> executedScriptNames,
        ScriptNameComparer comparer)
    {
        return sorted.Where(script =>
        {
            if (script.SqlScriptOptions.ScriptType == ScriptType.RunAlways)
            {
                return true;
            }

            return !executedScriptNames.Any(executed => NamesMatch(script.Name, executed));
        });
    }

    static bool NamesMatch(string scriptName, string executedName)
    {
        if (string.Equals(scriptName, executedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var scriptFileName = GetFileName(scriptName);
        var executedFileName = GetFileName(executedName);

        return string.Equals(scriptFileName, executedFileName, StringComparison.OrdinalIgnoreCase)
            || executedName.EndsWith("." + scriptFileName, StringComparison.OrdinalIgnoreCase)
            || scriptName.EndsWith("." + executedFileName, StringComparison.OrdinalIgnoreCase);
    }

    static string GetFileName(string name)
    {
        var normalized = name.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFileName(normalized);
    }
}
