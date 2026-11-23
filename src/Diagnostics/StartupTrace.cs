using System.Diagnostics;

namespace pdf_fluent.Diagnostics;

/// <summary>
/// Opt-in startup timing. Set FLUENTPDF_STARTUP_TRACE=1 to write phase
/// timestamps to %TEMP%\fluentpdf-startup.log. Disabled by default; the
/// disabled path is a single static bool check per mark.
/// </summary>
internal static class StartupTrace
{
    private static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("FLUENTPDF_STARTUP_TRACE"),
        "1",
        StringComparison.Ordinal);

    private static readonly string LogPath =
        Environment.GetEnvironmentVariable("FLUENTPDF_STARTUP_TRACE_FILE")
        ?? Path.Combine(Path.GetTempPath(), "fluentpdf-startup.log");

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = new();
    private static double _lastMilliseconds;

    public static DateTime ProcessStartTime { get; private set; } = DateTime.Now;

    public static void Start()
    {
        if (!Enabled) return;

        try
        {
            ProcessStartTime = Process.GetCurrentProcess().StartTime;
        }
        catch (Exception)
        {
            ProcessStartTime = DateTime.Now;
        }

        try
        {
            File.WriteAllText(LogPath, string.Empty);
        }
        catch (Exception)
        {
        }

        Clock.Start();
        _lastMilliseconds = 0;
        double sinceProcessStart = (DateTime.Now - ProcessStartTime).TotalMilliseconds;
        Write($"+0.0 ms | since process start {sinceProcessStart:F1} ms | Main.enter");
    }

    public static void Mark(string phase)
    {
        if (!Enabled) return;

        double now = Clock.Elapsed.TotalMilliseconds;
        double delta = now - _lastMilliseconds;
        _lastMilliseconds = now;
        Write($"+{now:F1} ms (+{delta:F1} ms) | {phase}");
    }

    private static void Write(string line)
    {
        try
        {
            lock (Gate) File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // Never make startup fail because diagnostics could not be written.
        }
    }
}
