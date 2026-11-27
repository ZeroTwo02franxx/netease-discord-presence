using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using CloudMusicPresence;

int passed = 0;
void Check(bool result, string name)
{
    if (!result) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
var pausedTrack = new Track("測試歌", "歌手", "專輯", false, 15, 60);
var pausedActivity = JsonSerializer.SerializeToElement(pausedTrack.Activity());
Check(pausedActivity.GetProperty("details").GetString() == "測試歌"
    && pausedActivity.GetProperty("state").GetString() == "歌手 · 已暫停"
    && !pausedActivity.TryGetProperty("timestamps", out _), "暫停時繼續顯示並標示已暫停且不送時間戳記");
var shortArtistPaused = JsonSerializer.SerializeToElement(new Track("測試歌", "A", "", false, 0, 60).Activity());
Check(shortArtistPaused.GetProperty("state").GetString() == "A · 已暫停", "單字元歌手暫停時格式正確");
Check(!pausedTrack.HasSeeked(15, TimeSpan.FromSeconds(5)), "暫停時不誤判拖曳進度");
Check(pausedTrack.SyncKey() != (pausedTrack with { Playing = true }).SyncKey(), "播放與暫停切換會觸發狀態更新");
Check(new Track("", "歌手", "", true, 0, 60).Activity() is null, "空白歌曲不發佈活動");
var track = new Track("測試歌", "測試歌手", "", true, 20, 100);
var activity = JsonSerializer.SerializeToElement(track.Activity());
Check(activity.GetProperty("type").GetInt32() == 2 && activity.GetProperty("details").GetString() == "測試歌", "歌曲與聆聽類型");
foreach (var values in new[] { (0d, 0d), (20d, 0d), (double.NaN, 100d), (20d, double.PositiveInfinity) })
{
    var noTime = JsonSerializer.SerializeToElement(new Track("測試歌", "歌手", "", true, values.Item1, values.Item2).Activity());
    Check(!noTime.TryGetProperty("timestamps", out _) && noTime.GetProperty("details").GetString() == "測試歌", "無效時間不送 timestamps:null");
}
var stamps = activity.GetProperty("timestamps");
Check(stamps.GetProperty("end").GetInt64() - stamps.GetProperty("start").GetInt64() == 100, "時間戳記反映播放進度");
var fracStamps = JsonSerializer.SerializeToElement(new Track("測試歌", "歌手", "", true, 10.9, 100).Activity()).GetProperty("timestamps");
Check(fracStamps.GetProperty("end").GetInt64() - fracStamps.GetProperty("start").GetInt64() == 100, "小數進度不縮短總長度");
var untimed = new Track("測試歌", "測試歌手", "", true, 0, 0);
Check(!untimed.HasSeeked(0, TimeSpan.FromSeconds(5)), "無總長度時不誤判拖曳進度");
Check(!track.HasSeeked(17, TimeSpan.FromSeconds(3)) && track.HasSeeked(10, TimeSpan.FromSeconds(3)), "有效總長度可正確偵測拖曳進度");
Check(untimed.SyncKey() != track.SyncKey(), "取得時間後會觸發狀態更新");
Check(MediaReader.IsCloudMusic("C:\\NetEase\\CloudMusic\\cloudmusic.exe") && !MediaReader.IsCloudMusic("Spotify.exe") && !MediaReader.IsCloudMusic("chrome.exe"), "只接受網易雲媒體來源");
Check(NeteaseTimeline.Find(new byte[] { 0, 0x66, 0x0f, 0x2e, 0x13, 0xff }, "66 0F 2E ? FF") == 1, "播放器特徵碼支援萬用位元組");
Check(NeteaseTimeline.Find(new byte[] { 0x66, 0x0f }, "66 0F 2E") == -1, "截短特徵碼不誤判");
Check(NeteaseTimeline.IsValid(14, 176) && !NeteaseTimeline.IsValid(200, 176) && !NeteaseTimeline.IsValid(double.NaN, 176), "播放器時間範圍檢查");
string playlist = """
{"list":[{"id":"42","track":{"name":"測試歌","album":{"name":"測試專輯","cover":"http://p1.music.126.net/cover.jpg"}}}]}
""";
var album = AlbumMetadata.Parse(playlist, "42", "測試歌");
Check(album?.Name == "測試專輯" && album.CoverUrl == "https://p1.music.126.net/cover.jpg?param=512y512", "以歌曲 ID 和歌名取得正確封面");
Check(AlbumMetadata.Parse(playlist, "43", "測試歌") is null && AlbumMetadata.Parse(playlist, "42", "其他歌曲") is null, "換歌時不套用上一首封面");
Check(AlbumMetadata.NormalizeCoverUrl("file:///C:/secret.png") is null && AlbumMetadata.NormalizeCoverUrl("https://p1.music.126.net.evil.example/a.jpg") is null, "只使用網易雲公開封面網址");
var coverActivity = JsonSerializer.SerializeToElement((track with { Album = "測試專輯", CoverUrl = album!.CoverUrl }).Activity());
Check(coverActivity.GetProperty("assets").GetProperty("large_image").GetString() == album.CoverUrl && coverActivity.GetProperty("timestamps").ValueKind == JsonValueKind.Object, "封面與播放時間同時送到 Discord");
Check(!activity.TryGetProperty("assets", out _), "沒有封面時不發送 assets:null");
var budget = new PresenceUpdateGate();
bool Acquire(double seconds, bool clear = false) => budget.TryAcquire(TimeSpan.FromSeconds(seconds), clear, out _);
Check(Acquire(0) && !Acquire(0.25) && Acquire(1), "本機快速輪詢不會每次都傳送 Discord");
Check(Acquire(2) && Acquire(3) && !Acquire(4), "一般更新保留清除狀態的額度");
Check(Acquire(4, true) && !Acquire(5, true), "暫停可使用保留額度但不超過總額");
Check(!Acquire(20) && Acquire(20.1, true), "滾動視窗包含邊界緩衝");
Check(!Acquire(20.2) && !Acquire(21.1) && Acquire(22.1), "混合清除和一般更新仍遵守滾動限制");

async Task<(int Op, JsonElement Data)> Read(Stream stream, CancellationToken token)
{
    byte[] header = new byte[8]; await stream.ReadExactlyAsync(header, token);
    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
    byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, token);
    using var json = JsonDocument.Parse(body);
    return (BinaryPrimitives.ReadInt32LittleEndian(header), json.RootElement.Clone());
}
async Task Write(Stream stream, int opcode, object data, CancellationToken token)
{
    using var frame = new MemoryStream();
    await DiscordRpc.WriteFrameAsync(frame, opcode, JsonSerializer.SerializeToUtf8Bytes(data), token);
    // Emulate a server returning partial header and body reads.
    foreach (byte b in frame.ToArray()) await stream.WriteAsync(new byte[] { b }, token);
    await stream.FlushAsync(token);
}
async Task Scenario(Func<NamedPipeServerStream, CancellationToken, Task> serve, Func<DiscordRpc,CancellationToken,string,Task> client)
{
    string prefix = "cloudmusic-test-" + Guid.NewGuid().ToString("N") + "-";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var server = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var serverTask = Task.Run(async () => { await server.WaitForConnectionAsync(timeout.Token); await serve(server, timeout.Token); });
    using var rpc = new DiscordRpc();
    await client(rpc, timeout.Token, prefix);
    await serverTask;
}

