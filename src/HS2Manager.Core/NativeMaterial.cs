using System.Numerics;

namespace HS2Manager.Core;

public sealed class NativeTexture
{
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Pixels { get; set; } = []; // BGRA, top row first
    public bool IsValid => Width is > 0 and <= 2048 && Height is > 0 and <= 2048 && Pixels.Length == (long)Width * Height * 4;
    public int Offset(float u, float v) => (Math.Clamp((int)(v * Height), 0, Height - 1) * Width + Math.Clamp((int)(u * Width), 0, Width - 1)) * 4;
}

public sealed class NativeMaterial
{
    public string Name { get; set; } = "";
    public PreviewColor Color { get; set; } = new(1, 1, 1);
    public NativeTexture? MainTexture { get; set; }
    public NativeTexture? ColorMask { get; set; }
    public NativeTexture? AlphaMask { get; set; }
    public float[] UVTransform { get; set; } = [1, 1, 0, 0];
    public bool Transparent { get; set; }
    public bool Tint { get; set; }
    public bool Hair { get; set; }
    public bool MaskOnly { get; set; }
    public PreviewColor[] Colors { get; set; } = [];
    public List<NativeTexture> VisibilityMasks { get; set; } = [];
    public List<NativeTextureLayer> Layers { get; set; } = [];
}

public sealed record NativeTextureLayer(NativeTexture Texture, PreviewColor Color, bool Eyebrow = false, float[]? Layout = null, float Tilt = .5f, float[]? UVTransform = null);

public sealed class NativeBone
{
    public string Parent { get; set; } = "";
    public float[] Position { get; set; } = [0, 0, 0];
    public float[] Rotation { get; set; } = [0, 0, 0, 1];
    public float[] Scale { get; set; } = [1, 1, 1];
}

internal static class NativeMath
{
    public static float[] Array(Matrix4x4 m) => [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    public static Matrix4x4 Matrix(float[] m) => m.Length == 16 ? new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]) : throw new InvalidDataException("无效的变换矩阵。");
    public static Vector3 Vector(float[] a) => new(a[0], a[1], a[2]);
    public static Quaternion Euler(Vector3 degrees) => Quaternion.CreateFromYawPitchRoll(degrees.Y * MathF.PI / 180, degrees.X * MathF.PI / 180, degrees.Z * MathF.PI / 180);
    public static Matrix4x4 Local(NativeBone b) => Matrix4x4.CreateScale(Vector(b.Scale)) * Matrix4x4.CreateFromQuaternion(new(b.Rotation[0], b.Rotation[1], b.Rotation[2], b.Rotation[3])) * Matrix4x4.CreateTranslation(Vector(b.Position));
}
