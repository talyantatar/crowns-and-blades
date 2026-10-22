using System;

namespace CK3Bridge
{
    /// <summary>Battle phases; each one is also the status string of bannerlord_status.json (PROTOCOL.md §3.4).</summary>
    internal enum BattlePhase
    {
        Loading,
        Held,
        Visible,
        Fighting,
        Result,
        FadingOut,
        Black,
        Ended,
    }

    /// <summary>What a "title_in_engine" command reached (<see cref="TitleInEngineLatch.Receive"/>).</summary>
    internal enum TitleInEngineTarget
    {
        /// <summary>The current battle, still behind the loading card: it shows the module's title card now.</summary>
        Current,

        /// <summary>The request waiting in request.json: its battle shows the title card when it starts.</summary>
        Pending,

        /// <summary>Nothing: the current battle is already visible or ending, and no request is waiting.</summary>
        TooLate,

        /// <summary>Nothing: no battle and no waiting request.</summary>
        NoBattle,
    }

    /// <summary>
    /// Where "title_in_engine" (PROTOCOL.md §13.6) goes. The bridge sends it when it cannot show its own title card, so the
    /// battle it is about to show gets the module's card back. It may write the command in the same poll as the request
    /// (commands are read first) or while the previous battle is still ending; then the command is latched for the id of
    /// the request waiting in request.json, and only that request gets it, once, when it is taken.
    /// </summary>
    internal sealed class TitleInEngineLatch
    {
        /// <summary>The id of the waiting request the command was latched for (null when none).</summary>
        public string PendingFor { get; private set; }

        /// <summary>A battle can still take the title card while it is behind the loading card and not ending.</summary>
        public static bool CanShow(BattlePhase phase, bool stopping, bool ended) =>
            phase <= BattlePhase.Held && !stopping && !ended;

        /// <summary>
        /// The command arrived. <paramref name="currentBattleId"/> is the running battle (null when none) and
        /// <paramref name="currentCanShow"/> its <see cref="CanShow"/>; <paramref name="readPendingId"/> reads the id of the
        /// request waiting in request.json without taking it, and is called only when the current battle cannot take the
        /// card. Any earlier latch is replaced: request.json holds at most one request.
        /// </summary>
        public TitleInEngineTarget Receive(string currentBattleId, bool currentCanShow, Func<string> readPendingId)
        {
            if (currentBattleId != null && currentCanShow) return TitleInEngineTarget.Current;
            PendingFor = readPendingId();
            if (PendingFor != null) return TitleInEngineTarget.Pending;
            return currentBattleId != null ? TitleInEngineTarget.TooLate : TitleInEngineTarget.NoBattle;
        }

        /// <summary>A request was taken from request.json: true when the command was latched for it. Clears the latch either way.</summary>
        public bool TakeFor(string requestId)
        {
            bool latched = requestId != null && requestId == PendingFor;
            PendingFor = null;
            return latched;
        }
    }
}
