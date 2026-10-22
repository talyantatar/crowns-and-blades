// PROTOCOL §40: in a relief battle the player may hold the walls although CK3 has him attack the besiegers (or storm their camp
// although CK3 has him defend). Bannerlord's siege puts the attackers outside, so the request's sides are swapped once its looks
// are settled (the appearance pass pairs the snapshot's sides with the request's), and the result is swapped back before the
// outcome is built.
using CK3xBL.Protocol;

namespace CK3xBL.Core.Abstractions;

public static class SiegeSides
{
    /// <summary>Swaps the sides of a siege request whose player is on the wrong side of the walls (SiegeInfo.PlayerDefends);
    /// returns true when it did.</summary>
    public static bool Settle(BattleRequest request)
    {
        if (request.Siege is not { } siege || siege.SidesSwapped || request.Kind != "siege") return false;
        bool playerAttacks = request.PlayerSide == "attacker";
        if (playerAttacks != siege.PlayerDefends) return false;
        (request.Attacker, request.Defender) = (request.Defender, request.Attacker);
        request.PlayerSide = playerAttacks ? "defender" : "attacker";
        if (request.Presentation is { } p) (p.AttackerCoaPng, p.DefenderCoaPng) = (p.DefenderCoaPng, p.AttackerCoaPng);
        siege.SidesSwapped = true;
        return true;
    }

    /// <summary>The request and result as CK3 sees the sides (unchanged when they were never swapped).</summary>
    public static (BattleRequest Request, BattleResult Result) AsCk3(BattleRequest request, BattleResult result)
    {
        if (request.Siege is not { SidesSwapped: true }) return (request, result);
        var r = new BattleRequest
        {
            Protocol = request.Protocol, Id = request.Id, Kind = request.Kind, FallbackScene = request.FallbackScene, Tourney = request.Tourney,
            TourneyPar = request.TourneyPar, Scene = request.Scene, Season = request.Season, TimeOfDay = request.TimeOfDay,
            PlayerSide = request.PlayerSide == "attacker" ? "defender" : "attacker", PlayerIsGeneral = request.PlayerIsGeneral,
            Presentation = request.Presentation, Scale = request.Scale, Attacker = request.Defender, Defender = request.Attacker,
            Siege = request.Siege, Balance = request.Balance, Looks = request.Looks,
        };
        var b = new BattleResult
        {
            Protocol = result.Protocol, Id = result.Id, Status = result.Status, PlayerRetreated = result.PlayerRetreated,
            DurationSeconds = result.DurationSeconds, Heroes = result.Heroes, Error = result.Error,
            Attacker = result.Defender, Defender = result.Attacker,
            Winner = result.Winner switch { "attacker" => "defender", "defender" => "attacker", var w => w },
        };
        return (r, b);
    }
}