await Scenario(async (server, token) =>
{
    var handshake = await Read(server, token);
    Check(handshake.Op == 0 && handshake.Data.GetProperty("client_id").GetString() == "123456789012345678", "IPC 握手");
    await Write(server, 1, new { evt = "READY", data = new { user = new { id = "user-a", username = "測試帳號" } } }, token);
    var request = await Read(server, token);
    Check(request.Data.GetProperty("args").GetProperty("activity").GetProperty("details").GetString() == "測試歌", "IPC 發佈歌曲");
    await Write(server, 3, new { ping = "alive" }, token);
    var pong = await Read(server, token);
    Check(pong.Op == 4 && pong.Data.GetProperty("ping").GetString() == "alive", "回覆 Discord 心跳");
    await Write(server, 1, new { nonce = "unrelated", evt = "IGNORED" }, token);
    await Write(server, 1, new { nonce = request.Data.GetProperty("nonce").GetString(), data = new { } }, token);
    var clear = await Read(server, token);
    Check(clear.Data.GetProperty("args").GetProperty("activity").ValueKind == JsonValueKind.Null, "IPC 清除歌曲");
    await Write(server, 1, new { nonce = clear.Data.GetProperty("nonce").GetString(), data = new { } }, token);
}, async (rpc, token, prefix) =>
{
    await rpc.ConnectAsync("123456789012345678", token, prefix);
    Check(rpc.UserId == "user-a" && rpc.UserName == "測試帳號" && rpc.PipeIndex == 0, "辨識連線的 Discord 帳號");
    await rpc.SetAsync(track.Activity(), token);
    await rpc.SetAsync(null, token);
});

