# Windows 视觉改善 {{VERSION}}

{{CHANGES}}

---

## 下载

两种版本，按机器是否已装运行时选择。

| 文件 | 大小 | 说明 |
|---|---:|---|
| [`BetterScene-{{VERSION}}-x64-merged-setup.exe`](https://github.com/iwvw/BetterScene/releases/download/v{{VERSION}}/BetterScene-{{VERSION}}-x64-merged-setup.exe) | {{SIZE_X64_MERGED_SETUP}} | 合并版安装包，推荐。运行时随包，开箱即用 |
| [`BetterScene-{{VERSION}}-x64-merged-portable.zip`](https://github.com/iwvw/BetterScene/releases/download/v{{VERSION}}/BetterScene-{{VERSION}}-x64-merged-portable.zip) | {{SIZE_X64_MERGED_PORTABLE}} | 合并版便携包，解压即用 |
| [`BetterScene-{{VERSION}}-x64-split-setup.exe`](https://github.com/iwvw/BetterScene/releases/download/v{{VERSION}}/BetterScene-{{VERSION}}-x64-split-setup.exe) | {{SIZE_X64_SPLIT_SETUP}} | 分离版安装包，体积小，安装时自动补齐运行时 |
| [`BetterScene-{{VERSION}}-x64-split-portable.zip`](https://github.com/iwvw/BetterScene/releases/download/v{{VERSION}}/BetterScene-{{VERSION}}-x64-split-portable.zip) | {{SIZE_X64_SPLIT_PORTABLE}} | 分离版便携包，需机器已装运行时 |
| [`BetterScene-{{VERSION}}-arm64-merged-portable.zip`](https://github.com/iwvw/BetterScene/releases/download/v{{VERSION}}/BetterScene-{{VERSION}}-arm64-merged-portable.zip) | {{SIZE_ARM64_MERGED_PORTABLE}} | ARM64 合并版便携包 |
| [`BetterScene-{{VERSION}}-arm64-split-portable.zip`](https://github.com/iwvw/BetterScene/releases/download/v{{VERSION}}/BetterScene-{{VERSION}}-arm64-split-portable.zip) | {{SIZE_ARM64_SPLIT_PORTABLE}} | ARM64 分离版便携包 |

### 选哪个

| 场景 | 建议 |
|---|---|
| 大多数用户 | 合并版 x64 安装包（自带运行时，装完即用） |
| 已装 .NET 10 与 Windows App SDK 运行时 | 分离版（体积小很多） |
| 不想安装、随处运行 | 合并版便携 zip |
| ARM 设备（骁龙本等） | arm64 便携包 |

## 注意事项

- **显示器控制**：亮度通过 DDC/CI 调节；外接显示器支持最好，笔记本内屏走 WMI 背光。
- **位置检测**：日出日落规则使用 Windows 定位服务，需在「设置 → 隐私 → 定位」中允许桌面应用访问。
- **开机自启**：勾选后会写入 `HKCU\...\Run` 并带 `--minimized`，启动时最小化到托盘。
- **分离版运行时**：需 .NET 10 桌面运行时与 Windows App SDK Runtime，安装包会检测并引导安装，便携包需自行预装。
- 用户数据位于 `%LocalAppData%\BetterScene`，卸载时保留。
- 安装包未做代码签名，Windows SmartScreen 可能提示「未知发布者」，选择「仍要运行」即可。
