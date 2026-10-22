using System;
using System.Collections.Generic;
using System.Globalization;

namespace CK3Bridge
{
    public enum BridgeCommandKind
    {
        Unknown,
        Preload,
        Go,
        Cancel,
        Abort,
        ReturnNow,
        Quit,
        Foreground,
        Background,
        HandoffTo,
        TitleInEngine,
    }

    /// <summary>One line of command.txt (PROTOCOL.md §3.3).</summary>
    public sealed class BridgeCommand
    {
        public BridgeCommandKind Kind { get; private set; }

        /// <summary>Target window for <see cref="BridgeCommandKind.HandoffTo"/>; 0 otherwise.</summary>
        public long Hwnd { get; private set; }

        /// <summary>
        /// The battle a <see cref="BridgeCommandKind.Cancel"/> / <see cref="BridgeCommandKind.Abort"/> names
        /// ("cancel &lt;battleId&gt;", PROTOCOL.md §8.3); null for a bare command, which means whatever battle there is.
        /// </summary>
        public string BattleId { get; private set; }

        public string Raw { get; private set; }

        /// <summary>True when this command applies to <paramref name="battleId"/>: it names that battle or names none.</summary>
        public bool AppliesTo(string battleId) => BattleId == null || string.Equals(BattleId, battleId, StringComparison.Ordinal);

        /// <summary>
        /// Parses command.txt. The protocol says one command per write, but a fast bridge may overwrite or append
        /// before the module polls, so every non-empty line is taken in order.
        /// </summary>
        public static List<BridgeCommand> ParseAll(string text)
        {
            var list = new List<BridgeCommand>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) list.Add(Parse(trimmed));
            }
            return list;
        }

        public static BridgeCommand Parse(string line)
        {
            string[] parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var command = new BridgeCommand { Raw = line.Trim(), Kind = BridgeCommandKind.Unknown };
            if (parts.Length == 0) return command;
            switch (parts[0].ToLowerInvariant())
            {
                case "preload": command.Kind = BridgeCommandKind.Preload; break;
                case "go": command.Kind = BridgeCommandKind.Go; break;
                case "cancel":
                    command.Kind = BridgeCommandKind.Cancel;
                    command.BattleId = parts.Length >= 2 ? parts[1] : null;
                    break;
                case "abort":
                    command.Kind = BridgeCommandKind.Abort;
                    command.BattleId = parts.Length >= 2 ? parts[1] : null;
                    break;
                case "return_now": command.Kind = BridgeCommandKind.ReturnNow; break;
                case "quit": command.Kind = BridgeCommandKind.Quit; break;
                case "fg": command.Kind = BridgeCommandKind.Foreground; break;
                case "bg": command.Kind = BridgeCommandKind.Background; break;
                case "title_in_engine": command.Kind = BridgeCommandKind.TitleInEngine; break;
                case "handoff_to":
                    if (parts.Length >= 2 && TryParseHwnd(parts[1], out long hwnd))
                    {
                        command.Kind = BridgeCommandKind.HandoffTo;
                        command.Hwnd = hwnd;
                    }
                    break;
            }
            return command;
        }

        /// <summary>Accepts decimal ("123456") or hexadecimal ("0x1E240") window handles.</summary>
        public static bool TryParseHwnd(string text, out long hwnd)
        {
            hwnd = 0;
            if (string.IsNullOrEmpty(text)) return false;
            bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hwnd)
                : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out hwnd);
            return ok && hwnd != 0;
        }
    }
}
