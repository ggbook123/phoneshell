# PhoneShell Linux2 部署指南

本指南适用于在一台全新的 Linux 机器上部署 `linux2` 端服务，并给出常用命令。暂不包含 npm 全局安装方式。

## 前置要求
- Linux（支持 systemd）
- Node.js >= 18
- npm
- 构建依赖（node-pty 需要）：`python3`、`make`、`g++`

## 安装步骤（首次部署）
1. 安装依赖（Debian/Ubuntu 示例）
```bash
sudo apt-get update
sudo apt-get install -y git nodejs npm python3 build-essential
```

2. 拉取代码并进入仓库
```bash
git clone https://github.com/ggbook123/phoneshell phoneshell
cd phoneshell
```

3. 运行交互式安装（推荐）
```bash
sudo bash linux2/deploy/phoneshell install
```

安装菜单说明：
- 1 设置核心端口（默认 19090）
- 2 是否启用 Web 面板
- 3 启用面板时设置面板端口（默认 9090）
- 4 开始安装

安装完成后会自动注册 systemd 服务，并写入 `/usr/local/bin/phoneshell` 命令。

## 启动与运行
服务安装后默认已启动，如需手动启动或重启：
```bash
sudo systemctl restart phoneshell
```

进入 CLI（默认：有会话就附着，没有会话就新建）：
```bash
phoneshell
```

## 常用命令
- 列出会话
```bash
phoneshell list
```

- 选择并附着到已有会话
```bash
phoneshell attach
```

- 指定 shell 新建会话
```bash
phoneshell local --shell bash
```

- 查看服务状态
```bash
phoneshell status
```

- 查看服务日志
```bash
phoneshell logs
```

- 重置 group（清除 `group.json` 与 `group-membership.json`，需重启服务）
```bash
phoneshell group reset
sudo systemctl restart phoneshell
```
或一键重置并重启：
```bash
phoneshell group reset --restart
```

- 打开设置菜单（端口/面板/重置/服务/卸载/状态）
```bash
phoneshell set
```

- 启停服务
```bash
phoneshell start
phoneshell stop
phoneshell restart
```

## 配置文件
默认配置文件路径：
```text
/etc/phoneshell/config.json
```

常见字段：
- `port`: 核心端口（默认 19090）
- `panelPort`: Web 面板端口（默认 9090）
- `modules.webPanel`: 是否启用 Web 面板

修改配置后需重启服务：
```bash
sudo systemctl restart phoneshell
```

## Web 面板关闭说明
当 `modules.webPanel=false` 时，以下 URL 会返回 404：
- `/`
- `/panel/*`
- `/api/panel/*`

但 WebSocket `/ws/` 仍可用，CLI 与移动端连接不受影响。
