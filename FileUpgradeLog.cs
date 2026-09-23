using System.Globalization;
using DbUp.Engine.Output;

namespace DBMigragtionTool;

internal sealed class FileUpgradeLog : IUpgradeLog, IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter writer;

    public FileUpgradeLog(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        writer = new StreamWriter(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
        FilePath = Path.GetFullPath(filePath);
        WriteLine("INF", $"Log started: {FilePath}");
    }

    public string FilePath { get; }

    public void LogTrace(string format, params object[] args) => WriteLine("TRC", format, args);
    public void LogDebug(string format, params object[] args) => WriteLine("DBG", format, args);
    public void LogInformation(string format, params object[] args) => WriteLine("INF", format, args);
    public void LogWarning(string format, params object[] args) => WriteLine("WRN", format, args);
    public void LogError(string format, params object[] args) => WriteLine("ERR", format, args);

    public void LogError(Exception ex, string format, params object[] args)
    {
        WriteLine("ERR", format, args);
        WriteLine("ERR", ex.ToString());
    }

    public void WriteApp(string level, string message) => WriteLine(level, "{0}", message);

    void WriteLine(string level, string format, params object[] args)
    {
        var text = args is { Length: > 0 }
            ? string.Format(CultureInfo.InvariantCulture, format, args)
            : format;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {text}";
        lock (gate)
        {
            writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            writer.Dispose();
        }
    }
}