// A reachable but rejecting first client must not prevent a second client connecting.
{
    string prefix = "cloudmusic-multi-" + Guid.NewGuid().ToString("N") + "-";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var first = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    using var second = new NamedPipeServerStream(prefix + "1", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var serveFirst = Task.Run(async () => { await first.WaitForConnectionAsync(timeout.Token); await Read(first, timeout.Token); await Write(first, 2, new { code = 4000, message = "rejected" }, timeout.Token); });
    var serveSecond = Task.Run(async () => { await second.WaitForConnectionAsync(timeout.Token); await Read(second, timeout.Token); await Write(second, 1, new { evt = "READY", data = new { user = new { id = "user-b", username = "Second" } } }, timeout.Token); });
    using var rpc = new DiscordRpc();
    await rpc.ConnectAsync("123456789012345678", timeout.Token, prefix);
    Check(rpc.PipeIndex == 1 && rpc.UserId == "user-b", "多開時第一個 Discord 握手失敗仍可連到下一個");
    await Task.WhenAll(serveFirst, serveSecond);
}

{
    string prefix = "cloudmusic-select-" + Guid.NewGuid().ToString("N") + "-";
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var selected = new NamedPipeServerStream(prefix + "2", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    var serve = Task.Run(async () => { await selected.WaitForConnectionAsync(timeout.Token); await Read(selected, timeout.Token); await Write(selected, 1, new { evt = "READY", data = new { user = new { id = "user-c", username = "Selected" } } }, timeout.Token); });
    using var rpc = new DiscordRpc();
    await rpc.ConnectAsync("123456789012345678", timeout.Token, prefix, pipeIndex: 2, expectedUserId: "user-c");
    Check(rpc.PipeIndex == 2 && rpc.UserId == "user-c", "可指定第二個以外的 Discord 連線");
    await serve;
}

await Scenario(async (server, token) =>
{
    await Read(server, token);
    await Write(server, 1, new { evt = "READY" }, token);
    var request = await Read(server, token);
    await Write(server, 1, new { nonce = request.Data.GetProperty("nonce").GetString(), evt = "ERROR", data = new { message = "invalid activity" } }, token);
}, async (rpc, token, prefix) =>
{
    await rpc.ConnectAsync("123456789012345678", token, prefix);
    bool rejected = false;
    try { await rpc.SetAsync(track.Activity(), token); } catch (IOException) { rejected = true; }
    Check(rejected, "Discord 拒絕時不回報成功");
});

await Scenario(async (server, token) =>
{
    await Read(server, token);
    await Write(server, 2, new { message = "invalid client id" }, token);
}, async (rpc, token, prefix) =>
{
    bool rejected = false;
    try { await rpc.ConnectAsync("123456789012345678", token, prefix); } catch (IOException) { rejected = true; }
    Check(rejected, "無效 Application ID 握手失敗");
});

await Scenario(async (server, token) =>
{
    await Read(server, token);
    var header = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(header, 1);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), int.MaxValue);
    await server.WriteAsync(header, token);
}, async (rpc, token, prefix) =>
{
    bool rejected = false;
    try { await rpc.ConnectAsync("123456789012345678", token, prefix); } catch (IOException) { rejected = true; }
    Check(rejected, "拒絕不合法封包大小");
});

await Scenario(async (server, token) =>
{
    await Read(server, token);
    await Write(server, 1, new { evt = "READY", data = new { user = new { id = "user-d", username = "Budget" } } }, token);
    var request = await Read(server, token);
    await Write(server, 1, new { nonce = request.Data.GetProperty("nonce").GetString(), data = new { } }, token);
}, async (rpc, token, prefix) =>
{
    int disconnectedErrors = 0;
    for (int i = 0; i < 5; i++)
    {
        try { await rpc.SetAsync(track.Activity(), token); }
        catch (IOException) { disconnectedErrors++; }
    }
    await rpc.ConnectAsync("123456789012345678", token, prefix);
    await rpc.SetAsync(track.Activity(), token);
    Check(disconnectedErrors == 5, "未連線時發送失敗不消耗更新額度");
});
Check(DefaultAppId.IsValid(DefaultAppId.Resolve()), "內建加密預設 Application ID 可正確還原且格式合法");
Check(DefaultAppId.Resolve(" 999888777666555444 ") == "999888777666555444" && !DefaultAppId.IsValid("abc"), "自訂 Application ID 優先於內建預設並驗證格式");
Console.WriteLine($"{passed} checks passed.");
