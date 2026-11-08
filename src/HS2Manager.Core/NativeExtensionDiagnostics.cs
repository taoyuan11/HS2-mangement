namespace HS2Manager.Core;

internal sealed partial class NativeCardExtensions
{
    private static bool KnownExtension(string id) => id is "moreAccessories" or "KKABMPlugin.ABMData" or "com.deathweasel.bepinex.materialeditor"
        or "KSOX" or "KCOX" or "KK_InvisibleBody" or "TestCharaCustomFunctionController" or "com.fairbair.hs2_boobsettings"
        or "com.deathweasel.bepinex.eyecontrol" or "KK_PregnancyPlus" or "mikke.pushUpAI"
        or "orange.spork.additionalaccessorycontrolsplugin" or "orange.spork.advikplugin" or "orange.spork.outfitpainter"
        or "marco.authordata" or "orange.spork.earwiggle";

    private void ReadOther(string id, NativePluginData plugin, Stream stream, CardRecord card, CancellationToken cancellation)
    {
        if (id == "KSOX") { ReadSkinOverlays(plugin, card, cancellation); return; }
        if (id == "KCOX") { ReadClothesOverlays(plugin, stream, card, cancellation); return; }
        long expected = id == "mikke.pushUpAI" ? 3 : id is "KK_PregnancyPlus" or "marco.authordata" ? 1 : 0;
        if (plugin.Version != expected) throw new NotSupportedException("扩展数据版本尚未支持。");
        switch (id)
        {
            case "marco.authordata":
                if (plugin.Keys.Any(k => k != "Authors")) Warnings.Add("AuthorData：存在未识别的元数据字段。");
                break;
            case "orange.spork.earwiggle":
                if (plugin.Read("EarWiggleEnabled") is true) Warnings.Add("EarWiggle：已启用的耳部动态物理尚未还原。");
                break;
            case "KK_InvisibleBody":
                BodyVisible = plugin.Read("Visible") switch { bool visible => visible, null => true, _ => throw new InvalidDataException("身体显示状态无效。") };
                break;
            case "TestCharaCustomFunctionController":
                // KKAPI's controller identity has no visual effect. Unknown extra fields still warn.
                if (plugin.Keys.Any(k => k != "id")) Warnings.Add(id + "：存在未识别参数。");
                break;
            case "com.fairbair.hs2_boobsettings":
                if (new[] { "overridePhysics", "overrideGravity", "butt_overridePhysics", "butt_overrideGravity" }.Any(k => plugin.Read(k) is true))
                    Warnings.Add("BoobSettings：已启用的自定义胸部/臀部动态物理尚未还原。");
                break;
            case "com.deathweasel.bepinex.eyecontrol":
                float open = Convert.ToSingle(plugin.Read("EyeOpenMax") ?? 1f);
                if (!float.IsFinite(open) || open is < 0 or > 1) throw new InvalidDataException("眼睑开合值无效。");
                EyeOpenMax = open;
                DisableBlinking = plugin.Read("DisableBlinking") is true;
                break;
            case "KK_PregnancyPlus":
                if (plugin.Keys.Any(k => k is not ("pluginVersion" or "clothingOffsetVersion")))
                    Warnings.Add("PregnancyPlus：卡片含腹部形变参数，尚未还原。");
                break;
            case "mikke.pushUpAI":
                PushUp = NativePushUp.Read(plugin);
                break;
            case "orange.spork.additionalaccessorycontrolsplugin":
                ReadAccessoryControls(plugin);
                if (plugin.Unpack("overrideData") is List<object?> rules && rules.Any(v => v is not List<object?> { Count: 0 }))
                    Warnings.Add("AdditionalAccessoryControls：服装显示规则尚未还原。");
                break;
            case "orange.spork.advikplugin":
                if (new[] { "ShoulderRotatorEnabled", "EnableSpineFKHints", "EnableShoulderFKHints", "EnableToeFKHints", "EnableHeelzHoverAll", "EnableHeelzHoverLeftFoot", "EnableHeelzHoverRightFoot", "BreathingEnabled" }.Any(k => plugin.Read(k) is true)
                    || Convert.ToSingle(plugin.Read("SpineStiffness") ?? 0f) != 0)
                    Warnings.Add("AdvIK：已启用的附加姿势/呼吸控制尚未还原。");
                break;
            case "orange.spork.outfitpainter":
                // The plugin writes edits into the standard coordinate colors/gloss values.
                // Reload only restores editable channel bindings; it does not recolor the card.
                if (plugin.Unpack("OutfitPainterData") is List<object?> { Count: 1 } data && data[0] is List<object?> palettes &&
                    palettes.All(v => v is List<object?> { Count: 6 } row && row[5] is List<object?> assignments &&
                        assignments.All(a=>a is List<object?> {Count:4} binding && binding[0] is long slot && slot is >=0 and <=8 && binding[2] is long color && color is >=1 and <=4))) break;
                if (plugin.HasValue("OutfitPainterData")) Warnings.Add("OutfitPainter：调色绑定数据格式尚未支持。");
                break;
        }
    }
}
