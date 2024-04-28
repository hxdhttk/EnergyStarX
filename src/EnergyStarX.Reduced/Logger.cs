namespace EnergyStarX.Reduced;

internal static class Logger
{
    private static readonly TextWriter logFile = TextWriter.Synchronized(
        File.CreateText("log.txt")
    );

    private static void Log(string logLevel, string message)
    {
        logFile.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][{logLevel}] {message}");
        logFile.Flush();
    }

    public static void Info(string message) => Log("INFO", message);

    public static void Error(Exception e, string description) =>
        Log("ERROR", $"{description}: {e}");
}
