using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HS2Manager.Core;

// Windows Imaging Component codecs, usable without WPF or a UI dispatcher.
public static class ImageCodec
{
    public static async Task<NativeTexture> DecodeTextureAsync(byte[] encoded, CancellationToken cancellation, bool preserveResolution = false)
    {
        using var stream = new MemoryStream(encoded, false).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation).ConfigureAwait(false);
        if (decoder.PixelWidth is < 1 or > 8192 || decoder.PixelHeight is < 1 or > 8192 || (long)decoder.PixelWidth * decoder.PixelHeight > 64 * 1024 * 1024)
            throw new InvalidDataException("内嵌贴图尺寸超限。");
        double scale = preserveResolution ? 1 : Math.Min(1, 1024d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new NativeTexture { Width = (int)width, Height = (int)height, Pixels = data.DetachPixelData() };
    }
    public static async Task SavePngAsync(string path, int width, int height, byte[] pixels, BitmapAlphaMode alpha = BitmapAlphaMode.Straight)
    {
        if (width < 1 || height < 1 || pixels.Length != checked(width * height * 4)) throw new ArgumentException("Invalid BGRA frame dimensions.");
        using var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory).AsTask().ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, alpha, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask().ConfigureAwait(false);
        memory.Seek(0);
        using var file = File.Create(path);
        await memory.AsStreamForRead().CopyToAsync(file).ConfigureAwait(false);
    }
}
