using System;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Missions.BattleScore;
using TaleWorlds.MountAndBlade.ViewModelCollection;
using TaleWorlds.MountAndBlade.ViewModelCollection.Scoreboard;

namespace CK3Bridge
{
    /// <summary>
    /// The stock custom battle scoreboard with two CK3 touches:
    ///  - the side headers read the CK3 army names instead of "Attackers"/"Defenders" (stock builds them from
    ///    str_battle_result_side, CustomBattleScoreboardVM.Initialize; party rows already use the combatant names,
    ///    SPScoreboardPartyVM). The names are set on the existing side VMs, because BattleObserverMissionLogic.SetObserver
    ///    has already replayed every built agent into them during Initialize, and re-applied after RefreshValues, which
    ///    resets SPScoreboardStatsVM.NameText from its private TextObject;
    ///  - after the battle result, "Done" (button or Enter, MissionGauntletBattleScore.OnMissionScreenTick) returns
    ///    through the native fade (BattleSession.BeginReturn) instead of ending the mission on the spot.
    /// </summary>
    public sealed class CK3ScoreboardVM : CustomBattleScoreboardVM
    {
        private readonly BattleSession _session;
        private readonly string _attackerName;
        private readonly string _defenderName;

        internal CK3ScoreboardVM(BattleScoreContext context, BattleSession session, string attackerName, string defenderName)
            : base(context)
        {
            _session = session;
            _attackerName = attackerName;
            _defenderName = defenderName;
        }

        public override void Initialize(IMissionScreen missionScreen, Mission mission, Action releaseSimulationSources, Action<bool> onToggle)
        {
            base.Initialize(missionScreen, mission, releaseSimulationSources, onToggle);
            ApplySideNames();
        }

        public override void RefreshValues()
        {
            base.RefreshValues();
            ApplySideNames();
        }

        public override void ExecuteQuitAction()
        {
            if (_session.Owns(_mission))
            {
                if (_session.Phase >= BattlePhase.FadingOut) return;   // already on the way back
                if (_session.Phase == BattlePhase.Result)
                {
                    _session.BeginReturn();
                    return;
                }
            }
            base.ExecuteQuitAction();
        }

        /// <summary>Closes the scoreboard if it is open (the player chose to stay on the field).</summary>
        public void Close() => OnToggle?.Invoke(false);

        private void ApplySideNames()
        {
            if (Attackers?.Score != null && !string.IsNullOrEmpty(_attackerName)) Attackers.Score.NameText = _attackerName;
            if (Defenders?.Score != null && !string.IsNullOrEmpty(_defenderName)) Defenders.Score.NameText = _defenderName;
        }
    }
}
