using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

if(args.Length==0)throw new ArgumentException("Specify the unpacked native package directory and optional JSON report path.");
var root=Path.GetFullPath(args[0]);
if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
var failures=new List<object>();var binaries=new List<object>();var optionalRuntimeImports=new List<object>();
var engine=new Regex(@"^(UnityEngine(?:\.|$)|UnityEditor(?:\.|$)|UnityPlayer(?:\.|$)|HS2Manager\.(UnityPreview|Legacy[^.]*|Bridge)(?:\.|$)|UnityCrashHandler)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
var mono=new Regex(@"^(MonoBleedingEdge$|mono-2\.0|libmono)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
bool Forbidden(string name)=>engine.IsMatch(Path.GetFileName(name.Replace('\\','/')));
bool ForbiddenArtifact(string name)=>Forbidden(name)||mono.IsMatch(Path.GetFileName(name.Replace('\\','/')));
void Reject(string file,string kind,string dependency)=>failures.Add(new{File=Path.GetRelativePath(root,file),Kind=kind,Dependency=dependency});
foreach(var directory in Directory.EnumerateDirectories(root,"*",SearchOption.AllDirectories))
    if(ForbiddenArtifact(Path.GetFileName(directory)))Reject(directory,"Directory",Path.GetFileName(directory));
foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
{
    if(ForbiddenArtifact(Path.GetFileName(file)))Reject(file,"File",Path.GetFileName(file));
    using var stream=File.OpenRead(file);
    if(stream.Length<2||stream.ReadByte()!='M'||stream.ReadByte()!='Z')continue;
    stream.Position=0;
    try
    {
        using var pe=new PEReader(stream);
        var managed=new List<string>();var native=new List<string>();string? identity=null;
        if(pe.HasMetadata)
        {
            var metadata=pe.GetMetadataReader();
            string? digest=null;
            if(metadata.IsAssembly)
            {
                identity=metadata.GetString(metadata.GetAssemblyDefinition().Name);
                if(Forbidden(identity))Reject(file,"AssemblyIdentity",identity);
            }
            foreach(var handle in metadata.AssemblyReferences)
            {
                string name=metadata.GetString(metadata.GetAssemblyReference(handle).Name);managed.Add(name);
                if(Forbidden(name))Reject(file,"AssemblyReference",name);
            }
            foreach(var handle in metadata.MethodDefinitions)
            {
                var import=metadata.GetMethodDefinition(handle).GetImport();
                if(import.Module.IsNil)continue;
                string name=metadata.GetString(metadata.GetModuleReference(import.Module).Name);native.Add(name);
                if(ForbiddenArtifact(name))
                {
                    // Microsoft's .NET WinRT 2.2 binary retains an unreferenced
                    // Mono helper type. It has no callers in this .NET build.
                    // Record only this independently inspected, unchanged binary;
                    // every actual Mono artifact/import from another file fails.
                    // https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/MonoSupport.cs
                    var method=metadata.GetMethodDefinition(handle);var declaring=metadata.GetTypeDefinition(method.GetDeclaringType());
                    digest??=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                    bool optional=identity=="WinRT.Runtime"&&name.Equals("mono-2.0-bdwgc.dll",StringComparison.OrdinalIgnoreCase)
                        &&metadata.GetString(declaring.Namespace)=="WinRT"&&metadata.GetString(declaring.Name)=="Mono"
                        &&digest=="BCF3A14E8712A90837FC5C0D8C8A24696AF2BD7F74E9767597EC81EDEDFB23DB";
                    if(optional)optionalRuntimeImports.Add(new{File=Path.GetRelativePath(root,file),Method=metadata.GetString(method.Name),Dependency=name,SHA256=digest,
                        Reason="Unreferenced compatibility helper in the inspected Microsoft .NET WinRT 2.2 binary; no Mono runtime is included or loaded."});
                    else Reject(file,"PlatformInvoke",name);
                }
            }
            foreach(var handle in metadata.TypeDefinitions)
            {
                var type=metadata.GetTypeDefinition(handle);string name=metadata.GetString(type.Name);
                if(name is "UnityPreviewSession" or "UnityModelExportBackend" or "KernelWire")Reject(file,"LegacyRuntimeEndpoint",metadata.GetString(type.Namespace)+"."+name);
            }
        }
        if(pe.PEHeaders.PEHeader is {} header)
        {
            Imports(header.ImportTableDirectory,20,12,false);
            Imports(header.DelayImportTableDirectory,32,4,true);
        }
        binaries.Add(new{File=Path.GetRelativePath(root,file),Identity=identity,ManagedReferences=managed,NativeImports=native.Distinct().Order().ToArray()});
        void Imports(DirectoryEntry table,int stride,int nameOffset,bool delayed)
        {
            if(table.Size==0||table.RelativeVirtualAddress==0)return;
            var reader=pe.GetSectionData(table.RelativeVirtualAddress).GetReader();
            int available=Math.Min(table.Size,reader.Length);
            for(int offset=0;offset+stride<=available;offset+=stride)
            {
                reader.Offset=offset;uint first=reader.ReadUInt32();reader.Offset=offset+nameOffset;uint name=reader.ReadUInt32();
                if(first==0&&name==0)break;
                long rva=delayed&&(first&1)==0?(long)name-(long)pe.PEHeaders.PEHeader!.ImageBase:name;
                if(rva<=0||rva>int.MaxValue)throw new BadImageFormatException("Invalid import name address.");
                var data=pe.GetSectionData((int)rva).GetContent();int length=data.IndexOf((byte)0);
                if(length<1||length>32768)throw new BadImageFormatException("Invalid import name.");
                string dependency=System.Text.Encoding.ASCII.GetString(data.AsSpan(0,length));native.Add(dependency);
                if(ForbiddenArtifact(dependency))Reject(file,delayed?"DelayedNativeImport":"NativeImport",dependency);
            }
        }
    }
    catch(BadImageFormatException error){Reject(file,"InvalidPortableExecutable",error.Message);}
}
bool? nativeRuntimeSmokePassed=null;
if(args.Contains("--load-native"))
{
    try{NativeRuntimeSmoke.Run(root);nativeRuntimeSmokePassed=true;}
    catch(Exception error){nativeRuntimeSmokePassed=false;Reject(root,"NativeRuntimeSmoke",error.Message);}
}
var report=new{SchemaVersion=1,Directory=root,Passed=failures.Count==0&&binaries.Count>0,NativeRuntimeSmokePassed=nativeRuntimeSmokePassed,Binaries=binaries,OptionalRuntimeImports=optionalRuntimeImports,Failures=failures};
string json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
if(args.Length>1)File.WriteAllText(Path.GetFullPath(args[1]),json);
Console.WriteLine(JsonSerializer.Serialize(new{report.Passed,Binaries=binaries.Count,Failures=failures}));
return report.Passed?0:1;
