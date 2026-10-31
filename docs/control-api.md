# 外部控制接口

让脚本和其他程序调用 BetterScene 的能力：设亮度、切刷新率、换场景。

接口是**一层薄壳**，底层仍然是程序自己在用的那套代码。所以从外部设的亮度和
在界面上拖滑块的效果完全一致（HDR 下的 gamma 兜底、亮度范围夹取、
写入合并、主题文件与壁纸一起应用，都在里面）。

---

## 一、怎么调

两种方式，行为完全一致：

```powershell
# 1. 独立命令行程序（推荐，脚本里用它）
bsctl state
bsctl brightness set --value 35
bsctl display refresh set --display HWV6246 --hz 60

# 2. 主程序自带的子命令（不用额外文件，但输出要挂控制台）
BetterScene.exe --ctl state
```

两者都支持 `--json` 输出，方便程序解析：

```powershell
bsctl --json state | ConvertFrom-Json
```

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 命令执行失败（stderr 会打印错误码与原因） |
| 2 | 参数错误 |
| 3 | 连不上程序（没启动，或在忙） |

脚本判定只看退出码就够了：

```powershell
if ((bsctl display refresh set --hz 60) -and $?) { ... }
```

---

## 二、选择显示器

`--display` 接受这几种写法，按顺序尝试匹配：

| 写法 | 例子 | 说明 |
|---|---|---|
| 省略或 `*` | | 所有可控显示器 |
| `primary` | | 主屏 |
| 固件 ID | `HWV6246` | EDID 里的 PnP ID，最常用 |
| GDI 名称 | `\\.\DISPLAY1` | |
| 型号 | `QSN-CBA` | EDID 型号串 |
| 完整 Id | `\\?\DISPLAY#HWV6246#5&...` | 稳定 ID，最精确 |

**匹配到多台会报错**，不会随便挑一台。静默选错屏幕比失败更难排查。

固件 ID 和型号从 `bsctl state` 里看。

---

## 三、命令

### 查询

```powershell
bsctl state                    # 完整状态：每台屏的亮度/范围/模式/缩放/HDR
bsctl display list             # 精简列表
bsctl display refresh get      # 当前刷新率
bsctl display mode list        # 该显示器支持的所有分辨率与刷新率
bsctl brightness get           # 当前亮度（设置里的意图值）
bsctl brightness read          # 当前亮度（硬件实际值，可与意图值比对）
bsctl display vcp get          # 扩展控制项的硬件实际值
bsctl scene get                # 当前系统明暗
bsctl version                  # 版本与协议号
bsctl caps                     # 本接口支持的命令清单
```

`bsctl state` 的输出：

```
版本 0.2.10 · 协议 v1
明暗 dark · 关联亮度 关

HUAWEI QSN-CBA  [\\?\DISPLAY#HWV6246#5&37d6328&0&UID4352]  (主屏)
  亮度 78%  通道 ddc  范围 0–100%
  模式 2560×1440 @ 165 Hz  缩放 125%  HDR 关

BOE NE153QDM-NZ1  [\\?\DISPLAY#BOE0C8E#5&37d6328&0&UID4357]  内屏
  亮度 78%  通道 wmi  范围 0–100%
  模式 2560×1600 @ 240 Hz  缩放 150%  HDR 关
```

### 亮度

```powershell
bsctl brightness set --value 35                    # 所有屏
bsctl brightness set --display HWV6246 --value 35  # 指定屏
bsctl brightness step --delta -5                   # 相对调节
bsctl brightness get                               # 设置里的意图值
bsctl brightness read                              # 当场读硬件实际值
```

`value` 必须在 0–100 之间，越界直接报错而不是静默夹取。

**`brightness get` 和 `brightness read` 的区别很重要：**

| 命令 | 读什么 | 写入失败时会怎样 |
|---|---|---|
| `brightness get` | 设置里记的意图值 | 仍然显示你设的值，察觉不到问题 |
| `brightness read` | 当场从硬件读回 | 立刻看出和意图值不一致 |

```
$ bsctl brightness get
HUAWEI QSN-CBA  亮度 90%

$ bsctl brightness read
HUAWEI QSN-CBA  意图 90%  硬件 25%  ← 不一致，写入没有落到硬件
  通道 ddc · VCP 0x10 · 原始值 25/100
```

