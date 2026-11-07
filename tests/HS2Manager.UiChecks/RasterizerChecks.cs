using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HS2Manager.App;
using HS2Manager.Core;

internal static class RasterizerChecks
{
    public static void Run()
    {
        var lights = new Model3DGroup(); lights.Children.Add(new AmbientLight(Colors.White)); lights.Freeze();
        var camera = new PerspectiveCamera(new(0, 0, -4), new(0, 0, 1), new(0, 1, 0), 35) { NearPlaneDistance = .1 };
        // Both overlapping quads belong to ONE translucent renderer. Sorting whole objects
        // or putting every cutout last cannot fix the depth written by its transparent half.
        var texture = new NativeTexture { Width = 2, Height = 2, Pixels = [0,0,255,255, 0,0,0,0, 0,255,0,255, 0,255,0,255] };
        var mesh = new NativeMesh
        {
            Positions = [-1,-1,-.1f, 1,-1,-.1f, 1,1,-.1f, -1,1,-.1f, -1,-1,0, 1,-1,0, 1,1,0, -1,1,0],
            Normals = Enumerable.Range(0, 8).SelectMany(_ => new float[] { 0,0,-1 }).ToArray(),
            UV = [.25f,.25f, .75f,.25f, .75f,.25f, .25f,.25f, .25f,.75f, .75f,.75f, .75f,.75f, .25f,.75f],
            Indices = [0,2,1, 0,3,2, 4,6,5, 4,7,6], Materials = [new() { MainTexture = texture, Transparent = true }]
        };
        var first = Render(mesh);
        mesh.Indices = [4,6,5, 4,7,6, 0,2,1, 0,3,2];
        var reversed = Render(mesh);
        if (!first.SequenceEqual(reversed)) throw new Exception("Cutout pixels depend on triangle submission order");
        var left = Pixel(first, 80, 128); var right = Pixel(first, 176, 128);
        if (left.G < .8 || left.R > .1 || right.R < .65 || right.G > .1) throw new Exception("Intersecting cutouts occluded the rear surface");
        mesh.Materials[0].AlphaCutoff = .8f;
        var strict = Render(mesh);
        if (strict.SequenceEqual(first)) throw new Exception("Material alpha cutoff was ignored");

        // A triangle crosses the camera's near plane. The visible polygon must survive clipping.
        mesh = new() { Positions = [-.1f,-.1f,-4.1f, .7f,-.7f,-2, 0,.7f,-2], Normals = [0,0,-1, 0,0,-1, 0,0,-1],
            UV = [0,0, 1,0, .5f,1], Indices = [0,2,1], Materials = [new() { Colors = [new(1,0,0)] }] };
        if (Render(mesh).Where((_, i) => i % 4 == 2).Count(v => v > 100) < 1000) throw new Exception("Near-plane clipping dropped visible geometry");

        var gradient = new NativeTexture { Width = 128, Height = 128, Pixels = new byte[128 * 128 * 4] };
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
        { int p = (y * 128 + x) * 4; gradient.Pixels[p+2] = (byte)(x * 2); gradient.Pixels[p+1] = (byte)(y * 2); gradient.Pixels[p+3] = 255; }
        mesh = new() { Positions = [-1,-1,-2, 1,-1,2, 0,1,0], Normals = [0,0,-1, 0,0,-1, 0,0,-1], UV = [0,0, 1,0, .5f,1],
            Indices = [0,2,1], Materials = [new() { MainTexture = gradient }] };
        // World point with barycentric coordinates .2/.3/.5: position (.1,0,.2), UV (.55,.5).
        int projectedX = (int)(128 - .1 / 4.2 * 128 / Math.Tan(35 * Math.PI / 360));
        var point = Pixel(Render(mesh), projectedX, 128);
        if (Math.Abs(point.R - .55) > .03 || Math.Abs(point.G - .5) > .03) throw new Exception("Texture interpolation lost perspective correction");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { PreviewRasterizer.Render(NativePreviewRenderer.Build(new([mesh], [], 0, 0), default), camera, lights, Colors.Black, 256, 256, cancel.Token); throw new Exception("Cancelled frame rendered"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS intersecting cutouts, triangle order independence, material cutoff, near clipping, perspective UV and render cancellation");

        byte[] Render(NativeMesh item)
        {
            var frame = PreviewRasterizer.Render(NativePreviewRenderer.Build(new([item], [], 0, 0), default), camera, lights, Colors.Black, 256, 256);
            byte[] pixels = new byte[256 * 256 * 4]; frame.CopyPixels(pixels, 256 * 4, 0); return pixels;
        }
    }
    private static (double R, double G) Pixel(byte[] pixels, int x, int y) => (pixels[(y*256+x)*4+2]/255d, pixels[(y*256+x)*4+1]/255d);
}
