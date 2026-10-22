// Picks the IN channel per delivery (PROTOCOL §0): normal mode = keypad (the console would cost the campaign its achievements),
// dev mode = run file. The mode is read at delivery time because the tray can launch CK3 in either mode. There is deliberately no
// automatic fallback from one channel to the other: a run file that timed out may still have been applied, and a second channel
// could then apply the same battle twice.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;

namespace CK3xBL.Core.Delivery;

public sealed class DeliveryRouter(Func<BridgeMode> mode, string channel, ICk3Delivery keypad, ICk3Delivery runFile, IJournal journal)
    : ICk3Delivery
{
    public Task<DeliveryStatus> DeliverAsync(Ck3Outcome outcome, CancellationToken ct) => Select().DeliverAsync(outcome, ct);

    /// <summary>The payload version a channel learned is a fact about CK3's mod, not about the channel: an old mod found through
    /// either channel keeps both on version 3 (PROTOCOL §14.3).</summary>
    public int MaxPayloadVersion => Math.Min(keypad.MaxPayloadVersion, runFile.MaxPayloadVersion);

    public ICk3Delivery Select()
    {
        bool dev = mode() == BridgeMode.Dev;
        switch (channel.ToLowerInvariant())
        {
            case "keypad":
                return keypad;
            case "runfile":
                if (dev) return runFile;
                journal.Warn("delivery", "delivery.channel = runfile is ignored in normal mode (console use voids achievements); using the keypad");
                return keypad;
            default:
                return dev ? runFile : keypad;
        }
    }
}
