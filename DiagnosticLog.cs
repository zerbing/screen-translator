using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ScreenTranslator;

internal static class DiagnosticLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object gate = new();
    private static readonly string logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTranslator", "diagnostics.log");

    internal static long Begin(string operation, string? details = null)
    {
        Write(operation + ".start", details);
        return Stopwatch.GetTimestamp();
    }

    internal static void End(string operation, long started, string? details = null) => Write(operation + ".end",
        "ms=" + Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)
        + (details is null ? "" : " " + details));

    internal static void Write(string operation, string? details = null)
    {
        // Keep only timings and lifecycle state. Never include recognized text,
        // window titles, screenshots, credentials, or exception messages.
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaxBytes)
                    File.Move(logPath, logPath + ".1", overwrite: true);
                File.AppendAllText(logPath,
                    $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {operation} {details}\n", Encoding.UTF8);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Diagnostics must not prevent capture or shutdown.
        }
    }
}
