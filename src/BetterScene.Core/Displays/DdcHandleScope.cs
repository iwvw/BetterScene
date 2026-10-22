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
        IntPtr temporary = IntPtr.Zero;
        try
        {
            var describer = new NativeDescriber();
            List<PhysicalDescriptor> physical = describer.Enumerate();

            PhysicalDescriptor? item = physical.FirstOrDefault(p =>
                string.Equals(p.GdiName, target.GdiName, StringComparison.OrdinalIgnoreCase));

            if (item is null || item.Monitor == IntPtr.Zero)
            {
                error = "找不到该显示器的物理监视器句柄。";
                return false;
            }

            temporary = item.Monitor;
            if (withHandle(temporary)) return true;

            error = "设备未响应。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (temporary != IntPtr.Zero) NativeDdc.DestroyPhysicalMonitor(temporary);
        }
    }
}
