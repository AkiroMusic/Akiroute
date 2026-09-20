# Akiroute

[![License: Proprietary](https://img.shields.io/badge/License-Proprietary-red.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()
[![Framework](https://img.shields.io/badge/Framework-WinUI%203%20%2F%20.NET%2010-purple.svg)]()

> **A local proxy routing tool for Windows** — a native WinUI 3 desktop client
> with a clean, Apple/Google-grade interface. Built from scratch on top of
> `xray-core`, with per-process routing, node management, and real-time traffic
> monitoring. **For learning and communication purposes only.**

---

## ⚠️ Important Notice

> This project is a **personal learning project** of **Akiro**. It is
> released **for learning, academic research, and technical communication ONLY**.
> It is **NOT** licensed for commercial use, redistribution, or production
> deployment. Proxy-related technologies may be restricted in certain
> jurisdictions — **you are solely responsible for complying with your local
> laws**. See the [LICENSE](LICENSE) for full terms.

---

## Features

- **Node Management**
  - Import nodes from links (`vless://`, `vmess://`, `ss://`, `trojan://`,
    `hysteria2://`, `tuic://`) or **Clash YAML** configs
  - Import from subscription URLs (with optional auto-update)
  - Concurrent latency testing (ping) with per-node results
  - Node search and edit
- **Per-Process Routing**
  - Route each running process to **Proxy / Direct / Block**
  - Case-insensitive process matching (e.g. `QQ.exe`)
  - Rules persist across restarts
- **Proxy Engine**
  - Local HTTP/SOCKS5 listener (default port `3333`, auto-migrates on conflict)
  - Routing modes: Global / Rule / Direct Only / Process Only
  - Real-time traffic chart
- **Privacy & Polish**
  - **100% local** — no telemetry, no cloud, all data stays on your machine
  - Credentials at rest are DPAPI-encrypted (Windows CurrentUser scope) — the
    settings file is only decryptable by your Windows account on this machine
  - Backups are plaintext by design (portable across machines); the export
    dialog warns about the credential exposure
  - The generated engine config lives in the system temp directory only while
    the proxy runs and is deleted on stop/exit
  - Never touches Windows registry proxy settings
  - System/Dark/Light themes
  - Close-to-tray with quick switch from the tray menu
- **Reliability & Robustness** *(v0.1.1)*
  - Single-instance guard — launching a second copy shows a hint and exits
  - Engine lifecycle tied to the app: exiting (even from the tray) always
    kills `xray.exe`; startup cleans up engines orphaned by a previous crash
    (current session only) and removes the leftover engine config
  - Toggle debounce prevents rapid double-start races and phantom errors
  - Window size/position persists across restarts (clamped to the work area)
  - Keyboard focus rings on all custom-styled controls; context menus
    (edit / delete / copy) on node cards; empty-state hints
- **Subscriptions & Diagnostics** *(v0.1.2)*
  - Subscription auto-update: imported feeds register automatically and
    refresh on a configurable interval (global + per-entry override);
    stale nodes are replaced while manual nodes are never touched
  - Subscription management in the settings panel (name/url/last-updated)
    with one-click removal
  - Optional scheduled latency re-test keeps node badges fresh
  - Config backup/restore via file pickers with validation
  - Log viewer page — application log (auto-rotated, capped) and live xray
    engine output side by side, with copy/refresh

---

## Screenshots

| Nodes | Processes | Settings |
|---|---|---|
| Node list, search, ping, import | Per-process Proxy/Direct/Block | Mode, port, theme, auto-update |

*(Run the app to see the full UI — the sidebar brand logo is the Akiroute mascot.)*

---

## Requirements

| Item | Requirement |
|---|---|
| OS | Windows 10 (build 17763+) or Windows 11 |
| SDK | .NET 10 SDK (for building from source) |
| Architecture | x64 (x86 / ARM64 also supported) |
| Engine assets | Downloaded by `tools/fetch-assets.ps1` (xray-core + geo rules) |

---

## Build & Run

```bash
# 1. Fetch the xray engine assets (xray.exe, wintun.dll, geoip.dat, geosite.dat)
powershell -ExecutionPolicy Bypass -File tools/fetch-assets.ps1

# 2. Build (x64)
dotnet build Akiroute/Akiroute.csproj -p:Platform=x64

# 3. Run
./Akiroute/bin/x64/Debug/net10.0-windows10.0.19041.0/Akiroute.exe

# 4. Test
dotnet test Akiroute.Tests/Akiroute.Tests.csproj -p:Platform=x64
```

### Release Publish

```bash
# Self-contained publish (bundles WindowsAppSDK runtime + engine assets)
dotnet publish Akiroute/Akiroute.csproj -c Release -p:Platform=x64 -o <output-dir>
```

Measured on x64: output ≈ 208 MB total (bundled engine assets ≈ 35 MB + geo rules
≈ 29 MB sit alongside the app payload); running working set ≈ 178 MB — dominated by
the self-contained WindowsAppSDK/graphics stack rather than app logic. Startup
(process start → main window ready) ≈ 624 ms on Release config. The published
binary was smoke-tested: clean startup, settings persistence, and log output all
verified.

> **Native AOT status**: `-p:PublishAot=true` now compiles and links after a
> one-time MSVC environment setup (vcvars64 + explicit MSVC `lib`/`PATH`). All
> app-code IL3050/IL2026 warnings are resolved, and ResourceDictionary style
> lookups were replaced with AOT-safe applied-element captures. Remaining
> blocker (precisely root-caused): the XAML compiler generates a per-window
> bindings class whose lazy-initialization subscribes `Window.Activated` with
> an `object`-sender generic handler; its ABI marshalling fails under AOT
> ("Value does not fall within the expected range"). Fixing requires migrating
> the ~30 `x:Bind` bindings in MainWindow to classic event/property wiring —
> tracked as roadmap work. The self-contained Release publish above remains
> the validated deployment path.

Or open `Akiroute.slnx` in Visual Studio 2026+, select **x64** platform, and press **F5**.

---

## Usage Guide

### Quick Start

1. **Import nodes** — Nodes page → Import → paste link / Clash config / subscription URL
2. **Start the proxy** — click the switch on the top status bar
3. **Route processes** — Processes page → choose Proxy / Direct / Block for each process

### Data Storage

`settings.json` is stored as a DPAPI-encrypted wrapper (CurrentUser scope;
legacy plaintext files are still read and migrate to encrypted on the next
save). A copy of the file restored on another Windows user/machine cannot be
decrypted — use the in-app backup export instead (plaintext, portable):

All settings live in `%LOCALAPPDATA%\Akiroute\Config\settings.json`:

- `nodes` — imported nodes (restored on restart)
- `processRules` — per-process routing rules
- `mode` / `port` / `autoConnect` / `tunEnabled` / `theme` / `subscriptions`

Delete this file to reset to factory defaults.

---

## Roadmap *(backlog, not yet implemented)*

Ideas ranked by user value ÷ implementation cost, informed by v2rayN /
Clash Verge Rev / FlClash / Proxifier:

- Real-time connections viewer (per-process traffic via xray stats API)
- Update checker
- TUN mode for system-wide capture (requires administrator; needs wintun integration)
- Native AOT runtime enablement (migrate x:Bind → classic wiring across views)

---

## Tech Stack

| Layer | Technology |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.8, self-contained) |
| Language | C# / .NET 10 (self-contained publish; Native AOT pending, see roadmap) |
| Engine | [xray-core](https://github.com/XTLS/Xray-core) |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| Config parsing | YamlDotNet |

---

## Project Layout

```
Akiroute/
├── Akiroute/                 # Main app (WinUI 3)
│   ├── Assets/Icons/         # App icon & logo
│   ├── Helpers/              # AppPaths, Theme, Dispatcher helpers
│   ├── Models/               # ProxyNode, ProcessRule, AppSettings...
│   ├── Services/             # XrayService, NodeLinkParser, PingService...
│   ├── ViewModels/           # MainWindowViewModel, NodeListViewModel...
│   └── Views/                # MainWindow, dialogs, controls
├── Akiroute.Tests/           # xUnit test suite (246 tests)
├── tools/fetch-assets.ps1    # Engine asset downloader
└── LICENSE
```

---

## Copyright & License

**Copyright © 2026 Akiro. All rights reserved.**

This project is licensed under a **proprietary, non-commercial, educational
license**. It is provided **for learning and communication purposes only** —
commercial use, redistribution, copying, and production deployment are
**strictly prohibited** without written permission from the author. The author
assumes **no responsibility** for any misuse. See [LICENSE](LICENSE) for the
full terms.

---

## Disclaimer

This software is an independent learning project. It is **not affiliated with,
endorsed by, or related to** any brand mentioned in the code (e.g. names used in
examples). All trademarks belong to their respective owners. Use at your own
risk and responsibility.

---

# Akiroute（Windows 本地代理路由工具）

[![License: Proprietary](https://img.shields.io/badge/License-Proprietary-red.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()
[![Framework](https://img.shields.io/badge/Framework-WinUI%203%20%2F%20.NET%2010-purple.svg)]()

> **Windows 本地代理路由工具** —— 从零构建于 `xray-core` 之上的原生 WinUI 3
> 桌面客户端，拥有干净、Apple/Google 级别的界面，提供进程级分流、节点管理与
> 实时流量监控。**仅供学习与交流使用。**

---

## ⚠️ 重要声明

> 本项目是 **Akiro** 的**个人学习项目**，仅用于**学习、学术研究与技术
> 交流**。**禁止**商业使用、再分发或生产部署。代理相关技术在某些司法辖区
> 可能受到限制，**请自行遵守所在地法律法规**。完整条款见 [LICENSE](LICENSE)。

---

## 功能特性

- **节点管理**
  - 支持从链接（`vless://`、`vmess://`、`ss://`、`trojan://`、
    `hysteria2://`、`tuic://`）或 **Clash YAML** 配置导入节点
  - 支持从订阅链接导入（可选自动更新）
  - 并发延迟测试（ping），显示每个节点的测试结果
  - 节点搜索与编辑
- **进程级路由**
  - 将每个运行中的进程路由到 **代理 / 直连 / 阻止**
  - 进程匹配不区分大小写（例如 `QQ.exe`）
  - 规则在重启后依然保留
- **代理引擎**
  - 本地 HTTP/SOCKS5 监听端口（默认 `3333`，冲突时自动迁移）
  - 路由模式：全局 / 规则 / 仅直连 / 仅进程
  - 实时流量图表
- **隐私与体验**
  - **100% 本地** —— 无遥测、无云端，所有数据都保留在您的设备上
  - 静态存储的节点凭据使用 DPAPI 加密（Windows CurrentUser 范围）—— 配置文件
    仅可由本机当前 Windows 账户解密
  - 备份文件为明文（以便跨机器恢复），导出时会提示明文凭据风险
  - 生成的引擎配置仅在代理运行期间存在于系统临时目录，停止/退出即删除
  - 绝不改动 Windows 注册表中的代理设置
  - System / 深色 / 浅色主题
  - 关闭时最小化到托盘，可从托盘菜单快速切换
- **可靠性与健壮性** *（v0.1.1）*
  - 单实例保护 —— 重复启动时提示并退出
  - 引擎生命周期与应用绑定：退出（包括从托盘退出）始终会结束 `xray.exe`；
    启动时清理上次崩溃遗留的引擎进程（仅限当前会话）并删除遗留的引擎配置
  - 开关防抖，避免快速双击导致的竞态与虚假错误
  - 窗口大小/位置跨重启保留（并限制在工作区内）
  - 所有自绘控件带键盘焦点框；节点卡片右键菜单（编辑/删除/复制）；空状态提示
- **订阅与诊断** *（v0.1.2）*
  - 订阅自动更新：导入的订阅自动登记，按可配置间隔刷新（全局 + 单条覆盖）；
    过期节点被替换，手动导入的节点不受影响
  - 设置面板中的订阅源管理（名称/地址/最近更新），一键删除
  - 可选的定时自动测速，保持节点延迟徽标新鲜
  - 配置备份/恢复：文件选择器导出/导入，带有效性校验
  - 日志查看页 —— 应用日志（自动轮转、限量）与 xray 引擎实时输出并排显示，
    支持复制/刷新

---

## 界面预览

| 节点 | 进程 | 设置 |
|---|---|---|
| 节点列表、搜索、延迟测试、导入 | 按进程设置 代理/直连/阻止 | 模式、端口、主题、自动更新 |

*（启动应用即可查看完整界面——侧边栏品牌 Logo 为 Akiroute 吉祥物。）*

---

## 环境要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10（内部版本 17763+）或 Windows 11 |
| SDK | .NET 10 SDK（用于从源码构建） |
| 架构 | x64（同时支持 x86 / ARM64） |
| 引擎资源 | 由 `tools/fetch-assets.ps1` 下载（xray-core + geo 规则） |

---

## 构建与运行

```bash
# 1. 下载 xray 引擎资源（xray.exe、wintun.dll、geoip.dat、geosite.dat）
powershell -ExecutionPolicy Bypass -File tools/fetch-assets.ps1

# 2. 构建（x64）
dotnet build Akiroute/Akiroute.csproj -p:Platform=x64

# 3. 运行
./Akiroute/bin/x64/Debug/net10.0-windows10.0.19041.0/Akiroute.exe

# 4. 测试
dotnet test Akiroute.Tests/Akiroute.Tests.csproj -p:Platform=x64
```

### 发布

```bash
# 自包含发布（打包 WindowsAppSDK 运行时 + 引擎资源）
dotnet publish Akiroute/Akiroute.csproj -c Release -p:Platform=x64 -o <output-dir>
```

x64 实测：输出约 208 MB（捆绑引擎资源约 35 MB + geo 规则约 29 MB，与主程序
并列）；运行工作集约 178 MB —— 主要来自自包含的 WindowsAppSDK/图形栈而非应用
逻辑。启动耗时（进程启动 → 主窗口就绪）Release 配置约 624 ms。已对发布产物
做冒烟测试：正常启动、设置持久化、日志输出均验证通过。

> **Native AOT 现状**：`-p:PublishAot=true` 在一次性配置 MSVC 环境（vcvars64，
> 并显式设置 MSVC `lib`/`PATH`）后即可编译链接。应用代码的 IL3050/IL2026 警告
> 已全部清除，ResourceDictionary 样式查找也已替换为 AOT 安全的已应用元素捕获。
> 剩余阻塞点（已精确定位）：XAML 编译器生成的每窗口绑定类在惰性初始化时以
> `object`-sender 泛型处理器订阅 `Window.Activated`，其 ABI 封送在 AOT 下失败
> （"Value does not fall within the expected range"）。修复需要把 MainWindow
> 中约 30 处 `x:Bind` 绑定迁移为经典事件/属性绑定 —— 已列入路线图。上述
> 自包含 Release 发布仍是经过验证的部署方式。

或者使用 Visual Studio 2026+ 打开 `Akiroute.slnx`，选择 **x64** 平台，按 **F5** 运行。

---

## 使用指南

### 快速上手

1. **导入节点** — 节点页 → 导入 → 粘贴链接 / Clash 配置 / 订阅地址
2. **启动代理** — 点击顶部状态栏开关
3. **进程分流** — 进程页 → 为每个进程选择 代理/直连/阻止

### 数据存储

`settings.json` 以 DPAPI 加密包装格式存储（CurrentUser 范围；旧版明文文件仍可
读取，并在下一次保存时自动迁移为加密格式）。复制到其他 Windows 用户/机器的
配置文件无法解密——跨设备迁移请使用应用内的备份导出（明文、可移植）：

所有设置保存在 `%LOCALAPPDATA%\Akiroute\Config\settings.json` 中：

- `nodes` — 已导入的节点（重启后恢复）
- `processRules` — 进程级路由规则
- `mode` / `port` / `autoConnect` / `tunEnabled` / `theme` / `subscriptions`

删除该文件即可恢复出厂默认设置。

---

## 路线图 *（待办，尚未实现）*

按 用户价值 ÷ 实现成本 排序，参考 v2rayN / Clash Verge Rev / FlClash /
Proxifier：

- 实时连接查看器（通过 xray stats API 的每进程流量）
- 更新检查器
- TUN 模式，系统级全局接管（需要管理员权限，需集成 wintun）
- Native AOT 运行时支持（将各视图的 x:Bind 迁移为经典事件/属性绑定）

---

## 技术栈

| 分层 | 技术 |
|---|---|
| UI | WinUI 3（Windows App SDK 1.8，自包含） |
| 语言 | C# / .NET 10（自包含发布；Native AOT 待支持，见路线图） |
| 引擎 | [xray-core](https://github.com/XTLS/Xray-core) |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| 配置解析 | YamlDotNet |

---

## 项目结构

```
Akiroute/
├── Akiroute/                 # 主应用（WinUI 3）
│   ├── Assets/Icons/         # 应用图标与 Logo
│   ├── Helpers/              # AppPaths、Theme、Dispatcher 辅助类
│   ├── Models/               # ProxyNode、ProcessRule、AppSettings...
│   ├── Services/             # XrayService、NodeLinkParser、PingService...
│   ├── ViewModels/           # MainWindowViewModel、NodeListViewModel...
│   └── Views/                # MainWindow、对话框、控件
├── Akiroute.Tests/           # xUnit 测试套件（246 个测试）
├── tools/fetch-assets.ps1    # 引擎资源下载器
└── LICENSE
```

---

## 版权与许可

**版权所有 © 2026 Akiro，保留所有权利。**

本项目采用**专有、非商业、教育用途许可**。仅供**学习与交流**使用——未经作者
书面许可，**严禁**商业使用、再分发、复制与生产部署。作者对任何滥用行为
**不承担任何责任**。完整条款见 [LICENSE](LICENSE)。

---

## 免责声明

本软件为独立学习项目，与代码中出现的任何品牌（如示例中的名称）**无关联、无
背书、无任何关系**。所有商标归其各自所有者。请自行承担使用风险与责任。
