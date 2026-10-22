// Component log: %LOCALAPPDATA%/ck3xbl/appearance.log (truncated at 2 MB) plus an optional sink set by the host.
using System.Globalization;

namespace CK3xBL.Appearance;

internal sealed class AppearanceLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _lock = new();
    private string? _path;

    public Action<string>? Sink;

    public void SetFile(string? path)
    {
        lock (_lock) _path = path;
    }

    public void Write(string message)
    {
        string line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [appearance] {message}";
        try { Sink?.Invoke(line); }
        catch (Exception) { /* a host logger must never break the component */ }
        lock (_lock)
        {
            if (_path == null) return;
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes) File.WriteAllText(_path, "");
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
