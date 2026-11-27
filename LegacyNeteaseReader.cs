using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudMusicPresence;

public record LegacyTrack(string Title, string Artist, bool Playing);

/// <summary>
/// Fallback reader for older NetEase Cloud Music versions (e.g. v2.x) that do not expose Windows SMTC.
/// Reads the OrpheusBrowserHost window title (even when minimized to tray) and checks Core Audio session state.
/// </summary>
public sealed class LegacyNeteaseReader
{
    private const string WindowClass = "OrpheusBrowserHost";
    private DateTime firstSilentActiveUtc = DateTime.MinValue;
    public bool ProcessDetected { get; private set; }
    public string Diagnostic { get; private set; } = "尚未偵測舊版網易雲";
    public static string? LastAudioError { get; private set; }

    public LegacyTrack? Read()
    {
        var pids = new HashSet<int>();
        foreach (var proc in Process.GetProcessesByName("cloudmusic"))
        {
            try { pids.Add(proc.Id); }
            finally { proc.Dispose(); }
        }
        ProcessDetected = pids.Count > 0;
        if (pids.Count == 0)
        {
            Diagnostic = "未偵測到 cloudmusic.exe 行程";
            return null;
        }

        string? rawTitle = FindWindowTitle(pids);
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            Diagnostic = "已偵測到 cloudmusic.exe，但尚未取得播放視窗標題";
            return null;
        }

        var parsed = ParseWindowTitle(rawTitle);
        if (parsed is null)
        {
            Diagnostic = "已偵測到舊版網易雲，等待播放歌曲";
            return null;
        }

