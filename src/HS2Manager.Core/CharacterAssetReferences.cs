using System.Text.RegularExpressions;

namespace HS2Manager.Core;

/// <summary>Sideloader's persisted property names, not its process-local resolved IDs.</summary>
internal static class CharacterAssetReferences
{
    internal const string Resolver = "com.bepis.sideloader.universalautoresolver";
    private const string EcResolver = "EC.Core.Sideloader.UniversalAutoResolver";
    private static readonly string[] Hair = ["HairBack", "HairFront", "HairSide", "HairOption"];
    private static readonly string[] Clothes = ["ClothesTop", "ClothesBot", "ClothesBra", "ClothesShorts", "ClothesGloves", "ClothesPantyhose", "ClothesSocks", "ClothesShoes"];
    internal sealed record Binding(int Category, string Property);
    internal static Binding? Describe(CharacterDocument document, string path)
    {
        int accessory = 350;
        if (Regex.IsMatch(path, @"^accessory\.parts\[\d+\]\.id$")) accessory = Convert.ToInt32(document.GetValue(path[..^2] + "type"));
        return DescribePath(document.Sex, path, accessory);
    }
    internal static Binding? DescribePath(int sexValue, string path, int accessoryCategory = 350)
    {
        if (path.StartsWith("outfit.", StringComparison.Ordinal)) path = path[7..];
        int sex = sexValue == 0 ? 100 : 200;
        if (path.StartsWith("face.pupil[", StringComparison.Ordinal))
        {
            var match = Regex.Match(path, @"^face\.pupil\[(\d+)\]\.(pupilId|blackId)$");
            if (match.Success) return new(match.Groups[2].Value == "pupilId" ? 317 : 318,
                "ChaFileFace." + (match.Groups[2].Value == "pupilId" ? "Eye" : "EyeBlack") + (int.Parse(match.Groups[1].Value) + 1));
        }
        var paint = Regex.Match(path, @"^(face\.makeup|body)\.paintInfo\[(\d+)\]\.(id|layoutId)$");
        if (paint.Success)
        {
            bool face = paint.Groups[1].Value != "body", layout = paint.Groups[3].Value == "layoutId";
            return new(layout ? face ? 7 : 8 : 313, (face ? "MakeupInfo." : "ChaFileBody.") + (layout ? "PaintLayoutID" : "PaintID") + (int.Parse(paint.Groups[2].Value) + 1));
        }
        var part = Regex.Match(path, @"^(hair|clothes|accessory)\.parts\[(\d+)\]\.(.+)$");
        if (part.Success)
        {
            int slot = int.Parse(part.Groups[2].Value); string field = part.Groups[3].Value;
            switch (part.Groups[1].Value)
            {
                case "hair" when slot < 4 && field is "id" or "meshType": return new(field == "id" ? 300 + slot : 306, "ChaFileHair." + Hair[slot] + (field == "id" ? "" : "Mesh"));
                case "clothes" when slot < 8:
                    if (field == "id") return new((slot is 2 or 3 or 5 or 6 ? 200 : sex) + 40 + slot,
                        "outfit.ChaFileClothes." + Clothes[slot] + (sexValue == 0 && slot is 0 or 1 or 4 or 7 ? "M" : ""));
                    var pattern = Regex.Match(field, @"^colorInfo\[(\d+)\]\.pattern$");
                    if (pattern.Success) return new(348, "outfit.ChaFileClothes." + Clothes[slot] + "Pattern" + pattern.Groups[1].Value);
                    break;
                case "accessory" when field == "id": return new(accessoryCategory, $"outfit.accessory{slot}.ChaFileAccessory.PartsInfo.id");
            }
        }
        return path switch
        {
            "face.headId" => new(sex + 10, "ChaFileFace.headId"), "face.skinId" => new(sex + 11, "ChaFileFace.skinId"),
            "face.detailId" => new(sex + 12, "ChaFileFace.detailId"), "face.beardId" => new(121, "ChaFileFace.beardId"),
            "face.eyebrowId" => new(314, "ChaFileFace.eyebrowId"), "face.eyelashesId" => new(315, "ChaFileFace.eyelashesId"),
            "face.hlId" => new(319, "ChaFileFace.hlId"), "face.moleId" => new(323, "ChaFileFace.moleId"),
            "face.makeup.cheekId" => new(320, "MakeupInfo.cheekId"), "face.makeup.eyeshadowId" => new(316, "MakeupInfo.eyeshadowId"),
            "face.makeup.lipId" => new(322, "MakeupInfo.lipId"), "body.skinId" => new(sex + 31, "ChaFileBody.skinId"),
            "body.detailId" => new(sex + 32, "ChaFileBody.detailId"), "body.sunburnId" => new(sex + 33, "ChaFileBody.sunburnId"),
            "body.nipId" => new(334, "ChaFileBody.nipId"), "body.underhairId" => new(335, "ChaFileBody.underhairId"), _ => null
        };
    }

