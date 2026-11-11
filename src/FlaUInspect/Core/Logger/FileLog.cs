using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace FlaUInspect.Core.Logger;

/// <summary>
///     Appends to <c>log.txt</c> next to FlaUInspect.exe — the folder the release zip was unzipped into — so a crash or
///     freeze on a tester's machine leaves something to send back. Falls back to <c>%LOCALAPPDATA%\FlaUInspect</c> when
///     that folder is not writable. Every member swallows its own failures: logging must never be what takes the app
///     down.
/// </summary>
public static class FileLog {
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new ();
    private static object? _lastFatal;

    /// <summary>Full path of the log file, or null when no writable location was found.</summary>
    public static string? FilePath { get; private set; }

    public static void Initialize() {
        try {
            FilePath = ChooseWritablePath();
            RollOverIfTooLarge();

            string version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "N/A";
            string exe = Environment.ProcessPath ?? "N/A";
            string built = File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm") : "N/A";

            Write("INFO", "");
            Write("INFO", $"===== FlaUInspect started | version {version} | exe built {built} | {RuntimeInformation.OSDescription} "
                          + $"| .NET {Environment.Version} | pid {Environment.ProcessId} | {exe} =====");
        } catch {
            // ignored
        }
    }

    public static void Write(string level, string message) {
        string? path = FilePath;
        if (path == null) {
            return;
        }

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";

        try {
            lock (Gate) {
                File.AppendAllText(path, line);
            }
        } catch {
            // ignored
        }
    }

    /// <summary>
    ///     Logs an exception that is about to close the app. An unhandled UI-thread exception is reported both by
    ///     <c>DispatcherUnhandledException</c> and then <c>AppDomain.UnhandledException</c>; it is written once.
    /// </summary>
    public static void WriteFatal(string source, object? exception) {
        if (exception == null || ReferenceEquals(exception, _lastFatal)) {
            return;
        }
        _lastFatal = exception;
        Write("FATAL", $"{source} - the app closes after this:{Environment.NewLine}{exception}");
    }

    /// <summary>
    ///     Writes a line when the UI thread stops answering for 5 s, and another when it comes back. A freeze that ends
    ///     with the user (or Windows) killing the app leaves no exception behind, so without this a frozen-then-killed
    ///     session would look like a clean one; the lines just above say what was going on when it froze.
    /// </summary>
    public static void StartUiWatchdog(Dispatcher dispatcher) {
        Thread watchdog = new (() => {
            while (!dispatcher.HasShutdownStarted) {
                Thread.Sleep(2000);

                DispatcherOperation ping = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { }));
                if (ping.Wait(TimeSpan.FromSeconds(5)) != DispatcherOperationStatus.Pending) {
                    continue;
                }

                DateTime since = DateTime.Now.AddSeconds(-5);
                Write("ERROR", "UI thread has not responded for 5 s - the app is frozen. The lines above show what it was doing.");
                ping.Wait();
                Write("WARN", $"UI thread responsive again after {(DateTime.Now - since).TotalSeconds:F0} s.");
            }
        }) {
            IsBackground = true,
            Name = "FlaUInspect UI watchdog"
        };
        watchdog.Start();
    }

    private static string? ChooseWritablePath() {
        foreach (string dir in new[] {
                     AppContext.BaseDirectory,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlaUInspect")
                 }) {
            try {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "log.txt");
                using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return path;
            } catch {
                // not writable, try the next location
            }
        }
        return null;
    }

    private static void RollOverIfTooLarge() {
        if (FilePath == null) {
            return;
        }

        FileInfo info = new (FilePath);
        if (info.Exists && info.Length > MaxBytes) {
            File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.txt"), overwrite: true);
        }
    }
}
