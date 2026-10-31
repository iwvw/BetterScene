# 接入指南

给外部程序 / 脚本调用 BetterScene 的完整说明。协议细节见 [control-api.md](control-api.md)，
这篇只讲**怎么接进去**。

---

## 一、先选一种方式

| 方式 | 适用 | 依赖 |
|---|---|---|
| **调用 `bsctl.exe`** | 脚本、计划任务、批处理 | 无（随程序一起安装） |
| **直连命名管道** | 常驻服务、性能敏感、需要长连接 | 无（系统自带） |

**先用 `bsctl`。** 它已经处理了参数解析、超时、错误码、输出格式化，
几十毫秒的进程启动开销在绝大多数场景里可以忽略（实测 89ms）。

只有这两类情况才值得直连管道：

- 调用频率高到进程启动开销成了负担（比如每秒多次读状态）
- 你的宿主语言不方便起子进程（嵌入式、某些托管环境）

---

## 二、方式一：命令行

### 找到 bsctl.exe

它跟主程序在同一目录：

```
<安装目录>\bsctl.exe     # 安装版
<解压目录>\bsctl.exe     # 便携版
```

安装版默认装在 `%LOCALAPPDATA%\Programs\BetterScene`（具体路径以安装时选择为准）。

**建议**：把该目录加进 `PATH`，或者在你的项目里存绝对路径。

### 基本用法

```powershell
bsctl state                          # 人看的摘要
bsctl --json state                   # 机器解析的 JSON
bsctl brightness set --value 35
bsctl display refresh set --hz 60
bsctl scene apply --theme dark
```

### 在脚本里用

```powershell
$bsctl = "C:\Path\To\bsctl.exe"

$state = & $bsctl --json state | ConvertFrom-Json
foreach ($d in $state.displays) {
    "$($d.firmware)  亮度 $($d.brightness)%  $($d.width)x$($d.height)@$($d.refreshRate)"
}
```

### 判定成败

**只看退出码**，不要去匹配文字输出（措辞会变）：

```powershell
& $bsctl brightness set --value 35
switch ($LASTEXITCODE) {
    0 { "成功" }
    1 { "执行失败，原因在 stderr" }
    2 { "参数写错了" }
    3 { "程序没运行，或在忙" }
}
```

退出码 3 值得单独处理：它表示**没连上**，不是命令本身有问题。
常见于程序未启动，或另一个调用正占着管道（见第六节）。

---

## 三、方式二：直连命名管道

### 管道名

```
\\.\pipe\BetterScene.Control.v1.<会话ID>
```

会话 ID 是 Windows 会话号。**用你的进程自己的会话号**：

```
BetterScene.Control.v1.{Process.GetCurrentProcess().SessionId}
```

为什么带会话号：多用户登录、远程桌面下，每个登录会话有各自的 BetterScene 实例。
用当前进程的会话号就自然连到"你这边的那个实例"。

> 不要用 `WTSGetActiveConsoleSessionId()`：那是**控制台**会话，
> 在远程桌面里运行的程序拿到的是另一个会话的号，会连错或连不上。

### 协议

一行一条 JSON，UTF-8，`\n` 结尾。请求 → 应答严格一问一答。

请求：

```json
{"v":1,"id":"任意字符串","cmd":"brightness.set","args":{"display":"BOE0C8E","value":55}}
```

成功应答：

```json
{"v":1,"id":"任意字符串","ok":true,"result":{"applied":[{"id":"...","title":"...","value":55}]}}
```

失败应答：

```json
{"v":1,"id":"任意字符串","ok":false,"error":{"code":"display_not_found","message":"找不到显示器 NOPE。可用：..."}}
```

- `id` 原样回传
- `v` 必须是 `1`，不匹配会直接被拒
- 空行会被忽略，可以安全地用来分隔
- **同一条连接可以连发多条请求**，不用每次重连

### C#

```csharp
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

string pipeName = $"BetterScene.Control.v1.{Process.GetCurrentProcess().SessionId}";

using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
pipe.Connect(3000);

var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
var reader = new StreamReader(pipe, new UTF8Encoding(false));

string Call(string json)
{
    writer.WriteLine(json);
    return reader.ReadLine() ?? throw new IOException("没有应答");
}

Call("""{"v":1,"id":"1","cmd":"brightness.set","args":{"display":"BOE0C8E","value":55}}""");

JsonNode response = JsonNode.Parse(Call("""{"v":1,"id":"2","cmd":"state.get"}"""))!;
foreach (JsonNode? d in response["result"]!["displays"]!.AsArray())
{
    Console.WriteLine($"{d!["firmware"]} {d["brightness"]}%");
}
```

`AutoFlush = true` 是必须的：管道是流式传输，不刷新的话请求会卡在缓冲区里，
服务端一直在等换行符，看起来就是"没反应"。

### PowerShell

