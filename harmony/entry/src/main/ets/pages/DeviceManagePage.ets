import { LengthMetrics } from '@kit.ArkUI';
import { Context } from '@kit.AbilityKit';
import { scanBarcode } from '@kit.ScanKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState, DeviceMode, StorageKeys } from '../common/Constants';
import { ConnectionManager, SingleDeviceRecord } from '../services/ConnectionManager';
import { AuthManager, AuthPendingRequest } from '../services/AuthManager';
import { PreferencesUtil } from '../services/PreferencesUtil';
import { DeviceInfo, GroupMemberInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const PAGE_TAG = 'DeviceManagePage';

interface SessionListParams {
  deviceId: string;
  displayName: string;
  availableShells: Array<string>;
}

@Entry
@Component
struct DeviceManagePage {
  @State groupDevices: Array<DeviceInfo> = [];
  @State singleDevices: Array<DeviceInfo> = [];
  @State currentMode: DeviceMode = DeviceMode.STANDALONE;
  @State isRefreshing: boolean = false;
  @State groupMembers: Array<GroupMemberInfo> = [];
  @State groupId: string = '';
  @State scanResult: string = '';
  @State scanStatus: string = '';
  @State isScanning: boolean = false;
  @State parseError: string = '';
  @State showInviteConfirmDialog: boolean = false;
  @State inviteDeviceName: string = '';

  // Device settings sheet
  @State showSettingsSheet: boolean = false;
  @State settingsDeviceId: string = '';
  @State settingsDisplayName: string = '';
  @State settingsOs: string = '';
  @State settingsEditName: string = '';
  @State settingsRole: string = '';
  @State settingsIsGroupDevice: boolean = false;

  // Auth dialog
  @State showAuthDialog: boolean = false;
  @State authRequestId: string = '';
  @State authRequesterName: string = '';
  @State authDescription: string = '';
  @State authAction: string = '';

  // Pending auth badge
  @State pendingAuthCount: number = 0;

  private deviceListCallbackId: number = -1;
  private modeChangeCallbackId: number = -1;
  private messageCallbackId: number = -1;
  private authDialogCallbackId: number = -1;
  private pendingInviteDeviceId: string = '';
  private pendingInviteHttpUrl: string = '';
  private scanMessageListenerId: number = -1;
  private scanStateListenerId: number = -1;
  private scanInviteHttpUrl: string = '';

  aboutToAppear(): void {
    this.activatePage();
  }

  aboutToDisappear(): void {
    this.deactivatePage();
  }

  onPageShow(): void {
    this.activatePage();
  }

  onPageHide(): void {
    this.deactivatePage();
  }

  private activatePage(): void {
    this.currentMode = ConnectionManager.currentMode;
    this.groupDevices = ConnectionManager.getGroupDevices();
    this.singleDevices = ConnectionManager.getSingleDevices();
    this.groupMembers = ConnectionManager.getGroupMembers();
    this.groupId = ConnectionManager.getGroupId();
    this.pendingAuthCount = AuthManager.getPendingCount();

    if (this.deviceListCallbackId === -1) {
      this.deviceListCallbackId = ConnectionManager.addOnDeviceListChanged((_devices: Array<DeviceInfo>) => {
        this.groupDevices = ConnectionManager.getGroupDevices();
        this.singleDevices = ConnectionManager.getSingleDevices();
        this.groupMembers = ConnectionManager.getGroupMembers();
        this.isRefreshing = false;
      });
    }

    if (this.modeChangeCallbackId === -1) {
      this.modeChangeCallbackId = ConnectionManager.addOnModeChange((mode: DeviceMode) => {
        this.currentMode = mode;
        this.groupId = ConnectionManager.getGroupId();
        this.groupDevices = ConnectionManager.getGroupDevices();
        this.singleDevices = ConnectionManager.getSingleDevices();
      });
    }

    if (this.messageCallbackId === -1) {
      this.messageCallbackId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
        if (type === 'invite.create.response') {
          // Got invite code — UI could show it or auto-use it
          const inviteCode: string = (data['inviteCode'] ?? '') as string;
          const relayUrl: string = (data['relayUrl'] ?? '') as string;
          hilog.info(DOMAIN, PAGE_TAG, 'Got invite code=%{public}s relay=%{public}s', inviteCode, relayUrl);
          if (this.pendingInviteDeviceId.length > 0 && this.pendingInviteHttpUrl.length > 0) {
            const httpUrl: string = this.pendingInviteHttpUrl;
            this.pendingInviteDeviceId = '';
            this.pendingInviteHttpUrl = '';
            ConnectionManager.inviteToGroup(httpUrl, inviteCode, relayUrl);
          }
        } else if (type === 'group.join.accepted') {
          this.groupId = ConnectionManager.getGroupId();
          this.groupMembers = ConnectionManager.getGroupMembers();
        } else if (type === 'group.member.list' || type === 'group.member.joined' || type === 'group.member.left') {
          this.groupMembers = ConnectionManager.getGroupMembers();
        }
      });
    }

    // Auth dialog trigger
    if (this.authDialogCallbackId === -1) {
      this.authDialogCallbackId = AuthManager.addOnShowDialog((request: AuthPendingRequest) => {
        this.authRequestId = request.requestId;
        this.authRequesterName = request.requesterName;
        this.authDescription = request.description;
        this.authAction = request.action;
        this.showAuthDialog = true;
        this.pendingAuthCount = AuthManager.getPendingCount();
      });
    }
  }

  private deactivatePage(): void {
    if (this.deviceListCallbackId !== -1) {
      ConnectionManager.removeOnDeviceListChanged(this.deviceListCallbackId);
      this.deviceListCallbackId = -1;
    }
    if (this.modeChangeCallbackId !== -1) {
      ConnectionManager.removeOnModeChange(this.modeChangeCallbackId);
      this.modeChangeCallbackId = -1;
    }
    if (this.messageCallbackId !== -1) {
      ConnectionManager.removeOnMessage(this.messageCallbackId);
      this.messageCallbackId = -1;
    }
    if (this.authDialogCallbackId !== -1) {
      AuthManager.removeOnShowDialog(this.authDialogCallbackId);
      this.authDialogCallbackId = -1;
    }
    this.cleanupScanListeners();
  }

  private getOsIcon(os: string): string {
    const osLower: string = os.toLowerCase();
    if (osLower.indexOf('windows') >= 0) return '🖥';
    if (osLower.indexOf('linux') >= 0) return '🐧';
    if (osLower.indexOf('mac') >= 0 || osLower.indexOf('darwin') >= 0) return '🍎';
    return '💻';
  }

  private onDeviceClick(device: DeviceInfo): void {
    if (!device.isOnline) return;
    const deviceId: string = device.deviceId;
    if (deviceId.length === 0) return;

    hilog.info(DOMAIN, PAGE_TAG, 'navigateToSessionList device=%{public}s', deviceId);
    const params: SessionListParams = {
      deviceId: deviceId,
      displayName: device.displayName,
      availableShells: device.availableShells
    };
    try {
      this.getUIContext().getRouter().pushUrl({
        url: 'pages/SessionListPage',
        params: params
      });
    } catch (_e) {
      // Ignore navigation errors
    }
  }

  private openDeviceSettings(device: DeviceInfo, isPendingSingle: boolean): void {
    this.settingsDeviceId = device.deviceId;
    this.settingsDisplayName = device.displayName;
    this.settingsOs = device.os;
    this.settingsEditName = device.displayName;

    // Determine role from group members when available
    this.settingsIsGroupDevice = !isPendingSingle && this.currentMode === DeviceMode.GROUP;
    if (this.settingsIsGroupDevice) {
      const member: GroupMemberInfo | undefined = this.findGroupMember(device.deviceId);
      this.settingsRole = member !== undefined ? member.role : 'Member';
    } else {
      this.settingsRole = 'Pending';
    }

    this.showSettingsSheet = true;
  }

  private closeSettingsSheet(): void {
    this.showSettingsSheet = false;
    this.settingsDeviceId = '';
  }

  private saveDeviceName(): void {
    if (this.settingsEditName.trim().length === 0) return;
    if (this.settingsEditName.trim() !== this.settingsDisplayName) {
      ConnectionManager.updateDeviceDisplayName(this.settingsDeviceId, this.settingsEditName.trim());
    }
    this.closeSettingsSheet();
  }

  private kickDeviceFromGroup(): void {
    ConnectionManager.kickDevice(this.settingsDeviceId);
    this.closeSettingsSheet();
  }

  private disconnectSingleDevice(): void {
    ConnectionManager.disconnectDevice(this.settingsDeviceId);
    this.closeSettingsSheet();
  }

  private approveAuth(): void {
    AuthManager.approve(this.authRequestId);
    this.showAuthDialog = false;
    this.pendingAuthCount = AuthManager.getPendingCount();
  }

  private rejectAuth(): void {
    AuthManager.reject(this.authRequestId);
    this.showAuthDialog = false;
    this.pendingAuthCount = AuthManager.getPendingCount();
  }

  private cleanupScanListeners(): void {
    if (this.scanMessageListenerId !== -1) {
      ConnectionManager.removeOnMessage(this.scanMessageListenerId);
      this.scanMessageListenerId = -1;
    }
    if (this.scanStateListenerId !== -1) {
      ConnectionManager.removeOnStateChange(this.scanStateListenerId);
      this.scanStateListenerId = -1;
    }
  }

  private startScan(): void {
    if (this.isScanning) return;
    this.cleanupScanListeners();
    this.isScanning = true;
    this.scanStatus = '正在启动扫码...';
    this.parseError = '';
    this.scanResult = '';

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
      this.scanInviteHttpUrl = httpUrl;
      this.showInviteConfirmDialog = true;
      return;
    }

    this.scanStatus = `正在连接 ${displayName}...`;

    // Use ConnectionManager to establish single connection
    ConnectionManager.connectSingle(wsUrl, deviceId, displayName, httpUrl);

    this.scanStatus = `已添加设备 ${displayName}`;
  }

  // --- Invite a standalone device into the current group ---
  private inviteDeviceToGroup(): void {
    this.showInviteConfirmDialog = false;
    const httpUrl: string = this.scanInviteHttpUrl;
    const deviceName: string = this.inviteDeviceName;

    if (httpUrl.length === 0) return;

    this.scanStatus = `正在邀请 ${deviceName} 加入群组...`;

    // Listen for invite.create.response
    this.cleanupScanListeners();
    this.scanMessageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
      if (type === 'invite.create.response') {
        const inviteCode: string = (data['inviteCode'] ?? '') as string;
        const relayUrl: string = (data['relayUrl'] ?? '') as string;
        if (inviteCode.length > 0 && relayUrl.length > 0) {
          hilog.info(DOMAIN, PAGE_TAG, 'Got invite code, sending to device %{public}s', httpUrl);
          this.scanStatus = `正在发送邀请给 ${deviceName}...`;
          ConnectionManager.inviteToGroup(httpUrl, inviteCode, relayUrl,
            (success: boolean, message: string) => {
              if (success) {
                this.scanStatus = `已邀请 ${deviceName} 加入群组`;
                this.cleanupScanListeners();
              } else {
                this.parseError = `邀请发送失败: ${message}`;
                this.scanStatus = '邀请失败';
                this.cleanupScanListeners();
              }
            });
        }
      } else if (type === 'error') {
        const message: string = (data['message'] ?? '') as string;
        this.parseError = `邀请失败: ${message}`;
        this.scanStatus = '邀请失败';
        this.cleanupScanListeners();
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
        // Same group - already connected
        this.scanStatus = '已在该群组中';
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
      this.scanInviteHttpUrl = httpUrl;
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
    this.cleanupScanListeners();
    this.scanMessageListenerId = ConnectionManager.addOnMessage((type: string, _data: Record<string, Object>) => {
      if (type === 'group.join.accepted') {
        this.cleanupScanListeners();
        this.scanStatus = '已加入群组';
        // Bind mobile
        ConnectionManager.sendMobileBindRequest(groupId);
      } else if (type === 'group.join.rejected') {
        this.cleanupScanListeners();
        this.parseError = '加入群组被拒绝';
        this.scanStatus = '加入群组失败';
      }
    });

    this.scanStateListenerId = ConnectionManager.addOnStateChange((state: ConnectionState, _deviceId: string) => {
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
    // Listen for error responses from server
    this.cleanupScanListeners();
    this.scanMessageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
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
        this.cleanupScanListeners();
      } else if (type === 'panel.login.approved') {
        this.scanStatus = '网页登录已批准';
        this.cleanupScanListeners();
      }
    });

    // Check if connected to group and joined
    if (!ConnectionManager.isInGroup()) {
      this.scanStatus = '正在重新连接群组...';
      // Wait for group rejoin (auto-rejoin on reconnect), then send
      this.scanStateListenerId = ConnectionManager.addOnStateChange((state: ConnectionState, _deviceId: string) => {
        if (state === ConnectionState.CONNECTED) {
          // After reconnect, wait a moment for group.join.accepted
          // The auto-rejoin logic will fire group.join.request
        }
      });
      // Also listen for group.join.accepted to then send the login scan
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
    Stack({ alignContent: Alignment.TopStart }) {
      Column() {
        Blank()
          .height(140)
          .width('100%')
          .backgroundColor(AppColors.ACCENT_DIM)
          .opacity(0.2)
        Blank().layoutWeight(1)
      }
      .width('100%')
      .height('100%')

      Column() {
        Column() {
          Row() {
            Column() {
              Text(AppStrings.APP_NAME)
                .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
                .fontColor(AppColors.TEXT_PRIMARY)
                .fontWeight(FontWeight.Bold)
            }
            .layoutWeight(1)

            Row({ space: 8 }) {
              this.StatusChip(this.getModeLabel(), this.getModeColor())
              if (this.pendingAuthCount > 0) {
                Button(`AUTH ${this.pendingAuthCount}`)
                  .height(26)
                  .fontSize(11)
                  .fontColor(AppColors.ACCENT_PINK)
                  .backgroundColor(AppColors.HIGHLIGHT)
                  .borderWidth(1)
                  .borderColor(AppColors.ACCENT_PINK)
                  .borderRadius(AppSizes.BORDER_RADIUS_TAG)
                  .padding({ left: 10, right: 10 })
                  .onClick(() => {
                    AuthManager.showPending();
                  })
              }
            }
          }
          .width('100%')
          .margin({ bottom: 10 })

          Row() {
            Text(ConnectionManager.isInGroup()
              ? `群组已连接 · ${this.groupMembers.length} 成员`
              : '未加入群组')
              .fontSize(12)
              .fontColor(ConnectionManager.isInGroup() ? AppColors.ACCENT : AppColors.TEXT_MUTED)
              .layoutWeight(1)

            if (this.groupId.length > 0) {
              Text(this.groupId)
                .fontSize(11)
                .fontColor(AppColors.TEXT_SECONDARY)
                .fontFamily('monospace')
            }
          }
          .width('100%')
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 16, bottom: 12 })
        .backgroundColor(AppColors.SURFACE_1)

        Divider().color(AppColors.DIVIDER).width('100%')

        // Content area
        this.MainContent()

        // Bottom scan button (group mode)
        if (ConnectionManager.isInGroup()) {
          Column() {
            Divider().color(AppColors.DIVIDER).width('100%')
            Row() {
              Column() {
                Text('快捷操作')
                  .fontSize(12)
                  .fontColor(AppColors.TEXT_MUTED)
                  .fontFamily('monospace')
                Text('扫描二维码绑定/登录')
                  .fontSize(12)
                  .fontColor(AppColors.TEXT_SECONDARY)
              }
              .layoutWeight(1)

              Button('扫  码')
                .height(40)
                .fontSize(14)
                .fontColor('#FFFFFF')
                .backgroundColor(AppColors.ACCENT)
                .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
                .padding({ left: 16, right: 16 })
                .onClick(() => { this.startScan(); })
            }
            .width('100%')
            .padding({ left: 12, right: 12, top: 10, bottom: 10 })
            .backgroundColor(AppColors.SURFACE_1)
            .borderWidth(1)
            .borderColor(AppColors.CARD_BORDER)
            .borderRadius(AppSizes.BORDER_RADIUS_CARD)
          }
          .width('100%')
          .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 10, bottom: 16 })
        }
      }
      .width('100%')
      .height('100%')

      // Settings sheet overlay
      if (this.showSettingsSheet) {
        this.SettingsSheet()
      }

      // Auth dialog overlay
      if (this.showAuthDialog) {
        this.AuthDialog()
      }

      // Scan invite confirm dialog
      if (this.showInviteConfirmDialog) {
        this.InviteConfirmDialog()
      }
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }

  @Builder
  MainContent() {
    Scroll() {
      Column() {
        this.GroupSection()
        this.PendingSection()
      }
      .width('100%')
      .alignItems(HorizontalAlign.Start)
      .justifyContent(FlexAlign.Start)
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 16, bottom: 16 })
    }
    .align(Alignment.TopStart)
    .width('100%')
    .layoutWeight(1)
  }

  @Builder
  GroupSection() {
    if (!ConnectionManager.isInGroup()) {
      this.ScanConnectSection()
    } else {
      Column() {
        Row() {
          Column() {
            Text('群组设备')
              .fontSize(AppSizes.FONT_SIZE_BODY)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
            Text(`已连接 · ${this.groupMembers.length} 成员`)
              .fontSize(12)
              .fontColor(AppColors.TEXT_SECONDARY)
          }
          .layoutWeight(1)

          Row({ space: 8 }) {
            this.StatusChip(`${this.groupMembers.length} MEMBERS`, AppColors.ACCENT)
            Button('解散')
              .height(30)
              .fontSize(12)
              .fontColor(AppColors.ERROR_RED)
              .backgroundColor(Color.Transparent)
              .borderWidth(1)
              .borderColor(AppColors.ERROR_RED)
              .borderRadius(AppSizes.BORDER_RADIUS_TAG)
              .padding({ left: 10, right: 10 })
              .onClick(() => { ConnectionManager.dissolveGroup(); })
          }
        }
        .width('100%')
        .margin({ bottom: 14 })

        if (this.getGroupDevices().length === 0) {
          Text('群组暂无设备')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ bottom: 12 })
        } else {
          Column() {
            ForEach(this.getGroupDevices(), (device: DeviceInfo) => {
              Column() {
                this.DeviceCard(device, false, false)
              }
              .margin({ bottom: 12 })
            }, (device: DeviceInfo, index: number) => `group_${device.deviceId}_${index}`)
          }
          .width('100%')
        }

        this.ScanStatusPanel()
      }
      .width('100%')
      .alignItems(HorizontalAlign.Start)
      .padding(16)
      .backgroundColor(AppColors.SURFACE_1)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(AppSizes.BORDER_RADIUS_CARD)
      .margin({ bottom: 16 })
    }
  }

  @Builder
  ScanConnectSection() {
    Column() {
      Row() {
        Column() {
          Text('扫码连接服务器')
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Medium)
          Text('对准 PC 端命令中心生成的二维码')
            .fontSize(12)
            .fontColor(AppColors.TEXT_SECONDARY)
        }
        .layoutWeight(1)

        this.StatusChip('LINK', AppColors.ACCENT_BLUE)
      }
      .width('100%')
      .margin({ bottom: 12 })

      Text('将手机加入群组并绑定控制权限。')
        .fontSize(AppSizes.FONT_SIZE_SMALL)
        .fontColor(AppColors.TEXT_SECONDARY)
        .margin({ bottom: 20 })

      Button(this.isScanning ? '扫码中...' : '开始扫码')
        .width('100%')
        .height(44)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor('#FFFFFF')
        .backgroundColor(AppColors.ACCENT)
        .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
        .enabled(!this.isScanning)
        .onClick(() => {
          this.startScan();
        })
        .margin({ bottom: 16 })

      this.ScanStatusPanel()
    }
    .width('100%')
    .padding(16)
    .backgroundColor(AppColors.SURFACE_1)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .alignItems(HorizontalAlign.Start)
  }

  @Builder
  ScanStatusPanel() {
    if (this.scanStatus.length > 0 || this.parseError.length > 0 || this.scanResult.length > 0) {
      Column() {
        if (this.scanStatus.length > 0) {
          Text(this.scanStatus)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ bottom: 8 })
            .textAlign(TextAlign.Start)
            .width('100%')
        }

        if (this.parseError.length > 0) {
          Text(this.parseError)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.ERROR_RED)
            .margin({ bottom: 8 })
            .textAlign(TextAlign.Start)
            .width('100%')
        }

        if (this.scanResult.length > 0) {
          Column() {
            Text('扫码结果')
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_MUTED)
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
          .padding(12)
          .backgroundColor(AppColors.SURFACE_2)
          .borderWidth(1)
          .borderColor(AppColors.CARD_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_CARD)
          .alignItems(HorizontalAlign.Start)
        }
      }
      .width('100%')
      .margin({ top: 8 })
      .padding(12)
      .backgroundColor(AppColors.SURFACE_2)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(AppSizes.BORDER_RADIUS_CARD)
      .alignItems(HorizontalAlign.Start)
    }
  }

  @Builder
  PendingSection() {
    if (this.getSingleDevices().length > 0) {
      Column() {
        Row() {
          Column() {
            Text('待加入设备')
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
          }
          Blank()
          Text('长按移入群组')
            .fontSize(12)
            .fontColor(AppColors.TEXT_MUTED)
        }
        .width('100%')
        .margin({ bottom: 12 })

        Column() {
          ForEach(this.getSingleDevices(), (device: DeviceInfo) => {
            Column() {
              this.DeviceCard(device, true, true)
            }
            .margin({ bottom: 12 })
          }, (device: DeviceInfo, index: number) => `pending_${device.deviceId}_${index}`)
        }
        .width('100%')
      }
      .width('100%')
      .alignItems(HorizontalAlign.Start)
      .padding(16)
      .backgroundColor(AppColors.SURFACE_1)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    }
  }

  @Builder
  DeviceCard(device: DeviceInfo, allowMoveToGroup: boolean, isPendingSingle: boolean) {
    Column() {
      Blank()
        .height(2)
        .width('100%')
        .backgroundColor(device.isOnline ? AppColors.ACCENT : AppColors.CARD_BORDER)
        .margin({ bottom: 12 })

      Row() {
        Stack({ alignContent: Alignment.Center }) {
          Circle()
            .width(38)
            .height(38)
            .fill(AppColors.SURFACE_3)
          Text(this.getOsIcon(device.os))
            .fontSize(18)
        }
        .margin({ right: 12 })

        Column() {
          Text(device.displayName)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Medium)
            .maxLines(1)
            .textOverflow({ overflow: TextOverflow.Ellipsis })

          Text(device.os)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ top: 4 })

          Text(`ID ${device.deviceId}`)
            .fontSize(11)
            .fontColor(AppColors.TEXT_MUTED)
            .fontFamily('monospace')
            .maxLines(1)
            .textOverflow({ overflow: TextOverflow.Ellipsis })
            .margin({ top: 4 })
        }
        .alignItems(HorizontalAlign.Start)
        .layoutWeight(1)

        Column() {
          // Settings gear
          Text('⚙')
            .fontSize(18)
            .fontColor(AppColors.TEXT_SECONDARY)
            .padding(6)
            .onClick(() => { this.openDeviceSettings(device, isPendingSingle); })

          Row() {
            Circle()
              .width(6)
              .height(6)
              .fill(device.isOnline ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
              .margin({ right: 6 })

            Text(device.isOnline ? '在线' : '离线')
              .fontSize(11)
              .fontColor(device.isOnline ? AppColors.ONLINE_GREEN : AppColors.TEXT_MUTED)
          }
          .padding({ left: 8, right: 8, top: 4, bottom: 4 })
          .backgroundColor(AppColors.CHIP_BG)
          .borderWidth(1)
          .borderColor(device.isOnline ? AppColors.ONLINE_GREEN : AppColors.CHIP_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
        }
      }
      .width('100%')

      // Available shells
      if (device.availableShells.length > 0) {
        Flex({ wrap: FlexWrap.Wrap, space: { main: new LengthMetrics(8), cross: new LengthMetrics(8) } }) {
          ForEach(device.availableShells, (shell: string) => {
            Text(shell)
              .fontSize(12)
              .fontColor(AppColors.ACCENT)
              .backgroundColor(AppColors.HIGHLIGHT)
              .borderWidth(1)
              .borderColor(AppColors.ACCENT_DIM)
              .borderRadius(AppSizes.BORDER_RADIUS_TAG)
              .padding({ left: 8, right: 8, top: 4, bottom: 4 })
          }, (shell: string, index: number) => `${shell}_${index}`)
        }
        .width('100%')
        .margin({ top: 12 })
      }
    }
    .width('100%')
    .padding(AppSizes.PADDING_CARD)
    .backgroundColor(AppColors.CARD_BACKGROUND)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .opacity(device.isOnline ? 1.0 : 0.7)
    .onClick(() => { this.onDeviceClick(device); })
    .gesture(
      LongPressGesture({ repeat: false, duration: 600 })
        .onAction(() => {
          if (allowMoveToGroup && device.isOnline) {
            this.moveDeviceToGroup(device);
          }
        })
    )
  }

  @Builder
  SettingsSheet() {
    Column() {
      Blank().layoutWeight(1)
      Column() {
        // Title
        Text('设备设置')
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 6 })
          .textAlign(TextAlign.Center)
          .width('100%')

        Text(this.settingsDisplayName)
          .fontSize(12)
          .fontColor(AppColors.TEXT_MUTED)
          .fontFamily('monospace')
          .margin({ bottom: 18 })
          .textAlign(TextAlign.Center)
          .width('100%')

        // Name edit
        Row() {
          Text('名称:')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .width(48)

          TextInput({ text: $$this.settingsEditName })
            .type(InputType.Normal)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .backgroundColor(AppColors.INPUT_BG)
            .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
            .padding({ left: 12, right: 12 })
            .height(40)
            .layoutWeight(1)
        }
        .width('100%')
        .margin({ bottom: 12 })

        // OS info
        Row() {
          Text('系统:')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .width(48)

          Text(this.settingsOs)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
        }
        .width('100%')
        .margin({ bottom: 12 })

        // Role info
        Row() {
          Text('角色:')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .width(48)

          Text(this.getRoleLabel(this.settingsRole))
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(this.settingsRole === 'Server' ? AppColors.ACCENT : AppColors.TEXT_PRIMARY)
        }
        .width('100%')
        .margin({ bottom: 20 })

        // Rename button
        Button('重命名')
          .width('100%')
          .height(44)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor('#FFFFFF')
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .margin({ bottom: 8 })
          .onClick(() => { this.saveDeviceName(); })

        // Kick from group (group devices only)
        if (this.settingsIsGroupDevice) {
          Button('踢出群组')
            .width('100%')
            .height(44)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor('#FFFFFF')
            .backgroundColor(AppColors.ERROR_RED)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .margin({ bottom: 8 })
            .onClick(() => { this.kickDeviceFromGroup(); })

          // Designate as relay server (group mode, non-server devices only)
          if (this.settingsRole !== 'Server') {
            Button('指定为服务器')
              .width('100%')
              .height(44)
              .fontSize(AppSizes.FONT_SIZE_BODY)
              .fontColor('#FFFFFF')
              .backgroundColor(AppColors.ACCENT_BLUE)
              .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
              .margin({ bottom: 8 })
              .onClick(() => { this.designateAsServer(); })
          }
        }

        // Disconnect (pending single devices only)
        if (!this.settingsIsGroupDevice) {
          Button('断开连接')
            .width('100%')
            .height(44)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.ERROR_RED)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.ERROR_RED)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .margin({ bottom: 8 })
            .onClick(() => { this.disconnectSingleDevice(); })
        }

        // Cancel
        Button('取消')
          .width('100%')
          .height(44)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_SECONDARY)
          .backgroundColor(Color.Transparent)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .onClick(() => { this.closeSettingsSheet(); })
      }
      .width('100%')
      .padding(24)
      .backgroundColor(AppColors.SURFACE_2)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius({ topLeft: 16, topRight: 16 })
    }
    .width('100%')
    .height('100%')
    .backgroundColor('#B0000000')
    .onClick(() => { this.closeSettingsSheet(); })
  }

  @Builder
  AuthDialog() {
    Column() {
      Column() {
        Text('授权请求')
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 16 })

        Text(this.authRequesterName.length > 0 ? `${this.authRequesterName} 请求` : '收到授权请求')
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_PRIMARY)
          .margin({ bottom: 8 })

        if (this.authDescription.length > 0) {
          Text(this.authDescription)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .textAlign(TextAlign.Center)
            .margin({ bottom: 8 })
        }

        Text(`操作: ${this.getAuthActionLabel(this.authAction)}`)
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.TEXT_SECONDARY)
          .margin({ bottom: 20 })

        Row({ space: 12 }) {
          Button(AppStrings.AUTH_REJECT_BUTTON)
            .width(120)
            .height(44)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.TEXT_MUTED)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => { this.rejectAuth(); })

          Button(AppStrings.AUTH_APPROVE_BUTTON)
            .width(120)
            .height(44)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor('#FFFFFF')
            .backgroundColor(AppColors.ACCENT)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => { this.approveAuth(); })
        }
        .justifyContent(FlexAlign.Center)
      }
      .width('100%')
      .padding(24)
      .backgroundColor(AppColors.SURFACE_2)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(16)
      .alignItems(HorizontalAlign.Center)
    }
    .width('100%')
    .height('100%')
    .padding({ left: 24, right: 24 })
    .backgroundColor('#B0000000')
    .justifyContent(FlexAlign.Center)
  }

  @Builder
  InviteConfirmDialog() {
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
            .borderColor(AppColors.TEXT_MUTED)
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
      .backgroundColor(AppColors.SURFACE_2)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(16)
      .alignItems(HorizontalAlign.Center)
    }
    .width('100%')
    .height('100%')
    .padding({ left: 24, right: 24 })
    .backgroundColor('#B0000000')
    .justifyContent(FlexAlign.Center)
  }

  @Builder
  StatusChip(label: string, color: string) {
    Row() {
      Circle()
        .width(6)
        .height(6)
        .fill(color)
        .margin({ right: 6 })
      Text(label)
        .fontSize(11)
        .fontColor(color)
        .fontFamily('monospace')
    }
    .padding({ left: 10, right: 10, top: 4, bottom: 4 })
    .backgroundColor(AppColors.CHIP_BG)
    .borderWidth(1)
    .borderColor(color)
    .borderRadius(AppSizes.BORDER_RADIUS_TAG)
  }

  private getModeLabel(): string {
    if (this.currentMode === DeviceMode.GROUP) return '群组';
    if (this.currentMode === DeviceMode.SINGLE) return '单连';
    return '待机';
  }

  private getModeColor(): string {
    if (this.currentMode === DeviceMode.GROUP) return AppColors.ACCENT;
    if (this.currentMode === DeviceMode.SINGLE) return AppColors.ACCENT_BLUE;
    return AppColors.TEXT_MUTED;
  }

  private getAuthActionLabel(action: string): string {
    if (action === 'panel.login') return '面板登录';
    if (action === 'terminal.open.remote') return '打开远程终端';
    return action;
  }

  private getSingleDevices(): Array<DeviceInfo> {
    return this.singleDevices;
  }

  private getGroupDevices(): Array<DeviceInfo> {
    return this.groupDevices;
  }

  private findGroupMember(deviceId: string): GroupMemberInfo | undefined {
    for (let i: number = 0; i < this.groupMembers.length; i++) {
      if (this.groupMembers[i].deviceId === deviceId) {
        return this.groupMembers[i];
      }
    }
    return undefined;
  }

  private getRoleLabel(role: string): string {
    if (role === 'Server') return '服务器';
    if (role === 'Member') return '成员';
    if (role === 'Mobile') return '手机';
    if (role === 'Pending') return '待加入';
    if (role === '单连') return '待加入';
    return role;
  }

  private designateAsServer(): void {
    ConnectionManager.designateRelay(this.settingsDeviceId);
    this.closeSettingsSheet();
  }

  private moveDeviceToGroup(device: DeviceInfo): void {
    if (!ConnectionManager.isInGroup()) {
      // No group yet — create group with this device as relay
      ConnectionManager.designateRelay(device.deviceId);
    } else {
      // Group exists — invite this device via HTTP POST /api/invite
      const record: SingleDeviceRecord | null = ConnectionManager.getSingleDeviceRecord(device.deviceId);
      const httpUrl: string = record !== null ? record.httpUrl : '';
      if (httpUrl.length === 0) {
        hilog.error(DOMAIN, PAGE_TAG, 'inviteToGroup missing httpUrl for device=%{public}s', device.deviceId);
        return;
      }
      this.pendingInviteDeviceId = device.deviceId;
      this.pendingInviteHttpUrl = httpUrl;
      ConnectionManager.requestInviteCode();
    }
  }
}
