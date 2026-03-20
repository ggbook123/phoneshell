class AppColors {
  static const background = 0xFF07090D;
  static const surface1 = 0xFF0B1017;
  static const surface2 = 0xFF0F1520;
  static const surface3 = 0xFF141B29;
  static const cardBackground = 0xFF0F1520;
  static const cardBorder = 0xFF1C2A3C;
  static const accent = 0xFF00E5C3;
  static const accentPressed = 0xFF00BFA4;
  static const accentBlue = 0xFF4DA6FF;
  static const accentPink = 0xFFFF2EC4;
  static const accentYellow = 0xFFF5C542;
  static const accentDim = 0xFF0C2E2C;
  static const textPrimary = 0xFFF0F6FF;
  static const textSecondary = 0xFFB5C0D0;
  static const textMuted = 0xFF6E7A8A;
  static const terminalBg = 0xFF07090D;
  static const terminalText = 0xFFF0F6FF;
  static const onlineGreen = 0xFF00E5C3;
  static const offlineGray = 0xFF6E7A8A;
  static const errorRed = 0xFFFF4757;
  static const inputBg = 0xFF0C141E;
  static const toolbarBg = 0xFF0B1017;
  static const divider = 0xFF152232;
  static const statusBarBg = 0xCC0B1017;
  static const chipBg = 0xFF0A111A;
  static const chipBorder = 0xFF1B2A3A;
  static const highlight = 0xFF0D1B24;
  static const glow = 0xFF102632;
}

class AppSizes {
  static const double borderRadiusCard = 12;
  static const double borderRadiusButton = 10;
  static const double borderRadiusInput = 8;
  static const double borderRadiusTag = 6;
  static const double fontSizeTitle = 24;
  static const double fontSizeSubtitle = 18;
  static const double fontSizeBody = 16;
  static const double fontSizeSmall = 14;
  static const double fontSizeTerminal = 13;
  static const double paddingPage = 20;
  static const double paddingCard = 16;
  static const double paddingSmall = 8;
  static const double statusDotSize = 8;
  static const double shortcutKeyHeight = 36;
}

class AppStrings {
  static const appName = 'PhoneShell';
  static const statusDisconnected = '未连接';
  static const statusConnecting = '连接中...';
  static const statusConnected = '已连接';
  static const connectButton = '连接';
  static const disconnectButton = '断开';
  static const deviceListTitle = '设备列表';
  static const emptyDeviceList = '暂无在线设备';
  static const inputPlaceholder = '输入命令...';
  static const serverAddressHint = 'ws://192.168.1.100:9090/ws/';
  static const historyTitle = '历史连接';
  static const sendButton = '发送';
  static const terminalTitle = '终端';
  static const sessionListTitle = '会话列表';
  static const emptySessionList = '暂无活动会话';
  static const newSessionButton = '新建会话';
  static const groupSecretHint = '输入群组密钥';
  static const groupSecretLabel = '群组密钥';
  static const groupJoinButton = '加入群组';
  static const groupStatusJoined = '已加入群组';
  static const groupStatusNotJoined = '未加入群组';
  static const groupMembersTitle = '群组成员';
  static const groupManageTitle = '群组管理';
  static const scanConnectButton = '扫码连接';
  static const authRequestTitle = '授权请求';
  static const authApproveButton = '批准';
  static const authRejectButton = '拒绝';
  static const mobileBindSuccess = '手机绑定成功';
  static const mobileBindFailed = '手机绑定失败';
}

enum ConnectionState { disconnected, connecting, connected }

enum DeviceMode { standalone, single, group }

class StorageKeys {
  static const connectionHistory = 'connection_history';
  static const lastServerUrl = 'last_server_url';
  static const groupSecret = 'group_secret';
  static const mobileDeviceId = 'mobile_device_id';
  static const singleDevices = 'single_devices';
  static const groupRelayUrl = 'group_relay_url';
  static const groupId = 'group_id';
  static const appLanguage = 'app_language';
}