```powershell
$pipeName = "BetterScene.Control.v1.$((Get-Process -Id $PID).SessionId)"
$enc = New-Object System.Text.UTF8Encoding($false)

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(3000)

$w = New-Object System.IO.StreamWriter($pipe, $enc)
$w.AutoFlush = $true
$r = New-Object System.IO.StreamReader($pipe, $enc)

$w.WriteLine('{"v":1,"id":"a","cmd":"version.get"}')
$r.ReadLine()
$pipe.Dispose()
```

### Python

Windows 命名管道能用普通文件 API 打开，不用额外依赖：

```python
import json

class Ctl:
    def __init__(self, pipe_name):
        self.pipe_name = pipe_name
        self._f = None

    def __enter__(self):
        self._f = open(self.pipe_name, "r+b", buffering=0)
        return self

    def __exit__(self, *exc):
        self._f.close()

    def call(self, cmd, args=None, req_id="1"):
        request = {"v": 1, "id": req_id, "cmd": cmd}
        if args is not None:
            request["args"] = args

        self._f.write((json.dumps(request) + "\n").encode("utf-8"))
        line = self._f.readline()
        return json.loads(line.decode("utf-8"))


with Ctl(r"\\.\pipe\BetterScene.Control.v1.1") as ctl:
    print(ctl.call("version.get")["result"])
    print(ctl.call("brightness.set", {"display": "BOE0C8E", "value": 55})["result"])
```

`buffering=0` 别省：Python 默认会缓冲，请求发不出去。

### Node.js

```javascript
import net from 'node:net';

const sock = net.createConnection('\\\\.\\pipe\\BetterScene.Control.v1.1');
let buffer = '';
const pending = [];

sock.setEncoding('utf8');
sock.on('data', (chunk) => {
  buffer += chunk;
  let idx;
  while ((idx = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, idx);
    buffer = buffer.slice(idx + 1);
    const resolve = pending.shift();
    if (resolve && line) resolve(JSON.parse(line));
  }
});

function call(cmd, args, id) {
  return new Promise((resolve) => {
    pending.push(resolve);
    const req = { v: 1, id, cmd };
    if (args) req.args = args;
    sock.write(JSON.stringify(req) + '\n');
  });
}

await new Promise((r) => sock.on('connect', r));
console.log(await call('version.get', null, '1'));
sock.end();
```

Node 的管道是流，**必须自己按 `\n` 切分**。不切的话多条应答会粘成一块，
`JSON.parse` 直接失败。

---

## 四、常用命令的参数形状

走管道时 `cmd` 和 `args` 的对应关系（`bsctl` 会自动转换，直连时要自己拼）：

| cmd | args |
|---|---|
| `state.get` | 无 |
| `display.list` | 无 |
| `brightness.get` | `{"display":"*"}` |
| `brightness.read` | `{"display":"*"}` |
| `brightness.set` | `{"display":"*","value":35}` |
| `brightness.step` | `{"display":"*","delta":-5}` |
| `display.refresh.get` | `{"display":"*"}` |
| `display.refresh.set` | `{"display":"*","hz":60}` |
| `display.mode.list` | `{"display":"HWV6246"}` |
| `display.mode.set` | `{"display":"HWV6246","width":2560,"height":1440,"hz":165}` |
| `display.scale.set` | `{"display":"HWV6246","percent":125}` |
| `display.hdr.set` | `{"display":"HWV6246","on":true}` |
| `display.vcp.get` | `{"display":"HWV6246","code":"0x12"}` |
| `display.vcp.set` | `{"display":"HWV6246","code":"0x60","value":"0x0F"}` |
| `scene.get` | 无 |
| `scene.apply` | `{"theme":"dark","wait":true}` |
| `scene.toggle` | 无 |
| `version.get` | 无 |
| `caps.get` | 无 |

`display` 省略或写 `*` 表示所有可控显示器。

**注意**：`code` 和 `value`（VCP 的）是**字符串**，`"0x12"` 或 `"18"` 都行；
`value`（亮度的）是**数字**。类型不对会报 `bad_request`。

---

## 五、显示器怎么指定

| 写法 | 例子 | 稳定性 |
|---|---|---|
| 省略 / `*` | | 所有可控屏 |
| `primary` | | 主屏 |
| 固件 ID | `HWV6246` | 高，推荐 |
| 型号 | `QSN-CBA` | 中，同型号多台会歧义 |
| GDI 名 | `\\.\DISPLAY1` | 低，重插会变 |
| 完整 Id | `\\?\DISPLAY#HWV6246#5&...` | 最高 |

**用固件 ID。** 它来自 EDID 的 PnP ID，换接口不变，且能看懂。

从 `state.get` 的 `firmware` 字段取：

```powershell
$state = & $bsctl --json state | ConvertFrom-Json
$internal = $state.displays | Where-Object { $_.isInternal } | Select-Object -First 1
& $bsctl brightness set --display $internal.firmware --value 50
```

