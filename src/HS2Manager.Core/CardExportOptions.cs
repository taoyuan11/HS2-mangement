namespace HS2Manager.Core;

public enum CardExportLevel { CardOnly = 1, WithMods = 2, WithPlugins = 3, Full = 4 }
public sealed record CardExportOptions(CardExportMode Mode, CardExportLevel Level);

public static class CardExportLevels
{
    public static string Label(this CardExportLevel level) => level switch
    {
        CardExportLevel.CardOnly => "一级：仅原始 PNG",
        CardExportLevel.WithMods => "二级：包含 MOD",
        CardExportLevel.WithPlugins => "三级：包含插件",
        CardExportLevel.Full => "四级：包含配套资源",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public static string Contents(this CardExportLevel level) => level switch
    {
        CardExportLevel.CardOnly => "完整原始人物卡，保留嵌入的人物及扩展数据。",
        CardExportLevel.WithMods => "原始人物卡和关联 MOD，包括迁移映射包及可确认的资源提供包。",
        CardExportLevel.WithPlugins => "原始人物卡、关联 MOD、依赖插件及递归依赖 DLL。",
        CardExportLevel.Full => "原始人物卡、关联 MOD、依赖插件，以及可识别的外部贴图、独立资源和插件配套文件。",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public static string ScopeNote(this CardExportLevel level) => level switch
    {
        CardExportLevel.CardOnly => "未包含 MOD、插件及配套资源，需要目标游戏已有这些依赖。",
        CardExportLevel.WithMods => "未包含插件及配套资源，需要目标游戏已有这些依赖。",
        CardExportLevel.WithPlugins => "未包含外部贴图、独立资源及插件配套文件，需要目标游戏已有这些资源。",
        CardExportLevel.Full => "不包含插件配置、基础加载器或游戏本体；目标游戏需已安装 BepInEx，插件沿用目标游戏设置。",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}
