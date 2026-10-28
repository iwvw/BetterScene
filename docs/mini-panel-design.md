# 托盘迷你面板的动画与稳定性设计

这份文档记录 BetterScene 迷你面板（`FlyoutWindow`）在「点不快、展开卡、动画掉帧」这三类问题上的排查结论与最终实现。Momomi 的迷你面板（`MiniWindow`）是同一套结构，同样的结论适用，文末给出对照。

文中提到的每一个数字和判断都来自实测，不是估算。改动这些值之前请先读完对应章节的理由——大部分看起来「可以调小一点」的地方，调小之后都会退回到某个已知故障。

---

## 一、点击丢失：不是响应慢，是点击被吞掉了

### 现象

快速点托盘图标，只有第一下有效，后面的点击没有任何反应。点得越快越明显。

### 根因

Windows 托盘图标的点击消息序列是：

| 点击次数 | Explorer 发出的消息 |
|---|---|
| 1 次 | `WM_LBUTTONDOWN` → `WM_LBUTTONUP` |
| 2 次 | `DOWN` → `UP` → `WM_LBUTTONDBLCLK` → `UP` |
| 3 次 | `DOWN` → `UP` → `DBLCLK` → `UP` → `DBLCLK` → `UP` |

也就是说，**第 2 次及以后的每一次点击，抬起的动作都会变成 `DBLCLK`**，而不是再来一次 `UP`。

H.NotifyIcon 的 `MessageWindow.ProcessWindowMessage` 是这样处理的：

```csharp
case WM_LBUTTONUP:
    if (!IsDoubleClick)                     // ← 双击后的那次 UP 被静默丢弃
        OnMouseEventReceived(MouseEvent.IconLeftMouseUp, point);
    IsDoubleClick = false;
    break;

case WM_LBUTTONDBLCLK:
    IsDoubleClick = true;
    OnMouseEventReceived(MouseEvent.IconDoubleClick, point);   // ← 只驱动 DoubleClickCommand
    OnMouseEventReceived(MouseEvent.IconLeftDoubleClick, point);
    break;
```

库必须区分单击和双击（为了决定要不要弹右键菜单、要不要等双击间隔），所以它把「双击判定之后的那个 UP」当作双击的余波丢掉了。

只绑 `LeftClickCommand` 时的结果：

| 实际点击 | 触发 LeftClick | 触发 DoubleClick | 净切换次数 |
|---|---|---|---|
| 1 次 | 1 | 0 | 1 ✅ |
| 2 次 | 1 | 0 | **1** ❌ |
| 3 次 | 1 | 0 | **1** ❌ |
| N 次 | 1 | 0 | **1** ❌ |

**无论点多快、点几下，永远只切换一次。** 用户感受到的「点快了没反应」其实是「后续点击全部消失」。

### 修复

```csharp
icon.LeftClickCommand = new RelayCommand(() => LeftClicked?.Invoke());
icon.DoubleClickCommand = new RelayCommand(() => LeftClicked?.Invoke());
icon.NoLeftClickDelay = true;
```

两条命令都指向同一个动作：

- 2 次点击 = 1 次 `LeftClick` + 1 次 `DoubleClick` = 切换 2 次
- 3 次点击 = 1 次 `LeftClick` + 2 次 `DoubleClick` = 切换 3 次

`NoLeftClickDelay = true` 让库内部的 `DoubleClickWaitTime` 变成 0，于是 `SingleClickTimer.Change(0, ...)` 立即触发，单击没有系统双击间隔的等待。这一项本来就有，不是本次新增的——但它是「单击零延迟」的前提，去掉会让单击重新变成 500ms 延迟。

### 验证方法

托盘图标在部分环境下无法用 UI 自动化定位（任务栏在副屏、或新版任务栏用 XAML Island 渲染，`EnumChildWindows` 返回空）。可靠的做法是直接向 H.NotifyIcon 的隐藏消息窗口投递消息，精确复现 Explorer 的序列：

