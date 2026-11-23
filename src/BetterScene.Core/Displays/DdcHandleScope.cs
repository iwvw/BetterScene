namespace BetterScene.Core.Displays;

/// <summary>
/// 把「拿到一个可用 DDC 句柄」这件事收敛到一处。
///
/// 正常情况用 worker 长期持有的 DdcSession（避免反复 Open/Destroy 的开销，
/// 也避免某些显示器在频繁开合时抖）；
/// 句柄被回收时退回按 GDI 名临时枚举，用完立刻释放。
/// </summary>
internal static class DdcHandleScope
{
    /// <summary>
    /// 在 withHandle 回调里使用该显示器的 DDC 句柄。
    /// 找不到句柄时返回 false，并给出原因。
    /// </summary>
    public static bool TryUse(DisplayTarget target, DdcSession session,
        Func<IntPtr, bool> withHandle, out string? error)
    {
        error = null;

        if (session.TryGet(target.Id, out IntPtr handle) && handle != IntPtr.Zero)
        {
            if (withHandle(handle)) return true;

            error = "设备未响应。";
            return false;
        }

        // 没有会话句柄：临时取一个，用完释放。
        List<PhysicalDescriptor>? physical = null;
        try
        {
            var describer = new NativeDescriber();
            physical = describer.Enumerate();

            PhysicalDescriptor? item = physical.FirstOrDefault(p =>
                string.Equals(p.GdiName, target.GdiName, StringComparison.OrdinalIgnoreCase));

            if (item is null || item.Monitor == IntPtr.Zero)
            {
                error = "找不到该显示器的物理监视器句柄。";
                return false;
            }

            if (withHandle(item.Monitor)) return true;

            error = "设备未响应。";
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"temporary ddc handle for {target.FirmwareId} failed: {ex.Message}");
            error = ex.Message;
            return false;
        }
        finally
        {
            // Enumerate() 会为「所有」物理显示器打开句柄,这些句柄都归本方法所有,
            // 没用到的那 N-1 个也必须销毁,否则每次都泄漏一批。
            if (physical is not null)
            {
                foreach (PhysicalDescriptor p in physical)
                {
                    if (p.Monitor != IntPtr.Zero) NativeDdc.DestroyPhysicalMonitor(p.Monitor);
                }
            }
        }
    }
}