    internal static List<ModReference> All(CharacterDocument document)
    {
        var result = new Dictionary<string, ModReference>(StringComparer.Ordinal);
        foreach (string resolver in new[] { Resolver, EcResolver })
            foreach (var (_, info) in Read(document.Extensions?.Find(resolver)))
                result[Property(info)] = new(info.Require("ModID").Value as string ?? "", checked((int)CharacterDocument.Integer(info.Require("Slot"))),
                    checked((int)CharacterDocument.Integer(info.Require("CategoryNo"))), Property(info));
        return result.Values.ToList();
    }
    internal static ModReference? Find(CharacterDocument document, string path)
    {
        var binding = Describe(document, path); if (binding == null) return null;
        foreach (string resolver in new[] { EcResolver, Resolver })
            foreach (var (_, info) in Read(document.Extensions?.Find(resolver)))
                if (Property(info) == binding.Property)
                    return new(info.Find("ModID")?.Value as string ?? "", (int)CharacterDocument.Integer(info.Require("Slot")), binding.Category, binding.Property);
        return null;
    }
    internal static CharacterDocument Select(CharacterDocument document, string path, CharacterAsset asset)
    {
        var binding = Describe(document, path) ?? throw new ArgumentException("此字段不是素材选择项。");
        if (binding.Category != asset.Category) throw new ArgumentException("素材类别与参数不匹配。");
        var next = document.WithValue(path, asset.Id);
        var extensions = next.Extensions ?? EditableMessagePack.Map();
        foreach (string resolver in new[] { Resolver, EcResolver })
        {
            var plugin = extensions.Find(resolver);
            if (plugin == null && (resolver != Resolver || asset.Guid.Length == 0)) continue;
            plugin ??= EditableMessagePack.Array(EditableMessagePack.Scalar(0), EditableMessagePack.Map());
            var entries = Read(plugin).Where(p => Property(p.Info) != binding.Property).Select(p => p.Raw).ToList();
            // EC takes precedence on load. Update it too if this card already contains it.
            if (asset.Guid.Length > 0)
            {
                var info = EditableMessagePack.Map(("ModID", EditableMessagePack.Scalar(asset.Guid)), ("Slot", EditableMessagePack.Scalar(asset.Id)),
                    ("LocalSlot", EditableMessagePack.Scalar(asset.Id)), ("Property", EditableMessagePack.Scalar(binding.Property)),
                    ("CategoryNo", EditableMessagePack.Scalar(asset.Category)), ("Name", EditableMessagePack.Scalar(asset.Source)),
                    ("Author", EditableMessagePack.Scalar("")), ("Website", EditableMessagePack.Scalar("")));
                entries.Add(EditableMessagePack.Binary(info.ToBytes()));
            }
            var data = plugin.Require(1).Set("info", EditableMessagePack.Array(entries.ToArray()), allowAdd: true);
            extensions = extensions.Set(resolver, plugin.Set(1, data), allowAdd: true);
        }
        return next.WithExtensions(extensions.Count == 0 && document.Extensions == null ? null : extensions);
    }
    internal static CharacterDocument Restore(CharacterDocument draft, CharacterDocument baseline, string path)
    {
        var binding = Describe(baseline, path);
        bool Matches(string property) => binding != null ? property == binding.Property : path switch
        {
            "face" => property.StartsWith("ChaFileFace.", StringComparison.Ordinal),
            "face.makeup" => property.StartsWith("MakeupInfo.", StringComparison.Ordinal),
            "body" => property.StartsWith("ChaFileBody.", StringComparison.Ordinal), "hair" => property.StartsWith("ChaFileHair.", StringComparison.Ordinal),
            "clothes" => property.StartsWith("outfit.ChaFileClothes.", StringComparison.Ordinal),
            "accessory" => property.StartsWith("outfit.accessory", StringComparison.Ordinal), _ => false
        };
        if (draft.Extensions == null) return draft;
        var extras = draft.Extensions;
        foreach (string resolver in new[] { Resolver, EcResolver })
        {
            var plugin = extras.Find(resolver); if (plugin == null) continue;
            var original = baseline.Extensions?.Find(resolver);
            var entries = Read(plugin).Where(p => !Matches(Property(p.Info))).Select(p => p.Raw).ToList();
            int index=0;
            foreach(var item in Read(original))
            {if(Matches(Property(item.Info)))entries.Insert(Math.Min(index,entries.Count),item.Raw);index++;}
            if (original == null && entries.Count == 0) extras = extras.Remove(resolver);
            else extras = extras.Set(resolver, plugin.Set(1, plugin.Require(1).Set("info", EditableMessagePack.Array(entries.ToArray()), true)));
        }
        return draft.WithExtensions(extras.Count == 0 && baseline.Extensions == null ? null : extras);
    }
    private static string Property(EditableMessagePack info) => info.Require("Property").Value as string ?? throw new InvalidDataException("MOD 属性名称无效。");
    private static IEnumerable<(EditableMessagePack Raw, EditableMessagePack Info)> Read(EditableMessagePack? plugin)
    {
        if (plugin == null) yield break;
        var info = plugin.Require(1).Find("info"); if (info == null) yield break;
        if (info.Kind != EditableMessagePack.NodeKind.Array) throw new InvalidDataException("Sideloader 引用格式不支持编辑。");
        foreach (var node in info.Children)
        {
            if (node.Kind != EditableMessagePack.NodeKind.Binary) throw new InvalidDataException("Sideloader 引用不是二进制参数。");
            yield return (node, EditableMessagePack.Parse(node.Payload));
        }
    }
}
