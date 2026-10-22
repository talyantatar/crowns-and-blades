using System.Globalization;
using System.Text;

namespace CK3xBL.Ux;

/// <summary>
/// Log sink for the UX component. The bridge sets <see cref="Sink"/> to its own logger. Until it does, lines are appended
/// to <see cref="FallbackFile"/> (%LOCALAPPDATA%\ck3xbl\ux.log, next to the bridge's bridge.log), so the diagnostics of a
/// release tray app are never lost. Writers are called from several threads (overlay UI thread, audio ramps, the caller's
/// thread), so the sink must be thread-safe. A throwing sink never breaks the UX.
/// </summary>
public static class UxLog
{
    private const long MaxFallbackBytes = 4 << 20;
    private static readonly object FileGate = new();

    public static Action<string>? Sink { get; set; }

    /// <summary>Where lines go while no <see cref="Sink"/> is set (rotated to ux.1.log at 4 MB).</summary>
    public static string FallbackFile { get; internal set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ck3xbl", "ux.log");

    internal static void Write(string message)
    {
        string line = "[ux] " + message;
        try
        {
            if (Sink is { } sink) sink(line);
            else AppendToFallbackFile(line);
        }
        catch
        {
            // Logging must never take the transition down with it.
        }
    }

    private static void AppendToFallbackFile(string line)
    {
        string stamped = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine;
        lock (FileGate)
        {
            string path = FallbackFile;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFallbackBytes) File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
            else if (!info.Exists) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, stamped, Encoding.UTF8);
        }
    }
}