```powershell
# 找到类名以 H.NotifyIcon 开头的窗口（属于 BetterScene 进程）
# 然后按 Explorer 的顺序 PostMessage(0x400, 0, msg)
#   DOWN=0x0201  UP=0x0202  DBLCLK=0x0203
```

实测结果：

```
点击 1 下 -> left=1 double=0 切换=1 最终状态=展开
点击 2 下 -> left=1 double=1 切换=2 最终状态=展开
点击 3 下 -> left=1 double=2 切换=3 最终状态=收起
点击 4 下 -> left=1 double=3 切换=4 最终状态=收起
点击 5 下 -> left=1 double=4 切换=5 最终状态=展开
点击 6 下 -> left=1 double=5 切换=6 最终状态=展开
```

切换次数等于点击次数，最终状态的奇偶性也对（起始收起，奇数次展开、偶数次收起）。

### 对照：为什么开始菜单没有这个问题

任务栏直接消费 `WM_LBUTTONUP`，中间没有单击/双击仲裁，所以每一帧点击都是独立的。我们因为用了托盘图标库，必须经过那一层仲裁——这是代价，不是可以绕开的实现细节。

---

## 二、动画状态机：意图驱动，而不是状态枚举

### 现象

动画没播完就再点，点击无效；或者反向点击时窗口先跳一下才开始动。

### 根因

旧实现是四态枚举：

```csharp
enum VisState { Hidden, Showing, Shown, Hiding }
```

`Toggle()` 只看 `IsVisible`，而 `IsVisible` 在 `Hiding` 时返回 `false`。于是收起动画途中的点击被当成「展开」，但 `Show()` 里 `wasHidden == false` 又走另一条分支，状态就乱了。更糟的是 `Show()` 会把「当前窗口位置」当作起点——那是调用方以为的位置，动画可能已经跑掉一半，用它会跳。

### 修复：只记意图

```csharp
private bool _wantVisible;      // 应该开着还是关着，唯一的状态

public void Toggle()
{
    _wantVisible = !_wantVisible;
    ApplyIntent();
}

private void ApplyIntent()
{
    if (_wantVisible)
    {
        if (_windowShown) AnimateToShown();   // 已在屏幕上（可能正在收起）→ 反向滑回
        else ShowFromHidden();                // 完全隐藏 → 从屏幕外滑入
    }
    else
    {
        if (_windowShown) AnimateToHidden();
    }
}
```

关键点：

1. **点击只改意图，不判断当前在做什么。** 动画跑到一半也照样翻转，反向由动画器从窗口真实位置接着走。
2. **起点由动画器自己取**（`GetWindowRect`），不由调用方传入。这样反向是平滑折返，不会跳。
3. 回调里要校验意图是否已变（`if (!_wantVisible) return;`），否则一次「展开→收起」之后，展开的完成回调还会去改尺寸。

### 验证

连点 6 次、间隔 45ms：

```
toggle -> want=True   shown=False sliding=False
toggle -> want=False  shown=True  sliding=True
toggle -> want=True   shown=True  sliding=True
toggle -> want=False  shown=True  sliding=True
toggle -> want=True   shown=True  sliding=True
toggle -> want=False  shown=True  sliding=True
slide done frames=20 161ms/160ms
```

六次全部被处理，最终状态正确，动画只跑完最后一次。

---

## 三、帧输出：上限 120fps，中间帧异步

### 现象

展开动画掉帧，看起来一顿一顿；高刷新率屏幕上尤其明显。

### 根因一：按 `DwmFlush` 逐帧跑

```csharp
while (true) {
    ApplyFrame(...);
    if (DwmFlush() != 0) Thread.Sleep(2);   // ← 200Hz 屏上每 5ms 返回一次
}
```

200Hz 屏幕上这等于每秒两百多次窗口位移。Mica/亚克力的背景采样要跟着重算两百多次，开销比省下的那点位移精度大得多，帧率反而掉下来。

