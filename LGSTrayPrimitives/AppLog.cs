using System.Diagnostics;

namespace LGSTrayPrimitives;

// Small file log shared by the UI and the HID daemon, each writing its own file under
// %LOCALAPPDATA%\LGSTrayBattery\logs. The previous file is kept as <name>.old.log.
public static class AppLog
{
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly object _lock = new();
    private static string? _path;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LGSTrayBattery", "logs");

    public static void Init(string name)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _path = Path.Combine(LogDirectory, $"{name}.log");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Logging disabled, can't create {LogDirectory}: {ex.Message}");
        }

        Info($"--- {name} started, pid {Environment.ProcessId}, version {GetVersion()} ---");
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message, Exception? ex = null) => Write("WRN", ex == null ? message : $"{message}: {ex}");

    public static void Error(string message, Exception? ex = null) => Write("ERR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
#if DEBUG
        Console.WriteLine(line);
#endif
        Debug.WriteLine(line);

        if (_path == null) { return; }

        lock (_lock)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Move(_path, Path.ChangeExtension(_path, ".old.log"), overwrite: true);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
}
