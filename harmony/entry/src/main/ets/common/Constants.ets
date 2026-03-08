// App-wide constants for PhoneShell HarmonyOS client

export class AppColors {
  static readonly BACKGROUND: string = '#1A1A2E';
  static readonly CARD_BACKGROUND: string = '#16213E';
  static readonly ACCENT: string = '#00D4AA';
  static readonly ACCENT_PRESSED: string = '#00B894';
  static readonly TEXT_PRIMARY: string = '#E0E0E0';
  static readonly TEXT_SECONDARY: string = '#888888';
  static readonly TERMINAL_BG: string = '#0E1116';
  static readonly TERMINAL_TEXT: string = '#E6E6E6';
  static readonly ONLINE_GREEN: string = '#00D4AA';
  static readonly OFFLINE_GRAY: string = '#555555';
  static readonly ERROR_RED: string = '#FF6B6B';
  static readonly INPUT_BG: string = '#0F3460';
  static readonly TOOLBAR_BG: string = '#16213E';
  static readonly DIVIDER: string = '#2A2A4A';
  static readonly STATUS_BAR_BG: string = '#1A1A2ECC'; // with alpha
}

export class AppSizes {
  static readonly BORDER_RADIUS_CARD: number = 12;
  static readonly BORDER_RADIUS_BUTTON: number = 24;
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
}

export enum ConnectionState {
  DISCONNECTED = 0,
  CONNECTING = 1,
  CONNECTED = 2
}

export class StorageKeys {
  static readonly CONNECTION_HISTORY: string = 'connection_history';
  static readonly LAST_SERVER_URL: string = 'last_server_url';
}