**改成固定 120fps 出帧。** 再高对窗口位移没有可感知收益。

### 根因二：同步 `SetWindowPos`

同步的 `SetWindowPos` 会 `SendMessage` 到窗口所属线程。UI 线程只要正在排版（展开首帧正是最忙的时候），动画线程就跟着一起堵住。

**中间帧一律带 `SWP_ASYNCWINDOWPOS`**，只有末帧同步：

```csharp
private void ApplyFrame(RectInt32 rect, bool async, bool sizeFixed)
{
    uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
    if (async) flags |= SWP_ASYNCWINDOWPOS;
    if (sizeFixed) flags |= SWP_NOSIZE;
    SetWindowPos(_hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, flags);
}
```

末帧必须同步：异步队列里可能还有没应用的帧，同步调用排在它们之后才能保证最终位置精确。

### 根因三：每帧都声明尺寸

纯位移的动画（展开/收起）尺寸不变，但每次都传 `cx/cy` 仍会让窗口走一遍 size 变更流程——客户区重排、交换链跟着检查，纯属白做。整段动画尺寸不变时加 `SWP_NOSIZE`。

### 位置用插值，不用累加

```csharp
double progress = clock.Elapsed.TotalMilliseconds / effective;
double eased = easeIn ? EaseInQuad(progress) : EaseOutCubic(progress);
int y = (int)Math.Round(from.Y + dy * eased);
```

不要写成「每帧加固定步长」。某一帧因为系统忙而迟到时，插值会让下一帧自动补上；累加会让整段动画变慢，而且总时长不确定。

### 落后时不要空转

```csharp
double wait = nextDeadline - clock.Elapsed.TotalMilliseconds;
if (wait > 1.0) Thread.Sleep((int)Math.Round(wait));
else Thread.Sleep(1);   // 落后了也要让出时间片
```

`else` 分支不能省。省掉之后，落后于帧计划时会空转，把后面的帧全部挤在一起，动画看起来是「前半段卡、后半段瞬移」。

---

## 四、时长与缓动：数值怎么定的

### 时长

| 用途 | 值 | 理由 |
|---|---|---|
| 展开 | 260ms | 180ms 在 200Hz 屏上只有十几帧，眼睛跟不上，观感是「闪一下出现」；300ms 以上连续点几次会觉得拖沓 |
| 收起 | 220ms | 用户已经做出「关掉」的决定，拖太久会想再点一次 |
| 高度修正 | 240ms | 与展开同量级，避免看起来是另一段动作 |
| 拖拽回弹 | 160ms | 短距离，需要更快收尾 |
| 打断后最短 | 140ms | 见下 |

### 打断后的时长缩短

反向点击时剩余行程可能只有满程的一小段，按比例缩短时长才跟手。但有两个坑：

**坑一：基准取错。** 面板是竖向滑动的，宽度通常比高度大（540px vs 371px）。如果基准用 `max(宽,高)`，每次正常展开都会被判成「只走了一半」，时长被压到一半以下，动画快得像瞬移。

```csharp
// 正确：取主轴上的满程
bool vertical = Math.Abs(dy) >= Math.Abs(dx);
double reference = vertical ? to.Height + Overshoot : to.Width + Overshoot;
double distance  = vertical ? Math.Abs(dy) : Math.Abs(dx);
double ratio     = reference > 0 ? Math.Min(1.0, distance / reference) : 1.0;
int effective = Math.Clamp((int)Math.Round(durationMs * ratio), MinDurationMs, durationMs);
```

**坑二：下限太低。** 剩余行程很短时如果压到 80ms 以下，人眼会看到「跳」而不是「滑」——短位移配短时长反而更像瞬移，因为连减速段都来不及展示。140ms 能保证至少走完一个可辨识的缓动。

### 缓动曲线

