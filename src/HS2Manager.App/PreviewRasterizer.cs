using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace HS2Manager.App;

/// <summary>
/// A depth-tested, perspective-correct cutout renderer. WPF's Viewport3D writes depth
/// even for fully transparent texels, so it cannot render intersecting hair cards.
/// Prepared immutable geometry is shared between the interactive view and exports.
/// </summary>
public static class PreviewRasterizer
{
    private sealed record Surface(Vector3[] Positions, Vector3[] Normals, Vector2[] UV, int[] Indices, Texture Texture, float Specular, float Cutoff);
    private sealed record Texture(int Width, int Height, byte[] Pixels);
    private sealed record Scene(Surface[] Surfaces);
    private readonly record struct Vertex(Vector3 Position, Vector2 UV, Vector3 Light, float Specular);
    private readonly record struct Projected(float X, float Y, float InverseZ, Vector2 UV, Vector3 Light, float Specular);
    private readonly record struct Triangle(Projected A, Projected B, Projected C, Texture Texture, int Left, int Top, int Right, int Bottom, float Area, float Cutoff);
    private static readonly ConditionalWeakTable<Model3DGroup, Scene> Scenes = new();
    private static readonly float[] Linear = Enumerable.Range(0, 256).Select(i =>
    {
        float v = i / 255f;
        return v <= .04045f ? v / 12.92f : MathF.Pow((v + .055f) / 1.055f, 2.4f);
    }).ToArray();
    private static readonly byte[] Srgb = Enumerable.Range(0, 8193).Select(i =>
    {
        float v = i / 8192f;
        return (byte)Math.Clamp((int)MathF.Round(255 * (v <= .0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1 / 2.4f) - .055f)), 0, 255);
    }).ToArray();

    public static BitmapSource Render(Model3DGroup model, PerspectiveCamera camera, Model3DGroup lighting, Color background,
        int width, int height, CancellationToken cancellation = default)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(width));
        cancellation.ThrowIfCancellationRequested();
        var scene = Scenes.GetValue(model, Prepare);
        var eye = V(camera.Position); var forward = Vector3.Normalize(V(camera.LookDirection));
        var right = Vector3.Normalize(Vector3.Cross(forward, V(camera.UpDirection)));
        var up = Vector3.Cross(right, forward);
        float focal = width / (2 * MathF.Tan((float)camera.FieldOfView * MathF.PI / 360));
        float near = (float)Math.Max(.001, camera.NearPlaneDistance);
        float far = (float)camera.FarPlaneDistance;
        var ambient = lighting.Children.OfType<AmbientLight>().Aggregate(Vector3.Zero, (v, l) => v + C(l.Color));
        var lights = lighting.Children.OfType<DirectionalLight>().Select(l => (Direction: -Vector3.Normalize(V(l.Direction)), Color: C(l.Color))).ToArray();
        var triangles = new List<Triangle>(scene.Surfaces.Sum(s => s.Indices.Length / 3));
        Span<Vertex> clipped = stackalloc Vertex[5];
        foreach (var surface in scene.Surfaces)
        {
            cancellation.ThrowIfCancellationRequested();
            var vertices = new Vertex[surface.Positions.Length];
            var projected = new Projected[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var world = surface.Positions[i]; var delta = world - eye;
                var n = surface.Normals[i];
                var view = Vector3.Normalize(-delta);
                if (Vector3.Dot(n, view) < 0) n = -n;
                var diffuse = ambient; float specular = 0;
                foreach (var light in lights)
                {
                    float ndl = Math.Max(0, Vector3.Dot(n, light.Direction));
                    diffuse += light.Color * ndl;
                    var half = Vector3.Normalize(light.Direction + view);
                    specular += surface.Specular * MathF.Pow(Math.Max(0, Vector3.Dot(n, half)), 32) * ndl;
                }
                vertices[i] = new(new(Vector3.Dot(delta, right), Vector3.Dot(delta, up), Vector3.Dot(delta, forward)), surface.UV[i], diffuse, specular);
                if (vertices[i].Position.Z >= near) projected[i] = Project(vertices[i]);
            }
            for (int i = 0; i < surface.Indices.Length; i += 3)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                var a = vertices[surface.Indices[i]]; var b = vertices[surface.Indices[i + 1]]; var c = vertices[surface.Indices[i + 2]];
                if (a.Position.Z > far && b.Position.Z > far && c.Position.Z > far) continue;
                if (a.Position.Z >= near && b.Position.Z >= near && c.Position.Z >= near)
                {
                    AddTriangle(projected[surface.Indices[i]], projected[surface.Indices[i + 1]], projected[surface.Indices[i + 2]], surface);
                    continue;
                }
                int count = ClipNear(a, b, c, near, clipped);
                for (int j = 1; j < count - 1; j++)
                    AddTriangle(Project(clipped[0]), Project(clipped[j]), Project(clipped[j + 1]), surface);
            }
        }
        var pixels = new byte[width * height * 4]; var depth = new float[width * height];
        // Each worker owns complete scanlines. Depth and color never need locks.
        const int stripHeight = 32;
        var bins = Enumerable.Range(0, (height + stripHeight - 1) / stripHeight).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < triangles.Count; i++)
            for (int strip = triangles[i].Top / stripHeight; strip <= (triangles[i].Bottom - 1) / stripHeight; strip++) bins[strip].Add(i);
        Parallel.For(0, bins.Length, new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, strip =>
        {
            int firstY = strip * stripHeight, lastY = Math.Min(height, firstY + stripHeight);
            for (int p = firstY * width * 4; p < lastY * width * 4; p += 4)
            { pixels[p] = background.B; pixels[p + 1] = background.G; pixels[p + 2] = background.R; pixels[p + 3] = 255; }
            foreach (int index in bins[strip])
            {
                cancellation.ThrowIfCancellationRequested();
                var t = triangles[index]; float inverseArea = 1 / t.Area;
                float ax = (t.C.Y - t.B.Y) * inverseArea, bx = (t.A.Y - t.C.Y) * inverseArea;
                for (int y = Math.Max(firstY, t.Top); y < Math.Min(lastY, t.Bottom); y++)
                {
                    float wa = Edge(t.B.X, t.B.Y, t.C.X, t.C.Y, t.Left + .5f, y + .5f) * inverseArea;
                    float wb = Edge(t.C.X, t.C.Y, t.A.X, t.A.Y, t.Left + .5f, y + .5f) * inverseArea;
                    for (int x = t.Left; x < t.Right; x++, wa += ax, wb += bx)
                    {
                        float wc = 1 - wa - wb;
                        if (wa < -.00001f || wb < -.00001f || wc < -.00001f) continue;
                        float z = wa * t.A.InverseZ + wb * t.B.InverseZ + wc * t.C.InverseZ;
                        int pixel = y * width + x;
                        if (z <= depth[pixel]) continue;
                        var uv = (wa * t.A.UV + wb * t.B.UV + wc * t.C.UV) / z;
                        var texel = Sample(t.Texture, uv);
                        // Discard before writing depth, including intersecting triangles in the same mesh.
                        if (texel.W < t.Cutoff || texel.W <= .001f) continue;
                        var illumination = (wa * t.A.Light + wb * t.B.Light + wc * t.C.Light) / z;
                        float gloss = (wa * t.A.Specular + wb * t.B.Specular + wc * t.C.Specular) / z;
                        var rgb = new Vector3(texel.X, texel.Y, texel.Z) * illumination + new Vector3(gloss);
                        int dst = pixel * 4;
                        pixels[dst] = Encode(rgb.Z); pixels[dst + 1] = Encode(rgb.Y); pixels[dst + 2] = Encode(rgb.X);
                        depth[pixel] = z;
                    }
                }
            }
        });
        cancellation.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;

        Projected Project(Vertex v)
        {
            float z = 1 / v.Position.Z;
            return new(width * .5f + v.Position.X * z * focal, height * .5f - v.Position.Y * z * focal, z, v.UV * z, v.Light * z, v.Specular * z);
        }
        void AddTriangle(Projected p, Projected q, Projected r, Surface surface)
        {
            float area = Edge(p.X, p.Y, q.X, q.Y, r.X, r.Y);
            if (!float.IsFinite(area) || Math.Abs(area) < .00001f) return;
            if (area < 0) { (q, r) = (r, q); area = -area; }
            // Clamp before converting to int: near-plane intersections can project well offscreen.
            int left = (int)Math.Clamp(MathF.Floor(Math.Min(p.X, Math.Min(q.X, r.X))), 0, width);
            int top = (int)Math.Clamp(MathF.Floor(Math.Min(p.Y, Math.Min(q.Y, r.Y))), 0, height);
            int lastX = (int)Math.Clamp(MathF.Ceiling(Math.Max(p.X, Math.Max(q.X, r.X))), 0, width);
            int lastY = (int)Math.Clamp(MathF.Ceiling(Math.Max(p.Y, Math.Max(q.Y, r.Y))), 0, height);
            if (left < lastX && top < lastY) triangles.Add(new(p, q, r, surface.Texture, left, top, lastX, lastY, area, surface.Cutoff));
        }
    }

    private static Scene Prepare(Model3DGroup model)
    {
        var surfaces = new List<Surface>();
        foreach (var geometry in model.Children.OfType<GeometryModel3D>())
        {
            if (geometry.Geometry is not MeshGeometry3D mesh || mesh.Positions.Count == 0) continue;
            var materials = geometry.Material is MaterialGroup group ? group.Children.ToArray() : [geometry.Material];
            var brush = materials.OfType<DiffuseMaterial>().FirstOrDefault()?.Brush;
            Texture texture;
            if (brush is ImageBrush { ImageSource: BitmapSource source })
            {
                if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var bytes = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(bytes, source.PixelWidth * 4, 0);
                texture = new(source.PixelWidth, source.PixelHeight, bytes);
            }
            else
            {
                var color = (brush as SolidColorBrush)?.Color ?? Colors.White;
                texture = new(1, 1, [color.B, color.G, color.R, color.A]);
            }
            surfaces.Add(new(mesh.Positions.Select(V).ToArray(), mesh.Normals.Select(n => n.LengthSquared > 1e-12 ? Vector3.Normalize(V(n)) : -Vector3.UnitZ).ToArray(),
                mesh.TextureCoordinates.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray(), mesh.TriangleIndices.ToArray(), texture,
                materials.OfType<SpecularMaterial>().Any() ? .055f : .015f, (float)geometry.GetValue(NativePreviewRenderer.AlphaCutoffProperty)));
        }
        return new(surfaces.ToArray());
    }
    private static int ClipNear(Vertex a, Vertex b, Vertex c, float near, Span<Vertex> output)
    {
        int count = 0;
        Clip(c, a, near, output, ref count); Clip(a, b, near, output, ref count); Clip(b, c, near, output, ref count);
        return count;
    }
    private static void Clip(Vertex from, Vertex to, float near, Span<Vertex> output, ref int count)
    {
        bool before = from.Position.Z >= near, after = to.Position.Z >= near;
        if (before != after)
        {
            float t = (near - from.Position.Z) / (to.Position.Z - from.Position.Z);
            output[count++] = new(Vector3.Lerp(from.Position, to.Position, t), Vector2.Lerp(from.UV, to.UV, t), Vector3.Lerp(from.Light, to.Light, t), from.Specular + (to.Specular - from.Specular) * t);
        }
        if (after) output[count++] = to;
    }
    private static Vector4 Sample(Texture texture, Vector2 uv)
    {
        float u = (uv.X - MathF.Floor(uv.X)) * texture.Width - .5f, v = (uv.Y - MathF.Floor(uv.Y)) * texture.Height - .5f;
        int x = (int)MathF.Floor(u), y = (int)MathF.Floor(v); float fx = u - x, fy = v - y;
        int x0 = (x + texture.Width) % texture.Width, y0 = (y + texture.Height) % texture.Height;
        int x1 = (x0 + 1) % texture.Width, y1 = (y0 + 1) % texture.Height;
        return Vector4.Lerp(Vector4.Lerp(Read(x0, y0), Read(x1, y0), fx), Vector4.Lerp(Read(x0, y1), Read(x1, y1), fx), fy);
        Vector4 Read(int xx, int yy)
        {
            int p = (yy * texture.Width + xx) * 4; var b = texture.Pixels;
            return new(Linear[b[p + 2]], Linear[b[p + 1]], Linear[b[p]], b[p + 3] / 255f);
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Edge(float ax, float ay, float bx, float by, float x, float y) => (x - ax) * (by - ay) - (y - ay) * (bx - ax);
    private static byte Encode(float v) => Srgb[Math.Clamp((int)(v * 8192), 0, 8192)];
    private static Vector3 V(Point3D p) => new((float)p.X, (float)p.Y, (float)p.Z);
    private static Vector3 V(Vector3D p) => new((float)p.X, (float)p.Y, (float)p.Z);
    private static Vector3 C(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
}
