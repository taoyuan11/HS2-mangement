using System.Text.RegularExpressions;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

internal static class CharacterEditRules
{
    internal static CharacterDocument SetValue(CharacterDocument document, string path, object? value)
    {
        var next = document.WithValue(path, value);
        var eye = Regex.Match(path, @"^face\.pupil\[([01])\](.+)$");
        if (eye.Success && document.GetValue("face.pupilSameSetting") is true)
            next = next.WithValue($"face.pupil[{int.Parse(eye.Groups[1].Value) ^ 1}]" + eye.Groups[2].Value, value);
        var hair = Regex.Match(path, @"^hair\.parts\[(\d+)\]\.(metallic|smoothness)$");
        if (hair.Success && document.GetValue("hair.sameSetting") is true)
            for (int i = 0; i < document.Root("hair").Require("parts").Count; i++) next = next.WithValue($"hair.parts[{i}]." + hair.Groups[2].Value, value);
        var bundle = Regex.Match(path, @"^(hair\.parts\[\d+\]\.dictBundle)\{\d+\}\.(moveRate|rotRate)\[[012]\]$");
        if (bundle.Success && document.GetValue("hair.ctrlTogether") is true)
        {
            string root = bundle.Groups[1].Value, vector = bundle.Groups[2].Value;
            string changedVector = path[..path.LastIndexOf('[')]; var node = next.Node(changedVector);
            foreach (var item in document.Node(root).Entries()) next = next.WithNode(root + "{" + item.Key + "}." + vector, node);
        }
        if (Regex.IsMatch(path, @"^accessory\.parts\[\d+\]\.type$"))
        {
            string root = path[..^5];
            var empty = new CharacterAsset(Convert.ToInt32(value), 0, "无", "", "原版", new(Convert.ToInt32(value), 0, "", "", ""));
            next = CharacterAssetReferences.Select(next, root + ".id", empty);
            next = next.WithValue(root + ".parentKey", "").WithValue(root + ".noShake", false);
            var moves = next.Node(root + ".addMove"); string p = root + ".addMove";
            if (moves.Count == 3 && moves.Children[2].Kind == EditableMessagePack.NodeKind.Array) { moves = moves.Children[2]; p += "[2]"; }
            for (int vector = 0; vector < moves.Count; vector++) for (int axis = 0; axis < 3; axis++) next = next.WithValue(p + $"[{vector}][{axis}]", vector % 3 == 2 ? 1f : 0f);
        }
        return next;
    }
    internal static CharacterDocument Color(CharacterDocument document, string path, IReadOnlyList<float> color)
    {
        var next = SetColor(document, path, color);
        var eye = Regex.Match(path, @"^face\.pupil\[([01])\](.+)$");
        if (eye.Success && document.GetValue("face.pupilSameSetting") is true)
            next = SetColor(next, $"face.pupil[{int.Parse(eye.Groups[1].Value) ^ 1}]" + eye.Groups[2].Value, color);
        var hair = Regex.Match(path, @"^hair\.parts\[(\d+)\]\.(baseColor|topColor|underColor|specular)$");
        if (!hair.Success) return next;
        string field = hair.Groups[2].Value; int selected = int.Parse(hair.Groups[1].Value);
        for (int i = 0; i < document.Root("hair").Require("parts").Count; i++)
        {
            if (i != selected && document.GetValue("hair.sameSetting") is not true) continue;
            string root = $"hair.parts[{i}]."; next = SetColor(next, root + field, color);
            if (field != "baseColor" || document.GetValue("hair.autoSetting") is not true) continue;
            var (h, s, v) = Hsv(color);
            next = SetColor(next, root + "topColor", Rgb(h, s, Math.Max(v - .15f, 0)));
            next = SetColor(next, root + "underColor", Rgb(h, Math.Max(s - .1f, 0), Math.Min(v + .44f, 1)));
            next = SetColor(next, root + "specular", Rgb(h, s, Math.Min(v + .17f, 1)));
        }
        return next;
    }
    internal static CharacterDocument Select(CharacterDocument document, string path, CharacterAsset asset)
    {
        var next = CharacterAssetReferences.Select(document, path, asset);
        var eye = Regex.Match(path, @"^face\.pupil\[([01])\](.+)$");
        if (eye.Success && document.GetValue("face.pupilSameSetting") is true)
            next = CharacterAssetReferences.Select(next, $"face.pupil[{int.Parse(eye.Groups[1].Value) ^ 1}]" + eye.Groups[2].Value, asset);
        if (Regex.IsMatch(path, @"^hair\.parts\[\d+\]\.id$"))
        { string root = path[..^3]; next = next.WithValue(root + ".bundleId", asset.Id).WithNode(root + ".dictBundle", EditableMessagePack.Map()); }
        if (Regex.IsMatch(path, @"^accessory\.parts\[\d+\]\.id$"))
        { string root = path[..^3]; next = next.WithValue(root + ".parentKey", asset.Entry.Get("Parent")).WithValue(root + ".noShake", false); }
        return next;
    }
    internal static CharacterDocument InitializeAsset(CharacterDocument document, string path, ComponentDefinition[] components)
    {
        string root = path[..^3];
        var hair = components.FirstOrDefault(c => c.Type == "CmpHair")?.Data;
        if (path.StartsWith("hair.", StringComparison.Ordinal) && hair != null)
        {
            var bones = hair.Get("boneInfo"); var groups = new List<(object Key, EditableMessagePack Value)>();
            for (int i = 0; i < bones.Children.Length; i++)
            {
                var bone = bones.At(i);
                EditableMessagePack Rate(string property)
                {
                    var values = new List<EditableMessagePack>();
                    foreach (string axis in new[] { "x", "y", "z" })
                    {
                        float min = bone.Get(property + "Min").Float(axis), max = bone.Get(property + "Max").Float(axis), value = bone.Get(property == "pos" ? "basePos" : "baseRot").Float(axis);
                        values.Add(EditableMessagePack.Scalar(min == max ? 0f : Math.Clamp((value - min) / (max - min), 0, 1)));
                    }
                    return EditableMessagePack.Array(values.ToArray());
                }
                groups.Add((i, EditableMessagePack.Map(("moveRate", Rate("pos")), ("rotRate", Rate("rot")), ("noShake", EditableMessagePack.Scalar(false)))));
            }
            document = document.WithNode(root + ".dictBundle", EditableMessagePack.Map(groups.ToArray()));
            for (int i = 0; i < hair.Get("acsDefColor").Children.Length; i++)
                if (document.Contains(root + $".acsColorInfo[{i}].color")) document = SetColor(document, root + $".acsColorInfo[{i}].color", ParameterColor(hair.Get("acsDefColor").At(i)));
        }
        var accessory = components.FirstOrDefault(c => c.Type == "CmpAccessory")?.Data;
        if (path.StartsWith("accessory.", StringComparison.Ordinal) && accessory != null && accessory.Float("typeHair") == 0)
            for (int i = 0; i < 4; i++)
            {
                if (i == 3 ? accessory.Get("rendAlpha").Children.Length == 0 : accessory.Float("useColor0" + (i + 1)) == 0) continue;
                string color = root + $".colorInfo[{i}]", suffix = "0" + (i + 1);
                document = SetColor(document, color + ".color", ParameterColor(accessory.Get("defColor" + suffix)))
                    .WithValue(color + ".glossPower", accessory.Float("defGlossPower" + suffix)).WithValue(color + ".metallicPower", accessory.Float("defMetallicPower" + suffix));
            }
        return document;
    }
    private static float[] ParameterColor(Parameter p) => [p.Float("r"), p.Float("g"), p.Float("b"), p.Float("a", 1)];
    private static CharacterDocument SetColor(CharacterDocument document, string path, IReadOnlyList<float> color)
    {
        var node = document.Node(path);
        if (color.Count < 3 || color.Count > 4 || color.Any(c => !float.IsFinite(c) || c < 0 || c > 1)) throw new ArgumentException("颜色分量必须介于 0 和 1。");
        for (int i = 0; i < Math.Min(node.Count, color.Count); i++) node = node.Set(i, node.Require(i).WithScalar(color[i]));
        return document.WithNode(path, node);
    }
    private static (float H, float S, float V) Hsv(IReadOnlyList<float> c)
    {
        float max = Math.Max(c[0], Math.Max(c[1], c[2])), min = Math.Min(c[0], Math.Min(c[1], c[2])), delta = max - min;
        float h = delta == 0 ? 0 : max == c[0] ? (c[1] - c[2]) / delta : max == c[1] ? 2 + (c[2] - c[0]) / delta : 4 + (c[0] - c[1]) / delta;
        return ((h / 6 + 1) % 1, max == 0 ? 0 : delta / max, max);
    }
    private static float[] Rgb(float h, float s, float v)
    {
        float n = h * 6; int segment = (int)n; float f = n - segment, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return (segment % 6) switch { 0 => [v,t,p,1], 1 => [q,v,p,1], 2 => [p,v,t,1], 3 => [p,q,v,1], 4 => [t,p,v,1], _ => [v,p,q,1] };
    }
}
