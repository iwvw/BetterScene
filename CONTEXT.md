# CONTEXT.md

领域语言与项目心智模型。让所有 Agent 用统一术语沟通。

## 项目一句话

BetterScene：Windows 原生屏幕场景管理工具。把「系统明暗」和「显示器亮度」合成一个场景，一键或定时切换。

## 范围（V1）

### 做

- 每台显示器的**亮度**控制（硬件 DDC/CI、内屏 WMI、硬件不支持时 gamma 兜底）
- **系统深浅色**切换（`AppsUseLightTheme` / `SystemUsesLightTheme`）
- **壁纸**切换（明/暗各一张，全局单张）
- 托盘 + 迷你面板 + 主窗口设置界面，全部 WinUI 3 原生控件
- 快捷键、开机自启、单次场景切换

### 不做（明确排除，不要写入代码）

- 色温 / 夜灯接管（CloudStore / Bond 编解码）
- 强调色、光标、触摸键盘、灰度滤镜
- 对比度、音量、输入源、显示器电源（DDC 次要 VCP）
- 自动调度（日出日落 / 定时 / 传感器）—— V2
- 按应用的 profile —— V2

## 架构心智模型

```
┌────────────────────────────────────────────────────┐
│ BetterScene.App       UI 进程（WinUI 3）            │
│   主窗口（NavigationView）+ 迷你面板 + 托盘           │
├────────────────────────────────────────────────────┤
│ BetterScene.Host      常驻核心（隐藏进程）            │
│   调度器 · 明暗切换 · 主题引擎 COM · 管道服务端        │
├────────────────────────────────────────────────────┤
│ BetterScene.Displays  显示器工作者（隔离进程）        │
│   DDC/CI · WMI 内屏 · gamma 兜底 · 哨兵文件           │
└────────────────────────────────────────────────────┘
```

三进程的原因：

- **Displays 独立**：DDC 的 capabilities 读取与探测有把进程乃至整机挂死的实际案例（Twinkle Tray 的事故记录）。隔离进程 + 哨兵文件是硬性要求。
- **Host 独立**：托盘在主窗口关闭后必须存活；主题切换的 COM 调用耗时数百毫秒到数秒，不能阻塞 UI。

## 术语表

| 术语 | 含义 |
|---|---|
| 场景（Scene） | 一组可一次性下发的目标状态：亮度（每显示器）+ 系统明暗 + 壁纸 |
| 浅色组 / 深色组 | V1 的两个内置场景，对应浅色主题与深色主题 |
| 显示目标（DisplayTarget） | 一台可控制的显示器，持有稳定 ID、后端类型、能力描述 |
| 稳定 ID | `DISPLAY_DEVICE.DeviceID` 去掉接口 GUID 段，形如 `\\?\DISPLAY#DEL41D9#5&abcd&0&UID4352`，硬件+端口级稳定 |
| 后端（Backend） | 亮度控制通路：`Ddc` / `Wmi` / `Gamma` |
| Fast 扫描 | 只试 3 个常见 VCP 码（`0x02`/`0xDF`/`0x10`）判定可用性，秒出结果 |
| Accurate 扫描 | 读 capabilities 字符串解析完整能力集，**有挂死风险**，后台做 |
| 哨兵文件（Sentinel） | `ddc-probe.lock`：DDC 操作前写入、正常结束删除；启动时发现残留即判定上次崩溃，把该显示器永久降级为 Fast |
| 双段节流（Leading + Trailing） | 每显示器独立计时：立即写一次，窗口内重复请求合并成最后一个值，到窗口末尾再写一次 |
| 受管主题（Managed Theme） | 程序自己维护的 `.theme` 文件，`IThemeManager2` 一次性应用 |
| `IThemeManager2` | 未文档化 COM（CLSID `9324da94-50ec-4a14-a770-e90ca03e7c8f`），主题应用的主路径 |
| 降级路径 | `IThemeManager2` 失败时：直接写两个 `Personalize` 键 + 广播 |
| 激进的 DWM 刷新 | 广播无效时：把强调色最低位 ±1 → 应用临时主题 → 等 1s → 应用正题 |
| `IgnoreBackground` | `ThemeApplyFlags` 之一；不涉及壁纸的切换必须带上，否则壁纸会重新加载而闪烁 |

## 关键约束（硬性）

1. **不提权**。全程 HKCU + 用户级主题文件 + 用户级命名管道。manifest 里不写 `requestedExecutionLevel`。开机自启走 `HKCU\...\Run`。
2. **DDC 调用不得在主进程**。全部在 `BetterScene.Displays` 内，且该进程内也是串行队列。
3. **不得阻塞 UI 线程**。主题 COM 调用放专用 STA 线程；显示器读写全异步。
4. **熄屏时不写 DDC**。显示器真正关闭（会话不在 Present）时拒绝任何 DDC 写入，否则可能导致唤醒失败。
5. **广播必须用 `SendMessageTimeoutW`**，禁止 `SendMessage` / `PostMessage`。挂起窗口会卡死整个切换。
6. **不搬 GPL 代码**。`Windows-Auto-Night-Mode` 是 GPL-3.0，只能参考协议与思路；`twinkle-tray` 是 MIT，可参考实现。
7. **不写注释**（除少数必要的平台行为说明）。

## 关键数值（设计目标）

| 指标 | 目标 |
|---|---|
| 冷启动到托盘可见 | < 1.5s |
| 托盘左键到面板可见 | < 150ms（窗口常驻 + cloak） |
| 亮度滑杆响应 | UI 立即跟随，写入按双段节流 |
| 明暗切换总耗时 | < 3s（含 DWM 刷新兜底） |
| 常驻内存 | Host + Displays < 120MB，App 隐藏时 < 150MB |
| DDC 最小写入间隔 | 默认 500ms，可选 100–2000ms |

## 项目结构

```
BetterScene.slnx
├─ src/BetterScene.App/        WinUI 3 UI 进程
├─ src/BetterScene.Host/       常驻核心进程
├─ src/BetterScene.Displays/   显示器工作者进程
├─ src/BetterScene.Core/       共享：协议、配置、日志、注册表、主题文件
└─ build/                      打包脚本
```

## 参考来源

- `E:\Code\momomi` — 托盘、迷你面板、窗口外壳、主题跟随（本产品直接复用）
- `E:\Code\sox` — 材质引擎、卡片、热键、单实例、自更新
- `E:\Code\参考\twinkle-tray` — DDC/CI 全部实现细节（MIT）
- `E:\Code\参考\Windows-Auto-Night-Mode` — 明暗切换协议与流水线（**GPL，仅参考思路**）
