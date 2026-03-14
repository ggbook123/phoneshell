import { hilog } from '@kit.PerformanceAnalysisKit';
import { scanBarcode } from '@kit.ScanKit';
import { Context } from '@kit.AbilityKit';
import { AppColors, AppSizes, ConnectionState, StorageKeys } from '../common/Constants';
import { ConnectionManager } from '../services/ConnectionManager';
import { AuthManager } from '../services/AuthManager';
import { PreferencesUtil } from '../services/PreferencesUtil';

const DOMAIN = 0x0000;
const PAGE_TAG = 'ScanPage';

@Entry
@Component
struct ScanPage {
  @State scanResult: string = '';
  @State scanStatus: string = '';
  @State isScanning: boolean = false;
  @State parseError: string = '';
  @State showInviteConfirmDialog: boolean = false;
  @State inviteDeviceName: string = '';
  private pendingPanelLoginRequestId: string = '';
  private pendingInviteHttpUrl: string = '';
  private pendingInviteDeviceId: string = '';
  private messageListenerId: number = -1;
  private stateListenerId: number = -1;

  aboutToAppear(): void {
    hilog.info(DOMAIN, PAGE_TAG, 'ScanPage aboutToAppear');
  }

  aboutToDisappear(): void {
    this.cleanupListeners();
  }

  private cleanupListeners(): void {
    if (this.messageListenerId !== -1) {
      ConnectionManager.removeOnMessage(this.messageListenerId);
      this.messageListenerId = -1;
    }
    if (this.stateListenerId !== -1) {
      ConnectionManager.removeOnStateChange(this.stateListenerId);
      this.stateListenerId = -1;
    }
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

    const hostContext: Context | undefined = this.getUIContext().getHostContext();
    if (hostContext === undefined) {
      this.isScanning = false;
      this.scanStatus = '无法获取上下文';
      return;
    }

    scanBarcode.startScanForResult(hostContext, options)
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

  private async handleScanResult(payload: string): Promise<void> {
    // Supported formats:
    //   phoneshell://connect?http=...&ws=...&deviceId=...&displayName=...   (standalone device)
    //   phoneshell://bind?server=...&groupId=...&groupSecret=...            (group bind)
    //   phoneshell://login?server=...&groupId=...&requestId=...             (panel login)
    if (!payload.startsWith('phoneshell://')) {
      this.parseError = '无效的二维码格式';
      return;
    }

    const params: Map<string, string> = this.parseQrParams(payload);

    if (payload.startsWith('phoneshell://connect')) {
      this.handleConnectQr(params);
    } else if (payload.startsWith('phoneshell://login')) {
      this.handleLoginQr(params);
    } else if (payload.startsWith('phoneshell://bind')) {
      await this.handleBindQr(params);
    } else {
      this.parseError = '未知的二维码类型';
    }
  }

  // --- Standalone connect: phone directly connects to a device via WS ---
  private handleConnectQr(params: Map<string, string>): void {
    const wsUrl: string = params.get('ws') ?? '';
    const httpUrl: string = params.get('http') ?? '';
    const deviceId: string = params.get('deviceId') ?? '';
    const displayName: string = params.get('displayName') ?? deviceId;

    hilog.info(DOMAIN, PAGE_TAG, 'connect QR: ws=%{public}s deviceId=%{public}s', wsUrl, deviceId);

    if (wsUrl.length === 0 || deviceId.length === 0) {
      this.parseError = '二维码缺少必要参数 (ws/deviceId)';
      return;
    }

    // If phone is already in a group, invite this standalone device into the group
    if (ConnectionManager.isInGroup()) {
      if (httpUrl.length === 0) {
        this.parseError = '该设备不支持邀请加入群组（缺少HTTP地址）';
        return;
      }
      hilog.info(DOMAIN, PAGE_TAG, 'Already in group, inviting device %{public}s to group', deviceId);
      this.inviteDeviceName = displayName;
      this.pendingInviteHttpUrl = httpUrl;
      this.pendingInviteDeviceId = deviceId;
      this.showInviteConfirmDialog = true;
      return;
    }

    this.scanStatus = `正在连接 ${displayName}...`;

    // Use ConnectionManager to establish single connection
    ConnectionManager.connectSingle(wsUrl, deviceId, displayName, httpUrl);

    // Navigate back to device manage page
    this.scanStatus = `已添加设备 ${displayName}`;
    this.getUIContext().getRouter().back();
  }

  // --- Invite a standalone device into the current group ---
  private inviteDeviceToGroup(): void {
    this.showInviteConfirmDialog = false;
    const httpUrl: string = this.pendingInviteHttpUrl;
    const deviceName: string = this.inviteDeviceName;

    if (httpUrl.length === 0) return;

    this.scanStatus = `正在邀请 ${deviceName} 加入群组...`;

    // Listen for invite.create.response
    this.cleanupListeners();
    this.messageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
      if (type === 'invite.create.response') {
        const inviteCode: string = (data['inviteCode'] ?? '') as string;
        const relayUrl: string = (data['relayUrl'] ?? '') as string;
        if (inviteCode.length > 0 && relayUrl.length > 0) {
          hilog.info(DOMAIN, PAGE_TAG, 'Got invite code, sending to device %{public}s', httpUrl);
          this.scanStatus = `正在发送邀请给 ${deviceName}...`;
          ConnectionManager.inviteToGroup(httpUrl, inviteCode, relayUrl);
          this.scanStatus = `已邀请 ${deviceName} 加入群组`;
          this.cleanupListeners();
          this.getUIContext().getRouter().back();
        }
      } else if (type === 'error') {
        const message: string = (data['message'] ?? '') as string;
        this.parseError = `邀请失败: ${message}`;
        this.scanStatus = '邀请失败';
        this.cleanupListeners();
      }
    });

