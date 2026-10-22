using System;
using System.Collections.Generic;
using NavalDLC.View;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CK3Bridge.Naval
{
    /// <summary>
    /// Views of the CK3 naval missions: the DLC's custom battle views plus the CK3 presenter, as CK3BattleViews does on land. Only
    /// [ViewMethod] statics here (ViewCreatorManager reads attribute [0] of every static method of a [ViewCreatorModule]).
    /// </summary>
    [ViewCreatorModule]
    public static class CK3NavalViews
    {
        [ViewMethod(NavalMissions.BattleMissionName)]
        public static MissionView[] OpenCK3NavalBattle(Mission mission) => CK3NavalViewsSupport.WithPresenter(NavalViews.OpenNavalBattleForCustomMission(mission), mission, "naval battle");

        [ViewMethod(NavalMissions.RaidMissionName)]
        public static MissionView[] OpenCK3NavalRaid(Mission mission) => CK3NavalViewsSupport.WithPresenter(NavalViews.OpenNavalRaidBattleForCustomMission(mission), mission, "landing");
    }

    /// <summary>Helpers of <see cref="CK3NavalViews"/>, kept out of the [ViewCreatorModule] class.</summary>
    internal static class CK3NavalViewsSupport
    {
        public static MissionView[] WithPresenter(MissionView[] stock, Mission mission, string what)
        {
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
                BridgeIo.Log($"CK3 {what} views failed, using the War Sails views: {e}");
                return stock;
            }
        }
    }
}
