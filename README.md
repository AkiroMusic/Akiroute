# Akiroute

[![License: Proprietary](https://img.shields.io/badge/License-Proprietary-red.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue.svg)]()
[![Framework](https://img.shields.io/badge/Framework-WinUI%203%20%2F%20.NET%2010-purple.svg)]()

> **A local proxy routing tool for Windows** — a native WinUI 3 desktop client
> with a clean, Apple/Google-grade interface. Built from scratch on top of
> `xray-core`, with per-process routing, node management, and real-time traffic
> monitoring. **For learning and communication purposes only.**
>
> **Windows 本地代理路由工具** —— 基于 WinUI 3 + xray-core 的原生桌面客户端，
> 提供进程级分流、节点管理与实时流量监控。**仅供学习与交流使用。**

---

## ⚠️ Important Notice
## 重要声明

> **EN**: This project is a **personal learning project** of **Akiro**. It is
> released **for learning, academic research, and technical communication ONLY**.
> It is **NOT** licensed for commercial use, redistribution, or production
> deployment. Proxy-related technologies may be restricted in certain
> jurisdictions — **you are solely responsible for complying with your local
> laws**. See the [LICENSE](LICENSE) for full terms.
>
> **中**: 本项目是 **Akiro** 的**个人学习项目**，仅用于**学习、学术研究与技术
> 交流**。**禁止**商业使用、再分发或生产部署。代理相关技术在某些司法辖区
> 可能受到限制，**请自行遵守所在地法律法规**。完整条款见 [LICENSE](LICENSE)。

---

## Features
## 功能特性

- **Node Management**
- **节点管理**
  - Import nodes from links (`vless://`, `vmess://`, `ss://`, `trojan://`,
    `hysteria2://`, `tuic://`) or **Clash YAML** configs
  - 支持从链接（`vless://`、`vmess://`、`ss://`、`trojan://`、
    `hysteria2://`、`tuic://`）或 **Clash YAML** 配置导入节点
  - Import from subscription URLs (with optional auto-update)
  - 支持从订阅链接导入（可选自动更新）
  - Concurrent latency testing (ping) with per-node results
  - 并发延迟测试（ping），显示每个节点的测试结果
  - Node search and edit
  - 节点搜索与编辑
- **Per-Process Routing**
- **进程级路由**
  - Route each running process to **Proxy / Direct / Block**
  - 将每个运行中的进程路由到 **代理 / 直连 / 阻止**
  - Case-insensitive process matching (e.g. `QQ.exe`)
  - 进程匹配不区分大小写（例如 `QQ.exe`）
  - Rules persist across restarts
  - 规则在重启后依然保留
- **Proxy Engine**
- **代理引擎**
  - Local HTTP/SOCKS5 listener (default port `3333`, auto-migrates on conflict)
  - 本地 HTTP/SOCKS5 监听端口（默认 `3333`，冲突时自动迁移）
  - Routing modes: Global / Rule / Direct Only / Process Only
  - 路由模式：全局 / 规则 / 仅直连 / 仅进程
  - TUN mode for system-wide capture (requires administrator)
  - TUN 模式，系统级全局接管（需要管理员权限）
  - Real-time traffic chart
  - 实时流量图表
- **Privacy & Polish**
- **隐私与体验**
  - **100% local** — no telemetry, no cloud, all data stays on your machine
  - **100% 本地** —— 无遥测、无云端，所有数据都保留在您的设备上
  - Never touches Windows registry proxy settings
  - 绝不改动 Windows 注册表中的代理设置
  - Mica system backdrop, System/Dark/Light themes
  - Mica 系统背景，System / 深色 / 浅色主题
  - Close-to-tray with quick switch from the tray menu
  - 关闭时最小化到托盘，可从托盘菜单快速切换

---

## Screenshots
## 界面预览

| Nodes | Processes | Settings |
|---|---|---|
| Node list, search, ping, import | Per-process Proxy/Direct/Block | Mode, port, theme, auto-update |

| 节点 | 进程 | 设置 |
|---|---|---|
| 节点列表、搜索、延迟测试、导入 | 按进程设置 代理/直连/阻止 | 模式、端口、主题、自动更新 |

*(Run the app to see the full UI — the sidebar brand logo is the Akiroute mascot.)*
*（启动应用即可查看完整界面——侧边栏品牌 Logo 为 Akiroute 吉祥物。）*

---

## Requirements
## 环境要求

| Item | Requirement |
|---|---|
| OS | Windows 10 (build 19041+) or Windows 11 |
| SDK | .NET 10 SDK (for building from source) |
| Architecture | x64 (x86 / ARM64 also supported) |
| Engine assets | Downloaded by `tools/fetch-assets.ps1` (xray-core + geo rules) |

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10（内部版本 19041+）或 Windows 11 |
| SDK | .NET 10 SDK（用于从源码构建） |
| 架构 | x64（同时支持 x86 / ARM64） |
| 引擎资源 | 由 `tools/fetch-assets.ps1` 下载（xray-core + geo 规则） |

---

## Build & Run
## 构建与运行

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

Or open `Akiroute.slnx` in Visual Studio 2026+, select **x64** platform, and press **F5**.

或者使用 Visual Studio 2026+ 打开 `Akiroute.slnx`，选择 **x64** 平台，按 **F5** 运行。

---

## Usage Guide
## 使用指南

### Quick Start
### 快速上手

1. **Import nodes** — Nodes page → Import → paste link / Clash config / subscription URL
2. **导入节点** — 节点页 → 导入 → 粘贴链接 / Clash 配置 / 订阅地址
3. **Start the proxy** — click the switch on the top status bar
4. **启动代理** — 点击顶部状态栏开关
5. **Route processes** — Processes page → choose Proxy / Direct / Block for each process
6. **进程分流** — 进程页 → 为每个进程选择 代理/直连/阻止

### Data Storage
### 数据存储

All settings live in `%LOCALAPPDATA%\Akiroute\Config\settings.json`:

所有设置保存在 `%LOCALAPPDATA%\Akiroute\Config\settings.json` 中：

- `nodes` — imported nodes (restored on restart)
- `nodes` — 已导入的节点（重启后恢复）
- `processRules` — per-process routing rules
- `processRules` — 进程级路由规则
- `mode` / `port` / `autoConnect` / `tunEnabled` / `theme` / `subscriptions`
- `mode` / `port` / `autoConnect` / `tunEnabled` / `theme` / `subscriptions`

Delete this file to reset to factory defaults.

删除该文件即可恢复出厂默认设置。

---

## Tech Stack
## 技术栈

| Layer | Technology |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.8, self-contained) |
| Language | C# / .NET 10 (Native AOT ready) |
| Engine | [xray-core](https://github.com/XTLS/Xray-core) |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| Config parsing | YamlDotNet |

| 分层 | 技术 |
|---|---|
| UI | WinUI 3（Windows App SDK 1.8，自包含） |
| 语言 | C# / .NET 10（支持 Native AOT） |
| 引擎 | xray-core |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| 配置解析 | YamlDotNet |

---

## Project Layout
## 项目结构

```
Akiroute/
├── Akiroute/                 # Main app (WinUI 3)
│   ├── Assets/Icons/         # App icon & logo
│   ├── Helpers/              # AppPaths, Theme, Dispatcher helpers
│   ├── Models/               # ProxyNode, ProcessRule, AppSettings...
│   ├── Services/             # XrayService, NodeLinkParser, PingService...
│   ├── ViewModels/           # MainWindowViewModel, NodeListViewModel...
│   └── Views/                # MainWindow, dialogs, controls
├── Akiroute.Tests/           # xUnit test suite (152 tests)
├── tools/fetch-assets.ps1    # Engine asset downloader
└── LICENSE
```

---

## Copyright & License
## 版权与许可

**Copyright © 2026 Akiro. All rights reserved.**

This project is licensed under a **proprietary, non-commercial, educational
license**. It is provided **for learning and communication purposes only** —
commercial use, redistribution, copying, and production deployment are
**strictly prohibited** without written permission from the author. The author
assumes **no responsibility** for any misuse. See [LICENSE](LICENSE) for the
full terms.

**版权所有 © 2026 Akiro，保留所有权利。**

本项目采用**专有、非商业、教育用途许可**。仅供**学习与交流**使用——未经作者
书面许可，**严禁**商业使用、再分发、复制与生产部署。作者对任何滥用行为
**不承担任何责任**。完整条款见 [LICENSE](LICENSE)。

---

## Disclaimer
## 免责声明

This software is an independent learning project. It is **not affiliated with,
endorsed by, or related to** any brand mentioned in the code (e.g. names used in
examples). All trademarks belong to their respective owners. Use at your own
risk and responsibility.

本软件为独立学习项目，与代码中出现的任何品牌（如示例中的名称）**无关联、无
背书、无任何关系**。所有商标归其各自所有者。请自行承担使用风险与责任。
