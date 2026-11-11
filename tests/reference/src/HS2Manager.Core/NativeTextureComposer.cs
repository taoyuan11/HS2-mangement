namespace HS2Manager.Core;

public static class NativeTextureComposer
{
    public static NativeTexture? Compose(NativeMaterial material, CancellationToken cancellation)
    {
        var source = material.MainTexture ?? material.ColorMask ?? material.AlphaMask ?? material.VisibilityMasks.FirstOrDefault() ?? material.Layers.FirstOrDefault()?.Texture;
        if (source == null) return null;
        var result = new NativeTexture { Width = source.Width, Height = source.Height, Pixels = new byte[source.Width * source.Height * 4] };
        var colors = material.Colors.Length > 0 ? material.Colors : [material.Color];
        var layers = material.Layers.Select(l => (Layer: l, Alpha: l.FullColor || Enumerable.Range(0,l.Texture.Width*l.Texture.Height).Any(i => l.Texture.Pixels[i*4+3] < 250))).ToArray();
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
                    if (layer.FullColor)
                    {
                        if (layer.Replace) alpha = 0;
                        float combined = coverage + alpha * (1 - coverage);
                        for (int c = 0; c < 3; c++)
                            result.Pixels[p+c] = combined <= 0 ? (byte)0 : (byte)Math.Clamp(MathF.Round((result.Pixels[p+c] * alpha * (1-coverage) + layer.Texture.Pixels[layerOffset+c] * coverage) / combined), 0, 255);
                        alpha = combined;
                        continue;
                    }
                    for (int c = 0; c < 3; c++) result.Pixels[p+c] = (byte)Math.Clamp(result.Pixels[p+c]*(1-coverage)+255*Component(layer.Color,c)*coverage,0,255);
                }
                if (material.Transparent && material.ColorMask is { } hairMask) alpha *= hairMask.Pixels[hairMask.Offset(u,v)+3] / 255f;
                if (material.AlphaMask is { } a) alpha *= a.Pixels[a.Offset(u,v)+2] / 255f;
                foreach (var mask in material.VisibilityMasks) alpha *= mask.Pixels[mask.Offset(u,v)+2] / 255f;
                // Preserve coverage for filtered sampling. The viewport discards uncovered pixels
                // before the depth write, so intersecting hair cards cannot punch holes in one another.
                result.Pixels[p+3] = (byte)Math.Clamp((int)MathF.Round(alpha * material.Opacity * colors[0].A * 255), 0, 255);
            }
        }
        return result;
    }
    public static NativeTexture Eye(NativeTexture iris, Dictionary<string, object?> parameters, CancellationToken cancellation,
        NativeTexture? pupilMask = null, NativeTexture? highlight = null, Dictionary<string, object?>? face = null)
    {
        var result = new NativeTexture { Width = 512, Height = 512, Pixels = new byte[512*512*4] };
        var white = CardAppearanceReader.Color(parameters, "whiteColor", new(.9f,.9f,.9f));
        var color = CardAppearanceReader.Color(parameters, "pupilColor", new(.2f,.3f,.4f));
        var black = CardAppearanceReader.Color(parameters, "blackColor", new(0, 0, 0));
        face ??= [];
        var highlightColor = CardAppearanceReader.Color(face, "hlColor", new(1, 1, 1, 0));
        var layout = CardAppearanceReader.Flatten(face.GetValueOrDefault("hlLayout")).ToArray();
        if (layout.Length != 4) layout = [.5f, .5f, .5f, .5f];
        float tilt = (Parameter(face, "hlTilt") * 2 - 1) * MathF.PI;
        float offsetY = Parameter(face, "pupilY") - .5f;
        float sx = 2 - 1.5f * Convert.ToSingle(parameters.GetValueOrDefault("pupilW", .5f));
        float sy = 2 - 1.5f * Convert.ToSingle(parameters.GetValueOrDefault("pupilH", .5f));
        float bx = 4 - 3.6f * Parameter(parameters, "blackW"), by = 4 - 3.6f * Parameter(parameters, "blackH");
        bool pupilAlpha = pupilMask != null && Enumerable.Range(0, pupilMask.Width * pupilMask.Height).Any(i => pupilMask.Pixels[i * 4 + 3] < 250);
        for (int y = 0; y < 512; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < 512; x++)
            {
                float u = ((x+.5f)/512-.5f)*sx+.5f, v = ((y+.5f)/512-.5f + offsetY)*sy+.5f;
                int src = iris.Offset(u,v), dst = (y*512+x)*4;
                float mask = u is >= 0 and <= 1 && v is >= 0 and <= 1 ? iris.Pixels[src] / 255f : 0;
                // Red holds iris shading. Dividing by 128 and clamping flattened its bright
                // half into a solid disc; retain the full source range, including radial detail.
                float detail = .2f + .9f * iris.Pixels[src+2] / 255f;
                float radius = MathF.Sqrt((u-.5f)*(u-.5f)+(v-.5f)*(v-.5f));
                float pu = (u - .5f) * bx * 4 + .5f, pv = (v - .5f) * by * 4 + .5f;
                float pupil = pupilMask == null ? Math.Clamp((.085f - radius) * 512, 0, 1) :
                    pu is >= 0 and <= 1 && pv is >= 0 and <= 1 ? pupilMask.Pixels[pupilMask.Offset(pu, pv) + (pupilAlpha ? 3 : 2)] / 255f : 0;
                float dx = u - .5f, dy = v - .5f;
                float hu = (dx * MathF.Cos(tilt) - dy * MathF.Sin(tilt)) * (1.8f - 1.6f * layout[0]) + .5f + (layout[2] - .5f) * .6f;
                float hv = (dx * MathF.Sin(tilt) + dy * MathF.Cos(tilt)) * (1.8f - 1.6f * layout[1]) + .5f - (layout[3] - .5f) * .6f;
                float shine = highlight != null && hu is >= 0 and <= 1 && hv is >= 0 and <= 1 ? highlight.Pixels[highlight.Offset(hu, hv) + 2] / 255f * highlightColor.A : 0;
                for (int c = 0; c < 3; c++)
                {
                    float tint = Component(color,c) * detail;
                    tint = tint * (1 - pupil) + Component(black, c) * pupil;
                    float value = Component(white,c) * (1-mask) + tint * mask;
                    value = value * (1 - shine) + Component(highlightColor, c) * shine;
                    result.Pixels[dst+c] = (byte)Math.Clamp(255 * value,0,255);
                }
                result.Pixels[dst+3] = 255;
            }
        }
        return result;
    }
    private static float Parameter(Dictionary<string, object?> values, string key) => Math.Clamp(Convert.ToSingle(values.GetValueOrDefault(key, .5f)), 0, 1);
    private static float Component(PreviewColor color, int channel)
    {
        return Math.Clamp(channel == 0 ? color.B : channel == 1 ? color.G : color.R, 0, 1);
    }
}
