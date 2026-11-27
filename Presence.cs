using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Windows.Media.Control;

namespace CloudMusicPresence;

public record Track(string Title, string Artist, string Album, bool Playing, double Position, double Duration, string CoverUrl = "", bool PendingTiming = false)
{
    public string SyncKey() => $"{Title}\n{Artist}\n{Album}\n{CoverUrl}\n{Playing}\n{Duration > 0}";

    public bool HasSeeked(double lastPublishedPosition, TimeSpan elapsed) =>
        Playing && Duration > 0 && Math.Abs(Position - lastPublishedPosition - elapsed.TotalSeconds) > 2;

    public object? Activity()
    {
        if (string.IsNullOrWhiteSpace(Title)) return null;
        string baseState = string.IsNullOrWhiteSpace(Artist) ? "網易雲音樂" : Artist;
        var activity = new Dictionary<string, object>
        {
            ["type"] = 2,
            ["details"] = Clip(Title),
            ["state"] = Playing ? Clip(baseState) : Clip(TakeRunes(baseState, 114) + " · 已暫停")
        };
        string? cover = AlbumMetadata.NormalizeCoverUrl(CoverUrl);
        if (cover is not null)
            activity["assets"] = new { large_image = cover, large_text = Clip(string.IsNullOrWhiteSpace(Album) ? Title : Album) };
        // Optional objects must be absent, not JSON null. Omit timestamps while paused so Discord stops advancing.
        if (Playing && double.IsFinite(Duration) && double.IsFinite(Position) && Duration > 0 &&
            Duration < TimeSpan.FromDays(365).TotalSeconds && Position >= 0 && Position <= Duration)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long start = now - (long)Math.Round(Position);
            activity["timestamps"] = new { start, end = start + (long)Math.Round(Duration) };
        }
        return activity;
    }

    private static string TakeRunes(string text, int maxRunes) =>
        string.Concat(text.EnumerateRunes().Take(maxRunes).Select(r => r.ToString()));

    private static string Clip(string text, int maxRunes = 120)
    {
        // Keep within Discord's field limit without splitting a surrogate pair.
        var value = TakeRunes(text, maxRunes);
        return value.Length < 2 ? value + " " : value;
    }
}

public sealed class MediaReader : IDisposable
{
    private readonly NeteaseTimeline native = new();
    private readonly AlbumMetadataReader albums = new();
    public string TimingDiagnostic { get; private set; } = "尚未讀取時間";
    private GlobalSystemMediaTransportControlsSessionManager? manager;
    public string Sessions { get; private set; } = "尚未偵測";
    public async Task<Track?> ReadAsync()
    {
        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var sessions = manager.GetSessions();
        Sessions = sessions.Count == 0 ? "Windows 沒有提供任何媒體工作階段" :
            string.Join("\r\n", sessions.Select(s => s.SourceAppUserModelId));
        var session = sessions.Where(s => IsCloudMusic(s.SourceAppUserModelId))
            .OrderByDescending(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            .FirstOrDefault();
        if (session is null) return null;
        var properties = await session.TryGetMediaPropertiesAsync();
        var status = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        bool playing = status.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double position = (timeline.Position - timeline.StartTime).TotalSeconds;
        if (playing)
            position += Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds) * (status.PlaybackRate ?? 1);
        double duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
        bool smtcHasTiming = duration > 0 && timeline.LastUpdatedTime.Year >= 2000;
        var timing = await Task.Run(native.Read);
        TimingDiagnostic = "時間來源：Windows 媒體控制";
        bool pendingTiming = false;
        if (!smtcHasTiming)
        {
            TimingDiagnostic = native.Diagnostic;
            if (timing is not null) { position = timing.Position; duration = timing.Duration; playing = timing.Playing; }
            else if (native.IsTransitioning) pendingTiming = true;
        }
        var metadata = await Task.Run(() => albums.Read(timing?.SongId ?? "", properties.Title));
        return new(properties.Title, properties.Artist, string.IsNullOrWhiteSpace(properties.AlbumTitle) ? metadata?.Name ?? "" : properties.AlbumTitle, playing,
            Math.Clamp(position, 0, Math.Max(0, duration)), duration, metadata?.CoverUrl ?? "", pendingTiming);
    }

    public static bool IsCloudMusic(string source) =>
        new[] { "cloudmusic", "netease", "网易云", "網易雲" }.Any(s => source.Contains(s, StringComparison.OrdinalIgnoreCase));
    public void Dispose() => native.Dispose();
}

