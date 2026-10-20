using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace YorteImpact.Services;

public sealed class IconItem
{
    public string Id { get; }
    public string Prefix { get; }
    public string Name { get; }
    public string DisplayName { get; }
    public string? PathData { get; set; }
    public Geometry? Geometry { get; set; }

    public IconItem(string id, string? pathData = null)
    {
        Id = id;
        int colon = id.IndexOf(':');
        if (colon > 0)
        {
            Prefix = id.Substring(0, colon);
            Name = id.Substring(colon + 1);
        }
        else
        {
            Prefix = "mdi";
            Name = id;
        }
        DisplayName = Name.Replace('-', ' ');
        PathData = pathData;
        if (!string.IsNullOrWhiteSpace(pathData))
        {
            try
            {
                var g = Geometry.Parse(pathData);
                if (g.CanFreeze) g.Freeze();
                Geometry = g;
            }
            catch
            {
                // ignored
            }
        }
    }
}

public static class IconifyService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly ConcurrentDictionary<string, List<IconItem>> SearchCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> PathCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string[] PopularIconIds =
    {
        "mdi:home", "mdi:star", "mdi:heart", "mdi:cog", "mdi:account",
        "mdi:magnify", "mdi:bell", "mdi:check", "mdi:close", "mdi:plus",
        "mdi:minus", "mdi:arrow-right", "mdi:arrow-left", "mdi:arrow-up", "mdi:arrow-down",
        "mdi:calendar", "mdi:clock", "mdi:email", "mdi:camera", "mdi:image",
        "mdi:folder", "mdi:file", "mdi:delete", "mdi:pencil", "mdi:share-variant",
        "mdi:download", "mdi:upload", "mdi:lock", "mdi:lock-open", "mdi:eye",
        "mdi:bookmark", "mdi:cart", "mdi:tag", "mdi:map-marker", "mdi:phone",
        "mdi:weather-sunny", "mdi:weather-night", "mdi:cloud", "mdi:flash", "mdi:shield-check",
        "mdi:emoticon-happy-outline", "mdi:thumb-up", "mdi:palette", "mdi:format-paint",
        "mdi:shape", "mdi:layers", "mdi:vector-square", "mdi:information"
    };

    public static async Task<List<IconItem>> GetPopularIconsAsync(CancellationToken ct = default)
    {
        const string key = "__popular__";
        if (SearchCache.TryGetValue(key, out var cached))
            return cached;

        var items = await FetchIconsByIdsAsync(PopularIconIds, ct).ConfigureAwait(false);
        SearchCache[key] = items;
        return items;
    }

    public static async Task<List<IconItem>> SearchIconsAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return await GetPopularIconsAsync(ct).ConfigureAwait(false);

        if (SearchCache.TryGetValue(query, out var cached))
            return cached;

        string url = $"https://api.iconify.design/search?query={Uri.EscapeDataString(query)}&limit=48";
        using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var idList = new List<string>();
        if (doc.RootElement.TryGetProperty("icons", out var iconsProp) && iconsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in iconsProp.EnumerateArray())
            {
                string? id = item.GetString();
                if (!string.IsNullOrEmpty(id))
                    idList.Add(id);
            }
        }

        var results = await FetchIconsByIdsAsync(idList, ct).ConfigureAwait(false);
        SearchCache[query] = results;
        return results;
    }

    private static async Task<List<IconItem>> FetchIconsByIdsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var result = new List<IconItem>();
        var missingByPrefix = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fullId in ids)
        {
            var item = new IconItem(fullId);
            if (PathCache.TryGetValue(fullId, out var cachedPath))
            {
                item.PathData = cachedPath;
                try
                {
                    var g = Geometry.Parse(cachedPath);
                    if (g.CanFreeze) g.Freeze();
                    item.Geometry = g;
                }
                catch { }
                result.Add(item);
            }
            else
            {
                if (!missingByPrefix.TryGetValue(item.Prefix, out var list))
                {
                    list = new List<string>();
                    missingByPrefix[item.Prefix] = list;
                }
                list.Add(item.Name);
                result.Add(item);
            }
        }

        // Fetch missing in batches by prefix
        var tasks = missingByPrefix.Select(async kvp =>
        {
            string prefix = kvp.Key;
            var names = kvp.Value;
            // API allows up to ~64 icons per batch
            for (int i = 0; i < names.Count; i += 50)
            {
                var chunk = names.Skip(i).Take(50);
                string iconsParam = string.Join(",", chunk);
                string url = $"https://api.iconify.design/{prefix}.json?icons={iconsParam}";
                try
                {
                    using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) continue;

                    using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                    if (doc.RootElement.TryGetProperty("icons", out var iconsObj) && iconsObj.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in iconsObj.EnumerateObject())
                        {
                            string iconName = prop.Name;
                            string fullId = $"{prefix}:{iconName}";
                            if (prop.Value.TryGetProperty("body", out var bodyProp))
                            {
                                string body = bodyProp.GetString() ?? "";
                                string pathData = ExtractPathData(body);
                                if (!string.IsNullOrEmpty(pathData))
                                {
                                    PathCache[fullId] = pathData;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // If a batch fails, continue with others
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Assign fetched paths to items in result
        foreach (var item in result)
        {
            if (item.Geometry == null && PathCache.TryGetValue(item.Id, out var path))
            {
                item.PathData = path;
                try
                {
                    var g = Geometry.Parse(path);
                    if (g.CanFreeze) g.Freeze();
                    item.Geometry = g;
                }
                catch { }
            }
        }

        // Return only items that have valid geometry
        return result.Where(i => i.Geometry != null).ToList();
    }

    private static readonly Regex PathRegex = new(@"\bd=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ExtractPathData(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        var matches = PathRegex.Matches(body);
        if (matches.Count == 0) return string.Empty;

        var paths = new List<string>(matches.Count);
        foreach (Match m in matches)
        {
            string d = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(d))
                paths.Add(d);
        }

        return string.Join(" ", paths);
    }
}