排查「调了没反应」「亮度回弹」这类问题时用 `brightness read`。
判定容差是 1 个百分点（DDC 的百分比是四舍五入来的，差 1 属正常）。

内屏（笔记本自带屏）走的是电源方案的 `VIDEONORMALLEVEL`，不是 WMI 写入。
`brightness read` 读到的就是这个方案值，也就是系统设置滑块显示的那个值——
把它从外部改掉（比如 `powercfg /setacvalueindex`）也会被如实读出来：

```
$ bsctl brightness read --display BOE0C8E
BOE NE153QDM-NZ1  意图 70%  硬件 33%  ← 不一致，写入没有落到硬件
  通道 wmi · 原始值 33/100
```

关联亮度（设置里的「关联所有显示器」）开着时，指定单台屏也会同步到全部——
这是程序本来的语义，接口不绕过它。

### 刷新率

```powershell
bsctl display refresh set --hz 60                      # 所有屏
bsctl display refresh set --display HWV6246 --hz 60    # 指定屏
```

只改刷新率，**分辨率保持不变**。

目标刷新率不在该分辨率的可用档位里时会失败，并且把可用档位列出来：

```
apply_failed: HUAWEI QSN-CBA: 2560×1440 不支持 999 Hz，可用：200/165/144/120/100/60/59
```

注意读回值可能是 59 或 119 —— 那是 59.94Hz / 119.88Hz 的正常表现，
Windows 的 GDI 接口只报整数部分。

### 分辨率与刷新率一起改

```powershell
bsctl display mode set --display HWV6246 --width 2560 --height 1440 --hz 165
```

`--hz` 省略时取该分辨率下最高档。

### 缩放

```powershell
bsctl display scale set --display HWV6246 --percent 125
```

**需要重新登录才完全生效**，和系统设置里的行为一样。

### HDR

```powershell
bsctl display hdr set --display HWV6246 --on true
```

切换会让显示管线重建，黑屏 1–2 秒，不要连续调用。

### 任意 VCP 控制项

给输入源、对比度这类没有专用命令的项用：

```powershell
bsctl display vcp get --display HWV6246                  # 列出所有可读项
bsctl display vcp get --display HWV6246 --code 0x12      # 只读对比度
bsctl display vcp set --display HWV6246 --code 0x60 --value 0x0F
```

`--code` 和 `--value` 都接受十进制或 `0x` 十六进制。

`vcp get` 读的是**显示器自己报的硬件实际值**，不是设置里记的意图值。

注意它读的是**扩展控制项**列表，不含亮度（0x10）。亮度由专门的
亮度通道处理，用 `brightness read` 查——那条命令会走 DDC 的高电平
亮度分支、WMI 背光、gamma 三条通道，比通用 VCP 读更完整。

指定 `--code 0x10` 时会直接报出这一点：

```
apply_failed: HUAWEI QSN-CBA: 亮度不走通用 VCP，请用 brightness read（读硬件）或 brightness get（读意图）
```

`vcp set --code 0x10` 能写（它只是一条通用 VCP 写），但**不要用它调亮度**：
它绕过了意图值、写入合并和范围夹取，结果就是 `brightness read` 里那句
「意图 60% 硬件 40%」，设置和硬件从此对不上。

### 场景

```powershell
bsctl scene apply --theme dark    # 等主题真正落地再返回
bsctl scene apply --theme light --wait false   # 只发出请求
bsctl scene toggle
```

默认会等主题真正应用完才返回（最多 20 秒），否则脚本拿到成功时画面还没变，
后续动作可能抢在中间。

---

## 四、JSON 协议

如果想直接连管道（不用 bsctl），协议很简单：一行一条 JSON。

管道名：`\\.\pipe\BetterScene.Control.v1.<会话ID>`

会话 ID 是 Windows 会话号（`Process.GetCurrentProcess().SessionId`），
多用户登录/远程会话下各自的实例互不干扰。

```jsonc
// 请求
{"v":1,"id":"9f3c2a1b","cmd":"brightness.set","args":{"display":"*","value":35}}

// 成功
{"v":1,"id":"9f3c2a1b","ok":true,"result":{"applied":[{"id":"...","title":"HUAWEI QSN-CBA","value":35,"channel":35,"clamped":false}]}}

// 失败
{"v":1,"id":"9f3c2a1b","ok":false,"error":{"code":"display_not_found","message":"找不到显示器 NOSUCH。可用：..."}}
```

