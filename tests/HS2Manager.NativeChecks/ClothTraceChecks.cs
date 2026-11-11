using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Core.Simulation;
using HS2Manager.Rendering;
using Vector3=System.Numerics.Vector3;

internal static class ClothTraceChecks
{
    public static void Capture(NativeCharacterScene scene,NativeGpuAssetStore assets,string fixtureDirectory,string output)
    {
        using var writer=new StreamWriter(output);
        using var cloth=new NativePhysXClothSimulation(scene);
        cloth.Trace=(kind,value)=>writer.WriteLine(JsonSerializer.Serialize(new{Kind=kind,Value=value},UnityCharacterService.JsonOptions));
        var motion=new NativeCharacterMotion(scene,assets,cloth);
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory,"fixture.json")));
        var warmup=new NativeRenderCamera(new(0,15.9255455f,5.721683f),new(0,15.9255455f,-.2183166f),35,.08f,1000);
        void Step(NativeRenderCamera camera)
        {motion.UpdateMotion(1f/30,0);scene.PoseFace(1);NativeFramePipeline.CompleteVisibility(scene,camera,1280,720);}
        motion.UpdateMotion(0,0);scene.PoseFace(1);
        for(int i=0;i<4;i++)Step(warmup);
        motion.ResetFixturePhysics();
        for(int i=0;i<180;i++)Step(warmup);
        foreach(var view in fixture.RootElement.GetProperty("Views").EnumerateArray())
        {
            Vector3 V(string key){var p=view.GetProperty(key);return new(p.GetProperty("x").GetSingle(),p.GetProperty("y").GetSingle(),p.GetProperty("z").GetSingle());}
            var camera=new NativeRenderCamera(V("Position"),V("Target"),view.GetProperty("Fov").GetSingle(),.08f,1000);
            for(int i=0;i<48;i++)
            {
                Step(camera);
                if(view.GetProperty("Name").GetString()=="back"&&i==45)return;
            }
            var visible=scene.Renderers.Where(r=>r.Enabled).ToArray();foreach(var r in visible)r.Enabled=false;
            for(int i=0;i<48;i++)Step(camera);
            foreach(var r in visible)r.Enabled=true;
        }
        throw new InvalidDataException("The fixture has no back-view sample.");
    }
}
