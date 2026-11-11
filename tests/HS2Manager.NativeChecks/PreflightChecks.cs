using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Core.Simulation;
using HS2Manager.Rendering;
using Vector3=System.Numerics.Vector3;

internal static class PreflightChecks
{
    internal static void Run(GraphicsDevice graphics,NativeGpuAssetStore store,NativeCharacterScene scene,string fixtureDirectory,string output)
    {
        var watch=Stopwatch.StartNew();using var cloth=new NativePhysXClothSimulation(scene);
        var motion=new NativeCharacterMotion(scene,store,cloth.HasCloth?cloth:null);motion.UpdateMotion(0,0);scene.PoseFace(1);
        for(int i=0;i<4;i++){motion.UpdateMotion(1f/30,0);scene.PoseFace(1);}motion.ResetFixturePhysics();
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory,"fixture.json")));
        var view=fixture.RootElement.GetProperty("Views").EnumerateArray().Single(v=>v.GetProperty("Name").GetString()=="face");
        Vector3 Vector(string name){var v=view.GetProperty(name);return new(v.GetProperty("x").GetSingle(),v.GetProperty("y").GetSingle(),v.GetProperty("z").GetSingle());}
        using var pipeline=new NativeFramePipeline(graphics,scene,store);pipeline.Draw(new(Vector("Position"),Vector("Target"),view.GetProperty("Fov").GetSingle(),.08f,1000),160,90,0);
        Directory.CreateDirectory(output);MaterialStateChecks.Capture(scene,pipeline.Appearance,fixtureDirectory,output);
        Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"preflight.json"),JsonSerializer.Serialize(new
        {SceneLoaded=true,OriginalMaterialPassesExecuted=true,Cloth=cloth.HasCloth,Milliseconds=watch.Elapsed.TotalMilliseconds,Diagnostics=scene.Diagnostics.Concat(motion.Diagnostics).Concat(pipeline.SourceReferenceDiagnostics),VisualParityVerified=false}));
        Console.WriteLine("PASS native catalog preflight; visual acceptance remains separate");
    }
}
