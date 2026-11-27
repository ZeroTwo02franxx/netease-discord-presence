using System.Diagnostics;
using System.Text.Json;

namespace CloudMusicPresence;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Local\\CloudMusicPresence", out bool first);
        if (!first) { MessageBox.Show("小工具已經開啟，請查看系統匣。", "網易雲 × Discord"); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private const int CompactHeight = 344;
    private const int ExpandedHeight = 586;
    private readonly TextBox appId = new();
    private readonly Label status = new();
    private readonly Label song = new();
    private readonly Label artist = new();
    private readonly Label progress = new();
    private readonly PictureBox coverArt = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(40,43,55) };
    private string currentCover = "";
    private string activeAppId = "";
    private readonly Button toggle = new();
    private readonly Button advancedToggle = new();
    private readonly Panel advancedPanel = new();
    private readonly ComboBox discordChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button scan = new();
    private readonly NotifyIcon tray;
    private readonly Icon appIcon = LoadIcon(64);
    private readonly Icon trayIcon = LoadIcon(16);
    private readonly MediaReader media = new();
    private readonly DiscordRpc rpc = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly string configPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private CancellationTokenSource? run;
    private CancellationTokenSource? scanRun;
    private Task? pending;
    private bool connected, published, closing;
    private string? lastKey;
    private DateTime lastUpdate;
    private double lastPublishedPosition;
    private DateTime nextConnect;
    private int failures;

    public MainForm()
    {
        Text = "網易雲 × Discord · v1.4.5";
        ClientSize = new Size(560, CompactHeight);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(22, 24, 31);
        ForeColor = Color.FromArgb(234, 235, 242);
        Font = new Font("Microsoft JhengHei UI", 10);
        Icon = appIcon;

        var muted = Color.FromArgb(160, 166, 185);
        LabelAt(Controls, "網易雲 × Discord", 28, 22, 500, 40, 22, FontStyle.Bold);
        LabelAt(Controls, "讓朋友看見你此刻的旋律", 30, 66, 500, 24, 10, FontStyle.Regular, muted);

        status.SetBounds(30, 104, 500, 44);
        status.Text = "準備就緒 · 開啟網易雲與 Discord 後按「開始同步」";
        status.Cursor = Cursors.Hand;
        status.DoubleClick += (_, _) => MessageBox.Show(this, status.Text, "完整連線訊息");
        Controls.Add(status);

        coverArt.SetBounds(30, 156, 88, 88); Controls.Add(coverArt);
        song.SetBounds(132, 156, 398, 36); song.Font = new Font(Font.FontFamily, 15, FontStyle.Bold); song.Text = "等待音樂"; Controls.Add(song);
        artist.SetBounds(132, 196, 398, 26); Controls.Add(artist);
        progress.SetBounds(132, 222, 398, 22); progress.ForeColor = muted; Controls.Add(progress);

        toggle = ButtonAt(Controls, "開始同步", 30, 262, 186, 40);
        toggle.BackColor = Color.FromArgb(198, 54, 73);
        toggle.Click += async (_, _) => { if (run is null) StartSync(); else await StopSync(); };

        var hide = ButtonAt(Controls, "縮到系統匣", 228, 262, 148, 40);
        hide.Click += (_, _) => Hide();

        advancedToggle = ButtonAt(Controls, "進階設定 ▾", 388, 262, 142, 40);
        advancedToggle.Click += (_, _) => ToggleAdvanced();

        LabelAt(Controls, "暫停時顯示已暫停 · 停止同步或關閉視窗會清除狀態", 30, 312, 500, 22, 9, FontStyle.Regular, muted);

        advancedPanel.SetBounds(30, 344, 500, 226);
        advancedPanel.Visible = false;
        var divider = new Panel { Bounds = new Rectangle(0, 0, 500, 1), BackColor = Color.FromArgb(52, 56, 72) };
        advancedPanel.Controls.Add(divider);

        LabelAt(advancedPanel.Controls, "同步到哪個 Discord 帳號", 0, 14, 500, 22, 9.5f, FontStyle.Regular, muted);
        discordChoice.SetBounds(0, 40, 330, 30);
        discordChoice.Items.Add(new DiscordTarget(null, "", "自動連接第一個可用 Discord"));
        discordChoice.SelectedIndex = 0; advancedPanel.Controls.Add(discordChoice);
        scan = ButtonAt(advancedPanel.Controls, "掃描帳號", 342, 37, 158, 36);
        scan.Click += async (_, _) => await ScanDiscordAsync();
        advancedPanel.Controls.Add(new Label
        {
            Text = "掃描是讓多開DC可以選擇顯示哪隻帳號",
            Bounds = new Rectangle(0, 77, 500, 20),
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Regular),
            ForeColor = muted,
            TextAlign = ContentAlignment.MiddleRight
        });

        LabelAt(advancedPanel.Controls, "自訂 Discord Application ID（選填，留空使用內建預設）", 0, 106, 500, 22, 9.5f, FontStyle.Regular, muted);
        appId.SetBounds(0, 132, 330, 30);
        appId.BackColor = Color.FromArgb(40, 43, 55);
        appId.ForeColor = ForeColor;
        appId.PlaceholderText = "使用內建預設（免填）";
        advancedPanel.Controls.Add(appId);
        var setup = ButtonAt(advancedPanel.Controls, "設定教學 ↗", 342, 129, 158, 36);
        setup.Click += (_, _) => OpenGuide();

        var diagnostic = ButtonAt(advancedPanel.Controls, "偵測播放來源", 0, 178, 170, 36);
        diagnostic.Click += async (_, _) =>
        {
            diagnostic.Enabled = false;
            try
            {
                var track = await media.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (track is not null)
                {
                    song.Text = track.Title;
                    artist.Text = track.Artist + (track.Playing ? "  · 播放中" : "  · 已暫停");
                    ShowProgress(track);
                }
                string result = track is null ? "尚未找到網易雲的媒體資訊。" : $"歌曲：{track.Title}\r\n歌手：{track.Artist}\r\n狀態：{(track.Playing ? "播放中" : "已暫停")}\r\n進度：{progress.Text}\r\n{media.TimingDiagnostic}";
                status.Text = track is null ? "未偵測到網易雲，請確認已開啟並播放歌曲" : "已找到網易雲 · " + (run is null ? "按「開始同步」即可連接 Discord" : "同步執行中");
                MessageBox.Show(this, result + "\r\n\r\nWindows 播放來源：\r\n" + media.Sessions + (track is null ? "\r\n\r\n請開啟網易雲播放歌曲，並啟用系統媒體控制（若版本提供此設定）。" : "\r\n\r\n已成功找到網易雲。cloudmusic.exe 就是網易雲音樂。"), track is null ? "未找到網易雲" : "已找到網易雲");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "偵測失敗"); }
            finally { if (!closing && !IsDisposed) diagnostic.Enabled = true; }
        };
        LabelAt(advancedPanel.Controls, "偵測不到歌曲或時間時可點此檢查", 182, 187, 318, 22, 9, FontStyle.Regular, muted);
        Controls.Add(advancedPanel);

        tray = new NotifyIcon { Icon = trayIcon, Text = "網易雲 × Discord", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("開啟視窗", null, (_, _) => { Show(); Activate(); });
        menu.Items.Add("結束", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Show(); Activate(); };
        timer.Tick += (_, _) => { if (pending is null || pending.IsCompleted) pending = TickAsync(); };
        FormClosing += OnClosing;
        try
        {
            if (File.Exists(configPath))
            {
                string saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(configPath))?.ApplicationId?.Trim() ?? "";
                if (saved.Length > 0 && saved != DefaultAppId.Resolve()) appId.Text = saved;
            }
        }
        catch { status.Text = "設定檔讀取失敗，將改用內建預設值。"; }
    }

    private void ToggleAdvanced()
    {
        bool show = !advancedPanel.Visible;
        advancedPanel.Visible = show;
        advancedToggle.Text = show ? "收合設定 ▴" : "進階設定 ▾";
        ClientSize = new Size(560, show ? ExpandedHeight : CompactHeight);
    }

    private void StartSync()
    {
        string custom = appId.Text.Trim();
        string id = DefaultAppId.Resolve(custom);
        if (!DefaultAppId.IsValid(id))
        { MessageBox.Show(this, "自訂 Application ID 格式不正確（應為 17–22 位數字）。留空即可使用內建預設。", Text); return; }
        try
        {
            if (custom.Length > 0 && custom != DefaultAppId.Resolve())
                File.WriteAllText(configPath, JsonSerializer.Serialize(new Settings(custom)));
            else if (File.Exists(configPath))
                File.Delete(configPath);
        }
        catch (Exception ex) { MessageBox.Show(this, "無法儲存設定：" + ex.Message); return; }
        activeAppId = id;
        run = new(); appId.Enabled = false; discordChoice.Enabled = false; scan.Enabled = false; toggle.Text = "停止同步";
        lastKey = null; connected = false; published = false;
        failures = 0; nextConnect = DateTime.MinValue;
        timer.Start(); pending = TickAsync();
    }

    private async Task TickAsync()
    {
        if (run is null) return;
        var token = run.Token;
        bool mediaFailed = false;
        try
        {
            Track? track;
            try { track = await media.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch { mediaFailed = true; throw; }
            song.Text = track?.Title is { Length: > 0 } title ? title : "等待網易雲播放音樂";
            artist.Text = track is null ? "請開啟網易雲電腦版，並播放一首歌曲" : track.Artist + (track.Playing ? "" : "  · 已暫停");
            ShowProgress(track);
            if (!connected)
            {
                if (DateTime.UtcNow < nextConnect) return;
                status.Text = "正在連接 Discord…";
                var target = (DiscordTarget)discordChoice.SelectedItem!;
                await rpc.ConnectAsync(activeAppId, token, pipeIndex: target.Index, expectedUserId: target.UserId);
                connected = true; lastKey = null;
            }
            if (track is { PendingTiming: true }) return;
            var activity = track?.Activity();
            string key = activity is null ? "idle" : track!.SyncKey();
            // Discord advances timestamps itself. Refresh for actual seeks and a slow keepalive while playing.
            bool seeked = activity is not null && track!.HasSeeked(lastPublishedPosition, DateTime.UtcNow - lastUpdate);
            bool keepalive = activity is not null && track!.Playing && DateTime.UtcNow - lastUpdate > TimeSpan.FromSeconds(30);
            if (key != lastKey || seeked || keepalive)
            {
                await rpc.SetAsync(activity, token);
                published = activity is not null;
                lastKey = key; lastUpdate = DateTime.UtcNow;
                lastPublishedPosition = track?.Position ?? 0;
                failures = 0;
            }
            status.Text = track is null
                ? "Discord 已連線 · 尚未取得網易雲歌曲（可在「進階設定」偵測來源）"
                : activity is null
                    ? "Discord 已連線 · 目前沒有播放歌曲"
                    : track.Playing
                        ? $"● 正在同步到 {rpc.UserName}（IPC {rpc.PipeIndex}）"
                        : $"❚❚ 已暫停 · 同步顯示於 {rpc.UserName}（IPC {rpc.PipeIndex}）";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (PresenceUpdateDeferredException ex)
        {
            status.Text = $"已偵測最新狀態 · 等待 Discord 更新額度（約 {Math.Ceiling(ex.RetryAfter.TotalSeconds)} 秒）";
            // Do not queue stale songs or reconnect to bypass the budget. Next poll reads the latest song.
        }
        catch (Exception ex)
        {
            // A media read failure must not leave the previous song advertised.
            if (mediaFailed && connected && published)
            { try { await rpc.SetAsync(null, token); } catch { } }
            rpc.Dispose(); connected = false; published = false; lastKey = null;
            failures = Math.Min(failures + 1, 5);
            int seconds = Math.Min(60, 5 * (1 << (failures - 1)));
            nextConnect = DateTime.UtcNow.AddSeconds(seconds);
            status.Text = (ex is OperationCanceledException || ex is TimeoutException ? "讀取或連線逾時。" : ex.Message) + $"（{seconds} 秒後重試）";
        }
    }

    private async Task StopSync()
    {
        timer.Stop();
        var stopping = run;
        if (stopping is null) return;
        toggle.Enabled = false;
        stopping.Cancel();
        if (pending is not null) await pending;
        if (connected && published)
        {
            if (!IsDisposed) status.Text = "正在清除 Discord 狀態…";
            using var clearTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await rpc.ClearAsync(clearTimeout.Token); } catch { }
        }
        rpc.Dispose(); connected = false; published = false;
        stopping.Dispose(); run = null;
        if (!closing && !IsDisposed)
        { toggle.Enabled = true; toggle.Text = "開始同步"; appId.Enabled = true; discordChoice.Enabled = true; scan.Enabled = true; status.Text = "已停止同步"; }
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        scanRun?.Cancel();
        if (run is null)
        {
            closing = true; tray.Visible = false;
            return;
        }
        e.Cancel = true; Enabled = false;
        status.Text = "正在清除 Discord 狀態並關閉…";
        await StopSync();
        closing = true; tray.Visible = false;
        BeginInvoke(new Action(Close));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            scanRun?.Cancel(); scanRun?.Dispose(); scanRun = null;
            run?.Cancel(); run?.Dispose(); run = null;
            coverArt.CancelAsync();
            var img = coverArt.Image; coverArt.Image = null; img?.Dispose();
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
            timer.Dispose();
            rpc.Dispose();
            media.Dispose();
            Icon = null;
            appIcon.Dispose();
            trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OpenGuide()
    {
        MessageBox.Show(this, "本工具已內建預設值，一般使用不需填寫，直接按「開始同步」即可。\r\n\r\n若想自訂 Discord 顯示的應用程式名稱，請依以下步驟：\r\n1. 開啟 Discord 開發者入口，登入並按 New Application。\r\n2. 名稱填寫想顯示的文字（例如「網易雲音樂」）。\r\n3. 在 General Information 複製 Application ID，貼到進階設定欄位。\r\n4. 確認 Discord 設定 → 活動隱私，已允許顯示目前活動。\r\n\r\n僅需 Application ID；不用建立 Bot，也不用提供 Token。", "自訂 Application ID 教學");
        try { Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void LabelAt(Control.ControlCollection parent, string text, int x, int y, int w, int h, float size = 10, FontStyle style = FontStyle.Regular, Color? color = null)
    { parent.Add(new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = new Font(Font.FontFamily, size, style), ForeColor = color ?? ForeColor }); }
    private Button ButtonAt(Control.ControlCollection parent, string text, int x, int y, int w, int h = 38)
    {
        var button = new Button { Text = text, Bounds = new Rectangle(x, y, w, h), BackColor = Color.FromArgb(47, 50, 65), ForeColor = ForeColor, FlatStyle = FlatStyle.Flat };
        button.FlatAppearance.BorderSize = 0; parent.Add(button); return button;
    }
    private record Settings(string ApplicationId);
    private static Icon LoadIcon(int size)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("CloudMusicPresence.AppIcon")
            ?? throw new InvalidOperationException("找不到程式圖示資源");
        using var loaded = new Icon(stream, size, size);
        return (Icon)loaded.Clone();
    }
    private void ShowProgress(Track? track)
    {
        string url = track?.CoverUrl ?? "";
        if (url != currentCover)
        {
            coverArt.CancelAsync(); currentCover = url;
            var old = coverArt.Image; coverArt.Image = null; old?.Dispose();
            if (url.Length > 0) coverArt.LoadAsync(url);
        }
        progress.Text = track is { Duration: > 0 } ? $"{FormatTime(track.Position)} / {FormatTime(track.Duration)}" : track is null ? "" : media.TimingDiagnostic;
    }
    private static string FormatTime(double seconds) => $"{(int)(seconds / 60):00}:{(int)(seconds % 60):00}";
    private record DiscordTarget(int? Index, string UserId, string Label)
    { public override string ToString() => Label; }

    private async Task ScanDiscordAsync()
    {
        string id = DefaultAppId.Resolve(appId.Text);
        if (!DefaultAppId.IsValid(id))
        { MessageBox.Show(this, "自訂 Application ID 格式不正確（應為 17–22 位數字）。留空即可使用內建預設。", Text); return; }
        scan.Enabled = false; toggle.Enabled = false; appId.Enabled = false; discordChoice.Enabled = false;
        var cts = new CancellationTokenSource();
        scanRun = cts;
        var scanToken = cts.Token;
        var targets = new List<DiscordTarget>();
        var errors = new List<string>();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                if (closing || IsDisposed || scanToken.IsCancellationRequested) return;
                status.Text = $"掃描 Discord 帳號 {i + 1}/10…";
                using var probe = new DiscordRpc();
                try
                {
                    await probe.ConnectAsync(id, scanToken, pipeIndex: i);
                    targets.Add(new DiscordTarget(i, probe.UserId, $"{probe.UserName}（IPC {i}）"));
                }
                catch (OperationCanceledException) when (scanToken.IsCancellationRequested) { return; }
                catch (Exception ex) { if (!ex.Message.StartsWith("找不到指定")) errors.Add(ex.Message); }
            }
            if (closing || IsDisposed) return;
            discordChoice.Items.Clear();
            discordChoice.Items.Add(new DiscordTarget(null, "", "自動連接第一個可用 Discord"));
            foreach (var target in targets) discordChoice.Items.Add(target);
            discordChoice.SelectedIndex = targets.Count == 1 ? 1 : 0;
            status.Text = targets.Count > 0 ? $"找到 {targets.Count} 個 Discord · 請選擇要顯示狀態的帳號" : "未找到可連線的 Discord，請確認電腦版已登入。";
            if (errors.Count > 0) MessageBox.Show(this, string.Join("\r\n", errors), "部分 Discord 連線失敗");
        }
        finally
        {
            if (ReferenceEquals(scanRun, cts)) scanRun = null;
            cts.Dispose();
            if (!closing && !IsDisposed) { scan.Enabled = true; toggle.Enabled = true; appId.Enabled = true; discordChoice.Enabled = true; }
        }
    }
}
