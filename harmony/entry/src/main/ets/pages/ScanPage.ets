import { hilog } from '@kit.PerformanceAnalysisKit';
import { scanBarcode } from '@kit.ScanKit';
import { AppColors, AppSizes, AppStrings, ConnectionState, StorageKeys } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';
import { PreferencesUtil } from '../services/PreferencesUtil';
import { GroupMemberInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const PAGE_TAG = 'ScanPage';

@Entry
@Component
struct ScanPage {
  @State scanResult: string = '';
  @State scanStatus: string = '';
  @State isScanning: boolean = false;
  @State parseError: string = '';
  @State showMigrationDialog: boolean = false;
  @State migrationHint: string = '';
  private pendingServerDeviceId: string = '';

  aboutToAppear(): void {
    hilog.info(DOMAIN, PAGE_TAG, 'ScanPage aboutToAppear');
  }

  private startScan(): void {
    this.isScanning = true;
    this.scanStatus = '正在启动扫码...';
    this.parseError = '';

    let options: scanBarcode.ScanOptions = {
      scanTypes: [],
      enableMultiMode: false,
      enableAlbum: true
    };

    scanBarcode.startScanForResult(getContext(this), options)
      .then((result: scanBarcode.ScanResult) => {
        this.isScanning = false;
        const value: string = result.originalValue;
        hilog.info(DOMAIN, PAGE_TAG, 'scan result=%{public}s', value);
        this.scanResult = value;
        this.scanStatus = '扫码成功';
        this.handleScanResult(value);
      })
      .catch((err: Error) => {
        this.isScanning = false;
        hilog.error(DOMAIN, PAGE_TAG, 'scan error=%{public}s', err.message);
        this.scanStatus = '扫码失败: ' + err.message;
      });
  }

  private handleScanResult(payload: string): void {
    // Expected formats:
    //   phoneshell://bind?server=...&groupId=...&groupSecret=...&serverDeviceId=...
    //   phoneshell://login?server=...&groupId=...&requestId=...
    if (!payload.startsWith('phoneshell://')) {
      this.parseError = '无效的二维码格式';
      return;
    }

    // Detect scheme: bind or login
    const isLogin: boolean = payload.startsWith('phoneshell://login');

    const params: Map<string, string> = this.parseQrParams(payload);
    const server: string = params.get('server') ?? '';
    const groupId: string = params.get('groupId') ?? '';
    const groupSecret: string = params.get('groupSecret') ?? '';
    const serverDeviceId: string = params.get('serverDeviceId') ?? '';
    const requestId: string = params.get('requestId') ?? '';

    if (isLogin) {
      hilog.info(DOMAIN, PAGE_TAG, 'login QR: server=%{public}s groupId=%{public}s requestId=%{public}s',
        server, groupId, requestId);
      this.scanStatus = `登录码: server=${server.length > 0 ? server.substring(0, 30) : '(空)'}, requestId=${requestId.length > 0 ? requestId.substring(0, 8) : '(空)'}`;

      if (server.length === 0 || requestId.length === 0) {
        this.parseError = `登录码缺少必要参数 (server=${server.length > 0 ? '有' : '无'}, requestId=${requestId.length > 0 ? '有' : '无'})`;
        return;
      }

      this.handleLoginQr(server, groupId, requestId);
      return;
    }

    // --- Bind QR flow (existing logic) ---
    // Debug: show parsed params
    hilog.info(DOMAIN, PAGE_TAG, 'parsed params: server=%{public}s groupId=%{public}s groupSecret=%{public}s',
      server, groupId, groupSecret.length > 0 ? groupSecret.substring(0, 8) + '...' : '(empty)');
    this.scanStatus = `解析: server=${server.length > 0 ? server.substring(0, 30) : '(空)'}, ` +
      `groupId=${groupId.length > 0 ? groupId.substring(0, 8) : '(空)'}, ` +
      `secret=${groupSecret.length > 0 ? '有' : '(空)'}`;

    if (server.length === 0 || groupSecret.length === 0) {
      this.parseError = `二维码缺少必要参数 (server=${server.length > 0 ? '有' : '无'}, secret=${groupSecret.length > 0 ? '有' : '无'})`;
      return;
    }

    const alreadyJoined: boolean = WebSocketService.getIsGroupJoined();
    const currentGroupId: string = WebSocketService.getGroupId();
    const isSameGroup: boolean = alreadyJoined && currentGroupId.length > 0 && currentGroupId === groupId;

    if (isSameGroup && serverDeviceId.length > 0) {
      const members: Array<GroupMemberInfo> = WebSocketService.getCachedGroupMembers();
      let currentServerId: string = '';
      for (let i: number = 0; i < members.length; i++) {
        const member: GroupMemberInfo = members[i];
        if (member.role === 'Server') {
          currentServerId = member.deviceId;
          break;
        }
      }
      if (currentServerId.length > 0 && currentServerId === serverDeviceId) {
        this.scanStatus = '当前已使用该服务器，无需迁移';
        return;
      }

      this.pendingServerDeviceId = serverDeviceId;
      this.migrationHint = '检测到已加入该群组。继续将导致出现两个 relay-server。\n是否将此设备指定为新的 relay-server？';
      this.showMigrationDialog = true;
      return;
    }

    hilog.info(DOMAIN, PAGE_TAG, 'connecting to server=%{public}s groupId=%{public}s', server, groupId);

    // Save settings
    PreferencesUtil.setServerAddress(getContext(this), server);
    PreferencesUtil.setGroupSecret(getContext(this), groupSecret);

    // Connect to server (append token for handshake auth)
    this.scanStatus = `正在连接 ${server.substring(0, 40)}...`;
    const connectUrl: string = this.buildConnectUrlWithToken(server, groupSecret);
    WebSocketService.connect(connectUrl);

    // After connection, join group and bind mobile
    let didHandleConnected: boolean = false;
    let stateListenerId: number = -1;
    let msgListenerId: number = -1;
    const connectTimeoutMs: number = 8000;
    const timeoutId: number = setTimeout(() => {
      if (!didHandleConnected) {
        this.scanStatus = '连接失败，请检查网络或服务器地址';
        this.parseError = '无法连接到服务器';
        if (stateListenerId !== -1) {
          WebSocketService.removeOnStateChange(stateListenerId);
        }
      }
    }, connectTimeoutMs);

    const handleConnected = () => {
      if (didHandleConnected) {
        return;
      }
      didHandleConnected = true;
      clearTimeout(timeoutId);
      if (stateListenerId !== -1) {
        WebSocketService.removeOnStateChange(stateListenerId);
      }

      this.scanStatus = '已连接，正在加入群组...';

      const mobileDeviceId: string = PreferencesUtil.getMobileDeviceId(getContext(this));
      const displayName: string = 'HarmonyOS Device';

      // Join group first
      WebSocketService.joinGroup(groupSecret, mobileDeviceId, displayName);

      // Listen for group join acceptance to bind mobile
      msgListenerId = WebSocketService.addOnMessage((type: string, _data: Record<string, Object>) => {
        if (type === 'group.join.accepted') {
          WebSocketService.removeOnMessage(msgListenerId);
          this.scanStatus = '已加入群组，正在绑定手机...';
          const joinedGroupId: string = WebSocketService.getGroupId();
          WebSocketService.bindMobile(joinedGroupId, mobileDeviceId, displayName);
          hilog.info(DOMAIN, PAGE_TAG, 'group joined, binding mobile');

          // Navigate back
          this.getUIContext().getRouter().back();
        } else if (type === 'group.join.rejected') {
          WebSocketService.removeOnMessage(msgListenerId);
          this.parseError = '加入群组被拒绝';
          this.scanStatus = '加入群组失败';
        } else if (type === 'error') {
          const errMsg: Object | undefined = _data['message'];
          this.parseError = `错误: ${typeof errMsg === 'string' ? errMsg : '未知'}`;
        }
      });
    };

    stateListenerId = WebSocketService.addOnStateChange((state: ConnectionState) => {
      if (state === ConnectionState.CONNECTED) {
        handleConnected();
      } else if (state === ConnectionState.DISCONNECTED && !didHandleConnected) {
        this.scanStatus = '连接断开，正在重试...';
      }
    });

    // Handle race where connection is already established before listener attaches
    if (WebSocketService.getConnectionState() === ConnectionState.CONNECTED) {
      handleConnected();
    }
  }

  private confirmMigration(): void {
    this.showMigrationDialog = false;

    if (this.pendingServerDeviceId.length === 0) {
      this.parseError = '缺少服务器设备 ID，无法迁移';
      return;
    }

    if (WebSocketService.getConnectionState() !== ConnectionState.CONNECTED) {
      this.parseError = '请先连接当前服务器后再发起迁移';
      return;
    }

    this.parseError = '';
    this.scanStatus = '已发起服务器迁移请求，请在当前服务器确认...';
    WebSocketService.sendServerChangeRequest(this.pendingServerDeviceId);
    this.pendingServerDeviceId = '';
  }

  private cancelMigration(): void {
    this.showMigrationDialog = false;
    this.pendingServerDeviceId = '';
    this.scanStatus = '已取消服务器迁移';
  }

  /**
   * Handle login QR: connect to server using saved groupSecret, join group, then send panel.login.scan.
   * Server will verify this is the bound mobile and send an auth request for the user to confirm.
   */
  private async handleLoginQr(server: string, groupId: string, requestId: string): Promise<void> {
    // Get saved groupSecret from preferences
    const savedSecret: string = await PreferencesUtil.getString(StorageKeys.GROUP_SECRET);
    if (savedSecret.length === 0) {
      this.parseError = '本机未保存群组密钥，请先使用绑定码绑定';
      return;
    }

    hilog.info(DOMAIN, PAGE_TAG, 'login QR: connecting with saved secret');
    this.scanStatus = `正在连接 ${server.substring(0, 40)}...`;

    // Connect to server
    const connectUrl: string = this.buildConnectUrlWithToken(server, savedSecret);
    WebSocketService.connect(connectUrl);

    let didHandleConnected: boolean = false;
    let stateListenerId: number = -1;
    let msgListenerId: number = -1;
    const connectTimeoutMs: number = 8000;
    const timeoutId: number = setTimeout(() => {
      if (!didHandleConnected) {
        this.scanStatus = '连接失败，请检查网络或服务器地址';
        this.parseError = '无法连接到服务器';
        if (stateListenerId !== -1) {
          WebSocketService.removeOnStateChange(stateListenerId);
        }
      }
    }, connectTimeoutMs);

    const mobileDeviceId: string = PreferencesUtil.getMobileDeviceId(getContext(this));
    const displayName: string = 'HarmonyOS Device';

    const handleConnected = () => {
      if (didHandleConnected) {
        return;
      }
      didHandleConnected = true;
      clearTimeout(timeoutId);
      if (stateListenerId !== -1) {
        WebSocketService.removeOnStateChange(stateListenerId);
      }

      this.scanStatus = '已连接，正在加入群组...';

      // Join group first
      WebSocketService.joinGroup(savedSecret, mobileDeviceId, displayName);

      // Listen for group join acceptance then send panel.login.scan
      msgListenerId = WebSocketService.addOnMessage((type: string, _data: Record<string, Object>) => {
        if (type === 'group.join.accepted') {
          WebSocketService.removeOnMessage(msgListenerId);
          this.scanStatus = '已加入群组，正在发送登录扫码确认...';
          hilog.info(DOMAIN, PAGE_TAG, 'group joined for login, sending panel.login.scan');

          // Send panel.login.scan to notify server
          WebSocketService.sendPanelLoginScan(requestId, mobileDeviceId);
          this.scanStatus = '已发送登录请求，请在手机端确认授权...';

          // Navigate back — auth request will appear via AuthorizationPage
          this.getUIContext().getRouter().back();
        } else if (type === 'group.join.rejected') {
          WebSocketService.removeOnMessage(msgListenerId);
          this.parseError = '加入群组被拒绝，密钥可能已变更';
          this.scanStatus = '加入群组失败';
        } else if (type === 'error') {
          const errMsg: Object | undefined = _data['message'];
          this.parseError = `错误: ${typeof errMsg === 'string' ? errMsg : '未知'}`;
        }
      });
    };

    stateListenerId = WebSocketService.addOnStateChange((state: ConnectionState) => {
      if (state === ConnectionState.CONNECTED) {
        handleConnected();
      } else if (state === ConnectionState.DISCONNECTED && !didHandleConnected) {
        this.scanStatus = '连接断开，正在重试...';
      }
    });

    // Handle race where connection is already established
    if (WebSocketService.getConnectionState() === ConnectionState.CONNECTED) {
      handleConnected();
    }
  }

  private buildConnectUrlWithToken(baseUrl: string, groupSecret: string): string {
    const trimmedSecret: string = groupSecret.trim();
    if (trimmedSecret.length === 0) {
      return baseUrl;
    }

    if (baseUrl.indexOf('token=') >= 0) {
      return baseUrl;
    }

    const token: string = encodeURIComponent(trimmedSecret);
    if (baseUrl.indexOf('?') >= 0) {
      return baseUrl + '&token=' + token;
    }
    return baseUrl + '?token=' + token;
  }

  private parseQrParams(payload: string): Map<string, string> {
    const result: Map<string, string> = new Map();
    const questionIdx: number = payload.indexOf('?');
    if (questionIdx < 0) {
      return result;
    }
    const queryStr: string = payload.substring(questionIdx + 1);
    const pairs: Array<string> = queryStr.split('&');
    for (let i: number = 0; i < pairs.length; i++) {
      const pair: string = pairs[i];
      const eqIdx: number = pair.indexOf('=');
      if (eqIdx > 0) {
        const key: string = pair.substring(0, eqIdx);
        const value: string = decodeURIComponent(pair.substring(eqIdx + 1));
        result.set(key, value);
      }
    }
    return result;
  }

  build() {
    Stack() {
      Column() {
        // Header
        Row() {
          Button('< 返回')
            .height(32)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .backgroundColor(Color.Transparent)
            .onClick(() => {
              this.getUIContext().getRouter().back();
            })

          Blank()

          Text('扫码连接')
            .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Bold)

          Blank()
          Text('').width(60)
        }
        .width('100%')
        .height(56)
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
        .alignItems(VerticalAlign.Center)

        Divider().color(AppColors.DIVIDER).width('100%')

        // Content
        Column() {
          Text('扫描服务器二维码')
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .margin({ bottom: 8 })

          Text('使用 PC 端生成的 QR 码进行扫码连接，自动加入群组并绑定手机。')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .textAlign(TextAlign.Center)
            .margin({ bottom: 24 })
            .padding({ left: 20, right: 20 })

          Button(this.isScanning ? '扫码中...' : '开始扫码')
            .width(200)
            .height(44)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.BACKGROUND)
            .backgroundColor(AppColors.ACCENT)
            .borderRadius(AppSizes.BORDER_RADIUS_CARD)
            .enabled(!this.isScanning)
            .onClick(() => {
              this.startScan();
            })
            .margin({ bottom: 20 })

          if (this.scanStatus.length > 0) {
            Text(this.scanStatus)
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_SECONDARY)
              .margin({ bottom: 8 })
          }

          if (this.parseError.length > 0) {
            Text(this.parseError)
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.ERROR_RED)
              .margin({ bottom: 8 })
          }

          if (this.scanResult.length > 0) {
            Column() {
              Text('扫码结果')
                .fontSize(AppSizes.FONT_SIZE_SMALL)
                .fontColor(AppColors.TEXT_SECONDARY)
                .margin({ bottom: 4 })
              Text(this.scanResult)
                .fontSize(12)
                .fontColor(AppColors.TEXT_PRIMARY)
                .fontFamily('monospace')
                .copyOption(CopyOptions.InApp)
                .maxLines(3)
                .textOverflow({ overflow: TextOverflow.Ellipsis })
            }
            .width('100%')
            .padding(AppSizes.PADDING_CARD)
            .backgroundColor(AppColors.CARD_BACKGROUND)
            .borderRadius(AppSizes.BORDER_RADIUS_CARD)
            .alignItems(HorizontalAlign.Start)
          }
        }
        .width('100%')
        .layoutWeight(1)
        .justifyContent(FlexAlign.Center)
        .alignItems(HorizontalAlign.Center)
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
      }
      .width('100%')
      .height('100%')
      .backgroundColor(AppColors.BACKGROUND)

      if (this.showMigrationDialog) {
        Column() {
          Column() {
            Text('服务器迁移确认')
              .fontSize(18)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
              .margin({ bottom: 10 })

            Text(this.migrationHint)
              .fontSize(13)
              .fontColor(AppColors.TEXT_SECONDARY)
              .textAlign(TextAlign.Center)
              .margin({ bottom: 18 })

            Row({ space: 12 }) {
              Button('取消')
                .width(120)
                .height(40)
                .fontSize(AppSizes.FONT_SIZE_SMALL)
                .fontColor(AppColors.TEXT_PRIMARY)
                .backgroundColor(Color.Transparent)
                .borderWidth(1)
                .borderColor(AppColors.TEXT_SECONDARY)
                .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
                .onClick(() => {
                  this.cancelMigration();
                })

              Button('设为服务器')
                .width(140)
                .height(40)
                .fontSize(AppSizes.FONT_SIZE_SMALL)
                .fontColor('#FFFFFF')
                .backgroundColor(AppColors.ACCENT)
                .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
                .onClick(() => {
                  this.confirmMigration();
                })
            }
            .width('100%')
            .justifyContent(FlexAlign.Center)
          }
          .width('100%')
          .padding(24)
          .backgroundColor(AppColors.CARD_BACKGROUND)
          .borderRadius(16)
          .alignItems(HorizontalAlign.Center)
        }
        .width('100%')
        .height('100%')
        .padding({ left: 24, right: 24 })
        .backgroundColor('#90000000')
        .justifyContent(FlexAlign.Center)
      }
    }
    .width('100%')
    .height('100%')
  }
}
