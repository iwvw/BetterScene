namespace BetterScene.App.Pages;

/// <summary>
/// 页面的「离开界面」回调。
///
/// 存在的理由：MainWindow 最小化到托盘时会执行 <c>NavFrame.Content = null</c>
/// 来释放整棵可视化树，但直接给 Frame.Content 赋值<em>不会</em>触发导航流程，
/// 因此页面里的 <c>OnNavigatedFrom</c> 不会执行，DisplayBus / BrightnessHub /
/// App.AutoScene 等全局事件的强引用订阅仍会把页面对象钉住：
/// 隐藏后它们继续在后台刷新界面（白耗 CPU），页面也无法被回收。
///
/// 各个页面实现本接口，在方法里做与 OnNavigatedFrom 相同的清理。
/// </summary>
internal interface IPageLifecycle
{
    /// <summary>页面内容即将被释放：退订所有事件、停下定时器。</summary>
    void DetachFromHost();
}
