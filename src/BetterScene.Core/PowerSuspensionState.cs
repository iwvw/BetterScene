namespace BetterScene.Core;

public static class PowerSuspensionState
{
    private const long ResumeGracePeriodMs = 3000;

    private static volatile bool _isSuspended;
    private static long _resumedAtMs = MonoClock.Never;

    public static event Action? Suspended;
    public static event Action? Resumed;

    public static void NotifySuspend()
    {
        if (_isSuspended) return;
        _isSuspended = true;
        Volatile.Write(ref _resumedAtMs, MonoClock.Never);
        Log.Info("system is suspending");
        try { Suspended?.Invoke(); } catch { }
    }

    public static void NotifyResume()
    {
        _isSuspended = false;
        Volatile.Write(ref _resumedAtMs, MonoClock.NowMs);
        Log.Info("system resumed from suspend");
        try { Resumed?.Invoke(); } catch { }
    }

    public static bool IsSuspended => _isSuspended;

    public static bool IsResuming =>
        _isSuspended || MonoClock.Within(Volatile.Read(ref _resumedAtMs), ResumeGracePeriodMs);
}
