using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using HS2Manager.Core;
using Mono.Cecil;

internal static class CardExportFixtures
{
    internal const string Resolver = "com.bepis.sideloader.universalautoresolver";
    internal const string Save = "com.bepis.bepinex.extendedsave";
    internal const string Sideloader = "com.bepis.bepinex.sideloader";
    internal const string MaterialEditor = "com.deathweasel.bepinex.materialeditor";
    internal static Dictionary<string, object?> Map(params (string Key, object? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);
    internal static object Extension(long version, Dictionary<string, object?> fields) => new object?[] { version, fields };
    internal static object References(params string[] guids) => Extension(1, Map(("info", Pack(guids.Select(g => Map(("ModID", g), ("Slot", 42), ("CategoryNo", 245), ("Property", "clothes.parts[5].id"))).ToArray()))));

    internal static CardRecord Card(string path, string name, int sex, Dictionary<string, object?>? extensions = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] parameter = Pack(Map(("fullname", name), ("sex", sex)));
        byte[] extra = Pack(extensions ?? []);
        byte[] table = Pack(Map(("lstInfo", new[]
        {
            Map(("name", "Parameter"), ("pos", 0), ("size", parameter.Length)),
            Map(("name", "KKEx"), ("pos", parameter.Length), ("size", extra.Length))
        })));
        using (var file = File.Create(path))
        using (var binary = new BinaryWriter(file, Encoding.UTF8))
        {
            binary.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9xkAAAAASUVORK5CYII="));
            binary.Write(100); binary.Write("【AIS_Chara】"); binary.Write("1.0.0"); binary.Write(0); binary.Write(""); binary.Write("");
            binary.Write(table.Length); binary.Write(table); binary.Write((long)(parameter.Length + extra.Length)); binary.Write(parameter); binary.Write(extra);
        }
        var card = new CardParser().Parse(path);
        if (card.Status != CardStatus.Valid) throw new Exception("Invalid fixture: " + card.Error);
        return card;
    }
    internal static byte[] Pack(object? value)
    {
        using var stream = new MemoryStream(); Write(value); return stream.ToArray();
        void Number(int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); stream.Write(bytes); }
        void Write(object? item)
        {
            switch (item)
            {
                case null: stream.WriteByte(0xc0); break;
                case bool flag: stream.WriteByte(flag ? (byte)0xc3 : (byte)0xc2); break;
                case int or long:
                    stream.WriteByte(0xd3); Span<byte> number = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(number, Convert.ToInt64(item)); stream.Write(number); break;
                case string text:
                    byte[] utf8 = Encoding.UTF8.GetBytes(text); stream.WriteByte(0xdb); Number(utf8.Length); stream.Write(utf8); break;
                case byte[] blob: stream.WriteByte(0xc6); Number(blob.Length); stream.Write(blob); break;
                case Dictionary<string, object?> map:
                    stream.WriteByte(0xdf); Number(map.Count); foreach (var pair in map) { Write(pair.Key); Write(pair.Value); } break;
                case System.Collections.IEnumerable items:
                    var array = items.Cast<object?>().ToArray(); stream.WriteByte(0xdd); Number(array.Length); foreach (var child in array) Write(child); break;
                default: throw new ArgumentException("Unsupported fixture value " + item.GetType());
            }
        }
    }
    internal static string Mod(string path, string guid, string version = "1.0", string manifest = "", params (string Path, string Text)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add("manifest.xml", $"<manifest schema-ver=\"1\"><guid>{guid}</guid><name>{guid}</name><game>HS2</game><version>{version}</version>{manifest}</manifest>");
        foreach (var (name, text) in entries) Add(name, text);
        if (!entries.Any(e => e.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
            Add("abdata/list/characustom/fixture.csv", "245\nID,MainAB,MainData\n42,0,0\n");
        return path;
        void Add(string name, string text) { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(text); }
    }
    internal static string Plugin(string path, string[] guids, PluginDependency[]? dependencies = null, string[]? references = null, string? embeddedShaders = null, bool inherited = false, string version = "1.0.0")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var assembly = AssemblyDefinition.CreateAssembly(new(Path.GetFileNameWithoutExtension(path), Version.Parse(version)), Path.GetFileNameWithoutExtension(path), ModuleKind.Dll);
        var module = assembly.MainModule;
        var bepinex = new AssemblyNameReference("BepInEx", new(5, 4, 0, 0)); module.AssemblyReferences.Add(bepinex);
        foreach (string reference in references ?? []) module.AssemblyReferences.Add(new(reference, new(1, 0, 0, 0)));
        var type = new TypeDefinition("Fixture", "Plugin", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object); module.Types.Add(type);
        TypeDefinition dependencyType = type;
        if (inherited)
        {
            dependencyType = new TypeDefinition("Fixture", "Base", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(dependencyType); type.BaseType = dependencyType;
        }
        foreach (string guid in guids)
            type.CustomAttributes.Add(Attribute("BepInPlugin", new[] { module.TypeSystem.String, module.TypeSystem.String, module.TypeSystem.String }, new object[] { guid, guid, version }));
        foreach (var dependency in dependencies ?? [])
        {
            var parameters = new[] { module.TypeSystem.String, dependency.MinimumVersion.Length > 0 ? module.TypeSystem.String : module.TypeSystem.Int32 };
            object versionOrFlags = dependency.MinimumVersion.Length > 0 ? dependency.MinimumVersion : dependency.Optional ? 2 : 1;
            dependencyType.CustomAttributes.Add(Attribute("BepInDependency", parameters, new object[] { dependency.Guid, versionOrFlags }));
        }
        if (embeddedShaders != null) module.Resources.Add(new EmbeddedResource("KK_Plugins.Resources.default.xml", ManifestResourceAttributes.Private, Encoding.UTF8.GetBytes(embeddedShaders)));
        assembly.Write(path); return path;
        CustomAttribute Attribute(string name, TypeReference[] parameters, object[] values)
        {
            var ctor = new MethodReference(".ctor", module.TypeSystem.Void, new TypeReference("BepInEx", name, module, bepinex)) { HasThis = true };
            foreach (var parameter in parameters) ctor.Parameters.Add(new ParameterDefinition(parameter));
            var attribute = new CustomAttribute(ctor);
            for (int i = 0; i < parameters.Length; i++) attribute.ConstructorArguments.Add(new(parameters[i], values[i]));
            return attribute;
        }
    }
}
