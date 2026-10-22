// The release this bridge belongs to, and the versions of its two partners it expects (one source: bridge/Directory.Build.props sets
// the assembly version; the constants below must match ck3_mod/ck3xbl/descriptor.mod and bannerlord_module/CK3Bridge/SubModule.xml,
// which the self test release.versions_agree checks).
using System.Reflection;

namespace CK3xBL.Core;

public static class BridgeVersion
{
    /// <summary>The informational version with any "+commit" build metadata, e.g. "0.9.0-beta+1a2b3c4".</summary>
    public static readonly string Full =
        typeof(BridgeVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BridgeVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>What players see: "0.9.0-beta".</summary>
    public static readonly string Display = Full.Split('+')[0];

    /// <summary>version= of the CK3 mod's descriptor.mod shipped with this bridge.</summary>
    public const string Mod = "0.9.0-beta";

    /// <summary>Version/@value of the Bannerlord module's SubModule.xml shipped with this bridge.</summary>
    public const string Module = "v0.9.0";

    /// <summary>The game versions this release was built and tested for. Others get a warning, never a refusal.</summary>
    public const string Ck3Target = "1.20";
    public const string BannerlordTarget = "v1.4.8";
    /// <summary>Bannerlord versions starting with this are treated as compatible (patch releases of the target).</summary>
    public const string BannerlordTargetSeries = "v1.4.";

    public const string RepositoryUrl = "https://github.com/graff0n/ck3-bannerlord-bridge";
    public const string IssuesUrl = RepositoryUrl + "/issues";
    public const string Copyright = "Copyright (c) 2026 TaLyAn";
    public const string ProductName = "Crowns & Blades";
}
