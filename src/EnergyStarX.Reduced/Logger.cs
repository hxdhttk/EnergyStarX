namespace EnergyStarX.Reduced;

internal static class Logger
{
    private static readonly TextWriter logFile;

    static Logger()
    {
        StreamWriter logStream = File.CreateText("log.txt");
        logStream.AutoFlush = true;

        logFile = TextWriter.Synchronized(logStream);
    }

    private static void Log(string logLevel, string message)
    {
        logFile.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][{logLevel}] {message}");
    }

    public static void Info(string message) => Log("INFO", message);

    public static void Error(Exception e, string description) =>
        Log("ERROR", $"{description}: {e}");
}
