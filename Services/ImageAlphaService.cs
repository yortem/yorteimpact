using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YorteImpact.Services;

/// <summary>
/// Cheap alpha check for raster images. The AI brief uses it to tell the model
/// whether an image already has a transparent background, so it does not put a
/// white card behind a cutout that is already clean.
/// </summary>
public static class ImageAlphaService
{
    private const int SampleSize = 128;
    private const byte TransparentThreshold = 16;
    private const double MinimumTransparentRatio = 0.01;

    public static bool HasTransparency(BitmapSource? source)
    {
        if (source == null || source.PixelWidth < 1 || source.PixelHeight < 1)
            return false;

        try
        {
            BitmapSource sample = source;
            int longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest > SampleSize)
            {
                double scale = (double)SampleSize / longest;
                sample = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            if (sample.Format != PixelFormats.Bgra32)
                sample = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);

            int width = sample.PixelWidth;
            int height = sample.PixelHeight;
            if (width < 1 || height < 1) return false;

            int stride = width * 4;
            var pixels = new byte[stride * height];
            sample.CopyPixels(pixels, stride, 0);

            int transparent = 0;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] < TransparentThreshold)
                    transparent++;
            }

            return transparent >= Math.Max(1, (int)(width * height * MinimumTransparentRatio));
        }
        catch
        {
            return false;
        }
    }
}
