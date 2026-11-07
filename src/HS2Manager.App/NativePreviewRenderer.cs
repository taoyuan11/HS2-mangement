using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HS2Manager.Core;

namespace HS2Manager.App;

public static class NativePreviewRenderer
{
    public static Model3DGroup Build(NativePreviewScene scene, CancellationToken cancellation)
    {
        var model = new Model3DGroup();
        var cutouts = new List<GeometryModel3D>();
        foreach (var part in scene.Meshes)
        {
            cancellation.ThrowIfCancellationRequested();
            var positions = new Point3DCollection(); var normals = new Vector3DCollection();
            for (int i = 0; i < part.Positions.Length; i += 3)
            {
                positions.Add(new(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
                normals.Add(new(part.Normals[i], part.Normals[i + 1], part.Normals[i + 2]));
            }
            positions.Freeze(); normals.Freeze();
            var submeshes = part.Submeshes.Count > 0 ? part.Submeshes : [part.Indices];
            for (int slot = 0; slot < submeshes.Count; slot++)
            {
                cancellation.ThrowIfCancellationRequested();
                var material = part.Materials.Count > 0 ? part.Materials[Math.Min(slot, part.Materials.Count - 1)] : new NativeMaterial { Color = part.Color, Colors = [part.Color], Tint = true };
                if (material.Name.Contains("liquid", StringComparison.OrdinalIgnoreCase)) continue;
                var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = new Int32Collection(submeshes[slot]) };
                var t = material.UVTransform;
                for (int i = 0; i < part.UV.Length; i += 2) mesh.TextureCoordinates.Add(new(part.UV[i] * t[0] + t[2], part.UV[i+1] * t[1] + 1 - t[1] - t[3]));
                mesh.Freeze();
                Brush brush;
                var texture = NativeTextureComposer.Compose(material, cancellation);
                if (texture != null)
                {
                    var bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null, texture.Pixels, texture.Width * 4); bitmap.Freeze();
                    brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0,0,1,1), Stretch = Stretch.Fill };
                }
                else
                {
                    var color = material.Colors.FirstOrDefault() ?? material.Color;
                    brush = new SolidColorBrush(Color.FromRgb((byte)(Math.Clamp(color.R,0,1)*255), (byte)(Math.Clamp(color.G,0,1)*255), (byte)(Math.Clamp(color.B,0,1)*255)));
                }
                brush.Freeze();
                var materials = new MaterialGroup(); materials.Children.Add(new DiffuseMaterial(brush));
                if (!material.Transparent && material.VisibilityMasks.Count == 0) materials.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(24, 24, 24)), 28));
                materials.Freeze();
                var geometry = new GeometryModel3D(mesh, materials) { BackMaterial = materials }; geometry.Freeze();
                // WPF writes depth for transparent texels. Draw cutouts after the opaque surface
                // beneath them, otherwise eyelash quads punch rectangular holes in the face.
                if (material.Transparent || material.AlphaMask != null || material.VisibilityMasks.Count > 0) cutouts.Add(geometry);
                else model.Children.Add(geometry);
            }
        }
        foreach (var geometry in cutouts) model.Children.Add(geometry);
        model.Freeze(); return model;
    }
    public static Model3DGroup Lights(double strength)
    {
        byte intensity = (byte)Math.Clamp(strength * 160, 0, 255);
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 90)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(intensity, intensity, intensity), new(-.5, -.8, 1)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(65, 70, 80), new(.8, -.2, -.5)));
        group.Freeze(); return group;
    }
    public static void SaveScreenshot(string path, Model3DGroup model, PerspectiveCamera camera, Model3DGroup lights, Brush background, double sourceAspect)
    {
        var screenshotCamera = camera.Clone();
        // WPF FieldOfView is horizontal. Preserve vertical framing when exporting a wider viewport.
        screenshotCamera.FieldOfView = Math.Atan(Math.Tan(camera.FieldOfView * Math.PI / 360) * (1280d / 720) / sourceAspect) * 360 / Math.PI;
        var viewport = new Viewport3D { Camera = screenshotCamera };
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        viewport.Children.Add(new ModelVisual3D { Content = model });
        var grid = new Grid { Width = 1280, Height = 720, Background = background }; grid.Children.Add(viewport);
        grid.Measure(new Size(1280, 720)); grid.Arrange(new Rect(0, 0, 1280, 720)); grid.UpdateLayout();
        var frame = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); frame.Render(grid);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
