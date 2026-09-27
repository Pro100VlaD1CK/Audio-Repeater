using System.IO;

namespace EchoBridge.Services;

public static class LoggingService
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoBridge", "logs");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:O} {message}{(exception is null ? "" : Environment.NewLine + exception)}{Environment.NewLine}");
                foreach (var old in Directory.EnumerateFiles(LogDirectory, "*.log*")
                             .Where(file => File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)))
                    File.Delete(old);
            }
        }
        catch { /* Logging must never terminate audio or UI. */ }
    }
}