同一条连接可以连续发多条请求。`id` 原样回传，方便并发时对应。

### 错误码

| code | 含义 |
|---|---|
| `bad_request` | 参数缺失或非法 |
| `unknown_command` | 命令不存在 |
| `display_not_found` | 找不到指定的显示器 |
| `display_ambiguous` | 选择器匹配到多台 |
| `unsupported` | 该显示器不支持这个操作 |
| `apply_failed` | 系统拒绝了操作（详情在 message） |
| `timeout` | 命令超时 |
| `internal` | 程序内部错误 |

### 部分失败

批量操作（`--display *`）里部分成功时，退出码是 0，`failures` 里列出没成的：

```json
{
  "applied": [{"id": "...", "title": "HUAWEI QSN-CBA", "refreshRate": 60}],
  "failures": [{"id": "...", "title": "BOE NE153QDM-NZ1", "reason": "读不到当前显示模式"}]
}
```

**全部失败**才返回错误。这条规则让脚本的判定简单：成功就说明至少有一项生效。

---

## 五、用脚本做场景

`scripts/power-scene.ps1` 是一个完整例子：拔电切 60Hz + 35% 亮度，插电还原。

关键是**还原用快照而不是写死数值**——拔电前你可能是 78% / 165Hz，
插电后应该回到 78% / 165Hz，而不是被顶到 100% / 240Hz。

```powershell
# 手动跑一次（按当前电源状态决定进入还是退出）
pwsh -File scripts\power-scene.ps1 -Once

# 注册成登录时自启的常驻监听
pwsh -File scripts\power-scene.ps1 -Install

# 移除
pwsh -File scripts\power-scene.ps1 -Uninstall
```

脚本的核心只有三行：

```powershell
$state = bsctl --json state | ConvertFrom-Json    # 记快照
bsctl display refresh set --display * --hz 60     # 进入
bsctl brightness set --display * --value 35
```

退出时按快照逐项写回。

### 其他常见场景

```powershell
# 全屏游戏时提高刷新率、拉满亮度
bsctl display refresh set --hz 240
bsctl brightness set --value 100

# 夜间降低亮度
bsctl brightness set --value 25

# 接上外接屏后切深色
bsctl scene apply --theme dark
```

---

## 六、实现说明

### 为什么用命名管道

不占端口、不触发防火墙弹窗、能按用户 SID 收 ACL，而且项目里已经在用
（显示器工作进程、单实例激活）。

**没有复用单实例激活管道**：那条是 fire-and-forget，收到的 payload
直接丢弃、只负责把主窗口叫出来。外部调用必须拿到返回值。

### 线程模型

所有命令都在 UI 线程上执行（`DispatcherQueue`）。它们碰的是
`BrightnessHub`、`App.Settings`、`MainWindow` 这些 UI 线程状态，
换到别的线程反而要加一堆锁。

慢的调用（枚举显示模式要逐个刷新率做 CDS_TEST、切分辨率要等系统重协商）
是"等"而不是"算"，等的时候不占线程。

一次只处理一个连接：外部脚本是「发一条等一条」的，并发接入没有收益，
反而让 DDC 写入互相插队。

### 分发

`bsctl` 跟随主程序一起打包（便携版和安装版都有）。

它按**和主程序相同的 SelfContained 设置**构建，不能固定成框架依赖：
合并版目录里带的是自包含运行时，此时 bsctl 若声明"要全局框架"，
hostfxr 会去系统里找——机器上只有 x86 那套运行时，就会报
`No frameworks were found` 直接起不来。这和显示器工作进程当年踩的是同一个坑。

### 安全边界

BetterScene 不请求提权（`app.manifest` 里没有 `requestedExecutionLevel`），
控制接口能做的全部是当前用户权限内的事：调亮度、改显示模式、写 HKCU。

没有、也不会有任何「执行任意命令」的能力。

---

## 七、已知限制

- **`bsctl` 无法在程序未运行时工作**。它只是客户端，所有操作都要主程序在线。
- **管道按会话隔离**。从另一个登录会话（比如远程桌面）调用，
  连的是那个会话里的实例；那个会话没有实例就连不上。
- **枚举显示模式较慢**。`display mode list` 要对每个候选刷新率做一次
  CDS_TEST，实测单台屏约 300ms，不要放进高频循环。
- **缩放需要重新登录**。这是 Windows 的限制，接口改不了。
