using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace YorteImpact.Services;

/// <summary>One photo returned by the Pixabay search API.</summary>
public sealed class PhotoItem
{
    public long Id { get; init; }
    public string Tags { get; init; } = "";
    public string User { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string PreviewUrl { get; init; } = "";
    public string LargeUrl { get; init; } = "";
    public string WebUrl { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Short caption shown under the thumbnail.</summary>
    public string DisplayName
    {
        get
        {
            var first = Tags
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrEmpty(first) ? $"Photo {Id}" : first;
        }
    }
}

/// <summary>Search results plus the remaining API quota for the key.</summary>
public sealed class PhotoSearchResult
{
    public List<PhotoItem> Items { get; init; } = new();
    public int RateLimitLimit { get; init; }
    public int RateLimitRemaining { get; init; }
    public int RateLimitResetSeconds { get; init; }
    /// <summary>Page that was loaded (1 = first).</summary>
    public int Page { get; init; }
    /// <summary>Total photos matching the query.</summary>
    public int TotalHits { get; init; }
    /// <summary>True when the library has more photos for this query.</summary>
    public bool HasMore => TotalHits > 0
        ? Page * PerPage < TotalHits
        : Items.Count >= PerPage; // total unknown: assume a full page means more

    /// <summary>Photos requested per page (the API allows up to 200).</summary>
    public const int PerPage = 48;
}

/// <summary>Raised when the key ran out of requests for the current minute.</summary>
public sealed class PixabayRateLimitException : Exception
{
    public int RetryAfterSeconds { get; }

    public PixabayRateLimitException(string message, int retryAfterSeconds) : base(message)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}

/// <summary>
/// Photo library search backed by the Pixabay API. The user supplies their own
/// free API key (Settings → Integrations); every request is sent with it.
/// Results are cached in memory because the API asks clients not to repeat
/// the same query.
/// </summary>
public static class PixabayService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly ConcurrentDictionary<string, PhotoSearchResult> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The library asks clients to cache results for a day, so the search cache
    /// survives restarts (the download links are valid for 24h as well).
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YorteImpact", "photo-cache.json");
    private static readonly object DiskCacheGate = new();
    private static Dictionary<string, DiskEntry> _diskCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _diskCacheLoaded;

    private sealed class DiskEntry
    {
        public string Key { get; set; } = "";
        public long Timestamp { get; set; }
        public int TotalHits { get; set; }
        public List<PhotoItem> Items { get; set; } = new();
    }

    private sealed class DiskCacheFile
    {
        public List<DiskEntry> Entries { get; set; } = new();
    }

    private static void EnsureDiskCache()
    {
        if (_diskCacheLoaded) return;
        _diskCacheLoaded = true;
        lock (DiskCacheGate)
        {
            try
            {
                if (!File.Exists(CacheFilePath)) return;
                var doc = JsonSerializer.Deserialize<DiskCacheFile>(File.ReadAllText(CacheFilePath));
                if (doc?.Entries == null) return;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var entry in doc.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Key)) continue;
                    if (now - entry.Timestamp > (long)CacheLifetime.TotalSeconds) continue;
                    _diskCache[entry.Key] = entry;
                }
            }
            catch
            {
                // A broken cache just means we ask the library again.
            }
        }
    }

    private static void SaveDiskCache()
    {
        try
        {
            List<DiskEntry> entries;
            lock (DiskCacheGate)
            {
                entries = _diskCache.Values
                    .OrderByDescending(e => e.Timestamp)
                    .Take(24)
                    .ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
            // Write beside the target and swap, so a crash cannot truncate it.
            string temp = CacheFilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new DiskCacheFile { Entries = entries }));
            File.Move(temp, CacheFilePath, overwrite: true);
        }
        catch
        {
            // Caching is best-effort.
        }
    }

    /// <summary>Short fingerprint of the API key so caches never mix keys.</summary>
    private static string KeyFingerprint(string apiKey)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(apiKey.Trim()));
        return Convert.ToHexString(bytes, 0, 4);
    }

    /// <param name="orientation">"all", "horizontal" or "vertical".</param>
    /// <param name="page">1-based page number; scrolling pulls the next one.</param>
    public static async Task<PhotoSearchResult> SearchAsync(
        string apiKey, string query, string orientation, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Add your Pixabay API key in Settings to search photos.");
        if (page < 1) page = 1;

        string cacheKey = $"{KeyFingerprint(apiKey)}|{query.Trim().ToLowerInvariant()}|{orientation}|{page}";
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        EnsureDiskCache();
        DiskEntry? diskEntry = null;
        lock (DiskCacheGate)
        {
            _diskCache.TryGetValue(cacheKey, out diskEntry);
        }
        if (diskEntry != null)
        {
            var fromDisk = new PhotoSearchResult
            {
                Items = diskEntry.Items,
                Page = page,
                TotalHits = diskEntry.TotalHits,
            };
            Cache[cacheKey] = fromDisk;
            return fromDisk;
        }

        var url = new StringBuilder("https://pixabay.com/api/?key=")
            .Append(Uri.EscapeDataString(apiKey.Trim()));
        if (!string.IsNullOrWhiteSpace(query))
            url.Append("&q=").Append(Uri.EscapeDataString(query.Trim()));
        url.Append("&image_type=photo&safesearch=true&order=popular&per_page=")
           .Append(PhotoSearchResult.PerPage)
           .Append("&page=").Append(page);
        if (!string.IsNullOrWhiteSpace(orientation) && !string.Equals(orientation, "all", StringComparison.OrdinalIgnoreCase))
            url.Append("&orientation=").Append(Uri.EscapeDataString(orientation));

        using var resp = await Http.GetAsync(url.ToString(), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 429)
            {
                int wait = ReadResetSeconds(resp);
                if (wait <= 0) wait = 60; // the window is one minute long
                int limit = ReadHeader(resp, "X-RateLimit-Limit");
                string message = limit > 0
                    ? $"You used all {limit} photo library requests for this minute. Try again in {wait} seconds."
                    : $"The photo library allows a limited number of requests per minute. Try again in {wait} seconds.";
                throw new PixabayRateLimitException(message, wait);
            }
            throw new InvalidOperationException(DescribeError(body, resp.StatusCode));
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var results = new List<PhotoItem>();
        if (doc.RootElement.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray())
            {
                results.Add(new PhotoItem
                {
                    Id = GetLong(hit, "id"),
                    Tags = GetString(hit, "tags"),
                    User = GetString(hit, "user"),
                    PageUrl = GetString(hit, "pageURL"),
                    PreviewUrl = GetString(hit, "previewURL"),
                    LargeUrl = GetString(hit, "largeImageURL"),
                    WebUrl = GetString(hit, "webformatURL"),
                    Width = GetInt(hit, "imageWidth"),
                    Height = GetInt(hit, "imageHeight"),
                });
            }
        }

        int totalHits = 0;
        if (doc.RootElement.TryGetProperty("totalHits", out var hitsProp) && hitsProp.TryGetInt32(out int hv))
            totalHits = hv;

        var result = new PhotoSearchResult
        {
            Items = results,
            RateLimitLimit = ReadHeader(resp, "X-RateLimit-Limit"),
            RateLimitRemaining = ReadHeader(resp, "X-RateLimit-Remaining"),
            RateLimitResetSeconds = ReadResetSeconds(resp),
            Page = page,
            TotalHits = totalHits,
        };
        Cache[cacheKey] = result;

        lock (DiskCacheGate)
        {
            _diskCache[cacheKey] = new DiskEntry
            {
                Key = cacheKey,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                TotalHits = totalHits,
                Items = results,
            };
            // Keep both copies bounded: the oldest entries are dropped.
            if (_diskCache.Count > 48)
            {
                foreach (var stale in _diskCache.Values
                             .OrderBy(e => e.Timestamp)
                             .Take(_diskCache.Count - 48).ToList())
                {
                    _diskCache.Remove(stale.Key);
                    Cache.TryRemove(stale.Key, out _);
                }
            }
        }
        SaveDiskCache();
        return result;
    }

    /// <summary>Download a photo (1280px version) as a frozen bitmap.</summary>
    public static async Task<BitmapSource?> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Photo download failed ({resp.StatusCode}).");

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Download the best available size: 1280px first, then 640px, then the
    /// thumbnail. The generated links can expire, so a failure falls through
    /// to the next size.
    /// </summary>
    public static async Task<BitmapSource?> DownloadBestAsync(PhotoItem item, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in new[] { item.LargeUrl, item.WebUrl, item.PreviewUrl })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            try
            {
                var bmp = await DownloadAsync(url, ct).ConfigureAwait(false);
                if (bmp != null) return bmp;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (last != null) throw last;
        return null;
    }

    /// <summary>Pixabay reports failures as a plain-text body.</summary>
    private static string DescribeError(string body, System.Net.HttpStatusCode status)
    {
        string text = (body ?? "").Trim();
        if (text.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase))
        {
            int end = text.IndexOf(']');
            if (end > 0) text = text.Substring(end + 1).Trim();
        }
        if (text.Length == 0) text = $"Request failed ({status}).";
        return text;
    }

    private static int ReadHeader(HttpResponseMessage resp, string name)
    {
        if (!resp.Headers.TryGetValues(name, out var values)) return 0;
        foreach (var value in values)
            if (int.TryParse(value, out int n)) return n;
        return 0;
    }

    /// <summary>Seconds until the request window resets (X-RateLimit-Reset).</summary>
    private static int ReadResetSeconds(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("X-RateLimit-Reset", out var values)) return 0;
        foreach (var value in values)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) continue;
            if (n > 1_000_000_000) // sent as a unix timestamp
            {
                double diff = n - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return (int)Math.Clamp(Math.Ceiling(diff), 1, 600);
            }
            return (int)Math.Clamp(Math.Ceiling(n), 1, 600);
        }
        return 0;
    }

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n
            : 0;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)
            ? n
            : 0;
}