        bool playing = DetectPlayingState(pids);
        Diagnostic = playing ? "來源：舊版網易雲視窗偵測（無時間進度）" : "來源：舊版網易雲視窗偵測 · 已暫停";
        return new LegacyTrack(parsed.Value.Title, parsed.Value.Artist, playing);
    }

    public static (string Title, string Artist)? ParseWindowTitle(string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return null;
        string trimmed = rawTitle.Trim();
        if (trimmed is "网易云音乐" or "網易雲音樂" or "NetEase Cloud Music" or "CloudMusic") return null;

        int split = trimmed.LastIndexOf(" - ", StringComparison.Ordinal);
        if (split > 0 && split + 3 < trimmed.Length)
        {
            string title = trimmed[..split].Trim();
            string artist = trimmed[(split + 3)..].Trim();
            if (title.Length > 0) return (title, artist.Length > 0 ? artist : "網易雲音樂");
        }
        return (trimmed, "網易雲音樂");
    }

    private bool DetectPlayingState(HashSet<int> pids)
    {
        var audio = QueryAudioActivity(pids);
        if (audio is null) return true; // If Core Audio is unavailable, keep showing as playing.
        if (audio.Value.HasPeak)
        {
            firstSilentActiveUtc = DateTime.MinValue;
            return true;
        }
        if (!audio.Value.SessionActive)
        {
            firstSilentActiveUtc = DateTime.MinValue;
            return false;
        }
        // Session is active but silent: tolerate brief quiet intros (< 3s) before treating as paused.
        if (firstSilentActiveUtc == DateTime.MinValue) firstSilentActiveUtc = DateTime.UtcNow;
        return DateTime.UtcNow - firstSilentActiveUtc < TimeSpan.FromSeconds(3);
    }

    public static (bool SessionActive, bool HasPeak)? QueryAudioActivity(HashSet<int> pids)
    {
        LastAudioError = null;
        if (pids.Count == 0) return (false, false);
        object? enumeratorObj = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumeratorObj = new MMDeviceEnumeratorCom();
            var enumerator = (IMMDeviceEnumerator)enumeratorObj;
            // eRender = 0, DEVICE_STATE_ACTIVE = 1
            int hr = enumerator.EnumAudioEndpoints(0, 1, out devices);
            if (hr != 0 || devices is null) { LastAudioError = $"EnumAudioEndpoints=0x{hr:X8}"; return null; }
            hr = devices.GetCount(out int deviceCount);
            if (hr != 0) { LastAudioError = $"GetCount=0x{hr:X8}"; return null; }

            bool anyActive = false;
            bool anyPeak = false;
            Guid sessionManager2Iid = typeof(IAudioSessionManager2).GUID;

            for (int d = 0; d < deviceCount; d++)
            {
                IMMDevice? device = null;
                object? managerObj = null;
                IAudioSessionEnumerator? sessionEnum = null;
                try
                {
                    if (devices.Item(d, out device) != 0 || device is null) continue;
                    // CLSCTX_ALL = 23
                    if (device.Activate(ref sessionManager2Iid, 23, 0, out managerObj) != 0 || managerObj is null) continue;
                    var manager = (IAudioSessionManager2)managerObj;
                    if (manager.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum is null) continue;
                    if (sessionEnum.GetCount(out int sessionCount) != 0) continue;

                    for (int s = 0; s < sessionCount; s++)
                    {
                        IAudioSessionControl? control = null;
                        try
                        {
                            if (sessionEnum.GetSession(s, out control) != 0 || control is null) continue;
                            if (control is not IAudioSessionControl2 control2) continue;
                            if (control2.GetProcessId(out uint pid) != 0 || !pids.Contains((int)pid)) continue;
                            if (control.GetState(out int state) != 0 || state != 1) continue; // 1 = AudioSessionStateActive
                            anyActive = true;
                            if (control is IAudioMeterInformation meter && meter.GetPeakValue(out float peak) == 0 && peak > 0.0001f)
                            {
                                anyPeak = true;
                                return (true, true);
                            }
                        }
                        finally
                        {
                            if (control is not null) Marshal.ReleaseComObject(control);
                        }
                    }
                }
                finally
                {
                    if (sessionEnum is not null) Marshal.ReleaseComObject(sessionEnum);
                    if (managerObj is not null) Marshal.ReleaseComObject(managerObj);
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
            return (anyActive, anyPeak);
        }
        catch (Exception ex) { LastAudioError = ex.GetType().Name + ": " + ex.Message; return null; }
        finally
        {
            if (devices is not null) Marshal.ReleaseComObject(devices);
            if (enumeratorObj is not null) Marshal.ReleaseComObject(enumeratorObj);
        }
    }

    private static string? FindWindowTitle(HashSet<int> pids)
    {
        string? primary = null;
        string? fallback = null;
        var classBuf = new StringBuilder(64);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!pids.Contains((int)pid)) return true;
            int textLen = GetWindowTextLength(hwnd);
            if (textLen <= 0) return true;
            var textBuf = new StringBuilder(textLen + 1);
            GetWindowText(hwnd, textBuf, textBuf.Capacity);
            string title = textBuf.ToString().Trim();
            if (title.Length == 0) return true;
            classBuf.Clear();
            int clsLen = GetClassName(hwnd, classBuf, classBuf.Capacity);
            if (clsLen > 0 && string.Equals(classBuf.ToString(), WindowClass, StringComparison.Ordinal))
            {
                primary = title;
                return false;
            }
            if (fallback is null && title.Contains(" - ", StringComparison.Ordinal))
                fallback = title;
            return true;
        }, 0);
        return primary ?? fallback;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int pcDevices);
        [PreserveSig] int Item(int nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint audioSessionGuid, int streamFlags, out nint sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(nint audioSessionGuid, int streamFlags, out nint audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionCount, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName(out nint pRetVal);
        [PreserveSig] int SetDisplayName(nint value, nint eventContext);
        [PreserveSig] int GetIconPath(out nint pRetVal);
        [PreserveSig] int SetIconPath(nint value, nint eventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid overrideValue, nint eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(nint newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(nint newNotifications);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName(out nint pRetVal);
        [PreserveSig] int SetDisplayName(nint value, nint eventContext);
        [PreserveSig] int GetIconPath(out nint pRetVal);
        [PreserveSig] int SetIconPath(nint value, nint eventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid overrideValue, nint eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(nint newNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(nint newNotifications);
        [PreserveSig] int GetSessionIdentifier(out nint pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
    }
}
