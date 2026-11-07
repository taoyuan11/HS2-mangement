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
        foreach (var part in scene.Meshes)
        {
            cancellation.ThrowIfCancellationRequested();
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < part.Positions.Length; i += 3)
            {
                mesh.Positions.Add(new(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
                mesh.Normals.Add(new(part.Normals[i], part.Normals[i + 1], part.Normals[i + 2]));
            }
            foreach (int index in part.Indices) mesh.TriangleIndices.Add(index);
            mesh.Freeze();
            var brush = new SolidColorBrush(Color.FromScRgb(1, part.Color.R, part.Color.G, part.Color.B)); brush.Freeze();
            var materials = new MaterialGroup();
            materials.Children.Add(new DiffuseMaterial(brush));
            materials.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(36, 36, 36)), 28));
            materials.Freeze();
            var geometry = new GeometryModel3D(mesh, materials) { BackMaterial = materials }; geometry.Freeze(); model.Children.Add(geometry);
        }
        model.Freeze(); return model;
    }
    public static Model3DGroup Lights(double strength)
    {
        byte intensity = (byte)Math.Clamp(strength * 160, 0, 255);
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(65, 65, 65)));
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
