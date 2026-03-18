// App-wide constants for PhoneShell HarmonyOS client

export class AppColors {
  static readonly BACKGROUND: string = '#07090D';
  static readonly SURFACE_1: string = '#0B1017';
  static readonly SURFACE_2: string = '#0F1520';
  static readonly SURFACE_3: string = '#141B29';
  static readonly CARD_BACKGROUND: string = '#0F1520';
  static readonly CARD_BORDER: string = '#1C2A3C';
  static readonly ACCENT: string = '#00E5C3';
  static readonly ACCENT_PRESSED: string = '#00BFA4';
  static readonly ACCENT_BLUE: string = '#4DA6FF';
  static readonly ACCENT_PINK: string = '#FF2EC4';
  static readonly ACCENT_YELLOW: string = '#F5C542';
  static readonly ACCENT_DIM: string = '#0C2E2C';
  static readonly TEXT_PRIMARY: string = '#F0F6FF';
  static readonly TEXT_SECONDARY: string = '#B5C0D0';
  static readonly TEXT_MUTED: string = '#6E7A8A';
  static readonly TERMINAL_BG: string = '#07090D';
  static readonly TERMINAL_TEXT: string = '#F0F6FF';
  static readonly ONLINE_GREEN: string = '#00E5C3';
  static readonly OFFLINE_GRAY: string = '#6E7A8A';
  static readonly ERROR_RED: string = '#FF4757';
  static readonly INPUT_BG: string = '#0C141E';
  static readonly TOOLBAR_BG: string = '#0B1017';
  static readonly DIVIDER: string = '#152232';
  static readonly STATUS_BAR_BG: string = '#0B1017CC'; // with alpha
  static readonly CHIP_BG: string = '#0A111A';
  static readonly CHIP_BORDER: string = '#1B2A3A';
  static readonly HIGHLIGHT: string = '#0D1B24';
  static readonly GLOW: string = '#102632';
}

export class AppSizes {
  static readonly BORDER_RADIUS_CARD: number = 12;
  static readonly BORDER_RADIUS_BUTTON: number = 10;
  static readonly BORDER_RADIUS_INPUT: number = 8;
  static readonly BORDER_RADIUS_TAG: number = 6;
  static readonly FONT_SIZE_TITLE: number = 24;
  static readonly FONT_SIZE_SUBTITLE: number = 18;
  static readonly FONT_SIZE_BODY: number = 16;
  static readonly FONT_SIZE_SMALL: number = 14;
  static readonly FONT_SIZE_TERMINAL: number = 13;
  static readonly PADDING_PAGE: number = 20;
  static readonly PADDING_CARD: number = 16;
  static readonly PADDING_SMALL: number = 8;
  static readonly STATUS_DOT_SIZE: number = 8;
  static readonly SHORTCUT_KEY_HEIGHT: number = 36;
}

export class AppStrings {
  static readonly APP_NAME: string = 'PhoneShell';
  static readonly STATUS_DISCONNECTED: string = '未连接';
  static readonly STATUS_CONNECTING: string = '连接中...';
  static readonly STATUS_CONNECTED: string = '已连接';
  static readonly CONNECT_BUTTON: string = '连接';
  static readonly DISCONNECT_BUTTON: string = '断开';
  static readonly DEVICE_LIST_TITLE: string = '设备列表';
  static readonly EMPTY_DEVICE_LIST: string = '暂无在线设备';
  static readonly INPUT_PLACEHOLDER: string = '输入命令...';
  static readonly SERVER_ADDRESS_HINT: string = 'ws://192.168.1.100:9090/ws/';
  static readonly HISTORY_TITLE: string = '历史连接';
  static readonly SEND_BUTTON: string = '发送';
  static readonly TERMINAL_TITLE: string = '终端';
  static readonly SESSION_LIST_TITLE: string = '会话列表';
  static readonly EMPTY_SESSION_LIST: string = '暂无活动会话';
  static readonly NEW_SESSION_BUTTON: string = '新建会话';

  // Group-related strings
  static readonly GROUP_SECRET_HINT: string = '输入群组密钥';
  static readonly GROUP_SECRET_LABEL: string = '群组密钥';
  static readonly GROUP_JOIN_BUTTON: string = '加入群组';
  static readonly GROUP_STATUS_JOINED: string = '已加入群组';
  static readonly GROUP_STATUS_NOT_JOINED: string = '未加入群组';
  static readonly GROUP_MEMBERS_TITLE: string = '群组成员';
  static readonly GROUP_MANAGE_TITLE: string = '群组管理';
  static readonly SCAN_CONNECT_BUTTON: string = '扫码连接';
  static readonly AUTH_REQUEST_TITLE: string = '授权请求';
  static readonly AUTH_APPROVE_BUTTON: string = '批准';
  static readonly AUTH_REJECT_BUTTON: string = '拒绝';
  static readonly MOBILE_BIND_SUCCESS: string = '手机绑定成功';
  static readonly MOBILE_BIND_FAILED: string = '手机绑定失败';
}

export enum ConnectionState {
  DISCONNECTED = 0,
  CONNECTING = 1,
  CONNECTED = 2
}

export enum DeviceMode {
  STANDALONE = 'standalone',
  SINGLE = 'single',
  GROUP = 'group'
}

export class StorageKeys {
  static readonly CONNECTION_HISTORY: string = 'connection_history';
  static readonly LAST_SERVER_URL: string = 'last_server_url';
  static readonly GROUP_SECRET: string = 'group_secret';
  static readonly MOBILE_DEVICE_ID: string = 'mobile_device_id';
  static readonly SINGLE_DEVICES: string = 'single_devices';
  static readonly GROUP_RELAY_URL: string = 'group_relay_url';
  static readonly GROUP_ID: string = 'group_id';
}
