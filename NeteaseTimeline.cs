using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloudMusicPresence;

public record NativeTiming(string SongId, double Position, double Duration, bool Playing);

/// <summary>Read only the v3 player's timing fields when SMTC does not supply them.</summary>
public sealed class NeteaseTimeline : IDisposable
{
    // Player signatures/layout documented by Kxnrl/NetEase-Cloud-Music-DiscordRPC (MIT).
    // See THIRD-PARTY-NOTICES.txt. No injection or writes to the player are used.
    private const string PlayerPattern = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 0D ? ? ? ? E8 ? ? ? ? 90 48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 05 ? ? ? ? 48 8D A5 ? ? ? ? 5F 5D C3 CC CC CC CC CC 48 89 4C 24 ? 55 57 48 81 EC ? ? ? ? 48 8D 6C 24 ? 48 8D 7C 24";
    private const string SchedulePattern = "66 0F 2E 0D ? ? ? ? 7A ? 75 ? 66 0F 2E 15";
    private SafeProcessHandle? handle;
    private Process? process;
    private nint player, schedule;
    private readonly object gate = new();
    private DateTime nextScan;
    public bool IsTransitioning { get; private set; }
    public string Diagnostic { get; private set; } = "尚未讀取播放器時間";

    public NativeTiming? Read()
    {
        lock (gate)
        {
            IsTransitioning = false;
            try
            {
                if (handle is null || process is null || process.HasExited)
                {
                    Reset();
                    if (DateTime.UtcNow < nextScan) return null;
                    nextScan = DateTime.UtcNow.AddSeconds(10);
                    Locate();
                }
                if (handle is null) return null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string identity = ReadIdentity();
                    int state = BitConverter.ToInt32(ReadBytes(player + 0x60, 4));
                    double duration = BitConverter.ToDouble(ReadBytes(player + 0xa8, 8));
                    double position = BitConverter.ToDouble(ReadBytes(schedule, 8));
                    if (identity != ReadIdentity())
                    {
                        if (attempt < 2) { Thread.Sleep(30); continue; }
                        IsTransitioning = true;
                        Diagnostic = "正在切換歌曲，下一次重試";
                        return null;
                    }
                    if (state is not (1 or 2) || !IsValid(position, duration) || identity.Length == 0)
                    { Diagnostic = "播放器暫未提供有效時間"; return null; }
                    Diagnostic = "已從網易雲播放器讀取實際進度與總長度";
                    return new(identity, Math.Clamp(position, 0, duration), duration, state == 1);
                }
                return null;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
            {
                Diagnostic = "網易雲時間讀取失敗：" + ex.Message;
                Reset();
                return null;
            }
        }
    }

    public static bool IsValid(double position, double duration) =>
        double.IsFinite(position) && double.IsFinite(duration) && duration > 0 && duration < 86400 && position >= 0 && position <= duration + 2;

    private void Locate()
    {
        string? lastError = null;
        foreach (var candidate in Process.GetProcessesByName("cloudmusic").OrderByDescending(p => p.MainWindowHandle != 0))
        {
            bool keep = false;
            try
            {
                var module = candidate.Modules.Cast<ProcessModule>().FirstOrDefault(m => string.Equals(m.ModuleName, "cloudmusic.dll", StringComparison.OrdinalIgnoreCase));
                if (module is null) continue;
                using var file = File.Open(module.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(file);
                nint locatedPlayer = 0, locatedSchedule = 0;
                foreach (var section in pe.PEHeaders.SectionHeaders.Where(s => (s.SectionCharacteristics & SectionCharacteristics.ContainsCode) != 0))
                {
                    var bytes = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
                    int p = Find(bytes, PlayerPattern);
                    int s = Find(bytes, SchedulePattern);
                    if (p >= 0) locatedPlayer = module.BaseAddress + section.VirtualAddress + p + 7 + BitConverter.ToInt32(bytes, p + 3);
                    if (s >= 0) locatedSchedule = module.BaseAddress + section.VirtualAddress + s + 8 + BitConverter.ToInt32(bytes, s + 4);
                }
                if (locatedPlayer == 0 || locatedSchedule == 0) continue;
                var opened = OpenProcess(0x10 | 0x1000, false, candidate.Id); // VM_READ | QUERY_LIMITED_INFORMATION
                if (opened.IsInvalid) { opened.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                handle = opened; process = candidate; player = locatedPlayer; schedule = locatedSchedule;
                keep = true;
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or BadImageFormatException)
            { lastError = "無法讀取網易雲時間：" + ex.Message; }
            finally { if (!keep) candidate.Dispose(); }
        }
        Diagnostic = lastError ?? "目前網易雲版本的時間欄位尚未辨識，或程式權限不同";
    }

    public static int Find(byte[] bytes, string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x == "?" ? (byte?)null : Convert.ToByte(x, 16)).ToArray();
        for (int i = 0; i <= bytes.Length - tokens.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < tokens.Length; j++)
                if (tokens[j].HasValue && bytes[i + j] != tokens[j]!.Value) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private string ReadIdentity()
    {
        long info = BitConverter.ToInt64(ReadBytes(player + 0x50, 8));
        if (info <= 0) return "";
        nint value = (nint)info + 0x10;
        long length = BitConverter.ToInt64(ReadBytes(value + 0x10, 8));
        if (length is < 1 or > 128) return "";
        if (length > 15) value = (nint)BitConverter.ToInt64(ReadBytes(value, 8));
        string text = Encoding.UTF8.GetString(ReadBytes(value, (int)length));
        string id = text.Split('_')[0];
        return id.All(char.IsAsciiDigit) ? id : "";
    }

    private byte[] ReadBytes(nint address, int count)
    {
        var buffer = new byte[count];
        if (address == 0 || handle is null || !ReadProcessMemory(handle, address, buffer, count, out nint read) || read != count)
            throw new IOException("播放器時間欄位暫時無法讀取");
        return buffer;
    }
    private void Reset() { handle?.Dispose(); handle = null; process?.Dispose(); process = null; player = schedule = 0; }
    public void Dispose() { lock (gate) Reset(); }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] buffer, nint count, out nint read);
}