| 方向 | 曲线 | 峰值速度 |
|---|---|---|
| 展开 | `EaseOutCubic`：`1 - (1-t)³` | 平均速度的 3 倍 |
| 收起 | `EaseInQuad`：`t²` | 末段加速离开 |

**不要用 `EaseOutQuint`（5 次方）。** 它在短时长下的观感是「瞬间弹上去、再慢慢爬完剩下的」：起步 20% 的时间里已经走完 67% 的行程，眼睛只看到一次跳变。3 次方起步同样柔和，但前段不再那么极端，整段位移能被看清。这也是 Windows 自身进场动画的量级。

### 尊重系统设置

自绘动画不受系统自动接管。必须在每次动画前读「辅助功能 → 视觉效果 → 动画效果」：

```csharp
// SPI_GETCLIENTAREAANIMATION (0x1042) 和 SPI_GETANIMATION (0x0048)
// 任一为「关」就不播动画，直接落位
public static int Scale(int durationMs) => Enabled ? durationMs : 0;
```

返回 0 时动画器必须真的不做动画（同步落位），而不是给一个最短时长。带 5 秒缓存，避免每次展开都读注册表。

---

## 五、冷却后首次展开卡顿：工作集回收的代价

### 现象

面板收起后放置一段时间，再点开明显卡一下；但连续开合就很流畅。

### 根因

日志里的证据：

```
working set trimmed (flyout hidden): 72.8 MB -> 1.1 MB
```

每次收起都把 70MB 交还系统。再展开时，那批代码页、D3D 资源、字形位图全都要从页文件读回来——几十兆软缺页正好压在 UI 线程上。展开一次之后这些页已经在内存里了，所以「再收起再展开就很流畅」。

这解释了全部观察到的现象：**不是性能不够，是自己制造了冷启动。**

### 修复一：只在内存真的紧张时回收

```csharp
// GlobalMemoryStatusEx 的 ullAvailPhys，不是 GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
// （后者是物理内存总量，不是可用量，判断永远是「充足」）
private static bool MemoryPressure()
{
    long available = AvailableBytes();
    return available >= 0 && available < 3L * 1024 * 1024 * 1024;
}
```

物理内存充裕时回收纯粹是自残。阈值 3GB：低于这个量系统本身就开始感到压力，这时候交还几十兆才有意义。

### 修复二：收起后延迟排队，不立刻回收

```csharp
private const int PostHideDelayMs = 45_000;
```

收起往往只是临时看一眼，几秒后又要打开。延迟 45 秒再动手，期间任何界面重新打开都会取消（`BeginActivity` 里调 `CancelScheduledTrim`）。

### 修复三：`EmptyWorkingSet` 必须在锁外调用

它要遍历整个进程页表，工作集大时能跑几十毫秒。持锁期间 UI 线程上的 `BeginActivity`（用户点开面板的第一件事）会一起等锁，表现就是「点了没反应，过一会儿才弹出来」。

```csharp
public static bool TryTrim(string reason, bool force = false)
{
    lock (Gate) {
        if (!force) {
            if (HasRecentActivity()) return false;
            if (Within(_lastTrimMs, MinimumIntervalMs)) return false;
        }
        _lastTrimMs = NowMs;
    }                          // ← 锁在这里就放掉

    EmptyWorkingSet(GetCurrentProcess());   // 锁外执行
}
```

### 修复四：间隔从 20 秒提到 60 秒

间隔太小，连续开合会反复丢弃刚读回来的页，每次展开都要重新缺页。

---

## 六、预热：把冷启动代价提前付掉

即使不回收工作集，第一次展开也要建交换链、编译着色器、光栅化全部字形。这些在「用户还没点」的空闲时刻先做完，展开时就是热的。

### 做法：屏幕外渲染一帧

```csharp
RectInt32 offscreen = _targetRect;
offscreen.X = -32000;
offscreen.Y = -32000;

_appWindow.MoveAndResize(offscreen);
_appWindow.Show();          // 必须真的 Show
Panel.UpdateLayout();
// 顺便量出内容高度
```

