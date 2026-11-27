namespace CloudMusicPresence;

/// <summary>Conservative rolling activity budget; keep one slot for pausing/clearing.</summary>
public sealed class PresenceUpdateGate
{
    private readonly Queue<TimeSpan> sent = new();
    private TimeSpan? last;
    public bool TryAcquire(TimeSpan now, bool clearing, out TimeSpan retryAfter)
    {
        var window = TimeSpan.FromSeconds(20.1);
        while (sent.TryPeek(out var oldest) && now - oldest >= window) sent.Dequeue();
        int maximum = clearing ? 5 : 4;
        retryAfter = TimeSpan.Zero;
        if (sent.Count >= maximum)
        {
            // All sends (including clears) count against the same rolling window.
            retryAfter = sent.ElementAt(sent.Count - maximum) + window - now;
        }
        if (!clearing && last.HasValue && now - last.Value < TimeSpan.FromSeconds(1))
            retryAfter = TimeSpan.FromTicks(Math.Max(retryAfter.Ticks, (last.Value + TimeSpan.FromSeconds(1) - now).Ticks));
        if (retryAfter > TimeSpan.Zero) return false;
        sent.Enqueue(now); last = now;
        return true;
    }
}

public sealed class PresenceUpdateDeferredException(TimeSpan retryAfter) : Exception("等待 Discord 更新額度")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
