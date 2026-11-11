using System.IO;

namespace HS2Manager.Core;

public sealed record NativeEmbeddedTexture(string Path, long Offset, int Length)
{
    internal NativeTexture Decode(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var file = File.OpenRead(Path);
        if (Length is < 1 or > 64 * 1024 * 1024 || Offset < 0 || Offset > file.Length || Length > file.Length - Offset)
            throw new InvalidDataException("内嵌贴图数据长度超限或截断。");
        file.Position = Offset;
        byte[] encoded = new byte[Length]; file.ReadExactly(encoded);
        try
        {
            return ImageCodec.DecodeTextureAsync(encoded, cancellation).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        { throw new InvalidDataException("内嵌贴图无法解码。", ex); }
    }
}

public sealed record NativeSkinOverlay(int Type, NativeEmbeddedTexture Texture);
public sealed record NativeClothesOverlay(string Name, NativeEmbeddedTexture? Texture, bool Override);
public sealed record NativeMaterialTextureOverride(int ObjectType, int Slot, string Name, string Property, NativeEmbeddedTexture? Texture, float[]? Offset, float[]? Scale);
public sealed record NativeMaterialQueueOverride(int ObjectType, int Slot, string Name, int Queue);
