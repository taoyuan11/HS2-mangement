using System.Numerics;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using Vortice.DXGI;

namespace HS2Manager.Rendering;

/// <summary>Draws the serialized sky material and projects its linear radiance into a diffuse SH probe.</summary>
public sealed class NativeSkybox : IDisposable
{
    private readonly GraphicsDevice graphics;
    private readonly GpuMaterialInstance material;
    private readonly NativeMaterialPass pass;
    private readonly Dictionary<string,GpuTexture> textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string,float[]> values = new(StringComparer.Ordinal);
    private static readonly GpuMeshData cube=Cube();
    public NativeSkybox(GraphicsDevice graphics,GpuMaterialComposer composer,EnvironmentDefinition environment)
    {
        this.graphics=graphics;material=composer.Material("environment.skybox");
        if(environment.DhhEnabled){material.Set("_Exposure",environment.DhhFloats[31]);material.Set("_Rotation",environment.DhhFloats[40]);}
        foreach(var pair in material.Values)values.Add(pair.Key,(float[])pair.Value.Clone());
        foreach(var color in material.ColorProperties.Union(material.GammaProperties))if(values.TryGetValue(color,out var components))for(int c=0;c<Math.Min(3,components.Length);c++)components[c]=GpuMaterialComposer.Linear(components[c]);
        var source=material.Shader.Passes.First(p=>p.SubShader==0);
        foreach(var stage in new[]{NativeShaderStage.Vertex,NativeShaderStage.Pixel})
            foreach(var binding in ShaderBindings.Select(material.Shader,source,stage,material.Keywords).Textures)
            {
                textures[binding.Name]=composer.ResolveTexture(material,binding.Name);
                // These assets store non-HDR BC1 cubemaps; no RGBM decoding multiplier is present.
                if(textures[binding.Name].Texture.Description.Format is not (Format.BC1_UNorm_SRgb or Format.BC1_UNorm or Format.R16G16B16A16_Float or Format.R32G32B32A32_Float))
                    throw new NotSupportedException("Sky cubemap HDR decode metadata is required for this format.");
                values[binding.Name+"_HDR"]=[1,1,0,0];
            }
        pass=new(graphics,material.Shader,source,material.Keywords,material.Values.Where(p=>p.Value.Length==1).ToDictionary(p=>p.Key,p=>p.Value[0]),reversedDepth:true);
    }
    public void Draw(GpuTexture target,NativeRenderCamera camera,Vortice.Direct3D11.ID3D11DepthStencilView? depth=null)
        =>Draw(target.Target!,target.Width,target.Height,camera,depth);
    public void Draw(Vortice.Direct3D11.ID3D11RenderTargetView target,int width,int height,NativeRenderCamera camera,Vortice.Direct3D11.ID3D11DepthStencilView? depth=null)
    {
        var view=camera.View;view.M41=view.M42=view.M43=0;
        values["unity_ObjectToWorld"]=NativeForwardRenderer.Matrix(Matrix4x4.Identity);
        values["_ProjectionParams"]=[camera.BottomOrigin?-1:1,camera.Near,camera.Far,1/camera.Far];
        var projection=camera.Projection((float)width/height);
        if(depth!=null){projection.M33=0;projection.M43=0;}
        values["unity_MatrixVP"]=NativeForwardRenderer.Matrix(view*projection);
        graphics.Context.ClearState();graphics.Context.OMSetRenderTargets(target,depth);graphics.Context.RSSetViewport(0,0,width,height);
        pass.Draw(cube,0,values,textures);graphics.Context.ClearState();
    }
    public float[] GenerateAmbientProbe(int size=64)
    {
        if(size is < 16 or > 256)throw new ArgumentOutOfRangeException(nameof(size));
        using var target=new GpuTexture(graphics,size,size,true,Format.R32G32B32A32_Float,false);
        var coefficients=new double[27];double weightSum=0;
        Vector3[] directions=[Vector3.UnitX,-Vector3.UnitX,Vector3.UnitY,-Vector3.UnitY,Vector3.UnitZ,-Vector3.UnitZ];
        foreach(var direction in directions)
        {
            var camera=new NativeRenderCamera(Vector3.Zero,direction,90,.1f,10,Math.Abs(direction.Y)>.5f?Vector3.UnitZ:Vector3.UnitY);
            Draw(target,camera);var pixels=graphics.ReadbackFloat(target.Texture);Matrix4x4.Invert(camera.View,out var inverse);
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                double u=2d*(x+.5)/size-1,v=1-2d*(y+.5)/size;
                var d=Vector3.Normalize(Vector3.TransformNormal(new((float)u,(float)v,-1),inverse));
                double weight=4d/(size*size*Math.Pow(1+u*u+v*v,1.5));weightSum+=weight;
                double[] basis=[.28209479177387814,.4886025119029199*d.Y,.4886025119029199*d.Z,.4886025119029199*d.X,
                    1.0925484305920792*d.X*d.Y,1.0925484305920792*d.Y*d.Z,.31539156525252005*(3*d.Z*d.Z-1),1.0925484305920792*d.X*d.Z,.5462742152960396*(d.X*d.X-d.Y*d.Y)];
                for(int channel=0;channel<3;channel++)
                {
                    float value=pixels[(y*size+x)*4+channel];
                    if(!float.IsFinite(value))throw new InvalidDataException("Skybox generated non-finite radiance.");
                    for(int k=0;k<9;k++)coefficients[channel*9+k]+=value*basis[k]*weight;
                }
            }
        }
        double[] convolution=[.28209479177387814,.4886025119029199*2/3,.4886025119029199*2/3,.4886025119029199*2/3,
            1.0925484305920792/4,1.0925484305920792/4,.31539156525252005/4,1.0925484305920792/4,.5462742152960396/4];
        return coefficients.Select((c,index)=>(float)(c*convolution[index%9]*(4*Math.PI/weightSum))).ToArray();
    }
    public void Dispose()=>pass.Dispose();
    private static GpuMeshData Cube()
    {
        float[] p=[-1,-1,-1,1,-1,-1,-1,1,-1,1,1,-1,-1,-1,1,1,-1,1,-1,1,1,1,1,1];
        ushort[] i=[0,2,1,1,2,3,4,5,6,5,7,6,0,4,2,4,6,2,1,3,5,5,3,7,0,1,4,1,5,4,2,6,3,3,6,7];
        var vertices=new byte[p.Length*4];Buffer.BlockCopy(p,0,vertices,0,vertices.Length);var indices=new byte[i.Length*2];Buffer.BlockCopy(i,0,indices,0,indices.Length);
        return new("skybox",8,[new(0,0,0,0,3)],[0],[12],vertices,2,indices,[new(0,i.Length,0,0)],[],[]);
    }
}
