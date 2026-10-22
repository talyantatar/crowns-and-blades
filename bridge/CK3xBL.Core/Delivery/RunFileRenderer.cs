// The dev-mode IN channel body (PROTOCOL §2.3 as amended by §10.4): run/ck3xbl_apply.txt resets the keypad registers, sets the
// non-zero ones directly, then commits and applies with the same static effects the keypad path uses. Root of a run file is the
// player character. The reset first means registers left by an earlier partial attempt never
// count; it leaves an applied payload (ck3xbl_ready = 2) alone, so re-running the file never applies twice. The body is identical
// to the one printed by the mod's reference encoder (tools/gen/keypad_payload.mjs).
using System.Globalization;
using System.Text;

namespace CK3xBL.Core.Delivery;

public static class RunFileRenderer
{
    public const string FileName = "ck3xbl_apply.txt";
    public const string NoOp = "debug_log = \"CK3XBL|NOOP\"\n";

    /// <summary>Scripted effects of the mod the body calls, in call order (all defined in ck3_mod/ck3xbl/common/scripted_effects).</summary>
    public static readonly IReadOnlyList<string> CalledEffects =
        new[] { "ck3xbl_keypad_reset_effect", "ck3xbl_commit_effect", "ck3xbl_apply_effect" };

    public static string Render(KeypadPayload payload)
    {
        var sb = new StringBuilder();
        sb.Append("hidden_effect = {\n");
        sb.Append('\t').Append(CalledEffects[0]).Append(" = yes\n");
        foreach (var (index, value) in payload.NonZero())
            sb.Append("\tset_variable = { name = ck3xbl_r").Append(index.ToString(CultureInfo.InvariantCulture))
              .Append(" value = ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(" }\n");
        sb.Append('\t').Append(CalledEffects[1]).Append(" = yes\n");
        sb.Append('\t').Append(CalledEffects[2]).Append(" = yes\n");
        sb.Append("}\n");
        return sb.ToString();
    }
}
