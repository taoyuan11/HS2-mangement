using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace HS2Manager.Core;

public sealed record PluginDependency(string Guid, string MinimumVersion = "", bool Optional = false);
public sealed record PluginAssemblyReference(string Name, string Version);
public sealed class PluginDefinition
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<PluginDependency> Dependencies { get; set; } = [];
    public List<string> DataIds { get; set; } = [];
}
public sealed class PluginFileInfo
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string AssemblyName { get; set; } = "";
    public string Version { get; set; } = "";
    public List<PluginDefinition> Plugins { get; set; } = [];
    public List<PluginAssemblyReference> References { get; set; } = [];
    public List<string> NativeReferences { get; set; } = [];
    public string Error { get; set; } = "";
}

/// <summary>Reads ECMA-335 metadata only; never loads or executes an installed plugin.</summary>
internal static class PluginFileIndex
{
    private static readonly ConcurrentDictionary<string, PluginFileInfo> cache = new(StringComparer.OrdinalIgnoreCase);

    internal static List<PluginFileInfo> Scan(string folder, List<string> warnings, CancellationToken cancellation)
    {
        var result = new List<PluginFileInfo>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.dll", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var stat = new FileInfo(path);
                    if (!cache.TryGetValue(path, out var entry) || entry.Size != stat.Length || entry.ModifiedUtcTicks != stat.LastWriteTimeUtc.Ticks || entry.Error.Length > 0)
                        cache[path] = entry = Read(path);
                    result.Add(entry);
                    if (entry.Error.Length > 0) warnings.Add("插件元数据：" + path + "：" + entry.Error);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException)
                { warnings.Add("插件扫描：" + path + "：" + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { warnings.Add("插件目录：" + folder + "：" + ex.Message); }
        return result.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static PluginFileInfo Read(string path)
    {
        var stat = new FileInfo(path);
        var result = new PluginFileInfo { Path = System.IO.Path.GetFullPath(path), Size = stat.Length, ModifiedUtcTicks = stat.LastWriteTimeUtc.Ticks };
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return result; // Native libraries can still be explicitly included as companion files.
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly) return result;
        var assembly = reader.GetAssemblyDefinition();
        result.AssemblyName = reader.GetString(assembly.Name); result.Version = assembly.Version.ToString();
        result.References = reader.AssemblyReferences.Select(h => reader.GetAssemblyReference(h))
            .Select(r => new PluginAssemblyReference(reader.GetString(r.Name), r.Version.ToString())).ToList();
        foreach (var handle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(handle);
            if ((method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) == 0) continue;
            var import = method.GetImport();
            if (!import.Module.IsNil) result.NativeReferences.Add(reader.GetString(reader.GetModuleReference(import.Module).Name));
        }
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var plugins = new List<PluginDefinition>();
            var dependencies = new List<PluginDependency>();
            foreach (var attribute in Attributes(handle, new HashSet<TypeDefinitionHandle>()))
            {
                try
                {
                    string name = AttributeName(attribute, out var signature);
                    if (name is not ("BepInEx.BepInPlugin" or "BepInEx.BepInDependency")) continue;
                    var blob = reader.GetBlobReader(attribute.Value);
                    if (blob.ReadUInt16() != 1) throw new InvalidDataException("无效的插件属性");
                    string guid = blob.ReadSerializedString() ?? "";
                    if (guid.Length == 0) throw new InvalidDataException("插件属性缺少 GUID");
                    if (name == "BepInEx.BepInPlugin")
                    {
                        plugins.Add(new() { Guid = guid, Name = blob.ReadSerializedString() ?? guid, Version = blob.ReadSerializedString() ?? "" });
                    }
                    else
                    {
                        // The overload takes either a minimum-version string or an enum (Int32).
                        var sig = reader.GetBlobReader(signature); var header = sig.ReadSignatureHeader();
                        if (header.IsGeneric) _ = sig.ReadCompressedInteger();
                        int count = sig.ReadCompressedInteger(); _ = sig.ReadSignatureTypeCode(); _ = sig.ReadSignatureTypeCode();
                        var second = count > 1 ? sig.ReadSignatureTypeCode() : SignatureTypeCode.Invalid;
                        string version = second == SignatureTypeCode.String ? blob.ReadSerializedString() ?? "" : "";
                        bool optional = count > 1 && second != SignatureTypeCode.String && (blob.ReadInt32() & 2) != 0;
                        dependencies.Add(new(guid, version, optional));
                    }
                }
                catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException or ArgumentException)
                { result.Error = "无法完整读取插件声明：" + ex.Message; }
            }
            foreach (var plugin in plugins)
            {
                plugin.Dependencies = dependencies.Distinct().ToList();
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle); string name = reader.GetString(field.Name);
                    if (!name.Contains("ExtData", StringComparison.OrdinalIgnoreCase) && !name.Contains("DataId", StringComparison.OrdinalIgnoreCase)) continue;
                    var constantHandle = field.GetDefaultValue(); if (constantHandle.IsNil) continue;
                    var constant = reader.GetConstant(constantHandle); if (constant.TypeCode != ConstantTypeCode.String) continue;
                    var data = reader.GetBlobReader(constant.Value); string value = data.ReadUTF16(data.Length);
                    if (value.Length > 0) plugin.DataIds.Add(value);
                }
                result.Plugins.Add(plugin);
            }
        }
        return result;

        IEnumerable<CustomAttribute> Attributes(TypeDefinitionHandle handle, HashSet<TypeDefinitionHandle> visited)
        {
            if (!visited.Add(handle)) yield break;
            var type = reader.GetTypeDefinition(handle);
            foreach (var item in type.GetCustomAttributes()) yield return reader.GetCustomAttribute(item);
            if (!type.BaseType.IsNil && type.BaseType.Kind == HandleKind.TypeDefinition)
                foreach (var item in Attributes((TypeDefinitionHandle)type.BaseType, visited)) yield return item;
        }
        string AttributeName(CustomAttribute attribute, out BlobHandle signature)
        {
            EntityHandle type;
            if (attribute.Constructor.Kind == HandleKind.MemberReference)
            { var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor); signature = member.Signature; type = member.Parent; }
            else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
            { var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor); signature = method.Signature; type = method.GetDeclaringType(); }
            else { signature = default; return ""; }
            if (type.Kind == HandleKind.TypeReference)
            { var definition = reader.GetTypeReference((TypeReferenceHandle)type); return reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name); }
            if (type.Kind == HandleKind.TypeDefinition)
            { var definition = reader.GetTypeDefinition((TypeDefinitionHandle)type); return reader.GetString(definition.Namespace) + "." + reader.GetString(definition.Name); }
            return "";
        }
    }

    internal static string PluginGuid(string dataId) => dataId switch
    {
        "com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver" => "com.bepis.bepinex.sideloader",
        "KKABMPlugin.ABMData" => "KKABMX.Core",
        "moreAccessories" => "com.joan6694.illusionplugins.moreaccessories",
        "KK_InvisibleBody" => "com.deathweasel.bepinex.invisiblebody",
        "marco.authordata" or "TestCharaCustomFunctionController" => "marco.kkapi",
        _ => dataId
    };
    internal static Version VersionOf(string value) => Version.TryParse(value, out var version) ? version : new(0, 0);
}
