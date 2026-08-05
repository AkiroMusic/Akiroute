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

## ⚠️ Important Notice / 重要声明

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

## Features / 功能特性

- **Node Management / 节点管理**
  - Import nodes from links (`vless://`, `vmess://`, `ss://`, `trojan://`,
    `hysteria2://`, `tuic://`) or **Clash YAML** configs
  - Import from subscription URLs (with optional auto-update)
  - Concurrent latency testing (ping) with per-node results
  - Node search and edit
- **Per-Process Routing / 进程级路由**
  - Route each running process to **Proxy / Direct / Block**
  - Case-insensitive process matching (e.g. `QQ.exe`)
  - Rules persist across restarts
- **Proxy Engine / 代理引擎**
  - Local HTTP/SOCKS5 listener (default port `3333`, auto-migrates on conflict)
  - Routing modes: Global / Rule / Direct Only / Process Only
  - TUN mode for system-wide capture (requires administrator)
  - Real-time traffic chart
- **Privacy & Polish / 隐私与体验**
  - **100% local** — no telemetry, no cloud, all data stays on your machine
  - Never touches Windows registry proxy settings
  - Mica system backdrop, System/Dark/Light themes
  - Close-to-tray with quick switch from the tray menu

---

## Screenshots / 界面预览

| Nodes / 节点 | Processes / 进程 | Settings / 设置 |
|---|---|---|
| Node list, search, ping, import | Per-process Proxy/Direct/Block | Mode, port, theme, auto-update |

*(Run the app to see the full UI — the sidebar brand logo is the Akiroute mascot.)*
*（启动应用即可查看完整界面——侧边栏品牌 Logo 为 Akiroute 吉祥物。）*

---

## Requirements / 环境要求

| Item | Requirement |
|---|---|
| OS | Windows 10 (build 19041+) or Windows 11 |
| SDK | .NET 10 SDK (for building from source) |
| Architecture | x64 (x86 / ARM64 also supported) |
| Engine assets | Downloaded by `tools/fetch-assets.ps1` (xray-core + geo rules) |

---

## Build & Run / 构建与运行

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

---

## Usage Guide / 使用指南

### Quick Start / 快速上手

1. **导入节点** — 节点页 → 导入 → 粘贴链接 / Clash 配置 / 订阅地址
2. **启动代理** — 点击顶部状态栏开关
3. **进程分流** — 进程页 → 为每个进程选择 代理/直连/阻止

### Data Storage / 数据存储

All settings live in `%LOCALAPPDATA%\Akiroute\Config\settings.json`:
- `nodes` — imported nodes (restored on restart)
- `processRules` — per-process routing rules
- `mode` / `port` / `autoConnect` / `tunEnabled` / `theme` / `subscriptions`

Delete this file to reset to factory defaults.

---

## Tech Stack / 技术栈

| Layer | Technology |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.8, self-contained) |
| Language | C# / .NET 10 (Native AOT ready) |
| Engine | [xray-core](https://github.com/XTLS/Xray-core) |
| MVVM | CommunityToolkit.Mvvm 8.4 |
| Config parsing | YamlDotNet |

---

## Project Layout / 项目结构

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

## Copyright & License / 版权与许可

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

## Disclaimer / 免责声明

This software is an independent learning project. It is **not affiliated with,
endorsed by, or related to** any brand mentioned in the code (e.g. names used in
examples). All trademarks belong to their respective owners. Use at your own
risk and responsibility.

本软件为独立学习项目，与代码中出现的任何品牌（如示例中的名称）**无关联、无
背书、无任何关系**。所有商标归其各自所有者。请自行承担使用风险与责任。
