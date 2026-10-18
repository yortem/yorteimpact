using System.Windows;
using System.Windows.Media.Imaging;

namespace YorteImpact.Services;

/// <summary>Clipboard image intake: browser copies, screenshots, file copies.</summary>
public static class ClipboardHelper
{
    /// <summary>
    /// Try to get a bitmap from the clipboard.
    /// Covers: raw bitmap (PrintScreen / browser "Copy image") and
    /// file-drop lists (browser "Copy image" sometimes, Explorer Ctrl+C).
    /// Returns null when the clipboard holds no image.
    /// Must be called on an STA thread (the UI thread).
    /// </summary>
    public static BitmapSource? TryGetImage()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null) return img;
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                foreach (string? f in files)
                {
                    if (f == null) continue;
                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff")
                    {
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
                }
            }
        }
        catch { /* clipboard busy / unavailable */ }
        return null;
    }
}
