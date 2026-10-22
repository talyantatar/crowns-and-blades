// The bridge's event journal: %LOCALAPPDATA%\ck3xbl\bridge.log (one line per event, rotated at a size cap).
using System.Text;

namespace CK3xBL.Core.Diagnostics;

public enum JournalLevel { Debug, Info, Warn, Error }

public interface IJournal
{
    void Write(JournalLevel level, string area, string message, Exception? error = null);
}

public static class JournalExtensions
{
    public static void Debug(this IJournal j, string area, string message) => j.Write(JournalLevel.Debug, area, message);
    public static void Info(this IJournal j, string area, string message) => j.Write(JournalLevel.Info, area, message);
    public static void Warn(this IJournal j, string area, string message) => j.Write(JournalLevel.Warn, area, message);
    public static void Error(this IJournal j, string area, string message, Exception? e = null) => j.Write(JournalLevel.Error, area, message, e);
}

/// <summary>Thread-safe append-only text journal. Never throws: logging must not break the bridge.</summary>
public sealed class FileJournal : IJournal
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly JournalLevel _minLevel;
    private readonly object _gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public FileJournal(string path, JournalLevel minLevel = JournalLevel.Info, long maxBytes = 8 << 20)
    {
        _path = path;
        _minLevel = minLevel;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Write(JournalLevel level, string area, string message, Exception? error = null)
    {
        if (level < _minLevel) return;
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
          .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5))
          .Append(" [").Append(area).Append("] ").Append(message);
        if (error is not null) sb.AppendLine().Append("    ").Append(error.ToString().Replace("\n", "\n    "));
        sb.AppendLine();
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > _maxBytes)
                    File.Move(_path, Path.ChangeExtension(_path, ".1.log"), overwrite: true);
                File.AppendAllText(_path, sb.ToString(), Utf8NoBom);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Journal for tests: keeps lines in memory.</summary>
public sealed class MemoryJournal : IJournal
{
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

    public void Write(JournalLevel level, string area, string message, Exception? error = null)
    {
        lock (_lines) _lines.Add($"{level} [{area}] {message}{(error is null ? "" : " :: " + error.Message)}");
    }
}
