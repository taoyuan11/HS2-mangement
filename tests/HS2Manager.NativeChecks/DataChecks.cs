using System.Buffers.Binary;
using System.Numerics;
using HS2Manager.Core;
using HS2Manager.Rendering;

internal static class DataChecks
{
    public static void Run(GraphicsDevice graphics)
    {
        NativeMathChecks.VerifyReference();
        NativeCurveChecks.VerifyReference();
        // Measured from the frozen camera's native culling projection. The
        // analytic sphere formula drifts enough to change hair shadow coverage.
        float shadowFar=NativeDirectionalShadows.CascadeFar(new(Vector3.Zero,-Vector3.UnitZ,35,.08f,1000),1280f/720,120);
        if(Math.Abs(shadowFar-93.49427032470703f)>1e-5f)
            throw new Exception("Directional shadow culling no longer preserves the unprojected far plane.");
        var propertyShader=new NativeShaderAsset("property-sheet",1,new(),[],[]);
        var propertyMaterial=new GpuMaterialInstance(new("material",default,-1,[],new Dictionary<string,float>{{"_Glossiness",.7152174f}},new Dictionary<string,Vector4>(),new Dictionary<string,GpuTextureBinding>())
            {OverrideTags=new Dictionary<string,string>{{"RenderType","TransparentCutout"}}},propertyShader);
        if(propertyMaterial.HasProperty("_Glossiness"))
            throw new Exception("An orphan serialized property was exposed before runtime assignment.");
        propertyMaterial.Set("_Glossiness",.7152174f);
        if(!propertyMaterial.HasProperty("_Glossiness")||propertyMaterial.Values["_Glossiness"][0]!=.7152174f)
            throw new Exception("A runtime material property absent from ShaderLab was lost.");
        propertyMaterial.Set("_Glossiness2",.25f);propertyMaterial.SetShader(propertyShader);
        if(!propertyMaterial.HasProperty("_Glossiness2")||propertyMaterial.Values["_Glossiness2"][0]!=.25f)
            throw new Exception("MaterialEditor property history was lost across a shader change.");
        if(propertyMaterial.Tag("RenderType")!="TransparentCutout")throw new Exception("A serialized material render-type override was lost across a shader change.");
        // A red mask selects the saved second colour even when the current
        // shader no longer declares it. Dropping that history exports white
        // cloth where the established material copy produces black.
        using(var copyAssets=new NativeGpuAssetStore([]))
        using(var copyComposer=new GpuMaterialComposer(graphics,copyAssets,new()))
        using(var copyBake=new NativePbrBakePass(graphics,copyComposer))
        using(var redMask=new GpuTexture(graphics,new("red mask",1,1,1,1,2,4,false,1,1,0,0,0,0,[255,0,0,255])))
        {
            var copied=new GpuMaterialInstance(new("copied cloth",default,-1,[],new Dictionary<string,float>(),new Dictionary<string,Vector4>{{"_Color2",Vector4.Zero}},new Dictionary<string,GpuTextureBinding>()),propertyShader);
            if(copied.HasProperty("_Color2"))throw new Exception("Copyable material history leaked into shader property queries.");
            using var baked=copyBake.Draw(copied,new Dictionary<string,GpuTexture?>{{"_MainTex",copyComposer.Default("white")},{"_ColorMask",redMask}},
                new Dictionary<string,float[]>{{"_BakeKind",[0]},{"_Color",[1,1,1,1]},{"_BakeAlpha",[1]}},1,1,true);
            if(!graphics.Readback(baked.Texture,1,1).SequenceEqual(new byte[]{0,0,0,255}))throw new Exception("Export baking lost a copied colour absent from the current shader.");
            var missing=new GpuMaterialInstance(new("missing cloth colour",default,-1,[],new Dictionary<string,float>(),new Dictionary<string,Vector4>(),new Dictionary<string,GpuTextureBinding>()),propertyShader);
            using var missingBake=copyBake.Draw(missing,new Dictionary<string,GpuTexture?>{{"_MainTex",copyComposer.Default("white")},{"_ColorMask",redMask}},
                new Dictionary<string,float[]>{{"_BakeKind",[0]},{"_Color",[1,1,1,1]},{"_BakeAlpha",[1]}},1,1,true);
            if(!graphics.Readback(missingBake.Texture,1,1).SequenceEqual(new byte[]{0,0,0,255}))throw new Exception("Copying a source with no colour retained the destination shader default.");
        }
        var parent = new NativeSceneNode("parent","a") { LocalPosition = new(10,20,30), LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2), LocalScale = new(2,3,4) };
        var child = new NativeSceneNode("child","b") { LocalPosition = Vector3.UnitX }; child.SetParent(parent);
        Near(child.Position,new(10,22,30));
        Near(child.WorldMatrix.Translation,new(10,22,30));
        Near(child.InverseTransformPoint(child.TransformPoint(new(1,2,3))),new(1,2,3));
        parent.LocalPosition = new(11,20,30); Near(child.Position,new(11,22,30));
        Near(child.WorldMatrix.Translation,new(11,22,30));
        Throws<InvalidDataException>(() => parent.SetParent(child));
        if (parent.Parent != null || child.Parent != parent) throw new Exception("Rejected reparent modified the graph.");
        child.Position = new(11,24,30); Near(child.LocalPosition,new(2,0,0));
        var identity = new NativeSceneNode("identity","c"); child.SetParent(identity); Near(child.Position,new(2,0,0));
        identity.Active = false;
        if (child.ActiveInHierarchy) throw new Exception("Parent visibility was not inherited.");

