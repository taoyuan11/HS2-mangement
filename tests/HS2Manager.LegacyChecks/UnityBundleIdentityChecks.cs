using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;

internal static class UnityBundleIdentityChecks
{
    internal static void Run(string definitionPath, string output)
    {
        string json=File.ReadAllText(definitionPath);
        var original=JsonSerializer.Deserialize<CharacterDefinition>(json,UnityCharacterService.JsonOptions)!;
        var collisions=original.BundlePaths.GroupBy(NativeAssetBundle.CabName).Where(g=>g.Count()>1).SelectMany(g=>g).ToArray();
        if(collisions.Length<2)throw new InvalidOperationException("Fixture must contain a real CAB collision.");
        var hashes=collisions.ToDictionary(p=>p,Hash);
        var normalized=JsonSerializer.Deserialize<CharacterDefinition>(json,UnityCharacterService.JsonOptions)!;
        if(!UnityBundleIdentity.Normalize(normalized,output,default))throw new Exception("Collision not resolved.");
        for(int i=0;i<original.BundlePaths.Length;i++)
        {
            string source=original.BundlePaths[i],target=normalized.BundlePaths[i];
            if(source==target)continue;
            byte[] expected=Unpack(source),actual=Unpack(target);
            byte[] before=Encoding.ASCII.GetBytes(NativeAssetBundle.CabName(target)),after=Encoding.ASCII.GetBytes(NativeAssetBundle.CabName(source));
            int offset=0;
            while(offset<=actual.Length-before.Length){int found=actual.AsSpan(offset).IndexOf(before);if(found<0)break;offset+=found;after.CopyTo(actual,offset);offset+=before.Length;}
            if(!expected.AsSpan().SequenceEqual(actual))throw new Exception("Non-identity bytes changed in the normalized bundle.");
            // This file was created in this check's own sandbox, never a source bundle.
            File.WriteAllBytes(target,[0,1,2]);
        }
        var repaired=JsonSerializer.Deserialize<CharacterDefinition>(json,UnityCharacterService.JsonOptions)!;
        UnityBundleIdentity.Normalize(repaired,output,default);
        if(repaired.BundlePaths.Select(NativeAssetBundle.CabName).Distinct().Count()!=repaired.BundlePaths.Length)throw new Exception("CAB names still collide.");
        if(UnityBundleIdentity.Normalize(repaired,output,default))throw new Exception("Normalization must be idempotent.");
        foreach(var pair in hashes)if(Hash(pair.Key)!=pair.Value)throw new Exception("Original resource changed.");
        Console.WriteLine("PASS: real MOD CAB collisions, exact asset preservation, corrupt-cache recovery, idempotence, and original SHA-256.");
    }
    private static byte[] Unpack(string path)
    {
        var manager=new AssetsManager();
        try{var bundle=manager.LoadBundleFile(path,false);using var memory=new MemoryStream();using var writer=new AssetsFileWriter(memory);bundle.file.Unpack(writer);return memory.ToArray();}
        finally{manager.UnloadAll();}
    }
    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
}