    // Request invite code from relay server
    ConnectionManager.requestInviteCode();
  }

  // --- Group bind: connect to relay server and join group ---
  private async handleBindQr(params: Map<string, string>): Promise<void> {
    const server: string = params.get('server') ?? '';
    const groupId: string = params.get('groupId') ?? '';
    const groupSecret: string = params.get('groupSecret') ?? '';

    hilog.info(DOMAIN, PAGE_TAG, 'bind QR: server=%{public}s groupId=%{public}s', server, groupId);

    if (server.length === 0 || groupSecret.length === 0) {
      this.parseError = '二维码缺少必要参数 (server/groupSecret)';
      return;
    }

    // If already in a group, invite this device into the current group
    if (ConnectionManager.isInGroup()) {
      const currentGroupId: string = ConnectionManager.getGroupId();
      if (currentGroupId === groupId) {
        // Same group - already connected, just go back
        this.scanStatus = '已在该群组中';
        this.getUIContext().getRouter().back();
        return;
      }
      // Different server's bind QR — invite that device into our group
      const httpUrl: string = this.wsUrlToHttpUrl(server);
      if (httpUrl.length === 0) {
        this.parseError = '无法解析设备HTTP地址';
        return;
      }
      hilog.info(DOMAIN, PAGE_TAG, 'Already in group, inviting bind-QR device to current group, httpUrl=%{public}s', httpUrl);
      this.inviteDeviceName = params.get('serverDeviceId') ?? '新设备';
      this.pendingInviteHttpUrl = httpUrl;
      this.pendingInviteDeviceId = '';
      this.showInviteConfirmDialog = true;
      return;
    }

    await this.executeBindQr(server, groupId, groupSecret);
  }

  // Derive HTTP URL from WebSocket URL (ws://host:port/ws/ -> http://host:port)
  private wsUrlToHttpUrl(wsUrl: string): string {
    let url: string = wsUrl;
    // Strip query string
    const qIdx: number = url.indexOf('?');
    if (qIdx >= 0) url = url.substring(0, qIdx);
    // Convert protocol
    if (url.startsWith('wss://')) {
      url = 'https://' + url.substring(6);
    } else if (url.startsWith('ws://')) {
      url = 'http://' + url.substring(5);
    }
    // Remove trailing /ws/ path if present
    if (url.endsWith('/ws/')) {
      url = url.substring(0, url.length - 4);
    } else if (url.endsWith('/ws')) {
      url = url.substring(0, url.length - 3);
    }
    // Ensure trailing slash removed
    if (url.endsWith('/')) {
      url = url.substring(0, url.length - 1);
    }
    return url;
  }

  private async executeBindQr(server: string, groupId: string, groupSecret: string): Promise<void> {
    // Save settings
    await PreferencesUtil.setLastServerUrl(server);
    await PreferencesUtil.setString(StorageKeys.GROUP_SECRET, groupSecret);

    this.scanStatus = `正在连接群组 ${server.substring(0, 40)}...`;

    // Connect as group
    const connectUrl: string = this.buildConnectUrlWithToken(server, groupSecret);
    ConnectionManager.connectGroup(connectUrl, groupSecret, groupId);

    // Listen for group join result
    this.cleanupListeners();
    this.messageListenerId = ConnectionManager.addOnMessage((type: string, _data: Record<string, Object>) => {
      if (type === 'group.join.accepted') {
        this.cleanupListeners();
        this.scanStatus = '已加入群组';
        // Bind mobile
        ConnectionManager.sendMobileBindRequest(groupId);
        this.getUIContext().getRouter().back();
      } else if (type === 'group.join.rejected') {
        this.cleanupListeners();
        this.parseError = '加入群组被拒绝';
        this.scanStatus = '加入群组失败';
      }
    });

    this.stateListenerId = ConnectionManager.addOnStateChange((state: ConnectionState, _deviceId: string) => {
      if (state === ConnectionState.CONNECTED) {
        ConnectionManager.sendGroupJoinRequest(groupSecret);
      }
    });

    // If already connected, send join immediately
    if (ConnectionManager.getConnectionStateForDevice('group') === ConnectionState.CONNECTED) {
      ConnectionManager.sendGroupJoinRequest(groupSecret);
    }
  }

  // --- Panel login: scan login QR from web panel ---
  private handleLoginQr(params: Map<string, string>): void {
    const server: string = params.get('server') ?? '';
    const requestId: string = params.get('requestId') ?? '';

    hilog.info(DOMAIN, PAGE_TAG, 'login QR: server=%{public}s requestId=%{public}s', server, requestId);

    if (server.length === 0 || requestId.length === 0) {
      this.parseError = '登录码缺少必要参数';
      return;
    }

    this.scanStatus = '正在处理登录请求...';
    this.pendingPanelLoginRequestId = requestId;

    // Listen for error responses from server
    this.cleanupListeners();
    this.messageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
      if (type === 'error') {
        const code: string = (data['code'] ?? '') as string;
        const message: string = (data['message'] ?? '') as string;
        hilog.error(DOMAIN, PAGE_TAG, 'login scan error: code=%{public}s msg=%{public}s', code, message);
        if (code === 'not_bound_mobile') {
          this.scanStatus = '登录失败：需要重新连接群组';
          this.parseError = '手机未在群组中注册，请返回设备管理页等待自动重连';
        } else if (code === 'login_session_not_found') {
          this.scanStatus = '登录失败：登录会话已过期';
          this.parseError = '请刷新网页重新获取二维码';
        } else {
          this.scanStatus = '登录失败';
          this.parseError = message.length > 0 ? message : '未知错误';
        }
        this.cleanupListeners();
      } else if (type === 'panel.login.approved') {
        this.scanStatus = '网页登录已批准';
        this.cleanupListeners();
        this.getUIContext().getRouter().back();
      }
    });

    // Check if connected to group and joined
    if (!ConnectionManager.isInGroup()) {
      this.scanStatus = '正在重新连接群组...';
      // Wait for group rejoin (auto-rejoin on reconnect), then send
      this.stateListenerId = ConnectionManager.addOnStateChange((state: ConnectionState, _deviceId: string) => {
        if (state === ConnectionState.CONNECTED) {
          // After reconnect, wait a moment for group.join.accepted
          // The auto-rejoin logic will fire group.join.request
        }
      });
      // Also listen for group.join.accepted to then send the login scan
      const origMsgListener: number = this.messageListenerId;
      const joinListenerId: number = ConnectionManager.addOnMessage((type: string, _data: Record<string, Object>) => {
        if (type === 'group.join.accepted') {
          ConnectionManager.removeOnMessage(joinListenerId);
          this.scanStatus = '已重新连接，正在发送登录确认...';
          ConnectionManager.sendPanelLoginScan(requestId);
        }
      });
      return;
    }

    // Send panel.login.scan through ConnectionManager
    ConnectionManager.sendPanelLoginScan(requestId);
    this.scanStatus = '已发送登录确认，等待服务器响应...';
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

      if (this.showInviteConfirmDialog) {
        Column() {
          Column() {
            Text('邀请设备加入群组')
              .fontSize(18)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
              .margin({ bottom: 10 })

            Text(`是否将 ${this.inviteDeviceName} 邀请加入当前群组？`)
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
                  this.showInviteConfirmDialog = false;
                })

              Button('邀请加入')
                .width(140)
                .height(40)
                .fontSize(AppSizes.FONT_SIZE_SMALL)
                .fontColor('#FFFFFF')
                .backgroundColor(AppColors.ACCENT)
                .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
                .onClick(() => {
                  this.inviteDeviceToGroup();
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