        float[] source = [1,0,0, 0,1,0, 1,0,0,-1, .25f,.75f,0,0, 0,1,0,0];
        var raw = new byte[source.Length*4]; Buffer.BlockCopy(source,0,raw,0,raw.Length);
        var frames = new[] { new GpuBlendFrame(50,[0],[new(2,0,0)],[Vector3.Zero],[Vector3.Zero]), new GpuBlendFrame(100,[0],[new(4,0,0)],[Vector3.Zero],[Vector3.Zero]) };
        var mesh = new GpuMeshData("test",1,[new(0,0,0,0,3),new(1,0,12,0,3),new(2,0,24,0,4),new(12,0,40,0,4),new(13,0,56,0,4)],
            [0],[72],raw,2,[],[],[Matrix4x4.Identity,Matrix4x4.Identity],[new("blend",1,frames)]);
        var deformed = NativeMeshDeformer.Deform(mesh,[75],[Matrix4x4.CreateTranslation(4,0,0),Matrix4x4.CreateTranslation(0,4,0)]);
        Near(deformed.Positions[0],new(5,3,0)); Near(deformed.Normals[0],Vector3.UnitY);
        if (deformed.Tangents[0].W != -1) throw new Exception("Skinning changed tangent handedness.");
        var scaledBytes=(byte[])raw.Clone();BinaryPrimitives.WriteSingleLittleEndian(scaledBytes.AsSpan(12),1);BinaryPrimitives.WriteSingleLittleEndian(scaledBytes.AsSpan(16),1);
        var scaledNode=new NativeSceneNode("nonuniform","scaled"){LocalScale=new(2,3,4)};
        var scaledRenderer=new NativeSceneRenderer
        {
            Node=scaledNode,Part=new(),Source=new(1,true,true,default,[],[],default,[],new()),
            Mesh=mesh with{VertexBytes=scaledBytes},Materials=[],Bones=[new("bone0","0"),new("bone1","1")],BlendWeights=[0]
        };
        var scaledVertices=scaledRenderer.DeformAnimation();
        var worldNormal=Vector3.Normalize(Vector3.TransformNormal(scaledVertices.Normals[0],Matrix4x4.Transpose(scaledNode.InverseWorldMatrix)));
        Near(worldNormal,Vector3.Normalize(new Vector3(1,1,0)));
        // Captured no-bone SkinnedMeshRenderer inputs stay in authored space;
        // nonuniform scale belongs to its object matrix and normal transform.
        var noBone=new NativeSceneRenderer
        {
            Node=scaledNode,Part=new(),Source=new(1,true,true,default,[],[],default,[],new()),
            Mesh=mesh with{VertexBytes=scaledBytes,BindPoses=[]},Materials=[],Bones=[],BlendWeights=[0]
        };
        if(noBone.RenderWorldMatrix!=scaledNode.WorldMatrix)throw new Exception("A no-bone skin lost its full renderer transform.");
        Near(noBone.DeformAnimation().Normals[0],new(1,1,0));
        using(var gpuNoBone=new NativeGpuMeshDeformer(graphics))
        {
            var rawNoBone=gpuNoBone.Readback(gpuNoBone.Deform(noBone));
            Near(rawNoBone.Positions[0],Vector3.UnitX);Near(rawNoBone.Normals[0],new(1,1,0));
        }
        if (!raw.SequenceEqual(mesh.VertexBytes) || mesh.Read(0,0,0) != 1) throw new Exception("Deformation mutated the source mesh.");
        Throws<ArgumentException>(() => NativeMeshDeformer.Deform(mesh,[],[]));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Throws<OperationCanceledException>(() => NativeMeshDeformer.Deform(mesh,[75],[],canceled.Token));

