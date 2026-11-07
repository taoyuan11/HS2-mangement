using System.Security.Cryptography;
using System.Text.Json;
using HS2Manager.Core;

internal static class NativePreviewAudit
{
    public sealed record Result(string File, string Name, string Status, int Meshes, int Triangles, int Materials, double Seconds,
        string[] Warnings, string? Error, string BeforeHash, string AfterHash);
    public static async Task Run(string game, string output, string reportName, string? only = null)
    {
        string directory=Path.Combine(game,"UserData","chara","female");
        // Deliberately top-level only: the scope must not change with library scan settings.
        string[] paths=Directory.EnumerateFiles(directory,"*",SearchOption.TopDirectoryOnly)
            .Where(p=>Path.GetExtension(p).Equals(".png",StringComparison.OrdinalIgnoreCase) && (only==null || Path.GetFileName(p)==only))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if(paths.Length==0)throw new InvalidOperationException("No top-level cards matched the audit selection.");
        if(Path.GetFileName(reportName)!=reportName)throw new ArgumentException("Report name must be a file name.",nameof(reportName));
        string data=Path.Combine(output,"female-audit-data"), report=Path.Combine(output,reportName+".json");
        Directory.CreateDirectory(data);
        var parser=new CardParser();var preview=new NativePreviewService();var results=new List<Result>();
        for(int i=0;i<paths.Length;i++)
        {
            string path=paths[i], before=Hash(path);var card=parser.Parse(path);var warnings=new List<string>();
            int meshes=0,triangles=0,materials=0;double seconds=0;string? error=null;
            if(card.Status==CardStatus.Valid)
            {
                using var cancellation=new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try
                {
                    var scene=await preview.LoadAsync(game,data,card,null,cancellation.Token);
                    NativePreviewChecks.Validate(scene);meshes=scene.Meshes.Count;triangles=scene.Meshes.Sum(m=>m.Indices.Length/3);seconds=scene.LoadSeconds;
                    warnings.AddRange(scene.Warnings);
                    foreach(var material in scene.Meshes.SelectMany(m=>m.Materials))
                    { _=NativeTextureComposer.Compose(material,cancellation.Token);materials++; }
                }
                catch(Exception ex) { error=ex.ToString(); }
            }
            else error=card.Error;
            string after=Hash(path);if(before!=after) throw new Exception("Audit source changed: "+path);
            results.Add(new(Path.GetFileName(path),card.Name,card.Status.ToString(),meshes,triangles,materials,seconds,warnings.ToArray(),error,before,after));
            File.WriteAllText(report,JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine($"[{i+1}/{paths.Length}] {Path.GetFileName(path)} meshes={meshes}, warnings={warnings.Count}, error={(error==null?"none":"yes")}");
        }
        Console.WriteLine($"AUDIT COMPLETE {results.Count} files; {results.Count(r=>r.Status=="Valid")} cards; {results.Count(r=>r.Warnings.Length>0)} with warnings; {results.Count(r=>r.Status=="Valid"&&r.Error!=null)} failures; report={report}");
        if(results.Any(r=>r.Error!=null))throw new Exception("Preview audit has failures; see "+report);
        static string Hash(string path) {using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    }
}
