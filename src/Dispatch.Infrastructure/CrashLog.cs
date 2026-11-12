using System.Diagnostics;
using System.Text;

namespace Dispatch.Infrastructure;

/// <summary>
/// Writes unhandled exceptions to <c>%LOCALAPPDATA%\Dispatch\logs\crash-*.log</c> so a crash leaves a trace
/// the user can send along. Without this, an exception on a background thread or during startup kills the
/// process and the only record is the Windows Event Viewer entry, which names the exception but is easy to miss.
/// </summary>
public static class CrashLog
{
    private const int KeepFiles = 20;
    private static readonly object Gate = new();
    private static int _installed;

    /// <summary>Folder the crash files are written to.</summary>
    public static string Directory { get; set; } = Path.Combine(DispatchPaths.DataDirectory, "logs");

    /// <summary>
    /// Hooks the process-wide last-chance handlers: exceptions escaping any thread, and faulted tasks nobody awaited.
    /// Safe to call more than once; only the first call installs the handlers.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.IsTerminating ? "Unhandled exception (process terminating)" : "Unhandled exception",
                e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "unknown"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Appends <paramref name="exception"/> with its full stack trace under <paramref name="source"/> to today's crash file,
    /// and returns the file's path. Never throws: logging must not turn a recoverable error into a crash.
    /// </summary>
    public static string? Write(string source, Exception exception)
    {
        try
        {
            var text = new StringBuilder()
                .AppendLine($"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {source}")
                .AppendLine($"Dispatch {typeof(CrashLog).Assembly.GetName().Version}  .NET {Environment.Version}  {Environment.OSVersion}  " +
                            $"thread {Environment.CurrentManagedThreadId}  pid {Environment.ProcessId}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"crash-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(path, text);
                Prune();
                return path;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"CrashLog: could not write crash file: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keeps the newest <see cref="KeepFiles"/> crash files so the folder cannot grow without bound.</summary>
    private static void Prune()
    {
        var files = new DirectoryInfo(Directory).GetFiles("crash-*.log").OrderByDescending(f => f.Name).Skip(KeepFiles);
        foreach (var file in files)
        {
            try { file.Delete(); } catch { /* best effort */ }
        }
    }
}
