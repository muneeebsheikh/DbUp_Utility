using System.Text;
using System.Text.RegularExpressions;

namespace DBMigragtionTool;

internal sealed record DeployPack(
    string Sprint,
    string PbiFolder,
    int PbiId,
    string DeployPath,
    IReadOnlyList<string> CreatedSpNames,
    IReadOnlyList<string> AlteredSpNames,
    string? ScriptsSql,
    bool ScriptsIsPrintOnly);

internal static class DeployPackager
{
    static readonly Regex SectionHeader = new(
        @"^--\s*=+\s*$",
        RegexOptions.Compiled);

    static readonly Regex CreatedHeader = new(
        @"^--\s*Created\s+SPs\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex AlteredHeader = new(
        @"^--\s*Altered\s+SPs\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex ScriptsHeader = new(
        @"^--\s*Scripts\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex CreateProc = new(
        @"\bCREATE\s+(PROC|PROCEDURE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<DeployPack> DiscoverPacks(
        string scriptsRoot,
        IReadOnlyList<string>? sprints,
        IReadOnlyList<int>? pbiIds,
        bool includePriorUnreleased)
    {
        if (!Directory.Exists(scriptsRoot))
        {
            throw new DirectoryNotFoundException($"Scripts root not found: {scriptsRoot}");
        }

        var sprintDirs = Directory.GetDirectories(scriptsRoot)
            .Select(d => new DirectoryInfo(d))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sprints is { Count: > 0 })
        {
            var wanted = new HashSet<string>(sprints, StringComparer.OrdinalIgnoreCase);
            if (includePriorUnreleased)
            {
                var maxSprint = sprints.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Last();
                sprintDirs = sprintDirs
                    .Where(d => string.Compare(d.Name, maxSprint, StringComparison.OrdinalIgnoreCase) <= 0)
                    .ToList();
            }
            else
            {
                sprintDirs = sprintDirs.Where(d => wanted.Contains(d.Name)).ToList();
            }
        }

