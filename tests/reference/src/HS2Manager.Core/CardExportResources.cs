using System.Text;

namespace HS2Manager.Core;

/// <summary>Inspects extension references while leaving all embedded binary data in the original PNG.</summary>
internal sealed class CardExportResources
{
    internal HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> Shaders { get; } = new(StringComparer.Ordinal);
    internal List<string> Issues { get; } = [];
    internal HashSet<string> InactivePlugins { get; } = new(StringComparer.Ordinal);

    internal static bool KnownExtension(string id) => id is "com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver" or
        "moreAccessories" or "KKABMPlugin.ABMData" or "com.deathweasel.bepinex.materialeditor" or "KSOX" or "KCOX" or "KK_InvisibleBody" or
        "TestCharaCustomFunctionController" or "com.fairbair.hs2_boobsettings" or "com.deathweasel.bepinex.eyecontrol" or "KK_PregnancyPlus" or
        "mikke.pushUpAI" or "orange.spork.additionalaccessorycontrolsplugin" or "orange.spork.advikplugin" or "orange.spork.outfitpainter" or "marco.authordata" or "orange.spork.earwiggle";

    internal static CardExportResources Read(CardRecord card, CancellationToken cancellation, CardExportLevel level)
    {
        bool includeResources = level == CardExportLevel.Full;
        var result = new CardExportResources();
        try
        {
            using var stream = File.OpenRead(card.Path); CardParser.FindPngEnd(stream, cancellation);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            _ = reader.ReadInt32(); _ = ReadString(); _ = ReadString(); _ = reader.ReadInt32(); _ = ReadString(); _ = ReadString();
            int tableLength = reader.ReadInt32();
            if (tableLength is < 1 or > 1024 * 1024 || tableLength > stream.Length - stream.Position) throw new InvalidDataException("人物块目录无效。");
            long tableEnd = stream.Position + tableLength;
            var table = new BoundedMessagePack(stream, tableEnd).Read() as Dictionary<string, object?>;
            stream.Position = tableEnd; long length = reader.ReadInt64(), start = stream.Position;
            if (length < 0 || length > stream.Length - start) throw new InvalidDataException("人物块长度越界。");
            if (table?.GetValueOrDefault("lstInfo") is not List<object?> blocks) throw new InvalidDataException("人物块目录缺失。");
            foreach (var block in blocks.OfType<Dictionary<string, object?>>().Where(b => b.GetValueOrDefault("name") as string == "KKEx"))
            {
                if (block.GetValueOrDefault("pos") is not long offset || block.GetValueOrDefault("size") is not long size || offset < 0 || size < 0 || offset > length || size > length - offset)
                    throw new InvalidDataException("扩展块越界。");
                stream.Position = start + offset; long end = stream.Position + size;
                var unpacker = new BoundedMessagePack(stream, end); int count = unpacker.MapCount();
                for (int i = 0; i < count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string id = unpacker.Read() as string ?? throw new InvalidDataException("扩展标识无效。");
                    long position = stream.Position; unpacker.Skip(); long next = stream.Position;
                    try
                    {
                        // Lower levels only need Shader MOD providers and proven inactive plugin flags.
                        bool shaderProvider = id == "com.deathweasel.bepinex.materialeditor";
                        if (!includeResources && !shaderProvider &&
                            (level < CardExportLevel.WithPlugins || id is not ("KK_PregnancyPlus" or "com.fairbair.hs2_boobsettings"))) continue;
                        var plugin = new NativePluginData(stream, position, next);
                        if (IsInactive(id, plugin)) { result.InactivePlugins.Add(id); continue; }
                        if (includeResources && !SupportedVersion(id, plugin.Version)) result.Issues.Add($"扩展 {id} 的数据版本 {plugin.Version} 尚未验证，配套资源可能不完整。");
                        foreach (string key in plugin.Keys)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (!includeResources && !(shaderProvider && key == "MaterialShaderList")) continue;
                            // Known MessagePack containers; binary textures inside are skipped by VisitStrings.
                            bool packed = id == "com.deathweasel.bepinex.materialeditor" && key is "MaterialShaderList" or "MaterialTexturePropertyList" ||
                                id == "KCOX" && key == "Overlays" || id == "KSOX" && key == "Lookup";
                            plugin.VisitStrings(key, (field, value) =>
                            {
                                if (id == "com.deathweasel.bepinex.materialeditor" && field == "ShaderName" && value.Length > 0) result.Shaders.Add(value);
                                if (!includeResources) return;
                                string path = value.Replace('\\', '/');
                                if (path.StartsWith("UserData/", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(value)) result.Paths.Add(value);
                                else if ((field.Contains("path", StringComparison.OrdinalIgnoreCase) || field.EndsWith("file", StringComparison.OrdinalIgnoreCase) || field.EndsWith("filename", StringComparison.OrdinalIgnoreCase)) && value.Length > 0)
                                    result.Issues.Add("无法确定外部资源的相对基准：" + id + "/" + field + " = " + value);
                            }, cancellation, packed);
                        }
                    }
                    catch (Exception ex) when (ExportPaths.IsFileError(ex) || ex is OverflowException)
                    {
                        if (includeResources || id == "com.deathweasel.bepinex.materialeditor")
                            result.Issues.Add("扩展资源读取不完整：" + id + "：" + ex.Message);
                    }
                    finally { stream.Position = next; }
                }
            }
            string ReadString()
            {
                int count = reader.Read7BitEncodedInt();
                if (count < 0 || count > 65536 || count > stream.Length - stream.Position) throw new InvalidDataException("人物头文本越界。");
                return Encoding.UTF8.GetString(reader.ReadBytes(count));
            }
        }
        catch (Exception ex) when (ExportPaths.IsFileError(ex) || ex is OverflowException)
        { result.Issues.Add("外部资源检查不完整：" + ex.Message); }
        return result;
    }

    // Only proven no-op data can remove a plugin requirement. Unknown versions/fields stay conservative.
    private static bool IsInactive(string id, NativePluginData plugin)
    {
        if (id == "KK_PregnancyPlus" && plugin.Version == 1)
            return plugin.Keys.All(k => k is "pluginVersion" or "clothingOffsetVersion") &&
                (plugin.Read("pluginVersion") is null or string) && (plugin.Read("clothingOffsetVersion") is null or long);
        if (id != "com.fairbair.hs2_boobsettings" || plugin.Version != 0) return false;
        string[] flags = ["overridePhysics", "overrideGravity", "butt_overridePhysics", "butt_overrideGravity"];
        string[] parameters = ["damping", "elasticity", "stiffness", "inert", "gravityX", "gravityY", "gravityZ"];
        return flags.All(k => plugin.Read(k) is false) && plugin.Keys.All(k => flags.Contains(k) || parameters.Contains(k) ||
            k.StartsWith("butt_", StringComparison.Ordinal) && parameters.Contains(k[5..]));
    }

    private static bool SupportedVersion(string id, long version) => id switch
    {
        "com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver" => true,
        "KSOX" => version is >= 0 and <= 2,
        "KCOX" => version == 1,
        "com.deathweasel.bepinex.materialeditor" => version is 0 or 1,
        "KKABMPlugin.ABMData" => version == 2,
        "mikke.pushUpAI" => version == 3,
        "KK_PregnancyPlus" or "marco.authordata" => version == 1,
        _ => KnownExtension(id) && version == 0
    };
}
