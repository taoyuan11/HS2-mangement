using System.Numerics;
using System.Text.Json;
using HS2Manager.Core;
using HS2Manager.Preview.Contracts;
using HS2Manager.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

internal static class MaterialProbe
{
    public static void Run(GraphicsDevice graphics, string definitionPath, string selection, string output)
    {
        var definition = JsonSerializer.Deserialize<CharacterDefinition>(File.ReadAllText(definitionPath), UnityCharacterService.JsonOptions)!;
        using var store = new NativeGpuAssetStore(definition.BundlePaths, gamePath: @"Z:\games\hs2\HS two DX - TSYMQ");
        var source = definition.Selections.Single(s => s.Key == selection).Reference;
        var material = store.Material(store.Reader(source.Bundle).Find(source.Asset, 21));
        var shader = store.Shader(material.Shader);
        var pass = shader.Passes[0];
        var keywords = material.Keywords.ToHashSet(StringComparer.Ordinal);
        var vertex = ShaderBindings.Select(shader, pass, NativeShaderStage.Vertex, keywords);
        var pixel = ShaderBindings.Select(shader, pass, NativeShaderStage.Pixel, keywords);
        Console.WriteLine(JsonSerializer.Serialize(new { Shader = shader.Name, Material = material.Name,
            Constants = vertex.Buffers.Concat(pixel.Buffers).SelectMany(b => b.Constants).Select(c => c.Name).Distinct(),
            Textures = vertex.Textures.Concat(pixel.Textures).Select(t => t.Name).Distinct() }));
        var values = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in shader.Properties.Get("m_Props").Children)
        {
            string name = property.String("m_Name");
            int type = (int)property.Get("m_Type").Number;
            if (type == 4) { defaults[name] = property.Get("m_DefTexture").String("m_DefaultName"); continue; }
            float[] value = [property.Float("m_DefValue[0]"), property.Float("m_DefValue[1]"), property.Float("m_DefValue[2]"), property.Float("m_DefValue[3]")];
            values[name] = type is 2 or 3 ? [value[0]] : value;
        }
        foreach (var pair in material.Floats) values[pair.Key] = [pair.Value];
        foreach (var pair in material.Colors) values[pair.Key] = [pair.Value.X, pair.Value.Y, pair.Value.Z, pair.Value.W];
        foreach (string name in new[] { "unity_ObjectToWorld", "unity_WorldToObject", "unity_MatrixVP", "unity_MatrixV", "unity_MatrixInvV", "glstate_matrix_projection" })
            values[name] = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
        values["_ScreenParams"] = [128,128,1+1f/128,1+1f/128];
        values["_ProjectionParams"] = [1,.1f,1000,.001f];
        var textures = new Dictionary<string, GpuTexture>(StringComparer.Ordinal);
        try
        {
            foreach (var binding in vertex.Textures.Concat(pixel.Textures).DistinctBy(t => t.Name))
            {
                var textureBinding = material.Textures.GetValueOrDefault(binding.Name);
                GpuTextureData texture;
                if (textureBinding != null && textureBinding.Texture.PathId != 0) texture = store.Texture(textureBinding.Texture);
                else
                {
                    string name = defaults.GetValueOrDefault(binding.Name, "white");
                    byte[] rgba = name switch
                    {
                        "" or "white" => [255,255,255,255], "black" => [0,0,0,0], "gray" => [128,128,128,128], "bump" => [128,128,255,128], "red"=>[255,0,0,0],
                        _ => throw new NotSupportedException("Shader default texture is not implemented: " + name)
                    };
                    texture = new(name,1,1,1,1,2,4,false,1,1,0,0,0,0,rgba);
                }
                textures.Add(binding.Name, new GpuTexture(graphics, texture));
                values[binding.Name + "_TexelSize"] = [1f/texture.Width,1f/texture.Height,texture.Width,texture.Height];
                values[binding.Name + "_ST"] = textureBinding == null ? [1,1,0,0] : [textureBinding.Scale.X,textureBinding.Scale.Y,textureBinding.Offset.X,textureBinding.Offset.Y];
            }
            using var renderPass = new NativeMaterialPass(graphics, shader, pass, keywords, material.Floats);
            using var target = graphics.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 128,128,1,1,BindFlags.RenderTarget));
            using var targetView = graphics.Device.CreateRenderTargetView(target);
            graphics.Context.OMSetRenderTargets(targetView);
            graphics.Context.RSSetViewport(0,0,128,128);
            graphics.Context.ClearRenderTargetView(targetView, new Color4(1,0,1,1));
            renderPass.Draw(Quad(), 0, values, textures);
            graphics.Context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null);
            byte[] pixels = graphics.Readback(target,128,128);
            if (Enumerable.Range(0,128*128).All(i => pixels[i*4] == 255 && pixels[i*4+1] == 0 && pixels[i*4+2] == 255))
                throw new Exception("The original material pass did not draw any pixels.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            ImageCodec.SavePngAsync(output,128,128,pixels).GetAwaiter().GetResult();
            Console.WriteLine("PASS original material GPU draw: " + shader.Name);
        }
        finally { graphics.Context.ClearState(); foreach (var texture in textures.Values) texture.Dispose(); }
    }
    private static GpuMeshData Quad()
    {
        float[] vertex = [-1,-1,0, 0,0, 1,-1,0, 1,0, -1,1,0, 0,1, 1,1,0, 1,1];
        byte[] bytes = new byte[vertex.Length*4]; Buffer.BlockCopy(vertex,0,bytes,0,bytes.Length);
        ushort[] index = [0,2,1,2,3,1]; byte[] indices = new byte[index.Length*2]; Buffer.BlockCopy(index,0,indices,0,indices.Length);
        return new("fullscreen",4,[new(0,0,0,0,3),new(4,0,12,0,2)],[0],[20],bytes,2,indices,[new(0,6,0,0)],[],[]);
    }
}
