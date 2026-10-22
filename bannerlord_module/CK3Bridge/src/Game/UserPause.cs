using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace CK3Bridge
{
    /// <summary>
    /// Pauses that stop a mission from ending. While any "active state disable request" is registered,
    /// GameStateManager.OnTick runs only ActiveState.OnIdleTick. MissionState.OnIdleTick
    /// only idle-ticks a Continuing mission, so EndMission (processed by Mission.OnTick -> CheckMissionEnd) and the pop of a
    /// finished MissionState both wait until the request is gone. In a battle these requests come from:
    ///  - the escape menu (MissionGauntletEscapeMenuBase.OnEscapeMenuToggled), which also opens on focus loss while fighting;
    ///  - inquiries shown with pauseGameActiveState (GauntletQueryManager.HandleQueryCreated).
    /// Both can be closed the way the player would close them. The in-mission options screen only pauses the engine
    /// (MBCommon.PauseGameEngine), which does not stop EndMission.
    /// </summary>
    internal static class UserPause
    {
        public static bool IsActive => Game.Current?.GameStateManager?.ActiveStateDisabledByUser ?? false;

        /// <summary>
        /// Closes the escape menu and open inquiries so the mission can end. Returns true when nothing holds the mission
        /// state any more; false while something else (that must not be closed blindly) still does.
        /// </summary>
        public static bool TryRelease(Mission mission)
        {
            if (!IsActive) return true;
            if (mission != null)
            {
                foreach (MissionBehavior behavior in mission.MissionBehaviors)
                {
                    // OnEscape toggles an open escape menu closed (MissionGauntletEscapeMenuBase.OnEscape).
                    if (behavior is MissionEscapeMenuView menu && menu.IsActive && menu.OnEscape())
                        BridgeIo.Log("closed the escape menu so the battle can end");
                }
            }
            if (IsActive && InformationManager.IsAnyInquiryActive())
            {
                // One per call: closing an inquiry shows the next queued one, which the next tick closes.
                InformationManager.HideInquiry();
                BridgeIo.Log("closed an inquiry so the battle can end");
            }
            return !IsActive;
        }
    }
}