public sealed class DiscordRpc : IDisposable
{
    private readonly PresenceUpdateGate updateGate = new();
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private NamedPipeClientStream? pipe;
    public int PipeIndex { get; private set; } = -1;
    public string UserId { get; private set; } = "";
    public string UserName { get; private set; } = "未提供帳號名稱";
    public async Task ConnectAsync(string id, CancellationToken cancellation, string pipePrefix = "discord-ipc-", int? pipeIndex = null, string? expectedUserId = null)
    {
        if (pipeIndex is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(pipeIndex));
        Dispose();
        var failures = new List<string>();
        foreach (int i in pipeIndex.HasValue ? new[] { pipeIndex.Value } : Enumerable.Range(0, 10))
        {
            var candidate = new NamedPipeClientStream(".", $"{pipePrefix}{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await candidate.ConnectAsync(100, cancellation); }
            catch (TimeoutException) { candidate.Dispose(); continue; }
            catch (IOException) { candidate.Dispose(); continue; }
            catch { candidate.Dispose(); throw; }
            if (!candidate.IsConnected) { candidate.Dispose(); continue; }
            pipe = candidate;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                await SendAsync(0, new { v = 1, client_id = id }, timeout.Token);
                using var reply = await ReceiveAsync(timeout.Token);
                var root = reply.RootElement;
                if (!root.TryGetProperty("evt", out var evt) || evt.GetString() != "READY")
                    throw new IOException("Discord 拒絕連線：" + root.ToString());
                PipeIndex = i;
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("user", out var user))
                {
                    UserId = user.TryGetProperty("id", out var uid) ? uid.GetString() ?? "" : "";
                    UserName = user.TryGetProperty("username", out var name) ? name.GetString() ?? UserName : UserName;
                }
                if (!string.IsNullOrEmpty(expectedUserId) && UserId != expectedUserId)
                    throw new IOException("此連線的 Discord 帳號已改變，請停止同步後重新「掃描帳號」。");
                return;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Dispose(); throw; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                failures.Add($"IPC {i}：{(ex is OperationCanceledException ? "握手逾時" : ex.Message)}");
                Dispose();
            }
        }
        throw new IOException(failures.Count > 0 ? string.Join("；", failures) : "找不到指定的 Discord 電腦版，請先開啟並登入，再掃描帳號。");
    }

    public async Task SetAsync(object? activity, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (pipe is null || !pipe.IsConnected) throw new IOException("Discord 尚未連線");
        if (!updateGate.TryAcquire(clock.Elapsed, activity is null, out var retryAfter))
            throw new PresenceUpdateDeferredException(retryAfter);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var nonce = Guid.NewGuid().ToString();
        await SendAsync(1, new { cmd = "SET_ACTIVITY", args = new { pid = Environment.ProcessId, activity }, nonce }, timeout.Token);
        while (true)
        {
            using var reply = await ReceiveAsync(timeout.Token);
            var root = reply.RootElement;
            if (!root.TryGetProperty("nonce", out var n) || n.GetString() != nonce) continue;
            if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                throw new IOException("Discord 回報錯誤：" + root.GetProperty("data").ToString());
            return;
        }
    }

    public async Task ClearAsync(CancellationToken token)
    {
        while (true)
        {
            try { await SetAsync(null, token); return; }
            catch (PresenceUpdateDeferredException ex) { await Task.Delay(ex.RetryAfter, token); }
        }
    }

    private async Task SendAsync(int opcode, object payload, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteFrameAsync(pipe ?? throw new IOException("Discord 尚未連線"), opcode, bytes, token);
    }

    public static async Task WriteFrameAsync(Stream stream, int opcode, byte[] bytes, CancellationToken token)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private async Task<JsonDocument> ReceiveAsync(CancellationToken token)
    {
        var stream = pipe ?? throw new IOException("Discord 已斷線");
        while (true)
        {
            var header = new byte[8];
            await stream.ReadExactlyAsync(header, token);
            int opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
            int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (size < 0 || size > 1024 * 1024) throw new IOException("Discord 封包大小不合法");
            var bytes = new byte[size];
            await stream.ReadExactlyAsync(bytes, token);
            if (opcode == 3) { await WriteFrameAsync(stream, 4, bytes, token); continue; }
            if (opcode == 2) throw new IOException("Discord 關閉連線：" + System.Text.Encoding.UTF8.GetString(bytes));
            if (opcode != 1) throw new IOException("未知 Discord 封包");
            return JsonDocument.Parse(bytes);
        }
    }
    public void Dispose() { pipe?.Dispose(); pipe = null; PipeIndex = -1; UserId = ""; UserName = "未提供帳號名稱"; }
}