        var packs = new List<DeployPack>();
        foreach (var sprintDir in sprintDirs)
        {
            var pbiDirs = sprintDir.GetDirectories("PBI-*")
                .OrderBy(d => ParsePbiId(d.Name))
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var pbiDir in pbiDirs)
            {
                var pbiId = ParsePbiId(pbiDir.Name);
                if (pbiIds is { Count: > 0 } && !pbiIds.Contains(pbiId))
                {
                    continue;
                }

                var deploy = pbiDir.GetFiles("Deploy_*.sql").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                    ?? pbiDir.GetFiles("*.sql").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (deploy is null)
                {
                    Console.WriteLine($"Skipping {pbiDir.FullName}: no Deploy_*.sql");
                    continue;
                }

                packs.Add(ParseDeploy(sprintDir.Name, pbiDir.Name, pbiId, deploy.FullName));
            }
        }

        return packs;
    }

    public static DeployPack ParseDeploy(string sprint, string pbiFolder, int pbiId, string deployPath)
    {
        var lines = File.ReadAllLines(deployPath);
        var created = new List<string>();
        var altered = new List<string>();
        var scripts = new StringBuilder();
        var mode = "none";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (CreatedHeader.IsMatch(trimmed))
            {
                mode = "created";
                continue;
            }

            if (AlteredHeader.IsMatch(trimmed))
            {
                mode = "altered";
                continue;
            }

            if (ScriptsHeader.IsMatch(trimmed))
            {
                mode = "scripts";
                continue;
            }

            if (SectionHeader.IsMatch(trimmed))
            {
                continue;
            }

            if (mode is "created" or "altered")
            {
                if (trimmed.StartsWith("--"))
                {
                    var name = trimmed[2..].Trim();
                    if (name.Length == 0 || name.StartsWith('='))
                    {
                        continue;
                    }

                    if (mode == "created")
                    {
                        created.Add(name);
                    }
                    else
                    {
                        altered.Add(name);
                    }
                }

                continue;
            }

            if (mode == "scripts")
            {
                scripts.AppendLine(line);
            }
        }

        var scriptsSql = scripts.ToString().Trim();
        var printOnly = IsPrintOnly(scriptsSql);
        return new DeployPack(
            sprint,
            pbiFolder,
            pbiId,
            deployPath,
            created,
            altered,
            string.IsNullOrWhiteSpace(scriptsSql) ? null : scriptsSql,
            printOnly);
    }

    public static string StagePack(
        DeployPack pack,
        string storedProceduresRoot,
        string stageRoot,
        int sequence)
    {
        var packStage = Path.Combine(stageRoot, $"{sequence:D4}_{pack.Sprint}_{pack.PbiFolder}");
        var eOrderJsonPath = Path.GetFullPath(Path.Combine($"{pack.Sprint}/{pack.PbiFolder}", "eorder.json"));
        var spDir = Path.Combine(packStage, "sp");
        var scriptsDir = Path.Combine(packStage, "scripts");
        Directory.CreateDirectory(spDir);
        Directory.CreateDirectory(scriptsDir);

        if (!File.Exists(eOrderJsonPath))
        {
            throw new Exception($"eorder.json not found for pack {pack.PbiFolder} in sprint {pack.Sprint} - Path: {eOrderJsonPath}");
        }

        var eOrderJson = File.ReadAllText(eOrderJsonPath);
        var generatedJson = ExecutionGroup.GenerateArtifactExecutionConfig(eOrderJson);
        File.WriteAllText(
        Path.Combine(stageRoot, "eOrder.json"), generatedJson);

        var spNames = pack.CreatedSpNames.Concat(pack.AlteredSpNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var spIndex = 0;
        foreach (var name in spNames)
        {
            var source = ResolveSpFile(storedProceduresRoot, name)
                ?? throw new FileNotFoundException(
                    $"SP '{name}' listed in {pack.DeployPath} was not found under {storedProceduresRoot}");

            var body = File.ReadAllText(source);
            var rewritten = EnsureCreateOrAlter(body);
            var destName = $"{++spIndex:D2}_{SanitizeFileName(name)}.sql";
            File.WriteAllText(Path.Combine(spDir, destName), rewritten, Encoding.UTF8);
            Console.WriteLine($"  staged SP {name} <- {source}");
        }

        if (spNames.Count > 0 && Directory.GetFiles(spDir, "*.sql").Length == 0)
        {
            throw new InvalidOperationException($"Pack {pack.PbiFolder} listed SPs but staged zero SP scripts.");
        }

        // Extra non-Deploy .sql beside Deploy in the PBI folder (RunOnce)
        var pbiDir = Path.GetDirectoryName(pack.DeployPath)!;
        foreach (var extra in Directory.GetFiles(pbiDir, "*.sql")
                     .Where(f => !string.Equals(Path.GetFileName(f), Path.GetFileName(pack.DeployPath), StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var dest = Path.Combine(scriptsDir, Path.GetFileName(extra));
            File.Copy(extra, dest, overwrite: true);
            Console.WriteLine($"  staged extra script {Path.GetFileName(extra)}");
        }

        if (pack.ScriptsSql is not null && !pack.ScriptsIsPrintOnly)
        {
            var dest = Path.Combine(scriptsDir, $"DeployScripts_PBI-{pack.PbiId}.sql");
            File.WriteAllText(dest, pack.ScriptsSql, Encoding.UTF8);
            Console.WriteLine($"  staged Scripts section from Deploy_PBI-{pack.PbiId}.sql");
        }
        else if (pack.ScriptsIsPrintOnly || pack.ScriptsSql is null)
        {
            if (spNames.Count == 0 && Directory.GetFiles(scriptsDir, "*.sql").Length == 0)
            {
                Console.WriteLine($"  warning: {pack.PbiFolder} is PRINT-only with no SPs — nothing to apply");
            }
            else
            {
                Console.WriteLine($"  skipped journaling PRINT-only Deploy for {pack.PbiFolder}");
            }
        }

        if (spNames.Count > 0 && Directory.GetFiles(spDir, "*.sql").Length == 0)
        {
            throw new InvalidOperationException(
                $"Refusing to treat {pack.PbiFolder} as success: Created/Altered SPs listed but none staged.");
        }

        

        return packStage;
    }

    public static string? ResolveSpFile(string storedProceduresRoot, string spName)
    {
        var bare = spName.Trim();
        if (bare.Contains('.'))
        {
            bare = bare.Split('.').Last();
        }

        bare = bare.Trim('[', ']');
        var matches = Directory.EnumerateFiles(storedProceduresRoot, "*.sql", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return string.Equals(name, bare, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "dbo." + bare, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => matches.OrderBy(m => m.Length).First()
        };
    }

    public static string EnsureCreateOrAlter(string sql)
    {
        return CreateProc.Replace(sql, "CREATE OR ALTER PROCEDURE");
    }

    static bool IsPrintOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return true;
        }

        var withoutComments = Regex.Replace(sql, @"--.*?$", "", RegexOptions.Multiline);
        withoutComments = Regex.Replace(withoutComments, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var batches = withoutComments.Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Allow PRINT-only batches
            var lines = trimmed.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Any(l => !l.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    static int ParsePbiId(string folderName)
    {
        var m = Regex.Match(folderName, @"PBI-(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : int.MaxValue;
    }

    static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Replace('.', '_');
    }
}
