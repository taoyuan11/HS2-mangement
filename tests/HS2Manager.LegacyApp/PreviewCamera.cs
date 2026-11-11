using System.Numerics;

namespace HS2Manager.App;

// Camera state is independent of the XAML framework; Unity receives these coordinates.
public sealed record PreviewCamera(Vector3 Position, Vector3 LookDirection, Vector3 UpDirection, double FieldOfView);
public readonly record struct PreviewBounds(double X, double Y, double Z, double SizeX, double SizeY, double SizeZ)
{
    public static PreviewBounds Empty => default;
    public bool IsEmpty => SizeX <= 0 || SizeY <= 0 || SizeZ <= 0;
}
