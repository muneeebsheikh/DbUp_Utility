namespace DBMigragtionTool;

internal static class AppLog
{
    static FileUpgradeLog? file;

    public static void Attach(FileUpgradeLog? log) => file = log;

    public static void Info(string message)
    {
        Console.WriteLine(message);
        file?.WriteApp("INF", message);
    }

    public static void Warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
        file?.WriteApp("WRN", message);
    }

    public static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
        file?.WriteApp("ERR", message);
    }

    public static void Success(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
        file?.WriteApp("INF", message);
    }
}
