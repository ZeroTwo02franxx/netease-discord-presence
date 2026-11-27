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
            if (!track.TryGetProperty("name", out var name) || !EqualsNormalized(name.GetString(), expectedTitle)) return null;
            return ExtractAlbum(track);
        }
        return null;
    }

    public static AlbumMetadata? ParseByTitleAndArtist(string json, string expectedTitle, string expectedArtist)
    {
        if (string.IsNullOrWhiteSpace(expectedTitle)) return null;
        using var document = JsonDocument.Parse(json);
        JsonElement items;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            items = document.RootElement;
        else if (document.RootElement.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            items = list;
        else if (document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("historyList", out var hist) && hist.ValueKind == JsonValueKind.Array)
            items = hist;
        else
            return null;

        AlbumMetadata? titleOnlyFallback = null;
        string normArtist = NormalizeText(expectedArtist);
        foreach (var item in items.EnumerateArray())
        {
            var track = item.TryGetProperty("track", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : item;
            if (!track.TryGetProperty("name", out var name) || !EqualsNormalized(name.GetString(), expectedTitle)) continue;
            var meta = ExtractAlbum(track);
            if (meta is null) continue;
            if (normArtist.Length == 0 || ArtistsMatch(track, normArtist)) return meta;
            titleOnlyFallback ??= meta;
        }
        return titleOnlyFallback;
    }

    private static AlbumMetadata? ExtractAlbum(JsonElement track)
    {
        if (!track.TryGetProperty("album", out var album) && !track.TryGetProperty("al", out album)) return null;
        string albumName = album.TryGetProperty("name", out var a) ? a.GetString() ?? "" : "";
        string cover = album.TryGetProperty("cover", out var c) ? c.GetString() ?? "" : album.TryGetProperty("picUrl", out c) ? c.GetString() ?? "" : "";
        string? url = NormalizeCoverUrl(cover);
        return url is null ? null : new(albumName, url);
    }

    private static bool ArtistsMatch(JsonElement track, string normalizedExpected)
    {
        if (!track.TryGetProperty("artists", out var artists) && !track.TryGetProperty("ar", out artists)) return false;
        if (artists.ValueKind != JsonValueKind.Array) return false;
        foreach (var artist in artists.EnumerateArray())
        {
            string artistName = artist.TryGetProperty("name", out var n) ? NormalizeText(n.GetString()) : "";
            if (artistName.Length > 0 && normalizedExpected.Contains(artistName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string NormalizeText(string? value) => (value ?? "").Normalize(NormalizationForm.FormKC).Trim();

    private static bool EqualsNormalized(string? actual, string expected) =>
        string.Equals(NormalizeText(actual), NormalizeText(expected), StringComparison.Ordinal);

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
        cached = ReadFile(path, json => AlbumMetadata.Parse(json, songId, title));
        return cached;
    }

    public AlbumMetadata? ReadByTitle(string title, string artist)
    {
        string next = "title\n" + title + "\n" + artist;
        if (next == key && (cached is not null || DateTime.UtcNow < retry)) return cached;
        key = next; cached = null; retry = DateTime.UtcNow.AddSeconds(5);
        if (string.IsNullOrWhiteSpace(title)) return null;
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetEase", "CloudMusic", "WebData", "file");
        cached = ReadFile(Path.Combine(folder, "history"), json => AlbumMetadata.ParseByTitleAndArtist(json, title, artist))
              ?? ReadFile(Path.Combine(folder, "playingList"), json => AlbumMetadata.ParseByTitleAndArtist(json, title, artist));
        return cached;
    }

    private static AlbumMetadata? ReadFile(string path, Func<string, AlbumMetadata?> parser)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 32 * 1024 * 1024) return null;
            using var reader = new StreamReader(file);
            return parser(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }
}
