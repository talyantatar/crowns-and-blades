using System;
using System.Diagnostics;
using CK3xBL.Protocol;

namespace CK3Bridge
{
    /// <summary>
    /// bannerlord_status.json (PROTOCOL.md §3.4): rewritten on every change and as a heartbeat. The heartbeat runs on the
    /// main thread on purpose: a stale "t" is how the bridge detects a hung or crashed Bannerlord.
    ///
    /// A write is one attempt without waiting: a reader that holds the file without FILE_SHARE_DELETE (e.g. a
    /// PowerShell Get-Content -Wait tail) makes File.Replace fail for as long as it reads. The pending status is then
    /// retried every <see cref="RetryInterval"/>, never every frame, and only the first failure and the recovery are logged.
    /// </summary>
    internal sealed class StatusReporter
    {
        /// <summary>Below the protocol's 1 s so frame jitter at a 30 fps background limiter never exceeds it.</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(750);

        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

        private readonly int _pid = Process.GetCurrentProcess().Id;
        private readonly Stopwatch _sinceWrite = Stopwatch.StartNew();
        private readonly Stopwatch _sinceAttempt = Stopwatch.StartNew();
        private string _status = "booting";
        private string _battleId;
        private string _error;
        private bool _dirty = true;
        private bool _failing;

        public string Status => _status;

        public void Set(string status, string battleId, string error = null)
        {
            if (status == _status && battleId == _battleId && error == _error) return;
            BridgeIo.Log($"status {_status} -> {status}" + (battleId != null ? $" ({battleId})" : "") + (error != null ? $": {error}" : ""));
            _status = status;
            _battleId = battleId;
            _error = error;
            _dirty = true;
            if (!_failing) Flush();   // while writes fail, Tick retries on its own schedule
        }

        public void Tick()
        {
            bool due = _failing
                ? _sinceAttempt.Elapsed >= RetryInterval
                : _dirty || _sinceWrite.Elapsed >= HeartbeatInterval;
            if (due) Flush();
        }

        private void Flush()
        {
            _sinceAttempt.Restart();
            try
            {
                BridgeIo.WriteJsonAtomic(BridgeIo.StatusPath, new BannerlordStatus
                {
                    Status = _status,
                    BattleId = _battleId,
                    T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Pid = _pid,
                    Hwnd = WindowResidency.Hwnd.ToInt64(),
                    Error = _error,
                }, attempts: 1);
                _dirty = false;
                _sinceWrite.Restart();
                if (_failing) BridgeIo.Log("status writes work again");
                _failing = false;
            }
            catch (Exception e)
            {
                if (!_failing) BridgeIo.Log($"status write failed, retrying every {RetryInterval.TotalSeconds:0.##} s (logged once): {e.Message}");
                _failing = true;
            }
        }
    }
}
