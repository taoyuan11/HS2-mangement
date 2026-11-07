using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        using var stream = new MemoryStream(encoded, false);
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var source = decoder.Frames[0];
            if (source.PixelWidth is < 1 or > 8192 || source.PixelHeight is < 1 or > 8192 || (long)source.PixelWidth * source.PixelHeight > 64 * 1024 * 1024)
                throw new InvalidDataException("内嵌贴图尺寸超限。");
            cancellation.ThrowIfCancellationRequested();
            double scale = Math.Min(1, 1024d / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource resized = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            var converted = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
            var result = new NativeTexture { Width = converted.PixelWidth, Height = converted.PixelHeight, Pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4] };
            converted.CopyPixels(result.Pixels, result.Width * 4, 0);
            cancellation.ThrowIfCancellationRequested(); return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        { throw new InvalidDataException("内嵌贴图无法解码。", ex); }
    }
}

public sealed record NativeSkinOverlay(int Type, NativeEmbeddedTexture Texture);
public sealed record NativeClothesOverlay(string Name, NativeEmbeddedTexture? Texture, bool Override);
public sealed record NativeMaterialTextureOverride(int ObjectType, int Slot, string Name, string Property, NativeEmbeddedTexture? Texture, float[]? Offset, float[]? Scale);
public sealed record NativeMaterialQueueOverride(int ObjectType, int Slot, string Name, int Queue);
