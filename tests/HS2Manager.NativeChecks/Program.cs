using System.Diagnostics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Rendering;
using Vortice.Mathematics;

try
{
    GpuCapture.Initialize();
    using var graphics = new GraphicsDevice();
    DataChecks.Run(graphics);
    if(args.Contains("--verify-native-package"))
        NativeExportChecks.VerifyAsync(args[Array.IndexOf(args,"--verify-native-package")+1],args[Array.IndexOf(args,"--materials")+1],
            args[Array.IndexOf(args,"--output")+1],Enum.Parse<ModelExportTarget>(args[Array.IndexOf(args,"--target")+1]),
            args[Array.IndexOf(args,"--tool")+1]).GetAwaiter().GetResult();
    if(args.Contains("--convert-native-package"))
        NativeExportChecks.ConvertAsync(args[Array.IndexOf(args,"--convert-native-package")+1],
            args[Array.IndexOf(args,"--output")+1],Enum.Parse<ModelExportTarget>(args[Array.IndexOf(args,"--target")+1]),
            args[Array.IndexOf(args,"--tool")+1]).GetAwaiter().GetResult();
    if(args.Contains("--inject-reference-resources"))ReferenceResourceChecks.Inject(args[Array.IndexOf(args,"--inject-reference-resources")+1],args[Array.IndexOf(args,"--bake-bundle")+1],args[Array.IndexOf(args,"--output")+1]);
    if(args.Contains("--bundle-directory"))
    {
        var manager=new AssetsTools.NET.Extra.AssetsManager();
        try
        {
            var bundle=manager.LoadBundleFile(args[Array.IndexOf(args,"--bundle-directory")+1],true);var entries=new List<object>();
            for(int i=0;i<bundle.file.BlockAndDirInfo.DirectoryInfos.Count;i++)
            {
                var entry=bundle.file.BlockAndDirInfo.DirectoryInfos[i];
                if(entry.Name.EndsWith(".resS",StringComparison.OrdinalIgnoreCase))continue;
                var file=manager.LoadAssetsFileFromBundle(bundle,i,false);
                entries.Add(new{entry.Name,Version=file.file.Metadata.UnityVersion,Classes=file.file.AssetInfos.Select(a=>a.TypeId).ToArray()});
            }
            File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(entries));
        }
        finally{manager.UnloadAll();}
    }
    if(args.Contains("--shader-index"))
    {
        using var reader=new NativeGpuAssetReader(args[Array.IndexOf(args,"--shader-index")+1]);
        File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(reader.AssetIndex().Where(a=>a.ClassId==48).Select(a=>new{Id=a.Id.ToString(),reader.Shader(a.Id).Name})));
    }
    if(args.Contains("--math-probe"))NativeMathChecks.Run(args[Array.IndexOf(args,"--math-probe")+1],args[Array.IndexOf(args,"--output")+1]);
    if(args.Contains("--curve-probe"))NativeCurveChecks.Run(args[Array.IndexOf(args,"--curve-probe")+1],args[Array.IndexOf(args,"--output")+1]);
    if(args.Contains("--native-export-transaction"))
        NativeExportChecks.RunAsync(@"Z:\games\hs2\HS two DX - TSYMQ",args[Array.IndexOf(args,"--card")+1],args[Array.IndexOf(args,"--output")+1]).GetAwaiter().GetResult();
    var pixels = graphics.ClearAndReadback(67, 31, new Color4(.25f, .5f, .75f, 1));
    for (int i = 0; i < pixels.Length; i += 4)
        if (Math.Abs(pixels[i] - 191) > 1 || Math.Abs(pixels[i + 1] - 128) > 1 || Math.Abs(pixels[i + 2] - 64) > 1 || pixels[i + 3] != 255)
            throw new Exception("GPU readback changed color, channel order, or row stride.");
    int shaders = 0, programs = 0;
    if(args.Contains("--builtin-dump"))
    {
        string output=args[Array.IndexOf(args,"--output")+1];Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output,"dither.bin"),NativeBuiltinTextures.DitherMask());
        File.WriteAllBytes(Path.Combine(output,"roughness.bin"),NativeBuiltinTextures.RoughnessLookup());
    }
    if(args.Contains("--named-shader"))
    {
        var definition=JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(args[Array.IndexOf(args,"--definition")+1]),UnityCharacterService.JsonOptions)!;
        using var store=new NativeGpuAssetStore(definition.BundlePaths,gamePath:@"Z:\games\hs2\HS two DX - TSYMQ");
        var shader=store.FindShader(args[Array.IndexOf(args,"--named-shader")+1]);
        File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(shader with{Programs=[]},UnityCharacterService.JsonOptions));
    }
    if(args.Contains("--prefab-probe"))
    {
        using var reader=new NativeGpuAssetReader(args[Array.IndexOf(args,"--prefab-probe")+1]);
        var prefab=reader.Prefab(args[Array.IndexOf(args,"--asset")+1]);
        File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(new{prefab.Nodes,prefab.Renderers},UnityCharacterService.JsonOptions));
    }
    if(args.Contains("--session-check"))SessionChecks.RunAsync(args[Array.IndexOf(args,"--session-check")+1],args[Array.IndexOf(args,"--output")+1]).GetAwaiter().GetResult();
    if(args.Contains("--asset-index"))
    {
        using var reader=new NativeGpuAssetReader(args[Array.IndexOf(args,"--asset-index")+1]);
        File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(reader.AssetIndex().Select(a=>new{Id=a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),a.ClassId,a.Name})));
    }
    if(args.Contains("--resource-layout"))
    {
        string name=args[Array.IndexOf(args,"--resource-layout")+1];int type=int.Parse(args[Array.IndexOf(args,"--class")+1]);
        using var store=new NativeGpuAssetStore([],gamePath:@"Z:\games\hs2\HS two DX - TSYMQ");
        var p=store.GameResource(name,type);object data;
        if(type==48)
        {
            var shader=store.Shader(p);
            data=new{shader.Name,Passes=shader.Passes.Select(pass=>new{pass.SubShader,pass.Metadata})};
        }
        else if(type==72)
        {
            var compute=store.Reader(p.Bundle).Compute(p.PathId);
            data=new{compute.Name,Kernels=compute.Kernels.Select(k=>new{k.Name,k.GroupSize,k.ConstantBuffers,k.Metadata})};
        }
        else throw new NotSupportedException("Resource layout only supports shader and compute assets.");
        File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(data,UnityCharacterService.JsonOptions));
        Console.WriteLine("PASS resource metadata "+name);
    }
    if(args.Contains("--shader-layout"))
    {
        using var reader=new NativeGpuAssetReader(args[Array.IndexOf(args,"--shader-layout")+1]);
        var shader=reader.Shader(reader.Find(args[Array.IndexOf(args,"--asset")+1],48).PathId);
        Console.WriteLine(JsonSerializer.Serialize(new{shader.Name,Programs=shader.Programs.Count,Passes=shader.Passes.Count}));
        if(args.Contains("--output"))File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(shader with{Programs=[]},UnityCharacterService.JsonOptions));
    }
    if (args.Contains("--dump-bundle"))
    {
        using var reader = new NativeGpuAssetReader(args[Array.IndexOf(args,"--dump-bundle")+1]);
        var pointer = reader.Find(args[Array.IndexOf(args,"--asset")+1], int.Parse(args[Array.IndexOf(args,"--class")+1]));
        File.WriteAllText(args[Array.IndexOf(args,"--output")+1], JsonSerializer.Serialize(reader.Metadata(pointer.PathId), UnityCharacterService.JsonOptions));
    }
    if (args.Contains("--dump-texture"))
    {
        using var reader = new NativeGpuAssetReader(args[Array.IndexOf(args,"--dump-texture")+1]);
        var pointer = reader.Find(args[Array.IndexOf(args,"--asset")+1], 28);
        var texture = reader.Texture(pointer.PathId);
        Console.WriteLine(JsonSerializer.Serialize(texture with { Bytes = [] }));
        Console.WriteLine("TAIL " + Convert.ToHexString(texture.Bytes.AsSpan(Math.Max(0, texture.Bytes.Length - 128))));
    }
    if (args.Contains("--dump-material"))
    {
        var definition=JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(args[Array.IndexOf(args,"--dump-material")+1]),UnityCharacterService.JsonOptions)!;
        using var store=new NativeGpuAssetStore(definition.BundlePaths,gamePath:@"Z:\games\hs2\HS two DX - TSYMQ");
        var reference=definition.Selections.Single(s=>s.Key==args[Array.IndexOf(args,"--selection")+1]).Reference;
        var material=store.Material(store.Reader(reference.Bundle).Find(reference.Asset,21));var shader=store.Shader(material.Shader);
        File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(new{Material=material,Shader=shader with{Programs=[]}},UnityCharacterService.JsonOptions));
    }
    if (args.Contains("--scene-probe"))
    {
        var definition = JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(args[Array.IndexOf(args,"--scene-probe")+1]), UnityCharacterService.JsonOptions)!;
        using var store = new NativeGpuAssetStore(definition.BundlePaths, gamePath: @"Z:\games\hs2\HS two DX - TSYMQ");
        var scene = new NativeCharacterScene(definition, store);
        if(args.Contains("--ambient-probe"))
        {
            using var probeComposer=new GpuMaterialComposer(graphics,store,definition);
            using var probeSky=new NativeSkybox(graphics,probeComposer,definition.Environment);
            var probes=args[Array.IndexOf(args,"--ambient-probe")+1].Split(',').Select(int.Parse)
                .Select(size=>new{Size=size,Coefficients=probeSky.GenerateAmbientProbe(size)}).ToArray();
            File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(probes));
        }
        if(args.Contains("--cloth-trace"))ClothTraceChecks.Capture(scene,store,args[Array.IndexOf(args,"--fixture")+1],args[Array.IndexOf(args,"--output")+1]);
        if(args.Contains("--material-layout"))
        {
            using var materialComposer=new GpuMaterialComposer(graphics,store,definition);
            using var appearance=new GpuCharacterAppearance(scene,materialComposer);
            var materialRows=appearance.Materials.Where(p=>p.Key.Node.Name==args[Array.IndexOf(args,"--material-layout")+1]).SelectMany(p=>p.Value.Where(m=>m!=null).Select(material=>
            {
                var pass=material!.Shader.Passes.First(v=>v.SubShader==0&&NativeForwardRenderer.Tag(v,"LightMode").Equals("ForwardBase",StringComparison.OrdinalIgnoreCase));
                var keywords=material.Keywords.Concat(new[]{"DIRECTIONAL","LIGHTPROBE_SH","SHADOWS_SCREEN","LIGHTMAP_OFF","DIRLIGHTMAP_OFF","DYNAMICLIGHTMAP_OFF"}).ToHashSet(StringComparer.Ordinal);
                var variant=ShaderBindings.Select(material.Shader,pass,NativeShaderStage.Pixel,keywords);
                return new{Renderer=p.Key.Node.Name,material.Name,material.Shader.PathId,material.Shader.Properties,material.Values,Textures=variant.Textures,variant.HardwareTier};
            })).ToArray();
            File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(materialRows,UnityCharacterService.JsonOptions));
        }
        if(args.Contains("--blend-probe"))BlendChecks.Capture(scene,args[Array.IndexOf(args,"--blend-probe")+1],args[Array.IndexOf(args,"--output")+1]);
        if(args.Contains("--blend-shape-flags"))
            File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(scene.Renderers.Where(r=>r.Mesh.BlendShapes.Length>0).Select(r=>new{r.Node.Name,r.Source.Mesh,Flags=store.Reader(r.Source.Mesh.Bundle).Metadata(r.Source.Mesh.PathId).Get("m_Shapes").Get("shapes"),Frames=r.Mesh.BlendShapes.Select(s=>new{s.Name,Frames=s.Frames.Select(f=>new{f.Weight,NormalNonzero=f.Normals.Count(n=>n.LengthSquared()>0),TangentNonzero=f.Tangents.Count(t=>t.LengthSquared()>0)})})}),UnityCharacterService.JsonOptions));
        if(args.Contains("--reference-pose"))
        {
            HS2Manager.Core.Simulation.NativeReferencePose.ApplyHeels(scene);
            if(args.Contains("--meshes"))MeshChecks.Capture(scene,args[Array.IndexOf(args,"--meshes")+1]);
            string NodePath(NativeSceneNode n)=>n.Parent==null||n.Parent==scene.Root?n.Name:NodePath(n.Parent)+"/"+n.Name;
            File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(new{Bones=scene.Root.Traverse().Select(n=>new{Name=n.Name,Path=NodePath(n),Position=new{x=n.LocalPosition.X,y=n.LocalPosition.Y,z=n.LocalPosition.Z},Rotation=new{x=n.LocalRotation.X,y=n.LocalRotation.Y,z=n.LocalRotation.Z,w=n.LocalRotation.W},Scale=new{x=n.LocalScale.X,y=n.LocalScale.Y,z=n.LocalScale.Z},World=new{x=n.Position.X,y=n.Position.Y,z=n.Position.Z}})}));
        }
        if(args.Contains("--renderer-bounds"))RendererBoundsChecks.Run(scene,store,args[Array.IndexOf(args,"--output")+1]);
        if(args.Contains("--gpu-deform-check"))GpuDeformationChecks.Run(graphics,store,scene,args[Array.IndexOf(args,"--output")+1]);
        if(args.Contains("--invalid-pixel-check"))InvalidPixelChecks.Run(graphics,store);
        if(args.Contains("--scene-state"))
            File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(scene.Renderers.Select(r=>new{r.Node.Name,r.Visible,r.Enabled,r.Node.Active,Role=r.Part.Role,r.Part.Slot,SourceSettings=r.Source.Settings,Submeshes=r.Mesh.Submeshes,Materials=r.Materials.Select(m=>new{m?.Name,Shader=m==null?"":store.Shader(m.Shader).Name,RenderType=m==null?"":NativeDepthNormals.Tag(store.Shader(m.Shader),0,"RenderType")})}),UnityCharacterService.JsonOptions));
        if(args.Contains("--preflight"))PreflightChecks.Run(graphics,store,scene,args[Array.IndexOf(args,"--fixture")+1],args[Array.IndexOf(args,"--output")+1]);
        if(args.Contains("--cloth-data"))
        {
            var data=scene.Parts.SelectMany(part=>part.Definition.Components.Where(c=>c.Type=="Cloth").Select(c=>new{Part=part.Definition.Prefab.Asset,c.Node,c.Data,
                Renderers=scene.Renderers.Where(r=>r.Part==part.Definition&&r.Node.Name==c.Node).Select(r=>new{r.Node.Name,r.Mesh.VertexCount,UniquePositions=Enumerable.Range(0,r.Mesh.VertexCount).Select(v=>new System.Numerics.Vector3(r.Mesh.Read(0,v,0),r.Mesh.Read(0,v,1),r.Mesh.Read(0,v,2))).Distinct().Count()})}));
            File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(data,UnityCharacterService.JsonOptions));
        }
        if(args.Contains("--export-geometry"))
        {
            var report=new HS2Manager.Export.NativeFbxGeometryExporter().Export(scene,args[Array.IndexOf(args,"--output")+1],progress:Console.WriteLine);
            Console.WriteLine(JsonSerializer.Serialize(new{report.GeometryReopened,report.MaterialsComplete,report.Result.Meshes,report.Result.Vertices,report.Result.BlendShapes,VisualParityVerified=false}));
        }
        if(args.Contains("--export-materials"))
        {
            var report=new HS2Manager.Export.NativeCharacterExporter().Export(graphics,scene,store,args[Array.IndexOf(args,"--output")+1],progress:Console.WriteLine);
            Console.WriteLine(JsonSerializer.Serialize(new{report.GeometryReopened,report.MaterialsComplete,report.Result.Meshes,report.Result.Materials,VisualParityVerified=false}));
        }
        if(args.Contains("--export-native"))
        {
            // Native workers own their GPU on one thread and preserve the unanimated export pose.
            HS2Manager.Export.NativeModelExportBackend.ExportDefinitionAsync(@"Z:\games\hs2\HS two DX - TSYMQ",definition,
                args[Array.IndexOf(args,"--output")+1],definition.EditRevision,true,new(),new Progress<string>(Console.WriteLine)).GetAwaiter().GetResult();
            Console.WriteLine("PASS native scene export completed without an engine process");
        }
        if(args.Contains("--frame"))
            FrameChecks.Capture(graphics,store,scene,args[Array.IndexOf(args,"--fixture")+1],args[Array.IndexOf(args,"--output")+1],args.Contains("--view")?args[Array.IndexOf(args,"--view")+1]:null,args.Contains("--frame-dynamic"),!args.Contains("--acceptance"),args.Contains("--reference-probe-diagnostic"));
        if(args.Contains("--appearance"))
        {
            using var composer=new GpuMaterialComposer(graphics,store,definition);
            using var appearance=new GpuCharacterAppearance(scene,composer);
            string folder=args[Array.IndexOf(args,"--output")+1];Directory.CreateDirectory(folder);
            foreach(var pair in appearance.Materials)
            {
                if(args.Contains("--appearance-role")&&pair.Key.Part.Role!=args[Array.IndexOf(args,"--appearance-role")+1])continue;
                if(args.Contains("--appearance-slot")&&pair.Key.Part.Slot!=int.Parse(args[Array.IndexOf(args,"--appearance-slot")+1]))continue;
                foreach(var material in pair.Value.Where(m=>m!=null).Cast<GpuMaterialInstance>())
                {
                    string diagnosticName=string.Concat(pair.Key.Node.Name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c))+"-"+pair.Key.Part.Role+"-"+pair.Key.Part.Slot+"-"+Array.IndexOf(pair.Value,material);
                    var report=new{Renderer=pair.Key.Node.Name,Material=material.Name,Shader=material.Shader.Name,material.Queue,Values=material.Values,
                        Source=pair.Key.Materials.ElementAtOrDefault(Array.IndexOf(pair.Value,material)),
                        CopiedColors=new[]{"_Color","_Color2","_Color3","_Color4"}.ToDictionary(n=>n,n=>material.CopyPropertyValue(n,4)),
                        Properties=material.Shader.Properties,Passes=material.Shader.Passes.Select(p=>new{p.SubShader,Metadata=p.Metadata}),Textures=material.TextureOverrides.Select(p=>new{p.Key,p.Value.Width,p.Value.Height})};
                    File.WriteAllText(Path.Combine(folder,diagnosticName+".json"),JsonSerializer.Serialize(report,UnityCharacterService.JsonOptions));
                    if(args.Contains("--appearance-textures")&&(!args.Contains("--appearance-renderer")||pair.Key.Node.Name==args[Array.IndexOf(args,"--appearance-renderer")+1]))foreach(string name in material.DefaultTextures.Keys.Union(material.Textures.Keys))
                    {
                        var texture=composer.ResolveTexture(material,name);
                        ImageCodec.SavePngAsync(Path.Combine(folder,diagnosticName+name+".png"),texture.Width,texture.Height,composer.ReadImage(texture)).GetAwaiter().GetResult();
                    }
                }
            }
            Console.WriteLine("PASS native appearance composition and material edits");
        }
        if (args.Contains("--motion"))
        {
            using var cloth=new HS2Manager.Core.Simulation.NativePhysXClothSimulation(scene);
            var motion = new HS2Manager.Core.Simulation.NativeCharacterMotion(scene, store,cloth.HasCloth?cloth:null);
            using var motionFixture=args.Contains("--fixture")?JsonDocument.Parse(File.ReadAllText(Path.Combine(args[Array.IndexOf(args,"--fixture")+1],"fixture.json"))):null;
            var warmCamera=new NativeRenderCamera(new(0,15.9255455f,5.721683f),new(0,15.9255455f,-.2183166f),35,definition.Environment.Camera.Float("near clip plane",.08f),definition.Environment.Camera.Float("far clip plane",1000));
            var motionCamera=warmCamera;
            NativeRenderCamera FixtureCamera(JsonElement value)
            {
                System.Numerics.Vector3 V(string key){var p=value.GetProperty(key);return new(p.GetProperty("x").GetSingle(),p.GetProperty("y").GetSingle(),p.GetProperty("z").GetSingle());}
                return new(V("Position"),V("Target"),value.GetProperty("Fov").GetSingle(),warmCamera.Near,warmCamera.Far);
            }
            void AdvanceMotion(float time=0,bool open=true)
            {
                motion.UpdateMotion(1f/30,time);if(open)scene.PoseFace(1);
                if(motionFixture!=null)NativeFramePipeline.CompleteVisibility(scene,motionCamera,1280,720);
            }
            motion.UpdateMotion(0,0); scene.PoseFace(1);
            for (int i=0;i<4;i++)AdvanceMotion();
            motion.ResetFixturePhysics();
            for (int i=0;i<180;i++)AdvanceMotion();
            if(motionFixture!=null)motionCamera=FixtureCamera(motionFixture.RootElement.GetProperty("Views")[0]);
            for(int i=0;i<48;i++)AdvanceMotion();
            string folder=args[Array.IndexOf(args,"--output")+1];Directory.CreateDirectory(folder);
            SavePose(Path.Combine(folder,"front-pose.json"));
            motion.WritePhysicsDiagnostics(Path.Combine(folder,"dynamics.json"));
            if(args.Contains("--dynamic"))
            {
                foreach(string view in new[]{"front","side","back","face"})
                {
                    if(motionFixture!=null)motionCamera=FixtureCamera(motionFixture.RootElement.GetProperty("Views").EnumerateArray().Single(v=>v.GetProperty("Name").GetString()==view));
                    if(view!="front")
                    {
                        for(int i=0;i<48;i++)AdvanceMotion();
                        SavePose(Path.Combine(folder,view+"-pose.json"));
                    }
                    motion.WritePhysicsDiagnostics(Path.Combine(folder,view+"-dynamics.json"));
                    var visible=scene.Renderers.Where(r=>r.Enabled).ToArray();foreach(var r in visible)r.Enabled=false;
                    for(int i=0;i<48;i++)AdvanceMotion();
                    motion.WritePhysicsDiagnostics(Path.Combine(folder,view+"-background-dynamics.json"));
                    foreach(var r in visible)r.Enabled=true;
                }
                motionCamera=warmCamera;
                for(int i=0;i<48;i++)AdvanceMotion();
                string dynamic=Path.Combine(folder,"dynamic");Directory.CreateDirectory(dynamic);
                var blink=new List<string>();
                for(int frame=1;frame<=360;frame++)
                {
                    if(motionFixture!=null)motionCamera=FixtureCamera(motionFixture.RootElement.GetProperty("Views")[frame<=180?0:3]);
                    AdvanceMotion(NativeTimeline.FixedTime(frame),false);blink.Add(frame+" "+motion.BlinkDiagnostic);
                    if(frame%15==0)SavePose(Path.Combine(dynamic,"pose-"+frame.ToString("D3")+".json"));
                    if(frame==300&&Environment.GetEnvironmentVariable("HS2_PHYSICS_TRACE")=="1")motion.WritePhysicsDiagnostics(Path.Combine(dynamic,"dynamics-300.json"));
                    if(frame==300&&Environment.GetEnvironmentVariable("HS2_BLEND_TRACE")is {} blendTrace)BlendChecks.Capture(scene,blendTrace,Path.Combine(folder,"skin-probe300"),true);
                }
                File.WriteAllLines(Path.Combine(dynamic,"blink.txt"),blink);
            }
            Console.WriteLine(JsonSerializer.Serialize(new {MotionDiagnostics=motion.Diagnostics,motion.BlinkDiagnostic}));
            void SavePose(string path)
            {
                string NodePath(NativeSceneNode node) => node.Parent == null || node.Parent == scene.Root ? node.Name : NodePath(node.Parent) + "/" + node.Name;
                File.WriteAllText(path, JsonSerializer.Serialize(new { Bones=scene.Root.Traverse().Select(n=>new {Name=n.Name,Path=NodePath(n),Position=new{x=n.LocalPosition.X,y=n.LocalPosition.Y,z=n.LocalPosition.Z},Rotation=new{x=n.LocalRotation.X,y=n.LocalRotation.Y,z=n.LocalRotation.Z,w=n.LocalRotation.W},Scale=new{x=n.LocalScale.X,y=n.LocalScale.Y,z=n.LocalScale.Z},World=new{x=n.Position.X,y=n.Position.Y,z=n.Position.Z},Matrix=Environment.GetEnvironmentVariable("HS2_MATRIX_TRACE")=="1"?NativeForwardRenderer.Matrix(n.WorldMatrix):null}),Faces=scene.Renderers.Where(r=>r.BlendWeights.Length>0).Select(r=>new {Name=r.Node.Name,BlendWeights=r.BlendWeights})}));
            }
        }
        if(args.Contains("--animation-reference"))AnimationChecks.Run(store,definition,args[Array.IndexOf(args,"--output")+1]);
        if (args.Contains("--animation"))
        {
            var reference = definition.Selections.Single(s => s.Key == "motion.controller").Reference;
            var reader = store.Reader(reference.Bundle);
            var clip = new NativeAnimationClip(reader.Metadata(reader.Find(definition.MotionState, 74).PathId));
            var player = new NativeAnimationPlayer(clip, scene.Parts.Single(p => p.Definition.Role == "rig").Root);
            player.Sample(float.Parse(args[Array.IndexOf(args,"--animation")+1], System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine(JsonSerializer.Serialize(new { Animation = clip.Name, clip.Duration, clip.CurveCount, UnboundPaths = player.UnboundPaths }));
            string NodePath(NativeSceneNode node) => node.Parent == null || node.Parent == scene.Root ? node.Name : NodePath(node.Parent) + "/" + node.Name;
            if (args.Contains("--output")) File.WriteAllText(args[Array.IndexOf(args,"--output")+1],JsonSerializer.Serialize(scene.Root.Traverse().Select(n => new
            { Path = NodePath(n), Position = new[]{n.LocalPosition.X,n.LocalPosition.Y,n.LocalPosition.Z}, Rotation = new[]{n.LocalRotation.X,n.LocalRotation.Y,n.LocalRotation.Z,n.LocalRotation.W}, Scale = new[]{n.LocalScale.X,n.LocalScale.Y,n.LocalScale.Z}, World = new[]{n.Position.X,n.Position.Y,n.Position.Z} })));
        }
        int vertices = 0;
        foreach (var renderer in scene.Renderers)
        {
            var deformed = renderer.Deform(); vertices += deformed.Positions.Length;
            foreach (var point in deformed.Positions)
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) throw new Exception("Non-finite deformed scene vertex.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Parts = scene.Parts.Count, Nodes = scene.Root.Traverse().Count(), Bones = scene.Bones.Count,
            Renderers = scene.Renderers.Count, Visible = scene.Renderers.Count(r => r.Visible), Vertices = vertices, Components = scene.Parts.Sum(p => p.Components.Length),
            ScopedAdjustments = scene.ScopedAdjustments.Count, VisualParityVerified = false }));
    }
    if (args.Contains("--skin-probe"))
    {
        var definition = JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(args[Array.IndexOf(args,"--skin-probe")+1]),UnityCharacterService.JsonOptions)!;
        string output=Path.GetFullPath(args[Array.IndexOf(args,"--output")+1]);Directory.CreateDirectory(output);
        using var store=new NativeGpuAssetStore(definition.BundlePaths,gamePath:@"Z:\games\hs2\HS two DX - TSYMQ");
        using var composer=new GpuMaterialComposer(graphics,store,definition);
        int trace=0; composer.Trace=value=>File.WriteAllText(Path.Combine(output,"pass-"+(trace++)+".json"),JsonSerializer.Serialize(value));
        foreach(string key in new[]{"body.skin.MainTex","face.skin.MainTex","template.black4096","template.black2048"})
        {
            var selection=definition.Selections.Single(s=>s.Key==key).Reference;
            var texture=store.Texture(store.Reader(selection.Bundle).Find(selection.Asset,28));
            Console.WriteLine(JsonSerializer.Serialize(new {Key=key,texture.Width,texture.Height,texture.Format,texture.Srgb,texture.MipCount,FirstBytes=Convert.ToHexString(texture.Bytes.AsSpan(0,Math.Min(32,texture.Bytes.Length)))}));
        }
        foreach(bool face in new[]{false,true})
        {
            var result=new GpuSkinComposer(composer).Compose(definition,face);
            using var color=result.Color;using var detail=result.Detail;
            string prefix=face?"o_head":"o_body_"+(definition.Sex==0?"cm":"cf");
            ImageCodec.SavePngAsync(Path.Combine(output,prefix+"_MainTex.png"),color.Width,color.Height,composer.ReadImage(color)).GetAwaiter().GetResult();
            ImageCodec.SavePngAsync(Path.Combine(output,prefix+"_DetailMainTex.png"),detail.Width,detail.Height,composer.ReadImage(detail)).GetAwaiter().GetResult();
        }
        Console.WriteLine("PASS native skin material composition completed; visual comparison required");
    }
    if (args.Contains("--material-probe"))
        MaterialProbe.Run(graphics, args[Array.IndexOf(args, "--material-probe") + 1], args[Array.IndexOf(args, "--selection") + 1], args[Array.IndexOf(args, "--output") + 1]);
    if (args.Contains("--assets"))
    {
        string definitionPath = args[Array.IndexOf(args, "--assets") + 1];
        var definition = JsonSerializer.Deserialize<HS2Manager.Preview.Contracts.CharacterDefinition>(File.ReadAllText(definitionPath), UnityCharacterService.JsonOptions)!;
        using var store = new NativeGpuAssetStore(definition.BundlePaths, gamePath: args.Contains("--game") ? args[Array.IndexOf(args, "--game") + 1] : @"Z:\games\hs2\HS two DX - TSYMQ");
        int meshes = 0, vertices = 0, shapes = 0, textures = 0, materials = 0;
        var issues = new List<string>();
        var visitedMeshes = new HashSet<GpuAssetPointer>(); var visitedTextures = new HashSet<GpuAssetPointer>(); var visitedMaterials = new HashSet<GpuAssetPointer>();
        foreach (var part in definition.Parts)
        {
            try
            {
                var prefab = store.Reader(part.Prefab.Bundle).Prefab(part.Prefab.Asset);
                foreach (var renderer in prefab.Renderers)
                {
                    var meshId = store.Resolve(renderer.Mesh);
                    if (visitedMeshes.Add(meshId))
                    {
                        var mesh = store.Mesh(meshId); meshes++; vertices += mesh.VertexCount; shapes += mesh.BlendShapes.Length;
                        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
                            foreach (int semantic in new[] { 0, 1, 2, 4 })
                                for (int component = 0; component < (semantic == 2 ? 4 : semantic == 4 ? 2 : 3); component++)
                                    if (!float.IsFinite(mesh.Read(semantic, vertex, component))) throw new Exception("Non-finite mesh channel.");
                    }
                    foreach (var materialId in renderer.Materials.Where(m => m.PathId != 0))
                    {
                        var resolved = store.Resolve(materialId);
                        if (!visitedMaterials.Add(resolved)) continue;
                        var material = store.Material(resolved); materials++;
                        foreach (var binding in material.Textures.Values.Where(t => t.Texture.PathId != 0))
                        {
                            var textureId = store.Resolve(binding.Texture);
                            if (!visitedTextures.Add(textureId)) continue;
                            var texture = store.Texture(textureId); textures++;
                            if (texture.Bytes.Length == 0) throw new Exception("Texture payload is empty.");
                        }
                    }
                }
            }
            catch (Exception error) { issues.Add(part.Role + "/" + part.Prefab.Asset + ": " + error); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Meshes = meshes, Vertices = vertices, BlendShapes = shapes, Textures = textures, Materials = materials, Issues = issues }));
        if (issues.Count > 0) throw new Exception("Original asset decoding has unresolved issues.");
    }
    if (args.Contains("--bundle"))
    {
        foreach (var shader in NativeShaderReader.Read(args[Array.IndexOf(args, "--bundle") + 1]))
        {
            foreach (var program in shader.Programs) { using var compiled = graphics.CreateShader(program); programs++; }
            shaders++;
        }
        if (programs == 0) throw new Exception("No original shader programs were tested.");
    }
    if (args.Contains("--program-blob"))
        foreach (var program in NativeShaderReader.DecodePrograms(File.ReadAllBytes(args[Array.IndexOf(args, "--program-blob") + 1])))
        { using var compiled = graphics.CreateShader(program); programs++; }
    string[] forbidden = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Select(m => m.ModuleName).Where(n => n.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) ||
            n.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) || n.Equals("mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (forbidden.Length != 0) throw new Exception("Engine modules loaded: " + string.Join(", ", forbidden));
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, graphics.AdapterName, Shaders = shaders, Programs = programs,
        ReadbackPixels = pixels.Length / 4, EngineModules = forbidden, VisualParityVerified = false }));
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
