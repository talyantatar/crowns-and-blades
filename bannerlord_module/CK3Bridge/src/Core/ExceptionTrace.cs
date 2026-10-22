using System;
using System.Threading;

namespace CK3Bridge
{
    /// <summary>
    /// Logs the first exceptions of the game's mission code (first chance, so also the one that crashes the game) with their stack to
    /// bannerlord.log: a crash the watchdog only offers to report (and holds the dump of until someone answers its dialog) still leaves
    /// its cause in our log. Cheap: a type check per exception, at most <see cref="MaxLogged"/> lines per game.
    /// </summary>
    internal static class ExceptionTrace
    {
        public const int MaxLogged = 20;
        private static int _logged, _installed;
        [ThreadStatic] private static bool t_inside;

        public static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0) return;
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (t_inside || Volatile.Read(ref _logged) >= MaxLogged) return;
                var ex = e.Exception;
                // The faults that crash a mission; expected ones (IO retries, cancellations, parse attempts) are left out.
                if (!(ex is IndexOutOfRangeException || ex is NullReferenceException || ex is ArgumentOutOfRangeException ||
                      ex is InvalidCastException || ex is System.Collections.Generic.KeyNotFoundException)) return;
                try
                {
                    t_inside = true;
                    if (Interlocked.Increment(ref _logged) > MaxLogged) return;
                    string stack = new System.Diagnostics.StackTrace(1, false).ToString();
                    if (stack.Length > 2500) stack = stack.Substring(0, 2500);
                    BridgeIo.Log($"exception (first chance) {ex.GetType().Name}: {ex.Message}\n{stack}");
                }
                catch
                {
                    // never let the trace itself throw into the game
                }
                finally
                {
                    t_inside = false;
                }
            };
        }

    }
}
