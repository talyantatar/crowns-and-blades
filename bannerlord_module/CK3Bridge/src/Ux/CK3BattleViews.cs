using System;
using System.Collections.Generic;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.GauntletUI.Mission.Singleplayer;
using TaleWorlds.MountAndBlade.Missions.BattleScore;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.MissionViews.Singleplayer;

namespace CK3Bridge
{
    /// <summary>
    /// Views of the "CK3Battle" mission (ViewCreatorManager collects [ViewCreatorModule] types of every assembly that
    /// references the View assembly): the full stock custom battle UI
    /// (CustomBattleViews.OpenCustomBattleMission: escape menu, scoreboard, order of battle, deployment, music, objective
    /// view, face cache...) with the scoreboard's view model swapped for <see cref="CK3ScoreboardVM"/>, plus
    /// <see cref="CK3HandoffView"/>, the presenter of the battle session.
    ///
    /// Views are created inside MissionState.OpenNew, before Mission.AfterStart, so the presenter is attached before
    /// the session decides anything about holding or showing the battle.
    ///
    /// This class must contain nothing but [ViewMethod] statics: ViewCreatorManager.CheckAssemblyScreens reads attribute
    /// [0] of every static method (public or not, compiler-generated local functions included) without a length check,
    /// and an exception there breaks ViewCreatorManager's static constructor, i.e. every mission's views. Helpers live in
    /// <see cref="CK3BattleViewsSupport"/>; the self-check (GameBindings) enforces the rule.
    /// </summary>
    [ViewCreatorModule]
    public static class CK3BattleViews
    {
        [ViewMethod(CK3Missions.OwnFieldMissionName)]
        public static MissionView[] OpenCK3Battle(Mission mission)
        {
            MissionView[] stock = CustomBattleViews.OpenCustomBattleMission(mission);
            BattleSession session = BattleSession.ForOpeningMission(mission);
            if (session == null) return stock;
            try
            {
                var views = new List<MissionView>(stock);
                CK3ScoreboardVM scoreboard = CK3BattleViewsSupport.ReplaceScoreboard(views, mission, session);
                views.Add(new CK3HandoffView(session, scoreboard));
                session.AttachPresenter();
                return views.ToArray();
            }
            catch (Exception e)
            {
                BridgeIo.Log("CK3 battle views failed, using the stock custom battle views: " + e);
                return stock;
            }
        }

        /// <summary>An assault on a besieged holding (PROTOCOL.md §39): the stock custom siege views plus the CK3 presenter.</summary>
        [ViewMethod(CK3Missions.SiegeMissionName)]
        public static MissionView[] OpenCK3Siege(Mission mission)
        {
            MissionView[] stock = CustomBattleViews.OpenCustomSiegeBattleMission(mission);
            BattleSession session = BattleSession.ForOpeningMission(mission);
            if (session == null) return stock;
            try
            {
                var views = new List<MissionView>(stock);
                CK3ScoreboardVM scoreboard = CK3BattleViewsSupport.ReplaceScoreboard(views, mission, session);
                views.Add(new CK3HandoffView(session, scoreboard));
                session.AttachPresenter();
                return views.ToArray();
            }
            catch (Exception e)
            {
                BridgeIo.Log("CK3 siege views failed, using the stock custom siege views: " + e);
                return stock;
            }
        }

        /// <summary>A noble single combat in an arena (PROTOCOL.md §27): the stock lords hall views plus the CK3 presenter.</summary>
        [ViewMethod(CK3Missions.ArenaMissionName)]
        public static MissionView[] OpenCK3Arena(Mission mission)
        {
            MissionView[] stock = CustomBattleViews.OpenCustomBattleLordsHallMission(mission);
            BattleSession session = BattleSession.ForOpeningMission(mission);
            if (session == null) return stock;
            try
            {
                var views = new List<MissionView>(stock);
                CK3ScoreboardVM scoreboard = CK3BattleViewsSupport.ReplaceScoreboard(views, mission, session);
                views.Add(new CK3HandoffView(session, scoreboard));
                session.AttachPresenter();
                return views.ToArray();
            }
            catch (Exception e)
            {
                BridgeIo.Log("CK3 arena views failed, using the stock lords hall views: " + e);
                return stock;
            }
        }
    }

    /// <summary>Helpers of <see cref="CK3BattleViews"/>, kept out of the [ViewCreatorModule] class (see there).</summary>
    internal static class CK3BattleViewsSupport
    {
        /// <summary>
        /// Replaces the stock battle score view (created with a plain CustomBattleScoreboardVM in CustomBattleViews) by
        /// one with <see cref="CK3ScoreboardVM"/>. ViewCreator resolves the override type, so the result is the same
        /// MissionGauntletBattleScore the stock list holds. Returns null (stock view kept) when it is not found.
        /// </summary>
        public static CK3ScoreboardVM ReplaceScoreboard(List<MissionView> views, Mission mission, BattleSession session)
        {
            int index = views.FindIndex(v => v is MissionGauntletBattleScore || v is MissionBattleScoreUIHandler);
            if (index < 0)
            {
                BridgeIo.Log("stock battle score view not found; scoreboard keeps its stock side names");
                return null;
            }
            var scoreboard = new CK3ScoreboardVM(new CustomBattleScoreContext(mission), session,
                session.Armies.Attacker.Combatant.Name.ToString(), session.Armies.Defender.Combatant.Name.ToString());
            views[index] = ViewCreator.CreateMissionBattleScoreUIHandler(mission, scoreboard);
            return scoreboard;
        }
    }
}
