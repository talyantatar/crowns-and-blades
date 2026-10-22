// Leases on CK3xBL.Ux.InputGuard (low-level keyboard/mouse hooks) for the core's delivery and quiet-input checks. The hooks are
// installed on the first lease and removed with the last one, so they exist only around transitions.
using CK3xBL.Core.Delivery;
using UxInputGuard = CK3xBL.Ux.InputGuard;

namespace CK3xBL;

internal sealed class UxInputGuardSource : IInputGuardSource, IDisposable
{
    private readonly object _gate = new();
    private UxInputGuard? _guard;
    private int _leases;

    private System.Runtime.GCLatencyMode? _savedLatency;

    public IInputGuard Acquire()
    {
        lock (_gate)
        {
            if (_guard is null)
            {
                _guard = new UxInputGuard();
                // The hook callbacks are managed: a blocking garbage collection holds them, and Windows holds the cursor until they
                // answer (a "stuck" cursor while a result was typed, live 2026-10-20). No blocking gen-2 collections while hooked.
                _savedLatency = System.Runtime.GCSettings.LatencyMode;
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            }
            _leases++;
            return new Lease(this, _guard);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases > 0 || _guard is null) return;
            _guard.Dispose();
            _guard = null;
            RestoreLatency();
        }
    }

    private void RestoreLatency()
    {
        if (_savedLatency is { } mode) System.Runtime.GCSettings.LatencyMode = mode;
        _savedLatency = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _guard?.Dispose();
            _guard = null;
            _leases = 0;
            RestoreLatency();
        }
    }

    private sealed class Lease(UxInputGuardSource owner, UxInputGuard guard) : IInputGuard
    {
        private UxInputGuard.InputBlock? _block;
        private int _disposed;

        public long PhysicalIdleMs => (long)guard.PhysicalIdle.TotalMilliseconds;

        /// <summary>True while physical input is really blocked: a block ends by itself at its cap (4 s), or without working hooks.</summary>
        public bool Blocking
        {
            get => _block is { IsActive: true };
            set
            {
                if (value)
                {
                    if (_block is { IsActive: true }) return;
                    _block?.Dispose();                     // capped: start a fresh block
                    _block = guard.BlockPhysicalInput();
                }
                else
                {
                    _block?.Dispose();
                    _block = null;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Blocking = false;
            owner.Release();
        }
    }
}
