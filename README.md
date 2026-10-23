<h1 align="center">
  <img src="src/BetterScene.App/Assets/AppIcon.png" width="96" alt="BetterScene" />
  <br />
  Windows 视觉改善
</h1>

> 把「系统明暗」和「显示器亮度」合成一个场景，一键或定时切换。

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?logo=windows&logoColor=white" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET" />
  <img src="https://img.shields.io/badge/WinUI-3-0067C0" alt="WinUI 3" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License" />
  <img src="https://img.shields.io/github/v/release/iwvw/BetterScene" alt="Release" />
</p>

---

## 这是什么

Windows 自带的「夜间模式」只管色温，管不了亮度；显示器自带的 OSD 菜单要按实体按键，一次只能调一台。想要「晚上把屏幕调暗、顺便切深色」这件事，在 Windows 上要操作三四个地方。

BetterScene 把它收拢成一次操作：**一个场景 = 每台显示器的亮度 + 系统深浅色 + 壁纸**。点一下、按个热键、或者到点自动切。

## 功能特性

- **逐屏亮度控制** — 每台显示器独立调节。外接屏走 DDC/CI 硬件调光，笔记本内屏走 WMI 背光，都不支持时用 gamma 兜底。
- **场景一键切换** — 明暗两套配置，各自记住亮度和壁纸。托盘、热键、迷你面板都能触发。
- **迷你面板** — 点托盘图标弹出的小面板，只放常用的亮度滑块。每台显示器显示哪些控制项可以单独勾选。
- **按时间或系统联动自动切** — 规则支持固定时刻（如每天 19:00）、当地日出、当地日落、以及 Windows 夜间模式开关时触发。多条规则按时间先后自然覆盖。
- **触发条件过滤** — GPU 占用高、指定程序运行中、全屏播放、用户空闲时可以选择不切，避免打断游戏和观影。
- **HDR 感知** — 开启 HDR 的屏幕自动换用合适的调光通道：外接屏的背光在 HDR 下被固件锁死，改用 gamma；笔记本内屏的 WMI 背光不受影响，继续走真实背光。
- **显示器细节可调** — 分辨率、刷新率、缩放、HDR 开关都在显示器页；音量、色温、输入源这些 DDC 控制项在「控制项探测」里读写。
- **常驻内存个位数** — 界面收起后主动把工作集交还系统，托盘常驻时通常只有几 MB。
- **原生体验** — WinUI 3 + Fluent 控件，亚克力 / Mica / 纯色三种材质，深浅色跟随场景。

## 快速开始

### 下载安装

从 [Releases](https://github.com/iwvw/BetterScene/releases/latest) 下载，按需选择：

| 文件 | 说明 |
|---|---|
| `BetterScene-x.y.z-x64-merged-setup.exe` | **推荐**。自带运行时，双击安装即可 |
| `BetterScene-x.y.z-x64-split-setup.exe` | 体积小，需要系统已装 .NET 10 运行时 |
| `*-portable.zip` | 免安装，解压后运行 `BetterScene.exe` |

ARM64 设备选对应架构的包。

### 首次使用

1. 启动后程序常驻托盘。**左键**托盘图标弹出迷你面板，**右键**是菜单：打开主界面、切换明暗、暂停自动切换、设置、退出。
2. 进「场景」页，给浅色和深色各挑一张壁纸。
3. 进「显示器」页，确认每台屏都被识别到、亮度能正常调节。
4. 需要的话在「自动切换」页配置规则。

### 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download) 和 Windows 10 1809 以上。

```powershell
git clone https://github.com/iwvw/BetterScene.git
cd BetterScene
dotnet build BetterScene.slnx -p:Platform=x64
```

产物在 `src/BetterScene.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/BetterScene.exe`。

也可以直接用仓库里的脚本：

```powershell
.\run.ps1      # 编译并启动
.\dev.ps1      # 开发循环
```

## 使用示例

### 用热键切换场景

默认 `Ctrl + Alt + D` 切换明暗，`Ctrl + Alt + B` 呼出迷你面板，「输入」页里可以改键或整体关掉快捷键。

### 用滚轮调亮度

默认在**任务栏**上滚动鼠标滚轮即可调当前屏幕的亮度，每次 5%。生效区域可以改成桌面或任意位置，也能加修饰键避免误触，都在「输入」页设置。

### 迷你面板按屏配置

点托盘图标打开迷你面板，每台显示器默认只显示亮度滑块。想加分辨率、刷新率、缩放或 HDR，进「显示器」页对应屏幕的「配置…」勾选——**配置是按屏独立的**，不会互相影响。

### 关联模式

开启「关联显示器」后，所有屏幕的亮度一起调。迷你面板会显示一个组卡片，拖它等于同时拖所有屏。音量也会在这个卡片上出现（前提是某台屏的配置里勾过音量）。

## 技术栈

| 类别 | 使用 |
|---|---|
| 运行时 | .NET 10 |
| UI | WinUI 3（Windows App SDK）、CommunityToolkit.WinUI |
| 亮度 | DDC/CI（`dxva2.dll`）、WMI（`root\WMI`）、gamma ramp |
| 显示配置 | CCD API（`DisplayConfigGetDeviceInfo` / `SetDeviceInfo`） |
| 托盘 | H.NotifyIcon.WinUI |
| 窗口 | WinUIEx |

## 项目结构

```
BetterScene.slnx
├─ src/BetterScene.App/        WinUI 3 主界面、迷你面板、托盘
├─ src/BetterScene.Core/       共享逻辑：设置、主题、日志、显示器控制
├─ src/BetterScene.Displays/   显示器工作者（独立进程）
├─ src/BetterScene.Host/       常驻核心（当前为占位骨架，未启用）
├─ tests/BetterScene.Core.Tests/
└─ build/                      打包与资源生成脚本
```

### 为什么显示器操作要单独一个进程

DDC/CI 的 capabilities 读取在部分显示器上会把进程挂死，业界有过整机失去响应的案例。BetterScene 把全部 DDC 调用关在 `BetterScene.Displays` 这个独立进程里，并用哨兵文件记录操作阶段——如果上次探测中途崩溃，下次启动会把这台显示器降级为快速探测，避免反复踩同一个坑。降级记录 7 天后自动过期，给偶发问题一次重试机会。

## 已知限制

- **DDC 写入受显示器固件影响**：少数型号在 HDR 下会锁死背光，此时改用 gamma 调光，效果是画面变暗而非背光变暗。
- **缩放修改需要重新登录**：Windows 的 DPI 缩放在登录时读取，改完要注销重进才完全生效。
- **内屏没有 DDC**：笔记本内屏基本都不支持 DDC/CI，走 WMI 背光；部分机型连 WMI 也被厂商驱动占用。
- **单实例**：同一时间只能运行一个实例，重复启动会激活已有窗口。

## 贡献指南

欢迎提 Issue 反馈问题，尤其是**特定显示器的兼容性**——不同厂商的 DDC 实现差异很大，你的实测数据对改进很有价值。

提 Issue 时建议附上：

- 显示器型号（EDID 里的型号代码，如 `HWV6246`）
- 连接方式（DP / HDMI / eDP / Type-C）
- 日志：`%LOCALAPPDATA%\BetterScene\app.log` 与 `displays.log`

提交代码前请跑一遍测试：

```powershell
dotnet test tests/BetterScene.Core.Tests/BetterScene.Core.Tests.csproj -p:Platform=x64
```

## 许可证

[MIT](LICENSE) © DSUK

---

<p align="center">
  <sub>感谢并认可 <a href="https://linux.do/">LINUX DO</a> 社区</sub>
</p>
