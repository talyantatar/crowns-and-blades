// The setup log: one line per action, to a file (when given), the console (when there is one) and the wizard's progress list.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

/// <summary>Detail: one line per file or registry value (log file and console only; the wizard shows the rest).</summary>
internal enum LogLevel { Detail, Info, Plan, Warn, Error }

internal sealed class SetupLog : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _file;
    private readonly bool _console;

    /// <summary>Raised for every line (any thread). The wizard shows these.</summary>
    public event Action<LogLevel, string>? Line;

    public string? FilePath { get; private set; }

    public SetupLog(bool console)
    {
        _console = console;
    }

    /// <summary>Starts (appends to) the log file. Failure to open it is not fatal: setup still runs, without a file.</summary>
    public void OpenFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            lock (_gate) _file = new StreamWriter(fs, FileOps.Utf8NoBom) { AutoFlush = true };
            FilePath = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Write(LogLevel.Warn, $"could not open the log file {path}: {e.Message}");
        }
    }

    public void Info(string text) => Write(LogLevel.Info, text);
    public void Plan(string text) => Write(LogLevel.Plan, text);
    public void Warn(string text) => Write(LogLevel.Warn, text);
    public void Error(string text) => Write(LogLevel.Error, text);

    public void Write(LogLevel level, string text)
    {
        var tag = level switch
        {
            LogLevel.Plan => "PLAN ", LogLevel.Warn => "WARN ", LogLevel.Error => "ERROR", LogLevel.Detail => "     ", _ => "INFO ",
        };
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {tag} {text}";
        lock (_gate)
        {
            try { _file?.WriteLine(line); }
            catch (IOException) { }
            if (_console)
            {
                try
                {
                    if (level == LogLevel.Error) Console.Error.WriteLine($"{tag} {text}");
                    else Console.Out.WriteLine($"{tag} {text}");
                }
                catch (IOException) { }
            }
        }
        Line?.Invoke(level, text);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>A GUI-subsystem exe has no console. Started from a terminal, attach to it so /silent and /dry-run can print;
    /// started with redirected output (a script or a test), the handles are already there and are left alone.</summary>
    public static bool PrepareConsole()
    {
        var h = Native.GetStdHandle(Native.StdOutputHandle);
        if (h != IntPtr.Zero && h != new IntPtr(-1))
        {
            // Redirected (pipe or file): write UTF-8, so non-ASCII paths survive.
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), FileOps.Utf8NoBom) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), FileOps.Utf8NoBom) { AutoFlush = true });
            }
            catch (IOException) { return false; }
            return true;
        }
        if (!Native.AttachConsole(Native.AttachParentProcess)) return false;
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Console.OutputEncoding) { AutoFlush = true });
            Console.Out.WriteLine();
        }
        catch (IOException) { return false; }
        return true;
    }
}
