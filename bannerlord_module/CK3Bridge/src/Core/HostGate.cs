using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace CK3Bridge
{
    /// <summary>Whether this module may take over the game, and why.</summary>
    public struct HostDecision
    {
        public bool Active;
        public string Reason;
    }

    /// <summary>
    /// The module is active only in a Bannerlord the Crowns & Blades bridge started. A player who ticks "CK3 Bridge" in the
    /// Bannerlord launcher for their own game gets an untouched game: no splash skip, no loading card, no heartbeat, no custom
    /// game. Proof, in order: the CK3XBL_HOST environment variable the bridge sets on the process it launches; else a fresh
    /// launch lease (bannerlord_launch.json in the shared folder, written by the bridge just before the launch) naming a live
    /// bridge process, for a game a store launcher restarted with its own environment. Pure .NET, so the self-check covers it.
    /// </summary>
    public static class HostGate
    {
        /// <summary>Set by the bridge (BannerlordLink.HostVariable): the bridge's process id.</summary>
        public const string Variable = "CK3XBL_HOST";
        /// <summary>The bridge's launch lease (BannerlordLink.LaunchLeaseFile): {"pid": bridge pid, "t": unix ms}.</summary>
        public const string LeaseFile = "bannerlord_launch.json";
        /// <summary>A lease older than this belongs to an earlier launch (the module loads within seconds of the launch).</summary>
        public static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(2);

        public static string LeasePath => Path.Combine(BridgeIo.Folder, LeaseFile);

        /// <summary>Decides for this process (environment, lease file, live processes).</summary>
        public static HostDecision Evaluate() =>
            Decide(Environment.GetEnvironmentVariable(Variable), BridgeIo.TryRead(LeasePath), DateTime.UtcNow, IsProcessAlive);

        /// <summary>The decision from its inputs (self-check).</summary>
        public static HostDecision Decide(string variable, string leaseJson, DateTime nowUtc, Func<int, bool> isProcessAlive)
        {
            if (!string.IsNullOrWhiteSpace(variable))
                return new HostDecision { Active = true, Reason = $"started by the bridge ({Variable}={variable.Trim()})" };
            if (string.IsNullOrWhiteSpace(leaseJson))
                return new HostDecision { Active = false, Reason = "not started by the Crowns & Blades bridge (no " + Variable + ", no launch lease)" };

            int pid;
            long t;
            try
            {
                JObject lease = JObject.Parse(leaseJson);
                pid = lease.Value<int?>("pid") ?? 0;
                t = lease.Value<long?>("t") ?? 0;
            }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is InvalidCastException || e is FormatException || e is OverflowException)
            {
                return new HostDecision { Active = false, Reason = "launch lease unreadable: " + e.Message };
            }
            DateTime written = DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime;
            TimeSpan age = nowUtc - written;
            if (pid <= 0 || age > LeaseLifetime || age < TimeSpan.FromSeconds(-10))
                return new HostDecision { Active = false, Reason = $"launch lease stale (written {age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s ago)" };
            if (!isProcessAlive(pid))
                return new HostDecision { Active = false, Reason = $"launch lease names bridge pid {pid}, which is not running" };
            return new HostDecision { Active = true, Reason = $"started by the bridge (launch lease, bridge pid {pid})" };
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }   // exists, but not ours to inspect
        }
    }

    /// <summary>Versions: the game's (bin/Win64_Shipping_Client/Version.xml) and the one this module was built for.</summary>
    public static class GameVersionInfo
    {
        /// <summary>The game version this build targets (SubModule.xml DependentVersion).</summary>
        public const string Target = "v1.4.8";
        public const string TargetSeries = "v1.4.";

        /// <summary>Singleplayer/@Value of a Version.xml ("v1.4.8"); null when missing or unreadable.</summary>
        public static string ReadGameVersion(string versionXml)
        {
            try
            {
                if (!File.Exists(versionXml)) return null;
                string value = XDocument.Load(versionXml).Root?.Element("Singleplayer")?.Attribute("Value")?.Value;
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Xml.XmlException)
            {
                return null;
            }
        }

        public static bool IsTarget(string version) =>
            version == null || version.StartsWith(TargetSeries, StringComparison.OrdinalIgnoreCase);
    }
}
