// The battle size that the free memory allows (PROTOCOL §31). Loading a field battle commits memory for the scene and for every
// agent on the field; when Windows has no commit left (RAM plus page file, with CK3 and everything else open), the allocation fails
// and Bannerlord crashes with an access violation. So a battle that would not fit is fought with fewer men on the field at once:
// the armies stay as they are, the rest come as reinforcements.
namespace CK3xBL.Core.Orchestration;

public static class MemoryGuard
{
    /// <summary>Bannerlord's field battle sizes (BannerlordConfig._battleSizes), and the larger ones the module sets for a field battle
    /// (up to 1500, RequestRules.MaxFieldBattleSize).</summary>
    public static readonly int[] FieldBattleSizes = { 200, 300, 400, 500, 600, 800, 1000, 1500 };

    /// <summary>Commit a field battle takes on top of an idle Bannerlord: the scene, and per agent on the field.</summary>
    public const int SceneMb = 2000, AgentMb = 6;

    /// <summary>
    /// The largest field battle size up to <paramref name="configured"/> whose commit fits in <paramref name="freeCommitMb"/>,
    /// keeping <paramref name="reserveMb"/> free; never below the smallest size. Null free memory (unknown) keeps the setting.
    /// </summary>
    public static int Fit(int configured, long? freeCommitMb, int reserveMb)
    {
        if (freeCommitMb is not { } free) return configured;
        long agents = (free - reserveMb - SceneMb) / AgentMb;
        if (agents >= configured) return configured;
        int fit = FieldBattleSizes[0];
        foreach (int size in FieldBattleSizes)
            if (size <= agents && size <= configured) fit = size;
        return Math.Min(fit, configured);
    }
}
