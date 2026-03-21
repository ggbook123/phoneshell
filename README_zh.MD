# PhoneShell

> 手机上的终端。随时随地远程操控 Windows / Linux 终端，AI 编程不受限。

[English](README.MD) | **中文**

---

## 为什么用 PhoneShell

- **手机即终端** — 在手机上获得原生 Shell 体验，随时随地 AI 编程。
- **会话不中断** — 以群组为概念，在群组中终端会话在手机、PC、Linux 之间无缝切换，进程持续运行。
- **兼容所有 CLI** — 支持一切命令行工具：`claude`、`codex`、`gemini`、`kiro`、`droid`、`vim`、`top`…
- **多平台覆盖** — Windows 桌面端 + Linux 服务端 + Android / HarmonyOS 手机端（iOS 计划中）。
- **新手友好** — 无需复杂配置，打开程序、扫码即连，开箱即用。

---

## 与同类工具对比

| 对比对象 | 痛点 | | PhoneShell 优势 | |
|---------|------|:---:|----------------|:---:|
| Claude Code、Codex 等 AI CLI | 只能在单一终端使用，离开电脑就断了 | :x: | 兼容所有 CLI，手机上也能用 claude、codex | :white_check_mark: |
| SSH 工具 / Web SSH | 界面古老，移动端体验差 | :x: | 现代化终端体验，xterm.js 渲染 | :white_check_mark: |
| Termius 等远程终端 | 付费昂贵，多设备无法无缝衔接 | :x: | 手机、PC、Linux 互连，会话不中断 | :white_check_mark: |
| HappyRemote 等远控 | 无法运行终端命令，可扩展性差 | :x: | 原生终端 + 高扩展性 | :white_check_mark: |

---

## 架构概览

```
┌─────────────┐  QR / WebSocket  ┌──────────────────┐  WebSocket  ┌─────────────┐
│  Android    │◄────────────────►│  Windows 桌面端   │◄──────────►│  Linux 端   │
│  HarmonyOS  │                  │  (Relay + 终端)   │            │  (Relay +   │
│  (手机端)    │                  │                   │            │   终端)     │
└─────────────┘                  └──────────────────┘            └─────────────┘
       │                                │                              │
       │         QR / WebSocket         │         QR / WebSocket       │
       │           ┌────────────────────┘                              │
       │           ▼                                                   │
       │    ┌─────────────┐                                           │
       └───►│  Web 管理面板 │◄──────────────────────────────────────────┘
            └─────────────┘
```

**工作模式：**
- **群组模式** — 多设备组成群组，使用邀请码加入，支持成员管理与踢出。

---

## 核心功能

| 功能 | 说明 |
|------|------|
| 多会话管理 | 创建、重命名、切换多个终端会话 |
| 历史回放 | 分页加载终端输出历史，断线重连不丢失上下文 |
| 二维码连接 | 扫码直连（`phoneshell://connect`）、群组绑定（`phoneshell://bind`）、Web 面板登录（`phoneshell://login`） |
| 群组与邀请码 | Relay 中继、邀请码加入、密钥轮换、服务器迁移 |
| 控制权管理 | 请求/授予终端控制权，强制断开连接 |
| Web 管理面板 | 设备列表、会话管理、扫码登录，支持 Vue.js 前端 |
| AI 终端助手 | 支持 OpenAI 兼容 API（可配置端点和模型），自动识别 Shell 类型，支持 TUI 应用操控 |
| TLS/SSL | 配置证书后自动启用 wss 加密连接 |
| 中英文切换 | 手机端内置中英文界面切换 |

---

## 快速开始

### Windows 端

1. 以管理员身份运行 `PhoneShell.App.exe`。
2. 点击 **Start**。

### Android / HarmonyOS 端

1. 安装并打开应用。
2. 扫描 Windows 或 Linux 端显示的二维码进行连接。

### Linux 端

```bash
# 安装依赖（Ubuntu / Debian）
sudo apt-get update
sudo apt-get install -y git nodejs npm python3 build-essential

# 克隆并安装
git clone https://github.com/ggbook123/phoneshell.git
cd phoneshell
sudo bash linux2/deploy/phoneshell install
```

---

## Linux 部署

### 前置要求

- Linux（systemd）
- Node.js >= 18
- `node-pty` 构建依赖：`python3`、`make`、`g++`

### 常用命令

```bash
phoneshell                       # 连接本地终端
phoneshell list                  # 列出活跃会话
phoneshell attach                # 附加到已有会话
phoneshell local --shell bash    # 指定 shell 类型
phoneshell set                   # 修改配置
phoneshell start                 # 启动服务
```

### 配置

默认配置文件：`/etc/phoneshell/config.json`

```jsonc
{
  "port": 19090,
  "panelPort": 0,
  "publicHost": "",
  "tls": {
    "enabled": true,
    "certPath": "/path/to/cert.pem",
    "keyPath": "/path/to/key.pem"
  },
  "modules": {
    "terminal": true,
    "relayServer": false,
    "relayClient": true,
    "webPanel": true,
    "aiChat": false
  }
}
```

也支持环境变量覆盖配置：`PHONESHELL_PORT`、`PHONESHELL_PUBLIC_HOST`、`PHONESHELL_TLS_CERT` 等。

修改配置后重启：`sudo systemctl restart phoneshell`

### Web 面板

- 启用后访问：`http://<host>:<panelPort>/panel/`
- 若 `modules.webPanel=false`，面板接口返回 404，但 `/ws/` 仍可用。
- 配置 SSL 证书后自动启用 wss 连接。

---

## 项目结构

```
phoneshell/
├── pc/              # Windows 桌面端（C# / WPF / .NET 8）
│   └── src/
│       ├── PhoneShell.App/      # WPF 应用 + WebView2 终端
│       └── PhoneShell.Core/     # 核心库：ConPTY、Relay、协议、AI
├── harmony/         # HarmonyOS 端（ArkTS）
├── phoneshell/      # Android 端（Flutter）
└── linux2/          # Linux 服务端（Node.js / TypeScript）
    ├── src/                     # 服务端源码
    │   ├── relay/               # WebSocket Relay 服务器/客户端
    │   ├── terminal/            # node-pty 终端管理
    │   ├── auth/                # 令牌、邀请码、QR 码
    │   └── protocol/            # 消息协议序列化
    ├── web/                     # Vue.js Web 管理面板
    └── deploy/                  # 安装脚本与 systemd 配置
```

---

## 技术栈

| 组件 | 技术 |
|------|------|
| Windows 桌面端 | C# / WPF / .NET 8 / WebView2 / ConPTY / xterm.js |
| Linux 服务端 | Node.js / TypeScript / node-pty / WebSocket (ws) |
| Web 管理面板 | Vue.js / xterm.js / Vite |
| Android 端 | Flutter / WebView / mobile_scanner |
| HarmonyOS 端 | ArkTS / WebView / 相机扫码 |
| 通信协议 | WebSocket + JSON 消息（支持 TLS/wss） |

---

## 许可证

本项目采用 GNU AGPL-3.0 许可证，详见 [LICENSE](LICENSE)。