写成 `--display *` 时，**任意一台失败都不会让整条命令失败**——退出码仍然是 0，
失败项在 `failures` 里。要严格确认每台都成功，就得自己检查这个数组。

---

## 六、必须知道的四条限制

### 1. 一次只服务一个连接

实测：连接 A 占着不放，连接 B 会在超时后失败：

```
连接1 正常
连接2 结果: 超时（被占满），耗时 2014ms
连接1 关掉后 → 可以，立刻进得去
```

对 `bsctl` 的影响是退出码 3 加一句"程序在忙"。

**所以：**

- 用完立刻关连接（`using` / `Dispose`）
- **不要**开一条长连接挂着不用
- 如果确实需要长连接复用，那就只用这一条，别的调用方都走它

> 这是当前实现的选择（服务端 `maxNumberOfServerInstances = 1`）。
> 理由写在 `control-api.md`：外部脚本是"发一条等一条"，
> 并发接入会让 DDC 写入互相插队。如果你的场景确实需要并发，
> 得改服务端，不是调用方绕得过去的。

### 2. 程序必须正在运行

`bsctl` 只是客户端。主程序退出后管道消失，所有调用返回退出码 3。

需要"随叫随到"的话，把程序设成登录自启（托盘常驻）。

### 3. 管道按会话隔离

从另一个登录会话（远程桌面、切换用户）调用，连的是**那个会话**里的实例。
那边没有实例就连不上。这是设计如此，不是 bug。

### 4. 慢命令的超时

| 命令 | 典型耗时 | 说明 |
|---|---|---|
| `state.get` / `brightness.get` | < 50ms | |
| `brightness.read` | 60–200ms | 要读硬件 |
| `display.refresh.set` | 1–2s | 等系统重协商 |
| `display.mode.list` | ~300ms/屏 | 每个刷新率做一次 CDS_TEST |
| `display.mode.set` | 2–4s | |
| `scene.apply --wait true` | 最长 20s | 等主题真正落地 |

客户端超时默认 10 秒。`scene.apply` 和 `mode.set` 建议显式放大：

```powershell
& $bsctl --timeout 25000 scene apply --theme dark
```

管道层面服务端有 **30 秒硬超时**，超过就返回 `timeout` 错误。

---

## 七、错误码对照

| code | 含义 | 该怎么做 |
|---|---|---|
| `bad_request` | 参数缺失/类型不对/越界 | 改调用，别重试 |
| `unknown_command` | 命令不存在 | 用 `caps.get` 查 |
| `display_not_found` | 选择器没匹配到 | 从 `state.get` 重新取 |
| `display_ambiguous` | 匹配到多台 | 换更精确的选择器 |
| `unsupported` | 该屏不支持该操作 | 换通道或跳过 |
| `apply_failed` | 系统拒绝了 | 看 message，通常带可用选项 |
| `timeout` | 超时 | 可重试一次 |
| `internal` | 程序内部错误 | 记日志、报 bug |

`apply_failed` 的 message 通常自带可选项，比如：

```
apply_failed: HUAWEI QSN-CBA: 2560×1440 不支持 999 Hz，可用：200/165/144/120/100/60/59
```

---

## 八、完整例子：拔电切省电

`scripts/power-scene.ps1` 是实际在用的例子，核心三行：

```powershell
$state = & $bsctl --json state | ConvertFrom-Json      # 记快照
& $bsctl display refresh set --display * --hz 60       # 进入
& $bsctl brightness set --display * --value 35
```

退出时按快照逐项写回。**关键是还原用快照而不是写死数值**——
拔电前你可能是 78% / 165Hz，插电后应该回到那个状态，不是被顶到 100% / 240Hz。

```powershell
pwsh -File scripts\power-scene.ps1 -Once        # 跑一次
pwsh -File scripts\power-scene.ps1 -Install     # 注册成常驻监听
pwsh -File scripts\power-scene.ps1 -Uninstall
```

---

## 九、自检清单

接完之后对一遍：

- [ ] 用固件 ID 而不是 `\\.\DISPLAY1` 指定显示器
- [ ] 退出码 3 和 1 分开处理
- [ ] 用完关连接（`using` / `Dispose` / `finally`）
- [ ] 字节流写入后 `Flush`（C# 用 `AutoFlush`，Python 用 `buffering=0`）
- [ ] 多行应答按 `\n` 切分（Node 尤其容易漏）
- [ ] 慢命令放宽超时
- [ ] `--display *` 时检查 `failures` 数组
- [ ] 不拿 `brightness.get` 判断硬件是否真的变了，用 `brightness.read`

最后一条容易被忽略：`brightness.get` 读的是**设置里记的意图值**，
写失败它也不会变，看起来一切正常。要确认硬件真的动了，只有 `brightness.read`。
