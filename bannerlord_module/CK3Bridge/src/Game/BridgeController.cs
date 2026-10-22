using System;
using System.Diagnostics;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// Process-level state machine, driven from OnApplicationTick on the engine main thread:
    ///   booting (intro skipped, CustomGame loading) -> idle (CK3IdleState) -> battle session -> ended/cancelled/aborted
    ///   (2 s) -> idle.
    /// Polls command.txt and request.json every 250 ms of wall time (ticks slow down while cloaked, so frames are never
    /// counted) and keeps bannerlord_status.json fresh.
    /// </summary>
    internal sealed class BridgeController
    {
        private const double PollSeconds = 0.25;
        private const double TransientStatusSeconds = 2.0;
        private const double GameStartRetrySeconds = 5.0;
        private const int MaxGameStartAttempts = 3;

        private readonly StatusReporter _status = new StatusReporter();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastTick;
        private double _nextPoll;
        private double _transientUntil = -1.0;
        private double _lastGameStart = double.NegativeInfinity;
        private int _gameStartAttempts;
        private bool _introSkipped;
        private bool _quitting;
        private bool _loadingCardFailed;
        private bool? _highQoS;

        /// <summary>A "title_in_engine" that reached a request before its battle started (see <see cref="ShowTitleInEngine"/>).</summary>
        private readonly TitleInEngineLatch _titleInEngine = new TitleInEngineLatch();

        public void OnLoad()
        {
            BridgeIo.Log($"CK3Bridge loaded (protocol {ProtocolInfo.Version}, pid {Process.GetCurrentProcess().Id})");
            WindowResidency.DisableGhosting();
            _status.Set("booting", null);
        }

        /// <summary>
        /// Puts the CK3 loading card in place of the stock loading screen (and back after anything replaced it). Called
        /// from OnBeforeInitialModuleScreenSetAsRoot, before Gauntlet installs its default on its first tick, and every
        /// tick. Guarded on its own so a failure can never stop the rest of the tick.
        /// </summary>
        public void InstallLoadingCard()
        {
            if (_loadingCardFailed) return;
            try
            {
                CK3LoadingWindowManager.EnsureInstalled();
            }
            catch (Exception e)
            {
                _loadingCardFailed = true;
                BridgeIo.Log("loading card install failed: " + e);
            }
        }

        public void Tick()
        {
            InstallLoadingCard();
            double now = _clock.Elapsed.TotalSeconds;
            float realDt = (float)(now - _lastTick);
            _lastTick = now;
            bool pollDue = now >= _nextPoll;
            if (pollDue)
            {
                _nextPoll = now + PollSeconds;
                PumpCommands();
            }

            if (!_quitting)
            {
                GameState active = GameStateManager.Current?.ActiveState;
                BootStep(active, now);

                BattleSession session = BattleSession.Current;
                session?.Tick(realDt);
                if (session?.Mission != null && active is CK3IdleState)
                {
                    // MissionState was popped (MissionState.OnTick): the battle is over, release it.
                    string finalStatus = session.Complete();
                    ShowTransient(finalStatus, session.BattleId, session.Final?.Error);
                    UpdateQoS();
                }
                else if (session == null && active is CK3IdleState)
                {
                    _gameStartAttempts = 0;
                    if (now >= _transientUntil) _status.Set("idle", null);
                    if (pollDue) TryStartBattle();
                }
            }
            _status.Tick();
        }

        /// <summary>Attach the CK3 logics before the behaviours' OnBehaviorInitialize loop (Mission.AfterStart).</summary>
        public void OnBeforeMissionBehaviorInitialize(Mission mission)
        {
            BattleSession session = BattleSession.Current;
            if (session == null || !session.Owns(mission)) return;
            mission.AddMissionBehavior(new BattleTracker(session));
            mission.AddMissionBehavior(new CK3FaceGuard(session.Armies.HeroByCharacter));
            BridgeIo.Log($"CK3 logics attached to {mission.SceneName}");
        }

        /// <summary>
        /// Runs before BannerlordConfig.Save on exit (see RuntimeSettings) and when the game ends otherwise (exit to main
        /// menu). A battle still open is reported as aborted so the bridge never waits on a dead mission.
        /// </summary>
        public void OnGameEnd()
        {
            BattleSession session = BattleSession.Current;
            if (session != null)
            {
                if (session.Final == null) session.Stop(StopReason.Abort);
                string finalStatus = session.Complete("the game ended during the battle");
                BridgeIo.Log($"game ended during battle {session.BattleId}: {finalStatus}");
            }
            CK3BuffRegistry.End();
            RuntimeSettings.RestoreAll(applyNative: false);
            // A battle's native fade stays black until someone fades in (GauntletCameraFadeView); outside our battles
            // nothing would, so a game ended mid-return must not leave the main menu black.
            ScreenFadeController.BeginFadeIn(0.1f);
            _status.Set("booting", null);
        }

        public void OnUnloaded()
        {
            RuntimeSettings.RestoreAll(applyNative: false);
            BridgeIo.Log("CK3Bridge unloaded");
        }

        // ------------------------------------------------------------------ boot

        private void BootStep(GameState active, double now)
        {
            // Fallback for the logo video; normally CK3BridgeSubModule prevents it from starting at all.
            if (active is VideoPlaybackState video && video.CanUserSkip && !_introSkipped)
            {
                _introSkipped = true;
                BridgeIo.Log("skipping intro video");
                video.OnVideoFinished();
                return;
            }

            // Main menu reached: start CustomGame with our game manager (verified flow).
            if (active is InitialState && now - _lastGameStart >= GameStartRetrySeconds)
            {
                if (_gameStartAttempts >= MaxGameStartAttempts)
                {
                    _status.Set("error", null, "custom game failed to start");
                    return;
                }
                _gameStartAttempts++;
                _lastGameStart = now;
                BridgeIo.Log($"starting CK3GameManager (attempt {_gameStartAttempts})");
                MBGameManager.StartNewGame(new CK3GameManager());
            }
        }

        // ------------------------------------------------------------------ commands

        private void PumpCommands()
        {
            string text = BridgeIo.TryTake(BridgeIo.CommandPath);
            if (text == null) return;
            foreach (BridgeCommand command in BridgeCommand.ParseAll(text))
            {
                BridgeIo.Log("command: " + command.Raw);
                try
                {
                    Execute(command);
                }
                catch (Exception e)
                {
                    BridgeIo.Log($"command '{command.Raw}' failed: {e}");
                }
            }
        }

        private void Execute(BridgeCommand command)
        {
            BattleSession session = BattleSession.Current;
            switch (command.Kind)
            {
                case BridgeCommandKind.Preload:
                    break;   // implicit: request.json carries the battle
                case BridgeCommandKind.Go:
                    if (session != null) session.Go();
                    break;
                case BridgeCommandKind.Cancel:
                    StopBattle(session, command, StopReason.Cancel, "cancelled");
                    break;
                case BridgeCommandKind.Abort:
                    StopBattle(session, command, StopReason.Abort, "aborted");
                    break;
                case BridgeCommandKind.ReturnNow:
                    session?.ReturnNow();
                    break;
                case BridgeCommandKind.Quit:
                    Quit();
                    break;
                case BridgeCommandKind.Foreground:
                    WindowResidency.EnterForeground();
                    UpdateQoS();
                    break;
                case BridgeCommandKind.Background:
                    WindowResidency.EnterBackground();
                    UpdateQoS();
                    break;
                case BridgeCommandKind.HandoffTo:
                    bool ok = WindowResidency.HandoffTo(new IntPtr(command.Hwnd));
                    BridgeIo.Log($"handoff_to 0x{command.Hwnd:X}: {(ok ? "foreground set" : "SetForegroundWindow refused")}");
                    break;
                case BridgeCommandKind.TitleInEngine:
                    ShowTitleInEngine(session);
                    break;
                default:
                    BridgeIo.Log("unknown command ignored");
                    break;
            }
        }

        /// <summary>
        /// "cancel|abort [&lt;battleId&gt;]". A command that names a battle acts only on that battle, so a late cancel never
        /// stops another battle's session or takes its pending request; a bare one keeps its original meaning (PROTOCOL.md
        /// §3.3: the running battle, or else whatever request is pending).
        /// </summary>
        private void StopBattle(BattleSession session, BridgeCommand command, StopReason reason, string status)
        {
            if (session != null && command.AppliesTo(session.BattleId))
            {
                session.Stop(reason);
                return;
            }
            if (session == null)
            {
                DropPendingRequest(command, status);
                return;
            }
            // Another battle is running: its result.json and status stay untouched; a pending request of the named battle
            // is withdrawn without an answer (the bridge dropped that battle itself).
            bool withdrawn = BridgeIo.TryTake(BridgeIo.RequestPath, json => command.AppliesTo(ProtocolJson.ReadId(json))) != null;
            BridgeIo.Log($"'{command.Raw}' does not name the running battle {session.BattleId}; " +
                         (withdrawn ? "its pending request was withdrawn" : "ignored"));
        }

        /// <summary>
        /// "title_in_engine" (PROTOCOL.md §13.6): the battle the bridge is about to show gets the module's title card back,
        /// the current one while it is behind the loading card, else the request waiting in request.json when its battle
        /// starts (<see cref="TitleInEngineLatch"/>).
        /// </summary>
        private void ShowTitleInEngine(BattleSession session)
        {
            TitleInEngineTarget target = _titleInEngine.Receive(session?.BattleId, session != null && session.CanShowTitleInEngine,
                () => ProtocolJson.ReadId(BridgeIo.TryRead(BridgeIo.RequestPath)));
            switch (target)
            {
                case TitleInEngineTarget.Current:
                    session.ShowTitleInEngine();
                    BridgeIo.Log($"{session.BattleId}: the title card is shown in engine");
                    break;
                case TitleInEngineTarget.Pending:
                    BridgeIo.Log($"the title card of the pending request {_titleInEngine.PendingFor} will be shown in engine");
                    break;
                case TitleInEngineTarget.TooLate:
                    BridgeIo.Log($"title_in_engine: too late, {session.BattleId} is already visible or ending; ignored");
                    break;
                default:
                    BridgeIo.Log("title_in_engine: no battle is loading or held and no request is waiting; ignored");
                    break;
            }
        }

        /// <summary>A cancel that overtakes its own request (no battle running): consume the request and answer it directly.</summary>
        private void DropPendingRequest(BridgeCommand command, string status)
        {
            string json = BridgeIo.TryTake(BridgeIo.RequestPath, text => command.AppliesTo(ProtocolJson.ReadId(text)));
            if (json == null)
            {
                if (command.BattleId != null) BridgeIo.Log($"'{command.Raw}': no pending request of that battle");
                return;
            }
            string id = ProtocolJson.ReadId(json);
            BridgeIo.TryWriteJsonAtomic(BridgeIo.ResultPath, new BattleResult { Id = id, Status = status, Winner = "none" });
            ShowTransient(status, id, null);
        }

        /// <summary>
        /// "quit": undo every override first (the engine writes BannerlordConfig.txt and engine_config.txt on the way out),
        /// then Utilities.QuitGame. A running battle is closed as aborted by OnGameEnd during shutdown.
        /// </summary>
        private void Quit()
        {
            _quitting = true;
            BattleSession.Current?.Stop(StopReason.Abort);
            RuntimeSettings.RestoreAll(applyNative: true);   // the window stays cloaked: no flash of the last frame over CK3
            BridgeIo.Log("quitting");
            TaleWorlds.Engine.Utilities.QuitGame();
        }

        // ------------------------------------------------------------------ requests

        private void TryStartBattle()
        {
            string json = BridgeIo.TryTake(BridgeIo.RequestPath);
            if (json == null) return;
            // A "title_in_engine" that overtook a request applies to that request only, and only once.
            bool titleInEngine = _titleInEngine.TakeFor(ProtocolJson.ReadId(json));
            BattleRequest request = null;
            try
            {
                request = RequestIntake.Parse(json, out RequestExtras extras);
                BridgeIo.Log($"request {request.Id}: kind={request.Kind} scene={request.Scene} season={request.Season} hour={request.TimeOfDay} " +
                             $"playerSide={request.PlayerSide} general={request.PlayerIsGeneral} preload={request.Presentation.Preload}");
                MissionSetup setup = RequestIntake.Plan(request, extras);
                BattleArmies armies = ArmyBuilder.Build(request);
                BattleSession session = BattleSession.Start(request, extras, setup, armies, _status);
                if (titleInEngine && session.ShowTitleInEngine()) BridgeIo.Log($"{request.Id}: the title card is shown in engine");
                _transientUntil = -1.0;
                UpdateQoS();
            }
            catch (Exception e)
            {
                string id = request?.Id ?? ProtocolJson.ReadId(json);
                string message = e is RequestException ? e.Message : e.GetType().Name + ": " + e.Message;
                BridgeIo.Log($"request {id ?? "?"} failed: {e}");
                BridgeIo.TryWriteJsonAtomic(BridgeIo.ResultPath, new BattleResult { Id = id, Status = "error", Winner = "none", Error = message });
                ShowTransient("error", id, message);
            }
        }

        private void ShowTransient(string status, string battleId, string error)
        {
            _status.Set(status, battleId, error);
            _transientUntil = _clock.Elapsed.TotalSeconds + TransientStatusSeconds;
        }

        /// <summary>No EcoQoS while a battle exists or while visible; Windows decides while idle and cloaked.</summary>
        private void UpdateQoS()
        {
            bool high = BattleSession.Current != null || !WindowResidency.IsCloaked;
            if (_highQoS == high) return;
            _highQoS = high;
            WindowResidency.SetHighQoS(high);
        }
    }
}