**必须 `Show`，不能用隐藏状态。** 隐藏的窗口不提交帧，XAML 也就不会真正生成渲染目标和字形缓存，预热等于没做。

预热完成后立刻隐藏并摆回原位。要记录当前前台窗口，万一系统把预热窗口激活了，事后还原——否则用户正在打字的窗口会失焦。

### 何时预热

| 时机 | 原因 |
|---|---|
| 启动后（显示器探测完成之后） | 首次点击不必付创建 + 首帧代价 |
| 主窗口显示时 | 用户短期不会再点面板，正好做 |
| 显示器列表变化时 | 卡片数量变了，缓存高度要重算 |
| 距上次超过 5 分钟 | 期间工作集可能被系统换出 |

### 一个必须避开的坑

预热时如果显示器还没探测到，面板是空的，测出来的高度只有「未检测到显示器」那行提示那么高。拿它当缓存会让首次展开先滑出一个小窗口、再被撑开。

```csharp
if (!Panel.HasDisplays) return;   // 空面板不预热、不记高度
```

---

## 七、布局测量：先滑、后量

### 旧路径的问题

展开首帧原本要依次完成：刷新全部卡片（含 HDR / 显示模式 / 缩放的 Win32 探测）→ 全量排版 → 逐项测量 → 落位 → 开动画。

工作集被回收过之后，这些调用还要额外从页文件读回代码页和资源。冷启动那一两秒就是这么来的。

### 新路径

```csharp
// 1. 按上次的高度立刻开滑
_slider.Animate(_targetRect, ShowDurationMs, easeIn: false, OnShownSettled);
RestartAutoHideTimer();

// 2. 重活排到当前消息之后
_ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, PrepareContent);
```

`PrepareContent` 负责刷新数据、重新排版、测量高度。高度真变了才走修正动画，否则不惊动窗口。

### 高度上报的时机

窗口未显示时也要记录高度，否则下次展开会按预热时那个「控制项还没到」的矮高度滑进来。控制项数据是异步到达的，卡片长高后高度会上报好几次。

```csharp
if (!_windowShown)
{
    if (!Panel.HasDisplays) return;          // 空面板的高度不可信
    _heightDip = (int)Math.Ceiling(Math.Max(heightDip, MinHeightDip));
    _targetRect = DpiLayout.ComputeBottomRight(...);
    return;
}
```

---

## 八、其余性能热点

这几项单个影响不大，但都发生在 UI 线程上、且都在展开路径里，叠加起来就是「展开首帧掉帧」。

### 1. `PlaceBelowTaskbar` 每次都 `EnumWindows`

```csharp
private static readonly Dictionary<IntPtr, IntPtr> TaskbarCache = new();

// 按显示器缓存 HWND，用 IsWindow 判失效
// 任务栏只在显示器热插拔或 Explorer 重启时换 HWND
```

系统上窗口一多，全量遍历就是 5–30ms。而展开、收起、拖动、改高度每一条路径都会调到这里。

### 2. 亮度变化触发整面板重建

拖动滑块时 `BrightnessHub.Changed` 每帧触发一次 `Refresh()`，而 `Refresh` 会走 `SyncDisplays` → `item.Update()`，后者要探测 HDR / 显示模式 / 缩放，再发二十多个 `PropertyChanged`。

**拆出 `RefreshLevels()`**，只更新滑块数值和组卡片平均值。亮度变化本身只影响这两个数字。

### 3. `RebuildExtras()` 无条件清空重建

```csharp
MiniSliders.Clear();   // ItemsControl 丢弃全部容器
MiniChoices.Clear();
MiniActions.Clear();   // 重新实例化模板 → 重建 Slider/ComboBox → 整棵子树重新排版
```

加结构指纹短路：勾选项 + 控件形态 + HDR 状态没变时只同步数值，不动集合。

