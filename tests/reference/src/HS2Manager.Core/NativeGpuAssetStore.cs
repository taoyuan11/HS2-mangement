namespace HS2Manager.Core;

/// <summary>Request-owned readers. External references are resolved by serialized CAB identity, never by asset name alone.</summary>
public sealed class NativeGpuAssetStore : IDisposable
{
    private readonly Dictionary<string, NativeGpuAssetReader> readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> cabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GpuAssetPointer, NativeShaderAsset> shaders = [];
    private readonly Dictionary<GpuAssetPointer,GpuAssetPointer> resolved=[];
    private readonly Dictionary<GpuAssetPointer,bool> existence=[];
    private readonly Dictionary<GpuAssetPointer,int?> classes=[];
    private readonly Dictionary<(string,int,int),GpuAssetPointer> gameResources=[];
    private readonly LinkedList<string> recent = new();
    private readonly string[] paths;
    private readonly CancellationToken cancellation;
    private bool indexed;
    private readonly string? gamePath;
    public NativeGpuAssetStore(IEnumerable<string> bundles, CancellationToken cancellation = default, string? gamePath = null)
    {
        this.cancellation = cancellation;
        this.gamePath = gamePath;
        paths = bundles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public NativeGpuAssetReader Reader(string path)
    {
        cancellation.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (readers.TryGetValue(path, out var cached)) { recent.Remove(path); recent.AddFirst(path); return cached; }
        while (readers.Count >= 3)
        {
            string last = recent.Last!.Value; readers[last].Dispose(); readers.Remove(last); recent.RemoveLast();
        }
        var reader = new NativeGpuAssetReader(path);
        readers.Add(path, reader); recent.AddFirst(path); return reader;
    }
    public GpuAssetPointer Resolve(GpuAssetPointer reference)
    {
        cancellation.ThrowIfCancellationRequested();
        if(reference.FileId==0||reference.PathId==0)return reference;
        if(resolved.TryGetValue(reference,out var found))return found;
        var result=ResolveUncached(reference);resolved.Add(reference,result);return result;
    }
    private GpuAssetPointer ResolveUncached(GpuAssetPointer reference)
    {
        cancellation.ThrowIfCancellationRequested();
        if (reference.PathId == 0 || reference.FileId == 0) return reference;
        var origin = Reader(reference.Bundle);
        if (reference.FileId < 1 || reference.FileId > origin.ExternalNames.Length) throw new InvalidDataException("External resource index is invalid.");
        string cab = origin.ExternalNames[reference.FileId - 1];
        if (cab is "unity default resources" or "unity_builtin_extra")
        {
            if (gamePath == null) throw new FileNotFoundException("A game directory is required for built-in resource data: " + cab);
            string builtIn = Path.Combine(gamePath, "HoneySelect2_Data", "Resources", cab);
            if (!File.Exists(builtIn)) throw new FileNotFoundException("Built-in resource data is missing.", builtIn);
            return new(builtIn, 0, reference.PathId);
        }
        if(cab.EndsWith(".assets",StringComparison.OrdinalIgnoreCase))
        {
            string file=Path.Combine(Path.GetDirectoryName(reference.Bundle)!,cab);
            if(!File.Exists(file)&&gamePath!=null)file=Path.Combine(gamePath,"HoneySelect2_Data",cab);
            if(File.Exists(file))return new(file,0,reference.PathId);
        }
        if (!indexed)
        {
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                string identity = NativeAssetBundle.CabName(path);
                if (!cabs.TryGetValue(identity, out var matches)) cabs[identity] = matches = [];
                matches.Add(path);
            }
            indexed = true;
        }
        if (!cabs.TryGetValue(cab, out var candidates)) throw new FileNotFoundException("Referenced resource bundle is missing: " + cab);
        if (candidates.Count > 1)
        {
            // Re-exported MOD bundles may share a CAB. Own-bundle references have an unambiguous identity.
            string? local = candidates.FirstOrDefault(p => p.Equals(Path.GetFullPath(reference.Bundle), StringComparison.OrdinalIgnoreCase));
            if (local != null) return new(local, 0, reference.PathId);
            throw new InvalidDataException("Ambiguous resource bundle identity: " + cab + " (" + string.Join(", ", candidates) + ")");
        }
        return new(candidates[0], 0, reference.PathId);
    }
    public GpuMeshData Mesh(GpuAssetPointer reference) { var p = Resolve(reference); return Reader(p.Bundle).Mesh(p.PathId, cancellation); }
    public GpuTextureData Texture(GpuAssetPointer reference) { var p = Resolve(reference); return Reader(p.Bundle).Texture(p.PathId); }
    public bool ContainsAsset(GpuAssetPointer reference)
    {var p=Resolve(reference);if(!existence.TryGetValue(p,out bool found))existence.Add(p,found=Reader(p.Bundle).ContainsAsset(p.PathId));return found;}
    public int? AssetClass(GpuAssetPointer reference)
    {var p=Resolve(reference);if(!classes.TryGetValue(p,out var found))classes.Add(p,found=Reader(p.Bundle).AssetClass(p.PathId));return found;}
    public GpuMaterialData Material(GpuAssetPointer reference) { var p = Resolve(reference); return Reader(p.Bundle).Material(p.PathId); }
    public NativeShaderAsset Shader(GpuAssetPointer reference)
    { var p = Resolve(reference); if (!shaders.TryGetValue(p,out var shader)) shaders.Add(p,shader=Reader(p.Bundle).Shader(p.PathId,cancellation) with{SourceBundle=p.Bundle}); return shader; }
    public NativeShaderAsset FindShader(string name)
    {
        foreach (var path in paths)
        {
            GpuAssetPointer pointer;
            try { pointer=Reader(path).Find(name,48); }
            catch (InvalidDataException) { continue; }
            return Shader(pointer);
        }
        throw new FileNotFoundException("Shader resource is missing: " + name);
    }
    public GpuAssetPointer GameResource(string name,int classId,int textureSize=0)
    {
        if(textureSize<0||textureSize>0&&classId!=28)throw new ArgumentException("Texture-size selection requires a 2D texture resource.");
        if(gameResources.TryGetValue((name,classId,textureSize),out var found))return found;
        if(gamePath==null)throw new InvalidOperationException("Game resources require a configured installation.");
        string directory=Path.Combine(gamePath,"HoneySelect2_Data");
        var files=Directory.EnumerateFiles(directory,"*.assets").OrderByDescending(p=>Path.GetFileName(p)=="sharedassets2.assets")
            .Concat(new[]{Path.Combine(directory,"Resources","unity_builtin_extra"),Path.Combine(directory,"Resources","unity default resources")}.Where(File.Exists));
        foreach(var path in files)
        {
            cancellation.ThrowIfCancellationRequested();
            try{found=Reader(path).Find(name,classId,textureSize);gameResources.Add((name,classId,textureSize),found);return found;}
            catch(InvalidDataException error) when(error.Message.StartsWith("Resource ",StringComparison.Ordinal)){}
        }
        throw new FileNotFoundException("Game resource is missing: "+classId+"/"+name);
    }
    public void Dispose()
    {
        foreach (var reader in readers.Values) reader.Dispose();
        readers.Clear(); recent.Clear(); cabs.Clear(); shaders.Clear();resolved.Clear();existence.Clear();classes.Clear();gameResources.Clear();
    }
}
