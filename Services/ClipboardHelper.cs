using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YorteImpact.Services;

/// <summary>Clipboard image intake: browser/app copies, screenshots and file copies.</summary>
public static class ClipboardHelper
{
    /// <summary>
    /// Try to get a bitmap from the clipboard. PNG data is preferred (best
    /// alpha fidelity); a raw DIB is decoded manually because some apps put a
    /// zeroed alpha channel in it, which would otherwise paste as an invisible
    /// image. File-drop lists and WPF's bitmap conversion are fallbacks.
    /// Must be called on an STA thread (the UI thread).
    /// </summary>
    public static BitmapSource? TryGetImage()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;

            var png = TryPng(data);
            if (png != null) return png;

            var file = TryFileDrop(data);
            if (file != null) return file;

            var dib = TryDib(data);
            if (dib != null) return dib;

            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                var img = Clipboard.GetImage();
                if (img != null) return RepairZeroAlpha(img);
            }
        }
        catch
        {
            // Clipboard busy / unavailable.
        }
        return null;
    }

    // ---------------- PNG ----------------

    private static BitmapSource? TryPng(IDataObject data)
    {
        foreach (string format in new[] { "PNG", "image/png", "PNG image" })
        {
            try
            {
                if (!data.GetDataPresent(format)) continue;
                BitmapSource? bmp = data.GetData(format) switch
                {
                    MemoryStream ms => DecodePng(ms),
                    Stream stream => DecodePng(stream),
                    byte[] bytes => DecodePng(new MemoryStream(bytes)),
                    _ => null,
                };
                if (bmp != null) return bmp;
            }
            catch { /* try the next format */ }
        }
        return null;
    }

    private static BitmapSource? DecodePng(Stream source)
    {
        if (source.CanSeek) source.Position = 0;
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        if (ms.Length == 0) return null;
        ms.Position = 0;

        var decoder = new PngBitmapDecoder(ms,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        if (frame.CanFreeze) frame.Freeze();
        return frame;
    }

    // ---------------- file drop ----------------

    private static BitmapSource? TryFileDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return null;

        foreach (string? f in files)
        {
            if (f == null) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff"))
                continue;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(f, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                return bmp;
            }
            catch { /* try next file */ }
        }
        return null;
    }

    // ---------------- raw DIB ----------------

    private static BitmapSource? TryDib(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.Dib)) return null;

        byte[]? bytes = data.GetData(DataFormats.Dib) switch
        {
            MemoryStream ms => ms.ToArray(),
            Stream stream => ReadAll(stream),
            byte[] b => b,
            _ => null,
        };
        return bytes == null ? null : DecodeDib(bytes);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Decode a CF_DIB / CF_DIBV5 payload into a frozen bitmap. Handles
    /// uncompressed 24/32-bit images; zeroed alpha bytes are treated as opaque.
    /// </summary>
    private static BitmapSource? DecodeDib(byte[] data)
    {
        if (data.Length < 40) return null;

        int headerSize = BitConverter.ToInt32(data, 0);
        if (headerSize < 40 || headerSize > data.Length) return null;

        int width = BitConverter.ToInt32(data, 4);
        int rawHeight = BitConverter.ToInt32(data, 8);
        short planes = BitConverter.ToInt16(data, 12);
        short bpp = BitConverter.ToInt16(data, 14);
        int compression = BitConverter.ToInt32(data, 16);
        int clrUsed = BitConverter.ToInt32(data, 32);

        int height = Math.Abs(rawHeight);
        bool topDown = rawHeight < 0;
        if (width <= 0 || height <= 0 || planes != 1) return null;
        if (bpp is not (24 or 32)) return null;
        if (compression is not (0 or 3 or 6)) return null;

        int paletteSize = bpp <= 8 ? (clrUsed == 0 ? (1 << bpp) : clrUsed) * 4 : 0;
        int maskSize = compression == 3 && headerSize == 40 ? 12
                     : compression == 6 && headerSize == 40 ? 16 : 0;
        int offset = headerSize + paletteSize + maskSize;
        int srcStride = ((width * bpp + 31) / 32) * 4;
        if (offset < 0 || (long)offset + (long)srcStride * height > data.Length) return null;

        int dstStride = width * 4;
        var pixels = new byte[(long)dstStride * height];
        bool anyAlpha = false;

        for (int y = 0; y < height; y++)
        {
            int srcRow = offset + (topDown ? y : height - 1 - y) * srcStride;
            int dstRow = y * dstStride;

            for (int x = 0; x < width; x++)
            {
                int si = srcRow + x * (bpp / 8);
                int di = dstRow + x * 4;
                byte b = data[si];
                byte g = data[si + 1];
                byte r = data[si + 2];
                byte a = bpp == 32 ? data[si + 3] : (byte)255;
                if (a != 0) anyAlpha = true;
                pixels[di] = b;
                pixels[di + 1] = g;
                pixels[di + 2] = r;
                pixels[di + 3] = a;
            }
        }

        // Many clipboard DIBs carry a 32-bit pixel layout with the alpha byte
        // zeroed although the image is meant to be opaque.
        if (bpp == 32 && !anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var bmp = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, dstStride);
        bmp.Freeze();
        return bmp;
    }

    // ---------------- WPF bitmap fallback ----------------

    /// <summary>
    /// Some clipboard sources produce a 32-bit bitmap whose alpha channel is
    /// entirely zero; make such an image opaque so its RGB content is visible.
    /// </summary>
    private static BitmapSource RepairZeroAlpha(BitmapSource bmp)
    {
        if (bmp.Format != PixelFormats.Bgra32 && bmp.Format != PixelFormats.Pbgra32)
            return bmp;

        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        if (w <= 0 || h <= 0) return bmp;

        int stride = w * 4;
        var pixels = new byte[stride * h];
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, stride, 0);

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { anyAlpha = true; break; }
        }
        if (anyAlpha) return bmp;

        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var repaired = BitmapSource.Create(w, h, bmp.DpiX, bmp.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        repaired.Freeze();
        return repaired;
    }
}
