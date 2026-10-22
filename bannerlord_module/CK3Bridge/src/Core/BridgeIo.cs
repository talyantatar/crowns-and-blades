using System;
using System.IO;
using System.Text;
using System.Threading;
using CK3xBL.Protocol;

namespace CK3Bridge
{
    /// <summary>
    /// File transport shared with the bridge (docs/PROTOCOL.md §3): the %LOCALAPPDATA%\ck3xbl folder, atomic UTF-8
    /// (no BOM) writes, read-and-delete for inbound files, and the module log. Pure .NET, no game types, so the
    /// headless self-check can exercise it.
    /// </summary>
    public static class BridgeIo
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object LogLock = new object();
        private const long MaxLogBytes = 4 * 1024 * 1024;

        /// <summary>Overridable for the self-check; the game always uses %LOCALAPPDATA%\ck3xbl.</summary>
        public static string Folder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProtocolInfo.FolderName);

        public static string RequestPath => Path.Combine(Folder, ProtocolInfo.RequestFile);
        public static string ResultPath => Path.Combine(Folder, ProtocolInfo.ResultFile);
        public static string StatusPath => Path.Combine(Folder, ProtocolInfo.StatusFile);
        public static string CommandPath => Path.Combine(Folder, ProtocolInfo.CommandFile);
        public static string LogPath => Path.Combine(Folder, "bannerlord.log");

        /// <summary>Appends one timestamped line to bannerlord.log. Never throws; rotates at 4 MB.</summary>
        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Folder);
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n", Utf8NoBom);
                }
            }
            catch (Exception)
            {
                // Logging must never take the game down.
            }
        }

        /// <summary>Replace attempts of a normal write: a reader may hold the destination for a moment.</summary>
        public const int DefaultWriteAttempts = 5;

        /// <summary>
        /// Writes <paramref name="text"/> to <paramref name="path"/> atomically (tmp file, then replace). The replace is tried
        /// <paramref name="attempts"/> times, 5 ms apart (main-thread time: callers that retry on their own pass 1).
        /// </summary>
        public static void WriteAtomic(string path, string text, int attempts = DefaultWriteAttempts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8NoBom);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (IOException) when (attempt < attempts)
                {
                    Thread.Sleep(5);
                }
                catch (UnauthorizedAccessException) when (attempt < attempts)
                {
                    Thread.Sleep(5);
                }
            }
        }

        public static void WriteJsonAtomic(string path, object value, int attempts = DefaultWriteAttempts) =>
            WriteAtomic(path, ProtocolJson.Serialize(value), attempts);

        /// <summary>
        /// Like <see cref="WriteJsonAtomic"/> but never throws (used from engine callbacks, where an exception would reach
        /// the game loop). Returns false when the file could not be written, and logs it if <paramref name="logFailure"/>.
        /// </summary>
        public static bool TryWriteJsonAtomic(string path, object value, int attempts = DefaultWriteAttempts, bool logFailure = true)
        {
            try
            {
                WriteJsonAtomic(path, value, attempts);
                return true;
            }
            catch (Exception e)
            {
                if (logFailure) Log($"writing {Path.GetFileName(path)} failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads and deletes an inbound file. It is first renamed to a private name (atomic), so a newer file the bridge
        /// writes meanwhile is never deleted unread: it simply waits for the next poll. With <paramref name="accept"/>, a
        /// file it rejects is put back where it was (unless a newer one has arrived, which supersedes it) and null is
        /// returned. Returns null as well when there is nothing to take or the file is in use (the caller retries on the
        /// next poll).
        /// </summary>
        public static string TryTake(string path, Func<string, bool> accept = null)
        {
            string claimed = path + ".taken";
            try
            {
                if (!File.Exists(path)) return null;
                if (File.Exists(claimed)) File.Delete(claimed);   // left behind by a crash between claim and delete
                File.Move(path, claimed);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            string text = null;
            try
            {
                text = File.ReadAllText(claimed, Utf8NoBom);   // detects a BOM if the writer added one
            }
            catch (IOException)
            {
                // unreadable for now (e.g. a scanner holds it): put it back for the next poll
            }
            catch (UnauthorizedAccessException)
            {
                // as above
            }

            if (text != null && (accept == null || accept(text)))
            {
                TryDelete(claimed);
                return text;
            }
            PutBack(claimed, path);
            return null;
        }

        /// <summary>
        /// Reads an inbound file without taking it (shared with the writer's replace and with <see cref="TryTake"/>). Returns
        /// null when there is none or it cannot be read right now.
        /// </summary>
        public static string TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Moves a claimed file back; File.Move never overwrites, so a newer file written meanwhile wins.</summary>
        private static void PutBack(string claimed, string path)
        {
            try
            {
                File.Move(claimed, path);
            }
            catch (IOException)
            {
                TryDelete(claimed);
            }
            catch (UnauthorizedAccessException)
            {
                TryDelete(claimed);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // best effort: a leftover claim file is deleted by the next TryTake
            }
            catch (UnauthorizedAccessException)
            {
                // as above
            }
        }
    }
}
