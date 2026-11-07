namespace HS2Manager.Core;

public static class NativeTextureComposer
{
    public static NativeTexture? Compose(NativeMaterial material, CancellationToken cancellation)
    {
        var source = material.MainTexture ?? material.ColorMask ?? material.AlphaMask ?? material.VisibilityMasks.FirstOrDefault();
        if (source == null) return null;
        var result = new NativeTexture { Width = source.Width, Height = source.Height, Pixels = new byte[source.Width * source.Height * 4] };
        var colors = material.Colors.Length > 0 ? material.Colors : [material.Color];
        var layers = material.Layers.Select(l => (Layer: l, Alpha: Enumerable.Range(0,l.Texture.Width*l.Texture.Height).Any(i => l.Texture.Pixels[i*4+3] < 250))).ToArray();
        for (int y = 0; y < result.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < result.Width; x++)
            {
                int p = (y * result.Width + x) * 4; float u = (x + .5f) / result.Width, v = (y + .5f) / result.Height;
                var main = material.MainTexture; int m = main?.Offset(u,v) ?? 0;
                float alpha = material.Transparent && main != null ? main.Pixels[m+3] / 255f : 1;
                if (material.MaskOnly && main != null) alpha *= main.Pixels[m+2] / 255f;
                for (int channel = 0; channel < 3; channel++)
                {
                    float value = main == null || material.MaskOnly ? 1 : material.Hair ? .75f + .25f * main.Pixels[m+2] / 255f : main.Pixels[m+channel] / 255f;
                    if (material.Tint) value *= Component(colors[0], channel);
                    else if (material.ColorMask is { } mask)
                    {
                        // The game's color mask starts with color 1 on black, then R/G/B select colors 2/3/4.
                        int c = mask.Offset(u,v); float tint = Component(colors[0],channel);
                        for (int i = 0; i < 3; i++) { float w = mask.Pixels[c+2-i] / 255f; tint = tint*(1-w)+w*Component(colors[Math.Min(i+1, colors.Length - 1)], channel); }
                        value *= tint;
                    }
                    result.Pixels[p+channel] = (byte)Math.Clamp(value * 255, 0, 255);
                }
                foreach (var (layer, useAlpha) in layers)
                {
                    float lu = u, lv = v;
                    if (layer.UVTransform is { Length: 4 } transform) { lu = u*transform[0]+transform[2]; lv = v*transform[1]+transform[3]; }
                    if (layer.Eyebrow)
                    {
                        var layout = layer.Layout is {Length: >= 4} ? layer.Layout : [.5f,.5f,.5f,.5f];
                        float side = u < .5f ? 1 : -1;
                        float cx = .5f - side * (.145f + (layout[0]-.5f)*.05f), cy = .445f + (layout[1]-.5f)*.1f;
                        float dx = (u-cx)*side, dy = v-cy, angle = (layer.Tilt-.5f)*.3f;
                        lu = (dx*MathF.Cos(angle)-dy*MathF.Sin(angle))*4*(2-1.5f*layout[2])+.5f;
                        lv = (dx*MathF.Sin(angle)+dy*MathF.Cos(angle))*4*(2-1.5f*layout[3])+.56f;
                    }
                    if (lu < 0 || lu > 1 || lv < 0 || lv > 1) continue;
                    // Eyebrow atlases pack coverage in blue; red/green also contain shader data.
                    int layerOffset = layer.Texture.Offset(lu,lv); float coverage = layer.Texture.Pixels[layerOffset+(layer.Eyebrow?0:useAlpha?3:2)] / 255f * layer.Color.A;
                    for (int c = 0; c < 3; c++) result.Pixels[p+c] = (byte)Math.Clamp(result.Pixels[p+c]*(1-coverage)+255*Component(layer.Color,c)*coverage,0,255);
                }
                if (material.Transparent && material.ColorMask is { } hairMask) alpha *= hairMask.Pixels[hairMask.Offset(u,v)+3] / 255f;
                if (material.AlphaMask is { } a) alpha *= a.Pixels[a.Offset(u,v)+2] / 255f;
                foreach (var mask in material.VisibilityMasks) alpha *= mask.Pixels[mask.Offset(u,v)+2] / 255f;
                // Binary coverage avoids WPF's unsorted translucent triangle artifacts on hair and clothing cutouts.
                result.Pixels[p+3] = alpha < .45f ? (byte)0 : (byte)255;
            }
        }
        return result;
    }
    public static NativeTexture Eye(NativeTexture iris, Dictionary<string, object?> parameters, CancellationToken cancellation)
    {
        var result = new NativeTexture { Width = 512, Height = 512, Pixels = new byte[512*512*4] };
        var white = CardAppearanceReader.Color(parameters, "whiteColor", new(.9f,.9f,.9f));
        var color = CardAppearanceReader.Color(parameters, "pupilColor", new(.2f,.3f,.4f));
        float sx = 2 - 1.5f * Convert.ToSingle(parameters.GetValueOrDefault("pupilW", .5f));
        float sy = 2 - 1.5f * Convert.ToSingle(parameters.GetValueOrDefault("pupilH", .5f));
        for (int y = 0; y < 512; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < 512; x++)
            {
                float u = ((x+.5f)/512-.5f)*sx+.5f, v = ((y+.5f)/512-.5f)*sy+.5f;
                int src = iris.Offset(u,v), dst = (y*512+x)*4;
                float mask = iris.Pixels[src] / 255f;
                float detail = Math.Clamp(iris.Pixels[src+2] / 128f, .15f, 1);
                float radius = MathF.Sqrt((u-.5f)*(u-.5f)+(v-.5f)*(v-.5f));
                bool black = radius < .085f;
                for (int c = 0; c < 3; c++)
                {
                    float pupil = black ? .015f : Component(color,c) * detail;
                    result.Pixels[dst+c] = (byte)Math.Clamp(255*(Component(white,c)*(1-mask)+pupil*mask),0,255);
                }
                result.Pixels[dst+3] = 255;
            }
        }
        return result;
    }
    private static float Component(PreviewColor color, int channel)
    {
        return Math.Clamp(channel == 0 ? color.B : channel == 1 ? color.G : color.R, 0, 1);
    }
}
