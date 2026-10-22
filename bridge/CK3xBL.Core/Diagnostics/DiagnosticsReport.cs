// "Copy diagnostics" (tray: About): the text a player pastes into an issue report. The bridge and game versions, the install check,
// the paths, and the tails of bridge.log and bannerlord.log, with the Windows user folder masked so a public report never carries the
// player's account name.
using System.Text;
using CK3xBL.Core.Config;

namespace CK3xBL.Core.Diagnostics;

public static class DiagnosticsReport
{
    public const int LogTailLines = 200;
    public const string UserProfileToken = "%USERPROFILE%";

    public static string Build(BridgePaths paths, BridgeConfig config, IReadOnlyList<InstallItem> checks, string? userProfile = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{BridgeVersion.ProductName} {BridgeVersion.Full}");
        sb.AppendLine($"Built for CK3 {BridgeVersion.Ck3Target}.x and Bannerlord {BridgeVersion.BannerlordTarget}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}, mode {config.Mode}");
        sb.AppendLine($"Install: {paths.InstallDir}{(paths.IsDevTree ? " (source checkout)" : "")}");
        sb.AppendLine($"Config:  {paths.ConfigFile}");
        sb.AppendLine($"Data:    {paths.DataDir}{(paths.HasData ? "" : " (MISSING)")}");
        sb.AppendLine($"Logs:    {paths.SharedDir}");
        sb.AppendLine();
        sb.AppendLine("Install check:");
        foreach (var item in checks)
            sb.AppendLine($"  [{(item.IsWarning ? "!!" : "ok")}] {item.Title}{(item.Text.Length > 0 ? " - " + item.Text : "")}");
        AppendTail(sb, Path.Combine(paths.SharedDir, "bridge.log"));
        AppendTail(sb, Path.Combine(paths.SharedDir, "bannerlord.log"));
        return MaskUserProfile(sb.ToString(), userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Replaces every occurrence of the user profile folder (backslash, forward-slash and JSON-escaped forms, any case) with
    /// %USERPROFILE%.</summary>
    public static string MaskUserProfile(string text, string? userProfile)
    {
        if (string.IsNullOrWhiteSpace(userProfile)) return text;
        string profile = Path.TrimEndingDirectorySeparator(userProfile.Trim());
        if (profile.Length < 4) return text;                     // "C:\" would mask every path
        foreach (var form in new[] { profile.Replace('/', '\\').Replace(@"\", @"\\"), profile.Replace('/', '\\'), profile.Replace('\\', '/') }.Distinct())
            text = text.Replace(form, UserProfileToken, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static void AppendTail(StringBuilder sb, string file)
    {
        sb.AppendLine();
        sb.AppendLine($"--- last {LogTailLines} lines of {Path.GetFileName(file)} ---");
        try
        {
            if (!File.Exists(file))
            {
                sb.AppendLine("(none)");
                return;
            }
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var tail = new Queue<string>(LogTailLines);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == LogTailLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            foreach (var line in tail) sb.AppendLine(line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            sb.AppendLine($"(could not read: {e.Message})");
        }
    }
}
