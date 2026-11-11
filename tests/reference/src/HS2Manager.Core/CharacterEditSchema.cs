using System.Globalization;

namespace HS2Manager.Core;

public enum CharacterFieldKind { Number, Color, Toggle, Asset, Choice, Preset }
public sealed record CharacterChoice(object Value, string Label);
public sealed record CharacterField(string Path, string Category, string Group, string Label, CharacterFieldKind Kind,
    double Minimum = 0, double Maximum = 1, double DisplayScale = 100, IReadOnlyList<CharacterChoice>? Choices = null);
public sealed record CharacterSliderLimits(double Minimum, double Maximum, double InputMinimum, double InputMaximum)
{
    public static CharacterSliderLimits Read(string game)
    {
        string plugins = System.IO.Path.Combine(game, "BepInEx", "plugins");
        if (!Directory.Exists(plugins) || !Directory.EnumerateFiles(plugins, "HS2_SliderUnlocker.dll", SearchOption.AllDirectories).Any()) return new(0, 1, 0, 1);
        double min = -1, max = 2; string config = System.IO.Path.Combine(game, "BepInEx", "config", "com.bepis.bepinex.sliderunlocker.cfg");
        if (File.Exists(config)) foreach (string line in File.ReadLines(config))
        {
            var pair = line.Split('=', 2, StringSplitOptions.TrimEntries); if (pair.Length != 2 || !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) continue;
            if (pair[0] == "Minimum slider value") min = Math.Clamp(number, -500, 0) / 100;
            if (pair[0] == "Maximum slider value") max = Math.Clamp(number, 100, 500) / 100;
        }
        return new(min, max, Math.Min(min, -5), Math.Max(max, 5));
    }
}

