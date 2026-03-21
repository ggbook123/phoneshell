# PhoneShell PC 客户端（Phase 1）

本模块为 Windows 桌面端客户端（WPF），用于展示二维码、托管本地 PowerShell 会话并提供基础控制权状态。

## 环境要求
- Windows 10/11
- .NET SDK 8.0+（项目目标为 `net8.0-windows`）
- PowerShell（优先 `pwsh.exe`，否则使用 `powershell.exe`）

## 常用命令
- 构建：`dotnet build pc/PhoneShell.sln`
- 运行：`dotnet run --project pc/src/PhoneShell.App/PhoneShell.App.csproj`

## 打包与签名（安装包）
- 前置：安装 Inno Setup 6（`iscc.exe`），安装 Windows SDK（`signtool.exe`），准备代码签名证书 `.pfx`
- 构建安装包（不签名）：`pwsh pc/scripts/build-installer.ps1`
- 构建并签名（自签名，免费）：`pwsh pc/scripts/build-installer.ps1 -SelfSign`
- 构建并签名（已有证书）：`$env:PHONESHELL_CERT_PFX="..."; $env:PHONESHELL_CERT_PASSWORD="..."; pwsh pc/scripts/build-installer.ps1 -Sign`
- 默认安装路径：`C:\Users\<当前用户>\PhoneShell`
- 输出位置：`pc/installer/out/PhoneShell-Setup-<版本>.exe`
- 打包脚本会自动移除 `publish/data` 与 `*.log`，避免携带本机敏感信息
- 运行依赖：目标机器需安装 Microsoft Edge WebView2 Runtime（Evergreen）

### 自签名安装包的信任
自签名不会被 Windows 公共信任。要让“其他电脑”不再弹“未知发布者”，请在目标电脑导入证书：
- 证书文件：`pc/installer/certs/PhoneShell-Dev-CodeSign.cer`
- 导入位置（两处都导入）：
`Trusted Root Certification Authorities`
`Trusted Publishers`

## 运行说明
- 首次启动会在应用目录下生成 `data/device.json`，用于保存设备 ID 与显示名称。
- 当前已支持局域网内手动连接调试：
  - PC 勾选 `Enable Relay Server` 后启动本机中转服务。
  - 启动后界面会显示可从手机访问的真实 `ws://<LAN-IP>:9090/ws/` 地址。
  - HarmonyOS 客户端当前使用手动输入地址连接；二维码绑定流程暂未完成。
