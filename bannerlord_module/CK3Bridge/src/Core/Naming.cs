using System;
using System.Collections.Generic;
using System.Text;

namespace CK3Bridge
{
    /// <summary>Object ids and display names that are safe for Bannerlord's object system and TextObject.</summary>
    public static class Naming
    {
        public const int MaxDisplayNameLength = 64;

        /// <summary>
        /// Keeps [A-Za-z0-9_] and maps everything else to '_'. Object references in XML are split on '.', so an id
        /// with a dot would resolve to a different (presumed, empty) object.
        /// </summary>
        public static string SanitizeIdPart(string value)
        {
            if (string.IsNullOrEmpty(value)) return "x";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' ? c : '_');
            return sb.ToString();
        }

        /// <summary>Bannerlord id of a CK3 hero for one battle: ck3xbl_&lt;battleId&gt;_&lt;key&gt; (PROTOCOL.md §3.2).</summary>
        public static string HeroObjectId(string battleId, string heroKey) =>
            "ck3xbl_" + SanitizeIdPart(battleId) + "_" + SanitizeIdPart(heroKey);

        /// <summary>
        /// Drops TextObject markup ("{...}" spans and stray braces), dots and control characters, collapses whitespace
        /// and caps the length. Returns <paramref name="fallback"/> when nothing printable is left.
        /// </summary>
        public static string SanitizeDisplayName(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var sb = new StringBuilder(value.Length);
            bool pendingSpace = false;
            int braceDepth = 0;
            foreach (char c in value)
            {
                if (c == '{')
                {
                    braceDepth++;
                    continue;
                }
                if (c == '}')
                {
                    if (braceDepth > 0) braceDepth--;
                    continue;
                }
                if (braceDepth > 0) continue;
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (c == '.' || char.IsControl(c)) continue;
                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
            string clean = sb.ToString();
            if (clean.Length > MaxDisplayNameLength) clean = clean.Substring(0, MaxDisplayNameLength).TrimEnd();
            return clean.Length == 0 ? fallback : clean;
        }
    }

    /// <summary>
    /// Hands out display names that are unique across both sides of a battle. The general is matched by name
    /// (GeneralsAndCaptainsAssignmentLogic.SetGeneralAgentOfTeam requires exactly one match), so duplicates get a
    /// " (2)", " (3)" suffix.
    /// </summary>
    public sealed class UniqueNames
    {
        private readonly HashSet<string> _taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Claim(string name)
        {
            string candidate = name;
            for (int n = 2; !_taken.Add(candidate); n++) candidate = name + " (" + n + ")";
            return candidate;
        }
    }
}
