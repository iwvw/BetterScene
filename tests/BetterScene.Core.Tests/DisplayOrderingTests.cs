using BetterScene.Core.Displays;
using Xunit;

namespace BetterScene.Core.Tests;

public class DisplayOrderingTests
{
    private static DisplayTarget CreateTarget(string id) => new()
    {
        Id = id,
        GdiName = "\\\\.\\DISPLAY1",
        Description = id,
    };

    [Fact]
    public void Initial_order_is_recorded_and_persisted()
    {
        var saved = new List<string>();
        var t1 = CreateTarget("MON_1");
        var t2 = CreateTarget("MON_2");

        var ordered = DisplayOrdering.Order(new[] { t1, t2 }, saved, out bool changed);

        Assert.True(changed);
        Assert.Equal(2, saved.Count);
        Assert.Equal("MON_1", saved[0]);
        Assert.Equal("MON_2", saved[1]);
        Assert.Equal("MON_1", ordered[0].Id);
        Assert.Equal("MON_2", ordered[1].Id);
    }

    [Fact]
    public void Shuffled_enumeration_preserves_saved_order()
    {
        var saved = new List<string> { "MON_1", "MON_2", "MON_3" };
        var t1 = CreateTarget("MON_1");
        var t2 = CreateTarget("MON_2");
        var t3 = CreateTarget("MON_3");

        // 底层驱动乱序返回 [3, 1, 2]
        var ordered = DisplayOrdering.Order(new[] { t3, t1, t2 }, saved, out bool changed);

        Assert.False(changed);
        Assert.Equal("MON_1", ordered[0].Id);
        Assert.Equal("MON_2", ordered[1].Id);
        Assert.Equal("MON_3", ordered[2].Id);
    }

    [Fact]
    public void Reconnecting_display_restores_its_original_slot()
    {
        var saved = new List<string> { "MON_A", "MON_B", "MON_C" };
        var tA = CreateTarget("MON_A");
        var tC = CreateTarget("MON_C");

        // B 断开了，只有 A 和 C
        var ordered = DisplayOrdering.Order(new[] { tC, tA }, saved, out _);
        Assert.Equal("MON_A", ordered[0].Id);
        Assert.Equal("MON_C", ordered[1].Id);

        // B 重连了，驱动先返回 B 再返回 A 再返回 C
        var tB = CreateTarget("MON_B");
        var restored = DisplayOrdering.Order(new[] { tB, tA, tC }, saved, out _);
        Assert.Equal("MON_A", restored[0].Id);
        Assert.Equal("MON_B", restored[1].Id);
        Assert.Equal("MON_C", restored[2].Id);
    }

    [Fact]
    public void New_display_appends_to_end()
    {
        var saved = new List<string> { "MON_1", "MON_2" };
        var t1 = CreateTarget("MON_1");
        var t2 = CreateTarget("MON_2");
        var tNew = CreateTarget("MON_NEW");

        var ordered = DisplayOrdering.Order(new[] { tNew, t2, t1 }, saved, out bool changed);

        Assert.True(changed);
        Assert.Equal("MON_1", ordered[0].Id);
        Assert.Equal("MON_2", ordered[1].Id);
        Assert.Equal("MON_NEW", ordered[2].Id);
    }
}
