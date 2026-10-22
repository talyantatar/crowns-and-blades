using System;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;

namespace CK3Bridge
{
    /// <summary>
    /// The assault in waves (PROTOCOL.md §46). Bannerlord's custom siege (CustomSiegeMissionSpawnHandler.AfterStart, v1.4.8) splits
    /// the battle size between the sides by their numbers, so a host five times the garrison fills the walls at once. Here the field
    /// holds at most <see cref="_ratio"/> attackers per defender: the battle size is cut to the garrison plus that many attackers, the
    /// defenders' advantage factor gives the garrison its full share, and the rest of the attackers come in Bannerlord's waves as the
    /// first ones fall. Everything else is the stock handler's: no horses, a single phase, wave reinforcements.
    /// </summary>
    internal sealed class CK3SiegeSpawnHandler : MissionLogic
    {
        /// <summary>Never a smaller battle than this, whatever the garrison.</summary>
        public const int MinBattleSize = 200;

        private readonly CustomBattleCombatant _defender;
        private readonly CustomBattleCombatant _attacker;
        private readonly float _ratio;

        public CK3SiegeSpawnHandler(CustomBattleCombatant defender, CustomBattleCombatant attacker, float ratio)
        {
            _defender = defender;
            _attacker = attacker;
            _ratio = ratio;
        }

        public override void AfterStart()
        {
            var logic = Mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
            int defenders = _defender.NumberOfHealthyMembers, attackers = _attacker.NumberOfHealthyMembers;
            logic.SetSpawnHorses(BattleSideEnum.Defender, false);
            logic.SetSpawnHorses(BattleSideEnum.Attacker, false);

            float factor = 1f;
            var size = typeof(DefaultBattleMissionAgentSpawnLogic).GetField("_battleSize", BindingFlags.NonPublic | BindingFlags.Instance);
            if (size != null && defenders > 0)
            {
                int before = (int)size.GetValue(logic);
                int wanted = Math.Max(MinBattleSize, (int)Math.Ceiling(defenders * (1 + _ratio)));
                if (wanted < before) size.SetValue(logic, wanted);
                // The defenders' share of the battle size is weighted by this factor: enough to put the whole garrison on the walls.
                if (attackers > defenders * _ratio) factor = attackers / (defenders * _ratio);
                BridgeIo.Log($"assault in waves: {defenders} defenders, {attackers} attackers, battle size {Math.Min(wanted, before)} " +
                             $"(was {before}), defenders' advantage x{factor:0.00}");
            }
            else BridgeIo.Log("assault in waves: the spawn logic's battle size not found; Bannerlord's split stays");

            var settings = new MissionSpawnSettings(
                MissionSpawnSettings.InitialSpawnMethod.BattleSizeAllocating,
                MissionSpawnSettings.ReinforcementTimingMethod.GlobalTimer,
                MissionSpawnSettings.ReinforcementSpawnMethod.Wave,
                3f, 0f, 0f, 0.5f, 0, 0f, 0f, factor, 0.75f);
            logic.InitWithSinglePhase(defenders, attackers, defenders, attackers, false, false, in settings);
        }
    }
}
