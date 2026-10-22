// PdxNode (save text, game files, or binary meta renamed through CoaTokenMap) -> CoaModel.
//   pattern = "pattern_solid.dds"  color1..color3 = <colour>  parent = "<template key>"
//   colored_emblem / textured_emblem = { texture = "ce_x.dds" color1..3 mask = { 1 } instance = { position = { x y } scale = { sx sy } rotation = deg depth = d } }
//   sub = { parent = "<key>" | inline coat of arms, instance = { offset = { x y } scale = { sx sy } } }
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Coa;

internal static class CoaParser
{
    public static CoaModel? Parse(PdxNode? node, string source, NamedColors colors, Func<string, PdxNode?>? parentLookup = null)
    {
        if (node == null) return null;
        var coa = new CoaModel { Source = source };
        Fill(coa, node, colors, parentLookup, 0);
        return coa.IsEmpty && node.Get("pattern") == null ? null : coa;
    }

    private static void Fill(CoaModel coa, PdxNode node, NamedColors colors, Func<string, PdxNode?>? parentLookup, int depth)
    {
        if (depth > 6) return;
        // A parent template is applied first, then this block's own fields override it.
        if (node.Str("parent") is string parentKey && parentLookup?.Invoke(parentKey) is PdxNode parent)
            Fill(coa, parent, colors, parentLookup, depth + 1);

        if (node.Str("pattern") is string pattern && pattern.Length > 0) coa.Pattern = StripDds(pattern);
        for (int i = 0; i < 3; i++)
            if (colors.Resolve(node.Get("color" + (i + 1))) is CoaColor c) coa.Colors[i] = c;

        bool ownEmblems = false;
        foreach (var it in node.Items)
        {
            bool textured = it.Key == "textured_emblem";
            if (it.Key != "colored_emblem" && !textured) continue;
            if (it.Value.Block is not PdxNode e) continue;
            if (!ownEmblems) { coa.Emblems.Clear(); ownEmblems = true; } // own emblems replace the parent's
            var emblem = new CoaEmblem { Textured = textured, Texture = StripDds(e.Str("texture") ?? "") };
            for (int i = 0; i < 3; i++) emblem.Colors[i] = colors.Resolve(e.Get("color" + (i + 1)));
            var mask = e.Block("mask")?.Numbers();
            if (mask is { Count: > 0 }) emblem.Mask = mask.Select(m => (int)m).ToArray();
            foreach (var inst in e.All("instance"))
                if (inst.Block is PdxNode ib) emblem.Instances.Add(ParseInstance(ib, "position"));
            if (emblem.Instances.Count == 0) emblem.Instances.Add(new CoaInstance());
            if (emblem.Texture.Length > 0) coa.Emblems.Add(emblem);
        }

        foreach (var sv in node.All("sub"))
        {
            if (sv.Block is not PdxNode sb) continue;
            var sub = new CoaSub();
            Fill(sub.Coa, sb, colors, parentLookup, depth + 1);
            foreach (var inst in sb.All("instance"))
                if (inst.Block is PdxNode ib)
                {
                    var ci = ParseInstance(ib, "offset");
                    // sub instances default to offset 0,0 (top-left) and scale 1,1
                    if (ib.Block("offset") == null) { ci.X = 0; ci.Y = 0; }
                    sub.Instances.Add(ci);
                }
            if (sub.Instances.Count == 0) sub.Instances.Add(new CoaInstance { X = 0, Y = 0 });
            coa.Subs.Add(sub);
        }
    }

    private static CoaInstance ParseInstance(PdxNode ib, string positionKey)
    {
        var inst = new CoaInstance();
        var pos = ib.Block(positionKey)?.Numbers();
        if (pos is { Count: >= 2 }) { inst.X = pos[0]; inst.Y = pos[1]; }
        var scale = ib.Block("scale")?.Numbers();
        if (scale is { Count: >= 2 }) { inst.ScaleX = scale[0]; inst.ScaleY = scale[1]; }
        else if (scale is { Count: 1 }) { inst.ScaleX = inst.ScaleY = scale[0]; }
        inst.Rotation = ib.Num("rotation") ?? 0;
        inst.Depth = ib.Num("depth") ?? 0;
        return inst;
    }

    public static string StripDds(string texture)
    {
        texture = texture.Trim();
        return texture.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? texture[..^4] : texture;
    }
}
