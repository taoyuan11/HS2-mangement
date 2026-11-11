using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Core.Simulation;
using HS2Manager.Rendering;
using Vector3=System.Numerics.Vector3;

internal static class FrameChecks
{
    public static void Capture(GraphicsDevice graphics,NativeGpuAssetStore store,NativeCharacterScene scene,string fixtureDirectory,string output,string? onlyView=null,bool dynamic=false,bool diagnostics=true,bool capturedProbeDiagnostic=false)
    {
        Directory.CreateDirectory(output);
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory,"fixture.json")));
        foreach(var expected in new[]{("Seed",1979),("Rate",30),("InitialFrames",180),("TemporalPhaseVersion",2),("PhysicsResetVersion",3)})
            if(fixture.RootElement.GetProperty(expected.Item1).GetInt32()!=expected.Item2)throw new InvalidDataException("Unsupported fixture setting: "+expected.Item1);
        File.Copy(Path.Combine(fixtureDirectory,"fixture.json"),Path.Combine(output,"fixture.json"),true);
        using var settings=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureDirectory,"render-settings.json")));
        var probe=settings.RootElement.GetProperty("AmbientProbe").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        using var cloth=new NativePhysXClothSimulation(scene);
        string? clothTracePath=Environment.GetEnvironmentVariable("HS2_CLOTH_TRACE");
        using var clothTrace=string.IsNullOrEmpty(clothTracePath)?null:new StreamWriter(clothTracePath);
        if(clothTrace!=null)cloth.Trace=(kind,value)=>clothTrace.WriteLine(JsonSerializer.Serialize(new{Kind=kind,Value=value},UnityCharacterService.JsonOptions));
        var motion=new NativeCharacterMotion(scene,store,cloth.HasCloth?cloth:null);
        var warmupCamera=new NativeRenderCamera(new(0,15.9255455f,5.721683f),new(0,15.9255455f,-.2183166f),35,settings.RootElement.GetProperty("Near").GetSingle(),settings.RootElement.GetProperty("Far").GetSingle());
        motion.UpdateMotion(0,0);scene.PoseFace(1);
        if(diagnostics)MeshChecks.Capture(scene,Path.Combine(output,"initial-meshes"));
        for(int i=0;i<4;i++){motion.UpdateMotion(1f/30,0);scene.PoseFace(1);NativeFramePipeline.CompleteVisibility(scene,warmupCamera,1280,720);}
        motion.ResetFixturePhysics();
        for(int i=0;i<180;i++){motion.UpdateMotion(1f/30,0);scene.PoseFace(1);NativeFramePipeline.CompleteVisibility(scene,warmupCamera,1280,720);}
        if(diagnostics&&cloth.HasCloth)MeshChecks.Capture(scene,Path.Combine(output,"warm-meshes"));
        using var pipeline=new NativeFramePipeline(graphics,scene,store);
        if(capturedProbeDiagnostic)pipeline.SetDiagnosticAmbientProbe(probe);
        var composer=pipeline.Composer;
        var appearance=pipeline.Appearance;
        MaterialStateChecks.Capture(scene,appearance,fixtureDirectory,output);
        if(diagnostics)
        {
        var hm=appearance.Materials.Single(p=>p.Key.Node.Name=="o_head").Value[0]!;
        var hp=hm.Shader.Passes.First(p=>NativeForwardRenderer.Tag(p,"LightMode").Equals("ForwardBase",StringComparison.OrdinalIgnoreCase));
        foreach(var stage in new[]{NativeShaderStage.Vertex,NativeShaderStage.Pixel})
        {
            var variant=ShaderBindings.Select(hm.Shader,hp,stage,new HashSet<string>(hm.Keywords.Concat(new[]{"DIRECTIONAL","LIGHTPROBE_SH","SHADOWS_SCREEN"})));
            File.WriteAllBytes(Path.Combine(output,"head-"+stage+".dxbc"),variant.Program.Bytecode);
            File.WriteAllText(Path.Combine(output,"head-"+stage+".json"),JsonSerializer.Serialize(new{variant.Buffers,variant.Channels,variant.Textures}));
            if(stage==NativeShaderStage.Pixel)
                foreach(var binding in variant.Textures.Where(t=>t.Dimension==2&&!t.Name.StartsWith("unity_",StringComparison.Ordinal)))
                {
                    var texture=composer.ResolveTexture(hm,binding.Name);
                    ImageCodec.SavePngAsync(Path.Combine(output,"head"+binding.Name+".png"),texture.Width,texture.Height,composer.ReadImage(texture)).GetAwaiter().GetResult();
                }
        }
        }
        var renderer=pipeline.Renderer;
        File.WriteAllText(Path.Combine(output,"post-settings.json"),JsonSerializer.Serialize(pipeline.PostSettings.Snapshot));
        var nativeProbe=pipeline.AmbientProbe.ToArray();
        File.WriteAllText(Path.Combine(output,"ambient-probe-64.json"),JsonSerializer.Serialize(new{Reference=probe,Native=nativeProbe,MaxError=probe.Zip(nativeProbe,(a,b)=>Math.Abs(a-b)).Max()}));
        int captureFrame=int.TryParse(Environment.GetEnvironmentVariable("HS2_NATIVE_CAPTURE_FRAME"),out int requestedCapture)?requestedCapture:47;
        if(captureFrame is <0 or >=48)throw new ArgumentOutOfRangeException("HS2_NATIVE_CAPTURE_FRAME");
        // Post-effect resets do not reset the camera's geometric velocity history.
        // Prime that history from the camera used during the reference warmup.
        Render(warmupCamera,0);
        foreach(var view in fixture.RootElement.GetProperty("Views").EnumerateArray())
        {
            string viewName=view.GetProperty("Name").GetString()!;
            bool capture=onlyView==null||onlyView==viewName;
            pipeline.ResetHistory();
            var trace=new List<object>();
            int headDraw=0,renderFrame=0;
            renderer.BindingTrace=diagnostics&&view.GetProperty("Name").GetString()=="face"?(name,values)=>
            {if(renderFrame==47&&name.StartsWith("o_head/",StringComparison.Ordinal))File.WriteAllText(Path.Combine(output,"head-values-"+(headDraw++)+".json"),JsonSerializer.Serialize(values));}:null;
            renderer.DrawTrace=viewName=="face"&&Environment.GetEnvironmentVariable("HS2_NATIVE_TRACE_DRAWS")=="1"?(name,texture)=>
            {if(renderFrame==47){var p=graphics.ReadbackFloat(texture.Texture);trace.Add(new{Name=name,Nose=p.AsSpan((461*1280+612)*4,4).ToArray()});}}:null;
            var camera=Camera(view);
            string? temporalTrace=Environment.GetEnvironmentVariable("HS2_TAA_PIXEL_TRACE");
            if(viewName=="side"&&!string.IsNullOrEmpty(temporalTrace))
            {
                int[] xy=(Environment.GetEnvironmentVariable("HS2_TAA_TRACE_PIXEL")??"623,363").Split(',').Select(int.Parse).ToArray();
                pipeline.TemporalDiagnosticTrace=(source,sharp,previous,current)=>
                {
                    var values=new[]{source,sharp,previous,current}.SelectMany(t=>graphics.ReadbackFloat(t.Texture).AsSpan((xy[1]*t.Width+xy[0])*4,4).ToArray());
                    File.AppendAllText(temporalTrace,renderFrame+","+string.Join(",",values.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))+"\n");
                };
            }
            for(renderFrame=0;renderFrame<48;renderFrame++)
            {
                motion.UpdateMotion(1f/30,0);scene.PoseFace(1);
                using var gpuCapture=capture&&renderFrame==captureFrame?GpuCapture.Capture(viewName,graphics.Device.NativePointer):null;
                Render(camera,0);
                if(capture&&renderFrame%12==11)Console.WriteLine($"Native {viewName} render warmup {renderFrame+1}/48");
            }
            pipeline.TemporalDiagnosticTrace=null;
            if(capture)
            {
            if(diagnostics&&view.GetProperty("Name").GetString()=="face")MeshChecks.Capture(scene,Path.Combine(output,"face-meshes"));
            Save(viewName+".png");SavePose(Path.Combine(output,viewName+"-pose.json"));
            if(trace.Count>0)File.WriteAllText(Path.Combine(output,"draw-trace.json"),JsonSerializer.Serialize(trace));
            }
            var visible=scene.Renderers.Where(r=>r.Enabled).ToArray();foreach(var r in visible)r.Enabled=false;
            for(int i=0;i<48;i++){motion.UpdateMotion(1f/30,0);scene.PoseFace(1);Render(camera,0);}
            if(capture)Save(viewName+"-background.png");
            foreach(var r in visible)r.Enabled=true;
        }
        if(dynamic)
        {
            if(onlyView!=null)throw new ArgumentException("Dynamic acceptance requires the full static fixture warmup.");
            renderer.BindingTrace=null;renderer.DrawTrace=null;
            pipeline.ResetHistory();
            // The fixture preserves the final face view's position while it sets
            // this intermediate camera before dynamic warmup.
            var initial=new NativeRenderCamera(new(0,15.9255455f,5.721683f),new(0,15.9255455f,-.2183166f),35,settings.RootElement.GetProperty("Near").GetSingle(),settings.RootElement.GetProperty("Far").GetSingle());
            for(int i=0;i<48;i++){motion.UpdateMotion(1f/30,0);scene.PoseFace(1);Render(initial,0);}
            string directory=Path.Combine(output,"dynamic");Directory.CreateDirectory(directory);var blink=new List<string>();
            for(int frame=1;frame<=360;frame++)
            {
                string? dynamicTrace=Environment.GetEnvironmentVariable("HS2_TAA_PIXEL_TRACE");
                if(Environment.GetEnvironmentVariable("HS2_TAA_TRACE_SCOPE")=="dynamic"&&!string.IsNullOrEmpty(dynamicTrace)&&frame>=270&&frame<=300)
                {
                    int diagnosticFrame=frame;int[] xy=(Environment.GetEnvironmentVariable("HS2_TAA_TRACE_PIXEL")??"623,363").Split(',').Select(int.Parse).ToArray();
                    pipeline.TemporalDiagnosticTrace=(source,sharp,previous,current)=>
                    {
                        var values=new[]{source,sharp,previous,current}.SelectMany(t=>graphics.ReadbackFloat(t.Texture).AsSpan((xy[1]*t.Width+xy[0])*4,4).ToArray());
                        File.AppendAllText(dynamicTrace+".dynamic.csv",diagnosticFrame+","+string.Join(",",values.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))+"\n");
                    };
                }
                else pipeline.TemporalDiagnosticTrace=null;
                float time=NativeTimeline.FixedTime(frame);motion.UpdateMotion(1f/30,time);blink.Add(frame+" "+motion.BlinkDiagnostic);
                using var gpuCapture=GpuCapture.Capture("dynamic-"+frame.ToString("D3"),graphics.Device.NativePointer);
                Render(Camera(fixture.RootElement.GetProperty("Views")[frame<=180?0:3]),time);
                if(frame==300&&Environment.GetEnvironmentVariable("HS2_BLEND_TRACE")is {} blendTrace)
                    BlendChecks.Capture(scene,blendTrace,Path.Combine(output,"skin-probe300"),true);
                if(diagnostics&&frame==195&&pipeline.MotionTexture is {} velocityTexture)
                {
                    var velocity=graphics.ReadbackFloat(velocityTexture.Texture);var bytes=new byte[velocity.Length*4];Buffer.BlockCopy(velocity,0,bytes,0,bytes.Length);File.WriteAllBytes(Path.Combine(directory,"frame-195.motion-f32"),bytes);
                }
                if(frame%15==0){Save("dynamic/frame-"+frame.ToString("D3")+".png");SavePose(Path.Combine(directory,"pose-"+frame.ToString("D3")+".json"));Console.WriteLine($"Native dynamic sample {frame}/360");}
            }
            pipeline.TemporalDiagnosticTrace=null;
            File.WriteAllLines(Path.Combine(directory,"blink.txt"),blink);File.WriteAllText(Path.Combine(directory,"complete.txt"),"360 fixed 1/30 steps; seed 1979; front then face.");
        }
        File.WriteAllText(Path.Combine(output,"render-scope.json"),JsonSerializer.Serialize(new
        {
            Mode="original-forward-passes",MaterialStateVersion=1,CapturedAmbientProbe=capturedProbeDiagnostic,VisualParityVerified=false,CapturedViews=onlyView==null?new[]{"front","side","back","face"}:[onlyView],
            RenderWarmupFrames=48,BetterAA=pipeline.HasTemporal,
            DynamicCaptured=dynamic,Cloth=cloth.HasCloth,ClothParityVerified=false,SourceReferenceDiagnostics=composer.SourceReferenceDiagnostics.ToArray(),
            EnabledEffects=pipeline.PostSettings.EnabledEffects,EffectsExecuted=true,UnsupportedEnabledEffects=Array.Empty<string>(),SceneDiagnostics=scene.Diagnostics.ToArray(),
            PendingImplementation=new[]{"depth of field","scalable ambient occlusion","LDR grading"},
            PendingValidation=new[]{"ambient probe","shadow maps","ambient occlusion","SMAA","dithering","BetterAA","transparent sort","deferred materials","screen-space reflections","motion blur"}
        }));
        cloth.WriteDiagnostics(Path.Combine(output,"cloth-topology.json"));
        NativeRenderCamera Camera(JsonElement value)=>new(Vector(value.GetProperty("Position")),Vector(value.GetProperty("Target")),value.GetProperty("Fov").GetSingle(),settings.RootElement.GetProperty("Near").GetSingle(),settings.RootElement.GetProperty("Far").GetSingle());
        void Render(NativeRenderCamera camera,float time)
        {
            pipeline.Draw(camera,1280,720,time);
        }
        void Save(string name)
        {
            var png=graphics.Readback(pipeline.LastFrame.Texture,1280,720);for(int pixel=3;pixel<png.Length;pixel+=4)png[pixel]=255;
            ImageCodec.SavePngAsync(Path.Combine(output,name),1280,720,png).GetAwaiter().GetResult();
        }
        void SavePose(string path)
        {
            string NodePath(NativeSceneNode node)=>node.Parent==null||node.Parent==scene.Root?node.Name:NodePath(node.Parent)+"/"+node.Name;
            File.WriteAllText(path,JsonSerializer.Serialize(new{Bones=scene.Root.Traverse().Select(n=>new{Name=n.Name,Parent=n.Parent?.Name??"",Path=NodePath(n),Position=new{x=n.LocalPosition.X,y=n.LocalPosition.Y,z=n.LocalPosition.Z},Rotation=new{x=n.LocalRotation.X,y=n.LocalRotation.Y,z=n.LocalRotation.Z,w=n.LocalRotation.W},Scale=new{x=n.LocalScale.X,y=n.LocalScale.Y,z=n.LocalScale.Z},World=new{x=n.Position.X,y=n.Position.Y,z=n.Position.Z},Matrix=Environment.GetEnvironmentVariable("HS2_MATRIX_TRACE")=="1"?NativeForwardRenderer.Matrix(n.WorldMatrix):null}),Faces=scene.Renderers.Where(r=>r.BlendWeights.Length>0).Select(r=>new{Name=r.Node.Name,BlendWeights=r.BlendWeights}),
                Renderers=scene.Renderers.Select(r=>new{r.Node.Name,r.Source.NodeId,Center=new[]{r.Bounds.Center.X,r.Bounds.Center.Y,r.Bounds.Center.Z},Extents=new[]{r.Bounds.Extents.X,r.Bounds.Extents.Y,r.Bounds.Extents.Z}})},
                new JsonSerializerOptions{NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
        }
    }
    private static Vector3 Vector(JsonElement value)=>new(value.GetProperty("x").GetSingle(),value.GetProperty("y").GetSingle(),value.GetProperty("z").GetSingle());
}
