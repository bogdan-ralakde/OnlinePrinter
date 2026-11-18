using System.Text;

namespace LabelBridge.Core;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogLine(DateTime TimestampUtc, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"{TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}  {Level,-5}  {Message}";
}

/// <summary>Small rolling file logger with an event feed for the GUI.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir = AppPaths.Logs;
    private static readonly List<LogLine> Recent = new();
    private const int KeepDays = 14;
    private const int MaxRecent = 2000;

    public static LogLevel Minimum { get; set; } = LogLevel.Info;

    public static event Action<LogLine>? Written;

    static Log()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
            foreach (var f in Directory.EnumerateFiles(Dir, "*.log"))
                if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
        }
        catch { }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, message + " :: " + ex.GetType().Name + ": " + ex.Message);

    public static IReadOnlyList<LogLine> Snapshot()
    {
        lock (Gate) return Recent.ToArray();
    }

    private static void Write(LogLevel level, string message)
    {
        if (level < Minimum) return;

        var line = new LogLine(DateTime.UtcNow, level, message);
        lock (Gate)
        {
            Recent.Add(line);
            if (Recent.Count > MaxRecent) Recent.RemoveRange(0, Recent.Count - MaxRecent);

            try
            {
                var file = Path.Combine(Dir, $"labelbridge-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        try { Written?.Invoke(line); } catch { }
    }
}
