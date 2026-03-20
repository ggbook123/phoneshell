# 鸿蒙转 Flutter 实现清单与优先级

目标：依据鸿蒙端 ArkTS 实现，100% 复刻 Flutter 端界面与功能。本清单以“可执行的开发任务 + 优先级 + 验收标准”形式给出，后续实现应严格对照。

说明：
- P0 = 必须先做，基础运行与关键路径。
- P1 = 核心能力与关键交互。
- P2 = 体验完善、边界与性能细节。

## P0 基础运行与框架搭建

1. 建立 Flutter 工程结构与路由
- 目标：实现与鸿蒙一致的 7 个页面路由顺序与命名。
- 页面：LoginPage、DeviceManagePage、SettingsPage、UsageGuidePage、AboutPage、SessionListPage、TerminalPage。
- 验收：启动直接进入 LoginPage；全局路由可跳转全部页面。

2. 全局主题与设计 Token
- 目标：完整复刻颜色/尺寸/字体规范。
- 配置：主题色、背景色、Card、Chip、Button、Divider；统一 TextStyle。
- 验收：各页面颜色/字体/间距一致，确保与鸿蒙端视觉一致。

3. SafeArea 与系统 UI 处理
- 目标：页面 padding 和 overlay 遮罩等整体对齐鸿蒙端。
- 行为：页面内边距全部叠加安全区；Terminal 页需考虑键盘避让。
- 验收：异形屏/刘海屏不遮挡内容，底部快捷栏不被键盘覆盖。

4. 资源接入
- 目标：接入所有 icon 与图片资源。
- 资源：phoneshell.png, phoneshell_header.png, ic_close.svg, startIcon.png, terminal html/js/css。
- 验收：各页面显示正确，SessionList 关闭按钮图标一致。

5. 基础本地化与语言切换
- 目标：实现 zh/en 语言切换（仅 LoginPage 提供按钮）。
- 行为：语言存储 `app_language`，重启后保持。
- 验收：LoginPage 切换后 UI 文案同步更新。

## P1 核心功能与关键交互

6. Preferences 持久化层
- 目标：实现鸿蒙端偏好存储键与行为一致。
- Keys：connection_history, last_server_url, group_secret, mobile_device_id, single_devices, group_relay_url, group_id, app_language。
- 验收：重启后能恢复群组与单连设备，语言、ID 还原。

7. WebSocket 连接模块（DeviceConnection）
- 目标：实现自动重连与状态监听。
- 行为：连接状态 DISCONNECTED/CONNECTING/CONNECTED，指数退避重连（1000ms 基准，最多 10 次，最大 30s）。
- 验收：断线后自动重连，超过次数停止重连。

8. 连接与模式管理（ConnectionManager）
- 目标：严格复刻单连/群组/待机模式切换逻辑。
- 行为：单连设备连接后进入 SINGLE；群组连接后进入 GROUP；群组断开回落到 SINGLE/ STANDALONE。
- 验收：设备列表与当前模式一致，切换无错误。

9. 设备管理页（DeviceManagePage）
- 目标：全功能复刻。
- 内容：顶部栏+SettingsChip+AUTH badge；GroupSection/ScanConnectSection/PendingSection；DeviceCard 与长按。
- 弹层：设备设置、授权弹窗、邀请确认。
- 验收：所有状态显示正确、点击跳转正确，长按/扫码/弹窗行为与鸿蒙一致。

10. 扫码流程与二维码解析
- 目标：完整实现 connect/bind/login 三种二维码逻辑。
- 规则：
  - connect：未入群直接 connectSingle；已入群 -> 邀请加入群组。
  - bind：未入群 -> connectGroup + join + mobile.bind；已入群 -> 邀请。
  - login：未入群 -> 等待 join 后 send panel.login.scan；已入群直接发送。
- 验收：扫码后状态/错误提示与鸿蒙一致。

11. SessionListPage
- 目标：显示会话列表、刷新、创建、关闭、重命名。
- 行为：
  - 下拉刷新请求 session.list。
  - 新建会话发送 terminal.open。
  - 长按重命名弹窗。
  - 点击进入 TerminalPage。
- 验收：会话列表、空状态、弹窗逻辑一致。

12. TerminalPage（WebView + JS Bridge）
- 目标：接入 xterm.js 与桥接 API。
- JS Bridge：nativeApp.onTerminalReady/onTerminalResize/sendInput/onTerminalInteraction。
- Native 注入：window.phoneShell.write/clear/focus/syncResize。
- 验收：终端显示、输入、快捷键、历史加载、resize 与鸿蒙一致。

13. SettingsPage / UsageGuidePage / AboutPage
- 目标：复刻静态 UI 与跳转。
- 验收：UI 文案/卡片结构/标签一致，跳转无误。

14. 授权管理（AuthManager）
- 目标：实现授权请求队列，UI 调用 approve/reject。
- 行为：
  - auth.request 入队
  - AUTH badge 显示待办数量
  - showPending 弹出最早请求
- 验收：审批结果发送 auth.response，队列 TTL 5 分钟。

## P2 体验与性能细节

15. 终端历史加载与缓冲
- 目标：复刻历史分页 + 缓冲替换逻辑。
- 行为：请求 terminal.history.request，缓存拼接，完成后替换终端 buffer。
- 验收：大量输出时不卡顿，历史加载遮罩一致。

16. 终端自定义滚动条与手势
- 目标：复刻 terminal/index.html 中的自定义滚动条逻辑。
- 验收：滚动条拖拽、展开/收起、惯性滑动与鸿蒙一致。

17. 设备与会话本地更新优化
- 目标：本地立即更新名称/状态，减少等待服务端回传。
- 验收：重命名/移除时 UI 即时反馈。

18. 细节一致性检查
- 包括：按钮禁用态、Chip 文案（例如 `OPEN` / `GUIDE` / `ABOUT` / `AUTH` 固定英文）。
- 验收：所有页面文案与排版与鸿蒙一致。

## 建议实现顺序（依赖链）

1. P0-1 ~ P0-5
2. P1-6 ~ P1-8（基础连接层）
3. P1-9 ~ P1-10（设备管理页 + 扫码）
4. P1-11（会话列表）
5. P1-12（终端）
6. P1-13 ~ P1-14（设置/引导/授权）
7. P2-15 ~ P2-18（细节完善）

## Flutter 模块拆分建议

- `core/theme/`：颜色、尺寸、文本样式。
- `core/storage/`：SharedPreferences 包装（PreferencesUtil）。
- `core/network/`：WebSocket 连接、HTTP invite。
- `core/protocol/`：消息模型定义。
- `core/connection/`：ConnectionManager + AuthManager。
- `pages/`：7 个页面。
- `widgets/`：通用组件（Chip、Card、Dialog）。
- `terminal/`：WebView + xterm 资源。

## 验收对照

- UI：每个页面与鸿蒙端对齐（布局/颜色/字体/间距/文案）。
- 功能：扫码、设备管理、会话创建/关闭/重命名、终端输入输出、授权弹窗。
- 连接：单连/群组模式切换与恢复。
- 稳定性：断线自动重连、终端历史加载无崩溃。
