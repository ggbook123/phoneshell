# PhoneShell Linux Headless

Linux 目录提供 PhoneShell 的无界面服务端实现，适合云主机、开发机、跳板机和机房节点部署。

## 当前能力
- 作为 Relay Server 对外提供 `ws://<host>:<port>/ws/` 连接入口
- 作为 Relay Client 连接上级 Relay 节点
- 在 Linux 上通过 PTY 创建和管理终端会话
- 支持共享 Bearer Token 鉴权
- 提供基础运维接口：`/ws/healthz`、`/ws/status`
- 提供 Web 管理面板：`http://<host>:<port>/panel`

## 构建与运行
- 构建：`dotnet build linux/PhoneShell.Linux.sln`
- 查看帮助：`dotnet linux/PhoneShell.Headless/bin/Debug/net8.0/PhoneShell.Headless.dll --help`
- 交互初始化：`dotnet run --project linux/PhoneShell.Headless/PhoneShell.Headless.csproj -- --setup`

说明：
- 若未配置 `relayUrl` 且未提供 `groupSecret`，服务会默认以 relay-server 模式启动并开启 Web 面板，便于首次扫码绑定。

## 常见启动方式
- Relay Server：`dotnet run --project linux/PhoneShell.Headless/PhoneShell.Headless.csproj -- --mode server --port 9090 --relay-token <token>`
- Relay Client：`dotnet run --project linux/PhoneShell.Headless/PhoneShell.Headless.csproj -- --mode client --relay ws://relay.example.com:9090 --relay-token <token>`

## 配置优先级
- `config.json`
- 环境变量
- CLI 参数

支持的环境变量：
- `PHONESHELL_MODE`
- `PHONESHELL_NAME`
- `PHONESHELL_PORT`
- `PHONESHELL_RELAY_URL`
- `PHONESHELL_RELAY_TOKEN`

默认配置文件位置：
- Linux：`~/.config/phoneshell/config.json`

示例配置见 `linux/config.example.json:1`。

## 运维接口
- 健康检查：`http://<host>:<port>/ws/healthz`
- 运行状态：`http://<host>:<port>/ws/status`

说明：
- `healthz` 不要求鉴权，适合存活探针
- `status` 在配置了 `relayAuthToken` 后必须带 `Authorization: Bearer <token>`

示例：
- `curl http://127.0.0.1:9090/ws/healthz`
- `curl -H "Authorization: Bearer <token>" http://127.0.0.1:9090/ws/status`

## 商业部署建议
- 生产环境务必配置 `relayAuthToken`
- 外网部署时应在反向代理或网关层加 TLS，优先使用 `wss://`
- 使用 `systemd` 托管进程，避免直接前台常驻
- 通过环境文件注入密钥，不要把生产密钥写入仓库

## systemd 部署
- 服务模板：`linux/systemd/phoneshell.service:1`
- 环境变量样例：`linux/phoneshell.env.example:1`

建议目录：
- 程序：`/opt/phoneshell/`
- 配置：`/etc/phoneshell/config.json`
- 环境文件：`/etc/phoneshell/phoneshell.env`

示例发布命令：
- `dotnet publish linux/PhoneShell.Headless/PhoneShell.Headless.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o linux/publish/linux-x64`
- `dotnet publish linux/PhoneShell.Headless/PhoneShell.Headless.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -o linux/publish/linux-arm64`

## 已知未完成项
- `ai-chat` 仅保留模块开关，尚未接入 Linux Headless 流程
- 还没有独立测试项目，当前以构建和联调验证为主
