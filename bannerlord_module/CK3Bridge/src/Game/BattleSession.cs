using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    internal enum StopReason
    {
        None,
        Cancel,
        Abort,
    }

    /// <summary>
    /// One CK3 battle from request to release: the phase machine
    ///   loading -> held -> visible -> fighting -> result -> fading_out -> black -> (idle state) ended
    /// plus bridge commands (go, cancel, abort, return_now). Runs on the main thread: <see cref="Tick"/> from
    /// OnApplicationTick (real time), mission callbacks from <see cref="BattleTracker"/>. "black" means the mission has
    /// ended and the final result.json is written (<see cref="OnMissionEnded"/>).
    ///
    /// A stop or return never starts while the escape menu or a pausing inquiry holds the mission state (the mission
    /// could not end then, see <see cref="UserPause"/>): <see cref="Tick"/> closes them first, and waits (logged once)
    /// while anything else holds it.
    ///
    /// Presenter (CK3HandoffView, attached by CK3BattleViews while the mission is created): it owns showing the battle.
    /// It keeps the loading card up while the battle must stay hidden (<see cref="MarkHeld"/>), reveals it
    /// (<see cref="OnRevealed"/>), reports <see cref="OnFighting"/>, runs the result countdown and calls
    /// <see cref="BeginReturn"/>. The session owns everything else: holding (paused) and the held status, the native
    /// fade to black and EndMission on the way back, stops, results, release. Without a presenter (the "CustomBattle"
    /// fallback) the session also reveals, detects fighting and counts down itself.
    /// </summary>
    internal sealed class BattleSession
    {
        /// <summary>Native fade to black on the way back (ScreenFadeController, global layer above every mission layer).</summary>
        private const float FadeOutSeconds = 0.6f;

        /// <summary>The mission ends this long after the fade started (the screen is fully black by then).</summary>
        private const float EndAfterSeconds = 0.75f;

        /// <summary>Clears a black left over from the previous battle's return; the title card is black underneath.</summary>
        private const float ClearBlackSeconds = 0.1f;

        private readonly StatusReporter _status;
        private StopReason _stopReason;
        private bool _endRequested;
        private float _countdownLeft = -1f;
        private float _fadeElapsed;
        private bool _returnRequested;
        private bool _pauseWaitLogged;
        private BattleResult _unwritten;
        private float _retryIn;

        private BattleSession(BattleRequest request, RequestExtras extras, BattleArmies armies, StatusReporter status)
        {
            Request = request;
            Extras = extras;
            Armies = armies;
            _status = status;
            Texts = CardTexts.From(request.Presentation, armies.PlayerSide.Combatant.Name.ToString(),
                armies.EnemySide.Combatant.Name.ToString(), GameFonts.Fold);
            InEngineTitle = request.Presentation.InEngineTitle;
        }

        public static BattleSession Current { get; private set; }

        public BattleRequest Request { get; }
        public RequestExtras Extras { get; }
        public BattleArmies Armies { get; }
        public CardTexts Texts { get; }

        /// <summary>
        /// The module shows the title on its loading card and in-engine card (PROTOCOL.md §13.6): presentation.inEngineTitle,
        /// or true once the bridge said "title_in_engine" because its own card could not be shown. False: both cards are only
        /// the black backdrop, the bridge's card dissolves into the battle.
        /// </summary>
        public bool InEngineTitle { get; private set; }

        /// <summary>The texts of the title block on the loading card and the in-engine card; null = no title block.</summary>
        public CardTexts TitleCard => InEngineTitle ? Texts : null;
        public string BattleId => Request.Id;
        public Mission Mission { get; private set; }
        public BattlePhase Phase { get; private set; } = BattlePhase.Loading;
        public bool GoReceived { get; private set; }
        public bool PresenterAttached { get; private set; }

        /// <summary>Preloading and the bridge has not said "go" yet.</summary>
        public bool AwaitingGo => Request.Presentation.Preload && !GoReceived;

        /// <summary>A cancel or abort is pending: the battle is never shown and ends as soon as that is safe.</summary>
        public bool IsStopping => _stopReason != StopReason.None;

        /// <summary>The final result.json content once the mission has ended (null before).</summary>
        public BattleResult Final { get; private set; }

        /// <summary>The escape menu, options or an inquiry paused the game, or the mission itself is paused.</summary>
        public static bool IsGamePaused => MBCommon.IsPaused || (MissionState.Current?.Paused ?? false);

        /// <summary>
        /// Opens the mission for a validated request. On failure everything created is released and the exception is
        /// rethrown for the controller to report.
        /// </summary>
        public static BattleSession Start(BattleRequest request, RequestExtras extras, MissionSetup setup, BattleArmies armies, StatusReporter status)
        {
            var session = new BattleSession(request, extras, armies, status);
            Current = session;
            try
            {
                // Until a presenter takes over (CK3BattleViews, inside OpenNew), the user's focus pause is suppressed: the
                // mission loads and may start rendering while CK3 has the focus.
                RuntimeSettings.SuppressFocusPause();
                bool playerIsAttacker = armies.PlayerIsAttacker;
                CoatOfArmsTextures.Load(playerIsAttacker ? request.Presentation.AttackerCoaPng : request.Presentation.DefenderCoaPng,
                    playerIsAttacker ? request.Presentation.DefenderCoaPng : request.Presentation.AttackerCoaPng);
                status.Set(StatusName(BattlePhase.Loading), request.Id);
                session.Publish(armies.Accounting.Snapshot("started", "none", false, 0f, null));
                CK3BuffRegistry.Begin(request, armies);
                session.Mission = CK3Missions.Open(setup, armies);
                return session;
            }
            catch
            {
                Current = null;
                // A failed open must not leave this battle's buffs on the next stock mission of the custom game.
                CK3BuffRegistry.End();
                armies.Release();
                CoatOfArmsTextures.Clear();
                Game.Current.PlayerTroop = null;
                RuntimeSettings.RestoreFocusPause();
                throw;
            }
        }

        /// <summary>
        /// The session that is opening <paramref name="mission"/> right now (views are created inside MissionState.OpenNew,
        /// before Start has stored the mission), or the session owning it; null for any other mission.
        /// </summary>
        public static BattleSession ForOpeningMission(Mission mission)
        {
            BattleSession session = Current;
            if (session == null || mission == null || session.Final != null) return null;
            return session.Mission == null || session.Mission == mission ? session : null;
        }

        /// <summary>True for the mission this session opened (OnBeforeMissionBehaviorInitialize attaches our logics).</summary>
        public bool Owns(Mission mission) => mission != null && mission == Mission;

        // ------------------------------------------------------------------ bridge commands

        public void Go()
        {
            GoReceived = true;
        }

        public void Stop(StopReason reason)
        {
            if (_stopReason == StopReason.None && Final == null) _stopReason = reason;
        }

        /// <summary>The battle is still behind the loading card and not ending, so it can take the title card (<see cref="TitleInEngineLatch.CanShow"/>).</summary>
        public bool CanShowTitleInEngine => TitleInEngineLatch.CanShow(Phase, IsStopping, Final != null);

        /// <summary>
        /// "title_in_engine": the bridge could not show its title card, so this battle shows the module's own (PROTOCOL.md
        /// §13.6). False when it is too late (<see cref="CanShowTitleInEngine"/>).
        /// </summary>
        public bool ShowTitleInEngine()
        {
            if (!CanShowTitleInEngine) return false;
            InEngineTitle = true;
            return true;
        }

        public void ReturnNow()
        {
            if (Phase == BattlePhase.Result) _returnRequested = true;
            else BridgeIo.Log($"return_now ignored in phase {Phase}");
        }

        // ------------------------------------------------------------------ presenter

        /// <summary>
        /// A presenter will show the battle. Its IsOpeningEscapeMenuOnFocusChangeAllowed gate replaces the in-memory
        /// "Stop Game On Focus Lost" suppression, so the user's setting is put back right away (it can then never be
        /// saved changed through Options -> Apply).
        /// </summary>
        public void AttachPresenter()
        {
            PresenterAttached = true;
            RuntimeSettings.RestoreFocusPause();
        }

        /// <summary>The presenter keeps the loaded battle behind the loading card: paused, status "held".</summary>
        public void MarkHeld()
        {
            if (Phase != BattlePhase.Loading || IsStopping || Final != null) return;
            SetPaused(true);
            SetPhase(BattlePhase.Held);
        }

        /// <summary>The loading card is gone and the title card is up: unpause, status "visible".</summary>
        public void OnRevealed()
        {
            if (Final != null || Phase >= BattlePhase.Visible) return;
            SetPaused(false);
            SetPhase(BattlePhase.Visible);
        }

        /// <summary>Title card gone and deployment finished: status "fighting".</summary>
        public void OnFighting()
        {
            if (Phase == BattlePhase.Visible) SetPhase(BattlePhase.Fighting);
        }

        /// <summary>
        /// The player (Enter, scoreboard "Done", countdown) returns to CK3: native fade, then EndMission. Starts at once
        /// unless a menu or dialog pauses the game; then <see cref="Tick"/> closes it first.
        /// </summary>
        public void BeginReturn()
        {
            if (Final != null || Phase != BattlePhase.Result) return;
            _returnRequested = true;
            if (!UserPause.IsActive) StartFadeOut();
        }

        // ------------------------------------------------------------------ mission callbacks (BattleTracker)

        /// <summary>
        /// Mission.AfterStart finished loading. With a presenter the battle stays paused until it is revealed (else
        /// a battle without an Order of Battle screen would start fighting behind the loading card); held when preloading.
        /// </summary>
        public void OnMissionLoaded()
        {
            if (IsStopping) return;   // Tick ends the mission now that it can be ended safely
            if (AwaitingGo)
            {
                SetPaused(true);
                SetPhase(BattlePhase.Held);
            }
            else if (PresenterAttached)
            {
                SetPaused(true);      // OnRevealed unpauses
            }
            else
            {
                SetPhase(BattlePhase.Visible);
            }
        }

        public void OnResultReady(BattleResult provisional)
        {
            Publish(provisional);
            int seconds = Request.Presentation.ReturnCountdownSeconds;
            _countdownLeft = seconds < 0 ? -1f : seconds;
            SetPhase(BattlePhase.Result);
        }

        public void OnMissionEnded(BattleResult final)
        {
            Final = final;
            Publish(final);
            BridgeIo.Log($"result {final.Status} for {BattleId}: winner={final.Winner} retreated={final.PlayerRetreated} " +
                         $"A k/w/r={final.Attacker.Killed}/{final.Attacker.Wounded}/{final.Attacker.Routed} " +
                         $"D k/w/r={final.Defender.Killed}/{final.Defender.Wounded}/{final.Defender.Routed} t={final.DurationSeconds}s");
            if (Phase < BattlePhase.Black) SetPhase(BattlePhase.Black);
        }

        /// <summary>Status of a mission that ended without a result: requested stops win, otherwise it was abandoned.</summary>
        public string EndStatus(bool fought)
        {
            switch (_stopReason)
            {
                case StopReason.Cancel: return "cancelled";
                case StopReason.Abort: return "aborted";
                default: return fought ? "finished" : "aborted";
            }
        }

        // ------------------------------------------------------------------ main-thread tick

        public void Tick(float realDt)
        {
            if (_unwritten != null && (_retryIn -= realDt) <= 0f) Publish(_unwritten);
            Mission mission = Mission;
            if (mission == null) return;
            if (_endRequested)
            {
                // EndMission is processed, and the ended mission state popped, only while nothing holds the mission state
                // (e.g. an inquiry that opened during the fade).
                ReleaseUserPause(mission, "ending the mission");
                return;
            }
            if (Final != null) return;
            bool continuing = mission.CurrentState == Mission.State.Continuing;

            if (IsStopping && Phase < BattlePhase.FadingOut)
            {
                // A mission is only ended once it has finished loading (EndMission mid-load is not a supported path). A
                // battle the player can see fades out first; a hidden one (loading, held) ends at once. Either way the
                // escape menu (opened by focus loss while fighting) is closed first, or EndMission would never run.
                if (!continuing || !ReleaseUserPause(mission, "stopping the battle")) return;
                if (Phase >= BattlePhase.Visible)
                {
                    StartFadeOut();
                }
                else
                {
                    EndMission(mission);
                    return;
                }
            }

            // The user's "Stop Game On Focus Lost" applies again once they fight in front (no-presenter path; with a
            // presenter it was never suppressed). Restoring it while Bannerlord is still behind CK3 would open the escape
            // menu (and pause) as soon as the scene renders (MissionGauntletSingleplayerEscapeMenu.OnFocusChangeOnGameWindow).
            if (Phase == BattlePhase.Fighting && RuntimeSettings.IsFocusPauseSuppressed && WindowResidency.IsForeground)
                RuntimeSettings.RestoreFocusPause();

            switch (Phase)
            {
                case BattlePhase.Held:
                    if (GoReceived && !PresenterAttached)
                    {
                        SetPaused(false);
                        SetPhase(BattlePhase.Visible);
                    }
                    break;
                case BattlePhase.Visible:
                    // Deployment ends when the player confirms the Order of Battle, or by itself with < 20 troops.
                    if (!PresenterAttached && mission.IsDeploymentFinished) SetPhase(BattlePhase.Fighting);
                    break;
                case BattlePhase.Result:
                    if (!_returnRequested && !PresenterAttached && _countdownLeft >= 0f && !IsGamePaused)
                    {
                        _countdownLeft -= realDt;
                        if (_countdownLeft <= 0f) _returnRequested = true;
                    }
                    if (_returnRequested && ReleaseUserPause(mission, "the return to CK3")) StartFadeOut();
                    break;
                case BattlePhase.FadingOut:
                    // "black" follows from OnMissionEnded, once the mission has really ended and the final result is out.
                    _fadeElapsed += realDt;
                    if (_fadeElapsed >= EndAfterSeconds && continuing && ReleaseUserPause(mission, "ending the mission"))
                        EndMission(mission);
                    break;
            }
        }

        /// <summary>
        /// Called once the mission state has been popped (idle state active again): releases the battle's objects and
        /// the focus override. Returns the transient status to show before "idle".
        /// <paramref name="reasonIfUnfinished"/> is the result error when the tracker never wrote a final result.
        /// </summary>
        public string Complete(string reasonIfUnfinished = "mission ended without a CK3 battle tracker")
        {
            if (Final == null)
            {
                // Never leave the bridge waiting on a mission that is gone.
                Final = Armies.Accounting.Snapshot(EndStatus(false), "none", false, 0f, reasonIfUnfinished);
                Publish(Final, lastChance: true);
            }
            else if (_unwritten != null)
            {
                Publish(_unwritten, lastChance: true);
            }
            if (_unwritten != null) BridgeIo.Log($"result.json for {BattleId} could not be written ({_unwritten.Status})");
            CK3BuffRegistry.End();
            Armies.Release();
            CoatOfArmsTextures.Clear();
            if (Game.Current != null) Game.Current.PlayerTroop = null;
            RuntimeSettings.RestoreFocusPause();
            SetPhase(BattlePhase.Ended);
            Current = null;
            switch (Final.Status)
            {
                case "cancelled": return "cancelled";
                case "aborted": return "aborted";
                case "error": return "error";
                default: return "ended";
            }
        }

        /// <summary>
        /// Writes result.json. A failed write (the bridge holding the file without FileShare.Delete) is retried every
        /// 0.25 s from Tick; a newer result replaces an unwritten older one. Retries are single attempts (no waiting on
        /// the main thread) and only the first failure and the recovery are logged; <paramref name="lastChance"/> (the
        /// session is being released) waits briefly for a reader again.
        /// </summary>
        private void Publish(BattleResult result, bool lastChance = false)
        {
            bool retrying = _unwritten != null;
            if (BridgeIo.TryWriteJsonAtomic(BridgeIo.ResultPath, result,
                    attempts: retrying && !lastChance ? 1 : BridgeIo.DefaultWriteAttempts, logFailure: !retrying))
            {
                if (retrying) BridgeIo.Log($"result.json for {BattleId} written ({result.Status}) after retrying");
                _unwritten = null;
                return;
            }
            _unwritten = result;
            _retryIn = 0.25f;
        }

        /// <summary>
        /// Closes what pauses the mission state so a stop or return can go on (<see cref="UserPause.TryRelease"/>). Returns
        /// false, logging once per wait, while something that must not be closed blindly still holds it.
        /// </summary>
        private bool ReleaseUserPause(Mission mission, string what)
        {
            if (UserPause.TryRelease(mission))
            {
                if (_pauseWaitLogged) BridgeIo.Log($"{BattleId}: {what} goes on, the game is no longer paused");
                _pauseWaitLogged = false;
                return true;
            }
            if (!_pauseWaitLogged) BridgeIo.Log($"{BattleId}: {what} waits until the menu or dialog pausing the game is closed");
            _pauseWaitLogged = true;
            return false;
        }

        /// <summary>
        /// Native fade to black (GauntletCameraFadeView stays black until BeginFadeIn, so Bannerlord remains black
        /// through EndMission and idle), then EndMission after <see cref="EndAfterSeconds"/> in Tick.
        /// </summary>
        private void StartFadeOut()
        {
            if (Phase >= BattlePhase.FadingOut) return;
            _fadeElapsed = 0f;
            ScreenFadeController.BeginFadeOut(FadeOutSeconds);
            SetPhase(BattlePhase.FadingOut);
        }

        private void EndMission(Mission mission)
        {
            _endRequested = true;
            SetPaused(false);
            mission.EndMission();
        }

        private void SetPhase(BattlePhase phase)
        {
            if (Phase == phase) return;
            Phase = phase;
            switch (phase)
            {
                case BattlePhase.Held:
                    // Loading is complete; holding the card is intentional, not a stuck load, so the engine's loading-stuck
                    // watchdog that the loading window started (StartLoadingStuckCheckState(720f)) is stopped.
                    Utilities.EndLoadingStuckCheckState();
                    break;
                case BattlePhase.Visible:
                    // A black left by the previous battle's return would cover the title card (the fade layer sits above
                    // every mission layer) and block the escape menu (ScreenFadeController.IsFadeActive).
                    ScreenFadeController.BeginFadeIn(ClearBlackSeconds);
                    break;
                case BattlePhase.Result:
                    // No-presenter path: suppressed again for the swap back to CK3; restored at completion.
                    if (!PresenterAttached) RuntimeSettings.SuppressFocusPause();
                    break;
            }
            if (phase != BattlePhase.Ended) _status.Set(StatusName(phase), BattleId);
        }

        private static void SetPaused(bool paused)
        {
            // MissionState.OnTick runs the mission with dt = 0 while Paused.
            if (MissionState.Current != null) MissionState.Current.Paused = paused;
        }

        public static string StatusName(BattlePhase phase)
        {
            switch (phase)
            {
                case BattlePhase.Loading: return "loading";
                case BattlePhase.Held: return "held";
                case BattlePhase.Visible: return "visible";
                case BattlePhase.Fighting: return "fighting";
                case BattlePhase.Result: return "result";
                case BattlePhase.FadingOut: return "fading_out";
                case BattlePhase.Black: return "black";
                default: return "ended";
            }
        }
    }
}