        var texture = new GpuTextureData("mips",7,5,3,1,2,10,true,2,1,0,0,0,0,Enumerable.Range(0,48).Select(i => (byte)i).ToArray());
        var layout = TextureUpload.Layout(texture);
        if (layout.Levels.Count != 3 || layout.Levels[0].RowPitch != 16 || layout.Levels[0].Bytes.Length != 32 ||
            layout.Levels[1].Bytes[0] != 32 || layout.Levels[2].Bytes[0] != 40) throw new Exception("Compressed mip layout changed.");
        Throws<InvalidDataException>(() => TextureUpload.Layout(texture with { Bytes = new byte[47] }));
        var implicitMips=TextureUpload.Layout(texture with{MipCount=0});
        if(implicitMips.Levels.Count!=3||!implicitMips.Levels.Zip(layout.Levels).All(p=>p.First.Bytes.SequenceEqual(p.Second.Bytes)))throw new Exception("Implicit legacy mip chain changed its payload.");
        Throws<InvalidDataException>(()=>TextureUpload.Layout(texture with{MipCount=0,Bytes=new byte[47]}));
        var blockTrailer=texture with{Bytes=[..texture.Bytes,..new byte[32]]};
        if(TextureUpload.Layout(blockTrailer).Levels.Count!=3)throw new Exception("Known BC trailer was interpreted as image levels.");
        blockTrailer.Bytes[^1]=1;Throws<InvalidDataException>(()=>TextureUpload.Layout(blockTrailer));
        Throws<NotSupportedException>(() => { using var rejected = new GpuTexture(graphics, texture); });
        using (var gpuTexture = new GpuTexture(graphics, texture with { Width=8, Height=8, MipCount=4, Bytes=new byte[56] }))
            if (gpuTexture.Texture.Description.MipLevels != 4 || gpuTexture.Texture.Description.Width != 8) throw new Exception("GPU upload changed texture dimensions or levels.");
        // The frozen composed-image reference reduces encoded bytes: three
        // 16 values and one 255 produce 76, rather than linear-light 138.
        using(var composed=new GpuTexture(graphics,4,4))
        using(var reduced=new GpuTexture(graphics,2,2,format:Vortice.DXGI.Format.B8G8R8A8_UNorm,mipmaps:false))
        {
            var pixels=new byte[4*4*4];
            for(int y=0;y<4;y++)for(int x=0;x<4;x++)for(int c=0;c<4;c++)pixels[(y*4+x)*4+c]=(byte)((x&1)!=0&&(y&1)!=0?255:16);
            graphics.Context.UpdateSubresource(pixels,composed.Texture,0,16,64);
            composed.GenerateMips(graphics);
            graphics.Context.CopySubresourceRegion(reduced.Texture,0,0,0,0,composed.Texture,1);
            if(graphics.Readback(reduced.Texture,2,2).Any(value=>value!=76))throw new Exception("Composed mip reduction changed the reference colour space.");
            if(composed.View.Description.Format!=Vortice.DXGI.Format.B8G8R8A8_UNorm_SRgb||composed.Target!.Description.Format!=Vortice.DXGI.Format.B8G8R8A8_UNorm_SRgb)
                throw new Exception("Composed mip generation changed rendering or sampling colour space.");
        }
        var rgb = new GpuTextureData("rgb",2,1,1,1,2,3,false,1,1,0,0,0,0,[1,2,3,4,5,6]);
        if (!TextureUpload.Layout(rgb).Levels[0].Bytes.SequenceEqual(new byte[]{1,2,3,255,4,5,6,255})) throw new Exception("RGB24 upload changed pixel ordering.");
        foreach(int format in new[]{3,5})
        {
            int stride=format==3?3:4;var payload=new byte[stride*2+26];
            rgb.Bytes.CopyTo(payload,0);"TRUEVISION-XFILE.\0"u8.CopyTo(payload.AsSpan(stride*2+8));
            for(int i=stride*2;i+stride<=payload.Length;i+=stride)payload.AsSpan(i,stride).Reverse();
            var withFooter=rgb with{Format=format,Bytes=payload};
            var withoutFooter=withFooter with{Bytes=payload[..(stride*2)]};
            if(!TextureUpload.Layout(withFooter).Levels[0].Bytes.SequenceEqual(TextureUpload.Layout(withoutFooter).Levels[0].Bytes))throw new Exception("Known TGA footer altered texture pixels.");
            payload[^2]^=1;Throws<InvalidDataException>(()=>TextureUpload.Layout(withFooter));
        }
        // Quality overrides must preserve explicit opt-out and higher authored levels.
        using(var forced=new GpuTexture(graphics,rgb with{Anisotropy=6},2))
            if(forced.Sampler.Description.MaxAnisotropy!=9||forced.Sampler.Description.Filter!=Vortice.Direct3D11.Filter.Anisotropic)throw new Exception("Forced anisotropy did not reach the GPU sampler.");
        using(var optedOut=new GpuTexture(graphics,rgb with{Anisotropy=0},2))
            if(optedOut.Sampler.Description.Filter==Vortice.Direct3D11.Filter.Anisotropic)throw new Exception("Forced quality ignored a texture's explicit anisotropy opt-out.");
        if(GpuTexture.EffectiveAnisotropy(16,2)!=16||GpuTexture.EffectiveAnisotropy(16,0)!=0)throw new Exception("Texture quality lost an authored level or disable override.");
        var constants = new ShaderBuffer("test",0,32,[new("a",0,1,2,false,0)]);
        var packed = ShaderBindings.Pack(constants,new Dictionary<string,float[]> { ["a"]=[.25f,.75f] });
        if (BinaryPrimitives.ReadSingleLittleEndian(packed) != .25f || BinaryPrimitives.ReadSingleLittleEndian(packed.AsSpan(16)) != .75f)
            throw new Exception("Constant array did not use HLSL register alignment.");
        Throws<InvalidDataException>(() => ShaderBindings.Pack(constants,new Dictionary<string,float[]>()));
        Throws<InvalidDataException>(() => NativeShaderReader.DecodePrograms(new byte[3]));
        byte[] malformed = new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(malformed,1);
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(4),int.MaxValue); BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(8),4);
        Throws<InvalidDataException>(() => NativeShaderReader.DecodePrograms(malformed));
        var unsupported=new HS2Manager.Preview.Contracts.EnvironmentDefinition{Profiles=[new(){Effects=[new(){Name="DepthOfField",Children=[new(){Name="enabled",Children=[new(){Name="overrideState",Number=1},new(){Name="value",Number=1}]}]}]}]};
        Throws<NotSupportedException>(()=>new NativePostSettings(unsupported).ValidateCoverage());
        var dither=NativeBuiltinTextures.DitherMask();
        if(Enumerable.Range(0,16).Any(i=>dither.AsSpan(i*16,16).Count((byte)255)!=(i<8?i:i+1)))throw new Exception("Dither volume changed alpha coverage.");
        using(var builtin=new NativeBuiltinTextures(graphics))if(!builtin.TryView("_DitherMaskLOD",out var view)||view.View.Description.ViewDimension!=Vortice.Direct3D.ShaderResourceViewDimension.Texture3D)throw new Exception("Dither shadow input is not a three-dimensional resource.");
        using(var builtin=new NativeBuiltinTextures(graphics))if(!builtin.TryView("unity_NHxRoughness",out var view)||view.View.Description.Format!=Vortice.DXGI.Format.R16_UNorm)throw new Exception("Legacy specular lookup lost its linear 16-bit precision.");
        Console.WriteLine("PASS native transform, deformation, texture mip, shader layout and malformed-data checks");
    }
    private static void Near(Vector3 actual, Vector3 expected)
    { if (Vector3.Distance(actual,expected) > .0001f) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
