# PhoneShell PC 客户端（Phase 1）

本模块为 Windows 桌面端客户端（WPF），用于展示二维码、托管本地 PowerShell 会话并提供基础控制权状态。

## 环境要求
- Windows 10/11
- .NET SDK 8.0+（项目目标为 `net8.0-windows`）
- PowerShell（优先 `pwsh.exe`，否则使用 `powershell.exe`）

## 常用命令
- 构建：`dotnet build pc/PhoneShell.sln`
- 运行：`dotnet run --project pc/src/PhoneShell.App/PhoneShell.App.csproj`

## 运行说明
- 首次启动会在应用目录下生成 `data/device.json`，用于保存设备 ID 与显示名称。
- 当前已支持局域网内手动连接调试：
  - PC 勾选 `Enable Relay Server` 后启动本机中转服务。
  - 启动后界面会显示可从手机访问的真实 `ws://<LAN-IP>:9090/ws/` 地址。
  - HarmonyOS 客户端当前使用手动输入地址连接；二维码绑定流程暂未完成。