```csharp
string signature = BuildExtrasSignature();
if (signature == _extrasSignature && populated) { SyncExtraValues(); return; }
```

### 4. 卡片入场动画与窗口滑入串行

旧实现是窗口滑完（180ms）才解锁卡片，卡片再各自淡入 240ms，末尾几张还要等 45ms×N 的错峰，总时长接近 600ms。

**改成窗口一显示就解锁**，两者叠加成一段约 300ms 的连贯动作。

### 5. 亮度值未变时跳过硬件写入

值没变就不下发，看起来是优化，实际是 bug 源：程序启动后硬件状态未必等于记忆值（上次退出时写入可能失败、显示器可能被实体按键改过、HDR 切换后通道也变了）。这时用户第一次拖回记忆值所在的位置，会被判定为「没变化」而不下发，表现为「前几次调节不生效」。

收敛器本身有去重，多写一次不会有额外的硬件压力。

---

## 九、Momomi 迷你面板的对照

Momomi 的 `MiniWindow` / `WindowSlider` / `WindowChrome` / `WorkingSetTrimmer` 与 BetterScene 同源，结构与问题一一对应。

### 已经正确的部分

- 收起后关闭、下次重建，避免了「隐藏但不销毁」的引用泄漏
- `WindowChrome` 的 `SubclassProcs` 按窗口句柄各存一份委托，避免新窗口覆盖旧委托后原生回调打到已释放的 thunk
- `WindowSlider.Dispose` 的幂等处理，以及 Join 超时时不释放信号量
- 启动后延迟预热迷你窗口（`WarmupMiniWindowAsync`）
- 收起后关闭、`Dismissed` 事件让宿主置空引用

### 需要按本文修正的部分

| 问题 | 对应章节 |
|---|---|
| 四态枚举导致动画中点击丢失 | 二 |
| 动画起点由调用方传入，反向会跳 | 二 |
| 按 `DwmFlush` 逐帧跑，高刷屏掉帧 | 三 |
| 中间帧同步 `SetWindowPos`，UI 排版时堵住动画线程 | 三 |
| 每帧都声明尺寸 | 三 |
| `WorkingSetTrimmer` 无条件回收 + 立即回收 | 五 |
| `EmptyWorkingSet` 在锁内调用 | 五 |
| `FindTaskbarForWindow` 每次都 `EnumWindows` | 八.1 |
| 收起后立刻 `TryTrim` | 五 |

### Momomi 特有的注意点

- **`_statsTimer`（3 秒轮询 `LoadAsync`）** 会在面板隐藏时也跑，必须在收起时停、展开时启。它已经在 `Hide()` / `Show()` 里处理了，但打断路径（反向点击）要保证不会留下未停止的定时器。
- **`RefreshOnShowAsync` 的网络加载**必须排在动画之后，不能和动画起步叠加。已经在 `OnShowCompleted` 里做了，改动状态机时要保留这个顺序。
- **托盘点击入口有两条**（`ToggleMiniRequested` 和热键），两条都要经过同一个意图状态机，不能各自维护可见性判断。

---

## 十、检查清单

改动这套代码后，按下面逐项确认：

- [ ] 托盘单击 → 切换 1 次；双击 → 切换 2 次；三击 → 切换 3 次
- [ ] 动画途中反向点击，窗口平滑折返，不跳
- [ ] 连点 6 次（间隔 45ms），最终状态与点击次数奇偶一致
- [ ] 展开动画在 200Hz 屏上不掉帧（日志确认 ~120fps）
- [ ] 面板收起后放置 5 分钟再点开，不卡
- [ ] 首次展开（刚启动）与之后的展开手感一致
- [ ] 系统关闭动画效果后，面板直接出现，不滑动
- [ ] 显示器热插拔后展开，高度正确、不被裁切
- [ ] 工作集日志里，物理内存充裕时不再出现 `trimmed (flyout hidden)`
