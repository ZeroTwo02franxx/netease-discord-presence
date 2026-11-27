using System.Text;
using System.Text.Json;

namespace CloudMusicPresence;

public record AlbumMetadata(string Name, string CoverUrl)
{
    public static AlbumMetadata? Parse(string json, string songId, string expectedTitle)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("list", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || id.ToString() != songId || !item.TryGetProperty("track", out var track)) continue;
            if (!track.TryGetProperty("name", out var name) || !string.Equals(name.GetString()?.Normalize(NormalizationForm.FormKC).Trim(), expectedTitle.Normalize(NormalizationForm.FormKC).Trim(), StringComparison.Ordinal)) return null;
            if (!track.TryGetProperty("album", out var album) && !track.TryGetProperty("al", out album)) return null;
            string albumName = album.TryGetProperty("name", out var a) ? a.GetString() ?? "" : "";
            string cover = album.TryGetProperty("cover", out var c) ? c.GetString() ?? "" : album.TryGetProperty("picUrl", out c) ? c.GetString() ?? "" : "";
            string? url = NormalizeCoverUrl(cover);
            return url is null ? null : new(albumName, url);
        }
        return null;
    }

    public static string? NormalizeCoverUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (!uri.Host.EndsWith(".music.126.net", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("music.126.net", StringComparison.OrdinalIgnoreCase)) return null;
        var builder = new UriBuilder(uri) { Scheme = "https", Port = -1, Fragment = "" };
        // NetEase image sizing keeps Discord's fetched asset small and square.
        builder.Query = string.IsNullOrEmpty(uri.Query) ? "param=512y512" : uri.Query.TrimStart('?');
        return builder.Uri.AbsoluteUri;
    }
}

public sealed class AlbumMetadataReader
{
    private string key = "";
    private AlbumMetadata? cached;
    private DateTime retry;
    public AlbumMetadata? Read(string songId, string title)
    {
        string next = songId + "\n" + title;
        if (next == key && (cached is not null || DateTime.UtcNow < retry)) return cached;
        key = next; cached = null; retry = DateTime.UtcNow.AddSeconds(5);
        if (songId.Length == 0) return null;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetEase", "CloudMusic", "WebData", "file", "playingList");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 32 * 1024 * 1024) return null;
            using var reader = new StreamReader(file);
            cached = AlbumMetadata.Parse(reader.ReadToEnd(), songId, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return cached;
    }
}
