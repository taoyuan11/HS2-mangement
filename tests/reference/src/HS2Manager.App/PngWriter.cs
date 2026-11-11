using Windows.Graphics.Imaging;
using HS2Manager.Core;

namespace HS2Manager.App;
public static class PngWriter
{
    public static Task SaveAsync(string path, int width, int height, byte[] pixels, BitmapAlphaMode alpha = BitmapAlphaMode.Ignore)
        => ImageCodec.SavePngAsync(path, width, height, pixels, alpha);
}
