namespace BetterScene.Core.Displays;

public static class DisplayOrdering
{
    /// <summary>
    /// 对显示器列表按用户历史记忆的顺序进行稳定排序。
    ///
    /// 为什么需要这个排序：
    /// 场景切换（切分辨率/刷新率）、系统唤醒或显卡热插拔时，
    /// Windows 底层驱动枚举返回的显示器顺序随时可能颠倒（如内屏与外接屏换位）。
    /// 纯靠底层枚举顺序会导致 mini 面板卡片上下颠倒跳动。
    ///
    /// 排序规则：
    /// 1. 已记录在 savedOrder 中的显示器，严格保持其先后相对顺序；
    /// 2. 新接入的显示器追加到 savedOrder 末尾并返回 orderChanged = true，以通知保存设置。
    /// </summary>
    public static List<DisplayTarget> Order(
        IEnumerable<DisplayTarget> targets,
        List<string> savedOrder,
        out bool orderChanged)
    {
        orderChanged = false;
        var list = targets.ToList();
        if (list.Count == 0) return list;

        var knownSet = new HashSet<string>(savedOrder, StringComparer.OrdinalIgnoreCase);

        foreach (DisplayTarget target in list)
        {
            if (!knownSet.Contains(target.Id))
            {
                savedOrder.Add(target.Id);
                knownSet.Add(target.Id);
                orderChanged = true;
            }
        }

        return list
            .OrderBy(target =>
            {
                int idx = savedOrder.FindIndex(id => string.Equals(id, target.Id, StringComparison.OrdinalIgnoreCase));
                return idx >= 0 ? idx : int.MaxValue;
            })
            .ToList();
    }
}