/// <summary>Original maker fields, verified against this game's CvsF/B/H/C/A and ChaFile data.
/// Version and runtime bookkeeping fields are intentionally absent, and unknown fields remain opaque.</summary>
public static class CharacterEditSchema
{
    public static readonly (string Id, string Label)[] Categories = [("face", "脸部"), ("body", "身体"), ("hair", "发型"), ("makeup", "妆容"), ("clothes", "服装"), ("accessory", "配饰")];
    public static readonly string[] HairNames = ["后发", "前发", "侧发", "附加发型"];
    public static readonly string[] ClothesNames = ["上衣", "下装", "内衣", "内裤", "手套", "连裤袜", "袜子", "鞋子"];
    public static int AutoClothingState(string category, string group)
    {
        // CustomChangeMainMenu.ChangeWindowSetting and CvsC_Clothes.UpdateCustomUI.
        if (category == "body") return 2;
        int slot = Array.IndexOf(ClothesNames, group.Split(" · ", 2, StringSplitOptions.None)[0]);
        return category == "clothes" && slot is >= 2 and <= 6 ? 1 : 0;
    }
    private static readonly (string Group, string[] Labels)[] FaceShape =
    [
        ("整体", ["脸部宽度", "上半脸前后", "上半脸高度", "下半脸前后", "下半脸宽度"]),
        ("下颌", ["下颌宽度", "下颌高度", "下颌前后", "下颌角度", "下颌底部高度", "下巴尖宽度", "下巴尖高度", "下巴尖前后"]),
        ("脸颊", ["下脸颊高度", "下脸颊前后", "下脸颊宽度", "上脸颊高度", "上脸颊前后", "上脸颊宽度"]),
        ("眼形", ["眼睛高度", "眼睛间距", "眼睛前后", "眼睛宽度", "眼睛高度比例", "眼睛倾斜", "眼睛前后角度", "内眼角横向", "外眼角横向", "内眼角高度", "外眼角高度", "眼睑形状一", "眼睑形状二"]),
        ("鼻子", ["鼻子高度", "鼻子前后", "鼻子角度", "鼻子宽度", "鼻梁高度", "鼻梁宽度", "鼻梁形状", "鼻翼宽度", "鼻翼高度", "鼻翼前后", "鼻翼上下角度", "鼻翼左右角度", "鼻尖高度", "鼻尖角度", "鼻尖大小"]),
        ("嘴形", ["嘴部高度", "嘴部宽度", "嘴部厚度", "嘴部前后", "上唇形状", "下唇形状", "嘴角形状"]),
        ("耳朵", ["耳朵大小", "耳朵前后角度", "耳朵倾斜", "上耳形状", "耳垂形状"])
    ];
    private static readonly (int[] Indices, string Group, string[] Labels)[] BodyShape =
    [
        ([0,9], "整体", ["身高", "头部大小"]),
        ([1,2,3,4,5,6,7,8,32], "胸部", ["胸部大小", "胸部高度", "胸部上下角度", "胸部间距", "胸部左右角度", "胸部形状", "乳晕隆起", "乳头大小", "乳头突出"]),
        ([10,11,12,13,14,15,16,17], "上半身", ["颈部宽度", "颈部厚度", "肩部宽度", "肩部厚度", "上躯干宽度", "上躯干厚度", "下躯干宽度", "下躯干厚度"]),
        ([18,19,20,21,22,23,24], "腰臀", ["腰部高度", "上腰宽度", "上腰厚度", "下腰宽度", "下腰厚度", "臀部大小", "臀部角度"]),
        ([29,30,31], "手臂", ["肩膀", "上臂粗细", "前臂粗细"]),
        ([25,26,27,28], "腿部", ["大腿根部", "大腿下部", "小腿粗细", "脚踝粗细"])
    ];
    public static IReadOnlyList<CharacterField> Build(CharacterDocument document, CharacterSliderLimits? limits = null)
    {
        limits ??= new(0, 1, 0, 1);
        var fields = new List<CharacterField>(); string category = "face", group = "整体";
        int faceIndex = 0;
        foreach (var shape in FaceShape)
        { group = shape.Group; foreach (string label in shape.Labels) Number($"face.shapeValueFace[{faceIndex++}]", label, true); }
        group = "脸型与皮肤";
        Asset("face.headId", "头部模型"); Asset("face.skinId", "面部皮肤"); Asset("face.detailId", "皮肤细节"); Number("face.detailPower", "细节强度");
        group = "眉毛"; Asset("face.eyebrowId", "眉毛样式"); Color("face.eyebrowColor", "眉毛颜色"); Layout("face.eyebrowLayout", "眉毛", ["横向位置", "纵向位置", "宽度", "高度"]); Number("face.eyebrowTilt", "眉毛角度");
        group = "睫毛"; Asset("face.eyelashesId", "睫毛样式"); Color("face.eyelashesColor", "睫毛颜色");
        group = "眼睛"; Toggle("face.pupilSameSetting", "左右眼同步"); Number("face.pupilY", "瞳孔高度"); Number("face.whiteShadowScale", "眼白阴影");
        for (int eye = 0; eye < Count("face.pupil"); eye++)
        {
            group = eye == 0 ? "左眼" : "右眼"; string path = $"face.pupil[{eye}]";
            Color(path + ".whiteColor", "眼白颜色"); Asset(path + ".pupilId", "虹膜样式"); Color(path + ".pupilColor", "虹膜颜色");
            Number(path + ".pupilW", "虹膜宽度"); Number(path + ".pupilH", "虹膜高度"); Number(path + ".pupilEmission", "虹膜发光");
            Asset(path + ".blackId", "瞳孔样式"); Color(path + ".blackColor", "瞳孔颜色"); Number(path + ".blackW", "瞳孔宽度"); Number(path + ".blackH", "瞳孔高度");
        }
        group = "眼部高光"; Asset("face.hlId", "高光样式"); Color("face.hlColor", "高光颜色"); Layout("face.hlLayout", "高光", ["宽度", "高度", "横向位置", "纵向位置"]); Number("face.hlTilt", "高光角度");
        group = "痣"; Asset("face.moleId", "痣的样式"); Color("face.moleColor", "痣的颜色"); Add("face.moleLayout", "位置预设", CharacterFieldKind.Preset); Layout("face.moleLayout", "痣", ["宽度", "高度", "横向位置", "纵向位置"]);
        if (document.Sex == 0) { group = "胡须"; Asset("face.beardId", "胡须样式"); Color("face.beardColor", "胡须颜色"); }

        category = "body";
        foreach (var shape in BodyShape)
        {
            if (shape.Group == "胸部" && document.Sex == 0) continue;
            group = shape.Group; for (int i = 0; i < shape.Indices.Length; i++) Number($"body.shapeValueBody[{shape.Indices[i]}]", shape.Labels[i], true);
        }
        if (document.Sex == 1) { group = "胸部"; Number("body.bustSoftness", "胸部柔软度"); Number("body.bustWeight", "胸部重量"); }
        group = "皮肤"; Asset("body.skinId", "身体皮肤"); Color("body.skinColor", "肤色"); Asset("body.detailId", "皮肤细节"); Number("body.detailPower", "细节强度"); Number("body.skinGlossPower", "皮肤光泽"); Number("body.skinMetallicPower", "皮肤金属感");
        group = "晒痕"; Asset("body.sunburnId", "晒痕样式"); Color("body.sunburnColor", "晒痕颜色");
        group = "乳头与乳晕"; Asset("body.nipId", "乳头样式"); Color("body.nipColor", "乳头颜色"); Number("body.nipGlossPower", "乳头光泽"); Number("body.areolaSize", "乳晕大小");
        group = "体毛"; Asset("body.underhairId", "体毛样式"); Color("body.underhairColor", "体毛颜色");
        group = "指甲"; Color("body.nailColor", "指甲颜色"); Number("body.nailGlossPower", "指甲光泽");
        Paint("body.paintInfo", "身体彩绘");

        category = "makeup"; group = "眼影"; Asset("face.makeup.eyeshadowId", "眼影样式"); Color("face.makeup.eyeshadowColor", "眼影颜色"); Number("face.makeup.eyeshadowGloss", "眼影光泽");
        group = "腮红"; Asset("face.makeup.cheekId", "腮红样式"); Color("face.makeup.cheekColor", "腮红颜色"); Number("face.makeup.cheekGloss", "腮红光泽");
        group = "唇妆"; Asset("face.makeup.lipId", "唇妆样式"); Color("face.makeup.lipColor", "唇妆颜色"); Number("face.makeup.lipGloss", "唇妆光泽"); Paint("face.makeup.paintInfo", "面部彩绘");

        category = "hair"; group = "统一设置"; Toggle("hair.sameSetting", "四组发色同步"); Toggle("hair.autoSetting", "自动生成渐变与高光色"); Toggle("hair.ctrlTogether", "同组发束同步调整");
        Choice("hair.shaderType", "头发渲染方式", [new(0L, "柔和透明"), new(1L, "透明裁切")]);
        for (int i = 0; i < Math.Min(4, Count("hair.parts")); i++)
        {
            group = HairNames[i]; string part = $"hair.parts[{i}]"; Asset(part + ".id", "发型");
            Color(part + ".baseColor", "基础颜色"); Color(part + ".topColor", "发根颜色"); Color(part + ".underColor", "发梢颜色"); Color(part + ".specular", "高光颜色");
            Number(part + ".metallic", "金属感"); Number(part + ".smoothness", "光滑度"); Asset(part + ".meshType", "挑染图案"); Color(part + ".meshColor", "挑染颜色"); Layout(part + ".meshLayout", "挑染", ["宽度", "高度", "横向位置", "纵向位置"]);
            for (int c = 0; c < Count(part + ".acsColorInfo"); c++) Color(part + $".acsColorInfo[{c}].color", "发饰颜色 " + (c + 1));
            if (document.Contains(part + ".dictBundle")) foreach (var item in document.Node(part + ".dictBundle").Entries())
            {
                if (item.Key is not long key) continue;
                group = HairNames[i] + " · 发束 " + (key + 1); string bundle = part + $".dictBundle{{{key}}}";
                Layout(bundle + ".moveRate", "位置", ["X", "Y", "Z"], true); Layout(bundle + ".rotRate", "角度", ["X", "Y", "Z"], true); Toggle(bundle + ".noShake", "固定发束");
            }
        }

        category = "clothes";
        for (int slot = 0; slot < Math.Min(8, Count("clothes.parts")); slot++)
        {
            if (document.Sex == 0 && slot is 2 or 3 or 5 or 6) continue;
            group = ClothesNames[slot]; string part = $"clothes.parts[{slot}]"; Asset(part + ".id", "服装样式"); Number(part + ".breakRate", "破损程度");
            for (int option = 0; option < Count(part + ".hideOpt"); option++) Toggle(part + $".hideOpt[{option}]", "隐藏可选部件 " + (option + 1));
            for (int c = 0; c < Count(part + ".colorInfo"); c++)
            {
                group = ClothesNames[slot] + " · 颜色 " + (c + 1); string color = part + $".colorInfo[{c}]";
                Color(color + ".baseColor", "基础颜色"); Asset(color + ".pattern", "花纹图案"); Color(color + ".patternColor", "花纹颜色");
                Layout(color + ".layout", "花纹", ["宽度", "高度", "横向位置", "纵向位置"]); Number(color + ".rotation", "花纹角度"); Number(color + ".glossPower", "光泽"); Number(color + ".metallicPower", "金属感");
            }
        }

        category = "accessory";
        for (int slot = 0; slot < Math.Min(20, Count("accessory.parts")); slot++)
        {
            group = "配饰 " + (slot + 1); string part = $"accessory.parts[{slot}]";
            Choice(part + ".type", "配饰类别", AccessoryTypes);
            if (Convert.ToInt32(document.GetValue(part + ".type")) is <= 350 or > 363) continue;
            Asset(part + ".id", "配饰样式"); Choice(part + ".parentKey", "挂点", AccessoryParents);
            Choice(part + ".hideCategory", "服装联动", [new(0L, "不联动"), new(1L, "上衣"), new(2L, "下装"), new(3L, "内衣"), new(4L, "内裤"), new(5L, "手套"), new(6L, "连裤袜"), new(7L, "袜子"), new(8L, "鞋子")]);
            Choice(part + ".hideTiming", "隐藏时机", [new(0L, "完全脱下"), new(1L, "半脱或脱下")]); Toggle(part + ".noShake", "固定配饰"); Toggle(part + ".partsOfHead", "作为头部部件");
            for (int c = 0; c < Count(part + ".colorInfo"); c++)
            {
                string color = part + $".colorInfo[{c}]"; Color(color + ".color", "颜色 " + (c + 1));
                Number(color + ".glossPower", "光泽 " + (c + 1)); Number(color + ".metallicPower", "金属感 " + (c + 1)); Number(color + ".smoothnessPower", "光滑度 " + (c + 1));
            }
            // MessagePack's Unity Vector3[,] formatter stores [rows, columns, flat values].
            if (document.Contains(part + ".addMove"))
            {
                var matrix = document.Node(part + ".addMove");
                string moves = matrix.Count == 3 && matrix.Children[2].Kind == EditableMessagePack.NodeKind.Array ? part + ".addMove[2]" : part + ".addMove";
                for (int vector = 0; vector < Count(moves); vector++)
                {
                    int operation = vector % 3; group = "配饰 " + (slot + 1) + " · 调整 " + (vector / 3 + 1);
                    for (int axis = 0; axis < 3; axis++)
                        Add(moves + $"[{vector}][{axis}]", new[] { "位置", "旋转", "缩放" }[operation] + " " + "XYZ"[axis], CharacterFieldKind.Number,
                            operation == 0 ? -100 : operation == 1 ? -360 : .01, operation == 0 ? 100 : operation == 1 ? 360 : 10, 1);
                }
            }
        }
        return fields;

        int Count(string path) => document.Contains(path) ? document.Node(path).Count : 0;
        void Add(string path, string label, CharacterFieldKind kind, double min = 0, double max = 1, double scale = 100, IReadOnlyList<CharacterChoice>? choices = null)
        { if (document.Contains(path)) fields.Add(new(path, category, group, label, kind, min, max, scale, choices)); }
        void Number(string path, string label, bool extended = false) => Add(path, label, CharacterFieldKind.Number, extended ? limits.Minimum : 0, extended ? limits.Maximum : 1);
        void Color(string path, string label) { if (Count(path) is 3 or 4) Add(path, label, CharacterFieldKind.Color); }
        void Toggle(string path, string label) => Add(path, label, CharacterFieldKind.Toggle);
        void Asset(string path, string label) => Add(path, label, CharacterFieldKind.Asset);
        void Choice(string path, string label, IReadOnlyList<CharacterChoice> choices) => Add(path, label, CharacterFieldKind.Choice, choices: choices);
        void Layout(string path, string label, string[] components, bool extended = false)
        { for (int i = 0; i < Math.Min(components.Length, Count(path)); i++) Number(path + $"[{i}]", label + " " + components[i], extended); }
        void Paint(string path, string label)
        {
            for (int i = 0; i < Count(path); i++)
            {
                group = label + " " + (i + 1); string p = path + $"[{i}]";
                Asset(p + ".id", "图案"); Color(p + ".color", "颜色"); Number(p + ".glossPower", "光泽"); Number(p + ".metallicPower", "金属感"); if(path.StartsWith("body.",StringComparison.Ordinal))Asset(p + ".layoutId", "位置预设");
                if(path.StartsWith("face.",StringComparison.Ordinal))Add(p+".layout","位置预设",CharacterFieldKind.Preset);
                Layout(p + ".layout", "图案", ["宽度", "高度", "横向位置", "纵向位置"]); Number(p + ".rotation", "旋转");
            }
        }
    }
    public static float[] ColorValue(CharacterDocument document, string path) => document.Node(path).Children.Select(n => Convert.ToSingle(n.Value, CultureInfo.InvariantCulture)).ToArray();
    public static int ExtraAccessoryCount(CharacterDocument document) => Math.Max(0, document.Root("accessory").Require("parts").Count - 20);
    public static readonly CharacterChoice[] AccessoryTypes = new[] { new CharacterChoice(350L, "无") }.Concat(
        new[] { "头部", "耳部", "眼镜", "面部", "颈部", "肩部", "胸部", "腰部", "背部", "手臂", "手部", "腿部", "胯部" }.Select((name, i) => new CharacterChoice((long)(351 + i), name))).ToArray();
    public static readonly CharacterChoice[] AccessoryParents = Parents();
    private static CharacterChoice[] Parents()
    {
        var result = new List<CharacterChoice> { new("", "素材默认挂点") };
        foreach (var (key, label) in new (string, string)[] { ("N_Hair_pony","马尾"), ("N_Head_top","头顶"), ("N_Hitai","额头"), ("N_Head","头部"), ("N_Face","面部"), ("N_Megane","眼镜"), ("N_Nose","鼻子"), ("N_Mouth","嘴部"), ("N_Neck","颈部"), ("N_Chest_f","胸前"), ("N_Chest","胸部"), ("N_Back","背部"), ("N_Waist","腰部"), ("N_Waist_f","腰前"), ("N_Waist_b","腰后"), ("N_Dan","生殖器"), ("N_Kokan","胯部"), ("N_Ana","肛门") }) result.Add(new(key, label));
        foreach (var (key, label) in new (string, string)[] { ("N_Hair_twin","双马尾"), ("N_Hair_pin","发夹"), ("N_Earring","耳环"), ("N_Tikubi","乳头"), ("N_Back","背部"), ("N_Waist","腰侧"), ("N_Leg","大腿"), ("N_Knee","膝盖"), ("N_Ankle","脚踝"), ("N_Foot","脚"), ("N_Shoulder","肩膀"), ("N_Elbo","肘部"), ("N_Arm","手臂"), ("N_Wrist","手腕"), ("N_Hand","手"), ("N_Index","食指"), ("N_Middle","中指"), ("N_Ring","无名指") })
        { result.Add(new(key + "_L", "左" + label)); result.Add(new(key + "_R", "右" + label)); }
        return result.ToArray();
    }
}
