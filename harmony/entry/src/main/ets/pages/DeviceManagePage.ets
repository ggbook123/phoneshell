import { LengthMetrics } from '@kit.ArkUI';
import { Context } from '@kit.AbilityKit';
import { scanBarcode } from '@kit.ScanKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState, DeviceMode, StorageKeys } from '../common/Constants';
import { I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';
import { ConnectionManager, SingleDeviceRecord } from '../services/ConnectionManager';
import { AuthManager, AuthPendingRequest } from '../services/AuthManager';
import { PreferencesUtil } from '../services/PreferencesUtil';
import { DeviceInfo, GroupMemberInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const PAGE_TAG = 'DeviceManagePage';

@Observed
class DeviceItemModel {
  deviceId: string = '';
  displayName: string = '';
  os: string = '';
  isOnline: boolean = false;
  availableShells: Array<string> = [];
}

@Observed
class GroupMemberModel {
  deviceId: string = '';
  displayName: string = '';
  os: string = '';
  role: string = '';
  isOnline: boolean = false;
  availableShells: Array<string> = [];
}

interface SessionListParams {
  deviceId: string;
  displayName: string;
  availableShells: Array<string>;
}

@Entry
@Component
struct DeviceManagePage {
  @State groupDevices: Array<DeviceItemModel> = [];
  @State singleDevices: Array<DeviceItemModel> = [];
  @State currentMode: DeviceMode = DeviceMode.STANDALONE;
  @State isInGroup: boolean = false;
  @State groupMembers: Array<GroupMemberModel> = [];
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
  @State safeAreaTop: number = 0;
  @State safeAreaBottom: number = 0;
  @State safeAreaLeft: number = 0;
  @State safeAreaRight: number = 0;

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
    this.syncSafeArea();
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

  private t(zhText: string, enText: string): string {
    return I18n.t(I18n.getLanguage(), zhText, enText);
  }

  private openSettingsPage(): void {
    try {
      this.getUIContext().getRouter().pushUrl({ url: 'pages/SettingsPage' });
    } catch (_e) {
      // Ignore navigation errors
    }
  }

  private async syncSafeArea(): Promise<void> {
    const insets = await SafeAreaUtil.resolveInsets(this.getUIContext());
    this.safeAreaTop = insets.top;
    this.safeAreaBottom = insets.bottom;
    this.safeAreaLeft = insets.left;
    this.safeAreaRight = insets.right;
  }

  private activatePage(): void {
    this.currentMode = ConnectionManager.currentMode;
    this.syncDeviceLists();
    if (ConnectionManager.consumePendingGroupDissolveUiReset()) {
      this.applyGroupExitLocal();
    }
    this.pendingAuthCount = AuthManager.getPendingCount();

    if (this.deviceListCallbackId === -1) {
      this.deviceListCallbackId = ConnectionManager.addOnDeviceListChanged((_devices: Array<DeviceInfo>) => {
        this.syncDeviceLists();
      });
    }

    if (this.modeChangeCallbackId === -1) {
      this.modeChangeCallbackId = ConnectionManager.addOnModeChange((mode: DeviceMode) => {
        this.currentMode = mode;
        this.syncDeviceLists();
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
        } else if (type === 'device.list') {
          this.syncDeviceLists();
        } else if (type === 'group.join.accepted') {
          this.syncDeviceLists();
        } else if (type === 'group.dissolved' || type === 'device.kicked') {
          this.applyGroupExitLocal();
        } else if (type === 'group.member.list' || type === 'group.member.joined' || type === 'group.member.left') {
          this.syncDeviceLists();
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

  private onDeviceClick(device: DeviceItemModel): void {
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

  private openDeviceSettings(device: DeviceItemModel, isPendingSingle: boolean): void {
    this.settingsDeviceId = device.deviceId;
    this.settingsDisplayName = device.displayName;
    this.settingsOs = device.os;
    this.settingsEditName = device.displayName;

    // Determine role from group members when available
    this.settingsIsGroupDevice = !isPendingSingle && this.currentMode === DeviceMode.GROUP;
    if (this.settingsIsGroupDevice) {
      const member: GroupMemberModel | undefined = this.findGroupMember(device.deviceId);
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
    const trimmed: string = this.settingsEditName.trim();
    if (trimmed.length === 0) return;
    if (trimmed !== this.settingsDisplayName) {
      ConnectionManager.updateDeviceDisplayName(this.settingsDeviceId, trimmed);
      this.updateDeviceNameLocal(this.settingsDeviceId, trimmed);
      this.settingsDisplayName = trimmed;
    }
    this.closeSettingsSheet();
  }

  private updateDeviceNameLocal(deviceId: string, newName: string): void {
    if (deviceId.length === 0 || newName.length === 0) {
      return;
    }
    for (let i: number = 0; i < this.groupDevices.length; i++) {
      if (this.groupDevices[i].deviceId === deviceId) {
        this.groupDevices[i].displayName = newName;
      }
    }
    this.groupDevices = this.groupDevices.slice();
    for (let i: number = 0; i < this.singleDevices.length; i++) {
      if (this.singleDevices[i].deviceId === deviceId) {
        this.singleDevices[i].displayName = newName;
      }
    }
    this.singleDevices = this.singleDevices.slice();
    for (let i: number = 0; i < this.groupMembers.length; i++) {
      if (this.groupMembers[i].deviceId === deviceId) {
        this.groupMembers[i].displayName = newName;
      }
    }
    this.groupMembers = this.groupMembers.slice();
  }

  private kickDeviceFromGroup(): void {
    ConnectionManager.kickDevice(this.settingsDeviceId);
    this.removeDeviceFromGroupLocal(this.settingsDeviceId);
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

  private syncDeviceLists(): void {
    const nextGroupDevices: Array<DeviceItemModel> =
      this.buildDeviceModels(ConnectionManager.getGroupDevices(), this.groupDevices);
    const nextSingleDevices: Array<DeviceItemModel> =
      this.buildDeviceModels(ConnectionManager.getSingleDevices(), this.singleDevices);
    const nextGroupMembers: Array<GroupMemberModel> =
      this.buildGroupMemberModels(ConnectionManager.getGroupMembers(), this.groupMembers);
    this.groupDevices = nextGroupDevices.slice();
    this.singleDevices = nextSingleDevices.slice();
    this.groupMembers = nextGroupMembers.slice();
    this.isInGroup = ConnectionManager.isInGroup();
    this.groupId = ConnectionManager.getGroupId();
  }

  private buildDeviceModels(raw: Array<DeviceInfo>, current: Array<DeviceItemModel>): Array<DeviceItemModel> {
    const existing: Map<string, DeviceItemModel> = new Map();
    for (let i: number = 0; i < current.length; i++) {
      const item: DeviceItemModel = current[i];
      if (item.deviceId.length > 0) {
        existing.set(item.deviceId, item);
      }
    }

    let result: Array<DeviceItemModel> = [];
    for (let i: number = 0; i < raw.length; i++) {
      const info: DeviceInfo = raw[i];
      if (info.deviceId.length === 0) {
        continue;
      }
      let model: DeviceItemModel | undefined = existing.get(info.deviceId);
      if (model === undefined) {
        model = new DeviceItemModel();
        model.deviceId = info.deviceId;
      }
      model.displayName = info.displayName;
      model.os = info.os;
      model.isOnline = info.isOnline;
      model.availableShells = info.availableShells.length > 0 ? info.availableShells.slice() : [];
      result.push(model);
    }

    return result;
  }

  private buildGroupMemberModels(raw: Array<GroupMemberInfo>, current: Array<GroupMemberModel>): Array<GroupMemberModel> {
    const existing: Map<string, GroupMemberModel> = new Map();
    for (let i: number = 0; i < current.length; i++) {
      const item: GroupMemberModel = current[i];
      if (item.deviceId.length > 0) {
        existing.set(item.deviceId, item);
      }
    }

    let result: Array<GroupMemberModel> = [];
    for (let i: number = 0; i < raw.length; i++) {
      const info: GroupMemberInfo = raw[i];
      if (info.deviceId.length === 0) {
        continue;
      }
      let model: GroupMemberModel | undefined = existing.get(info.deviceId);
      if (model === undefined) {
        model = new GroupMemberModel();
        model.deviceId = info.deviceId;
      }
      model.displayName = info.displayName;
      model.os = info.os;
      model.role = info.role;
      model.isOnline = info.isOnline;
      model.availableShells = info.availableShells.length > 0 ? info.availableShells.slice() : [];
      result.push(model);
    }

    return result;
  }

  private refreshDeviceView(): void {
    this.currentMode = ConnectionManager.currentMode;
    this.syncDeviceLists();
  }

  private startScan(): void {
    if (this.isScanning) return;
    this.cleanupScanListeners();
    this.isScanning = true;
    this.scanStatus = this.t('正在启动扫码...', 'Starting scan...');
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
      this.scanStatus = this.t('无法获取上下文', 'Unable to get context');
      return;
    }

    scanBarcode.startScanForResult(hostContext, options)
      .then((result: scanBarcode.ScanResult) => {
        this.isScanning = false;
        const value: string = result.originalValue;
        hilog.info(DOMAIN, PAGE_TAG, 'scan result=%{public}s', value);
        this.scanResult = value;
        this.scanStatus = this.t('扫码成功', 'Scan successful');
        this.handleScanResult(value);
      })
      .catch((err: Error) => {
        this.isScanning = false;
        hilog.error(DOMAIN, PAGE_TAG, 'scan error=%{public}s', err.message);
        this.scanStatus = this.t('扫码失败: ', 'Scan failed: ') + err.message;
      });
  }

  private async handleScanResult(payload: string): Promise<void> {
    // Supported formats:
    //   phoneshell://connect?http=...&ws=...&deviceId=...&displayName=...   (standalone device)
    //   phoneshell://bind?server=...&groupId=...&groupSecret=...            (group bind)
    //   phoneshell://login?server=...&groupId=...&requestId=...             (panel login)
    if (!payload.startsWith('phoneshell://')) {
      this.parseError = this.t('无效的二维码格式', 'Invalid QR code format');
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
      this.parseError = this.t('未知的二维码类型', 'Unknown QR code type');
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
      this.parseError = this.t('二维码缺少必要参数 (ws/deviceId)', 'QR code missing required params (ws/deviceId)');
      return;
    }

    // If already in a group, switch to scanned standalone relay first,
    // then migrate previous group devices into the new relay.
    if (ConnectionManager.isInGroup()) {
      const currentRelayUrl: string = ConnectionManager.getCurrentGroupRelayUrl();
      if (this.preferInviteScannedIntoCurrentGroup(currentRelayUrl, wsUrl)) {
        if (httpUrl.length === 0) {
          this.parseError = this.t('该设备不支持邀请加入群组（缺少HTTP地址）',
            'This device cannot be invited (missing HTTP address)');
          return;
        }
        this.scanInviteHttpUrl = httpUrl;
        this.inviteDeviceName = displayName;
        this.pendingInviteDeviceId = '';
        this.parseError = '';
        this.inviteDeviceToGroup();
        return;
      }
      const previousRelayHttp: string = this.wsUrlToHttpUrl(currentRelayUrl);
      if (previousRelayHttp.length === 0) {
        this.parseError = this.t('无法解析旧群组地址', 'Unable to resolve previous group relay address');
        return;
      }
      const targetRelayUrl: string = this.stripWsQuery(wsUrl);
      const targetSecret: string = this.extractTokenFromWsUrl(wsUrl);
      if (targetRelayUrl.length === 0 || targetSecret.length === 0) {
        this.parseError = this.t('目标设备缺少可迁移群组参数（token）',
          'Target device is missing migration token.');
        return;
      }
      this.scanStatus = this.t(`正在切换到 ${displayName} 的新群组...`, `Switching to ${displayName} group...`);
      this.parseError = '';
      this.executeBindQr(targetRelayUrl, '', targetSecret, previousRelayHttp);
      return;
    }

    this.scanStatus = this.t(`正在连接 ${displayName}...`, `Connecting to ${displayName}...`);

    // Use ConnectionManager to establish single connection
    ConnectionManager.connectSingle(wsUrl, deviceId, displayName, httpUrl);

    this.scanStatus = this.t(`已添加设备 ${displayName}`, `Added device ${displayName}`);
    this.refreshDeviceView();
  }

  // --- Invite a standalone device into the current group ---
  private inviteDeviceToGroup(): void {
    this.showInviteConfirmDialog = false;
    const httpUrl: string = this.scanInviteHttpUrl;
    const deviceName: string = this.inviteDeviceName;

    if (httpUrl.length === 0) return;

    this.scanStatus = this.t(`正在邀请 ${deviceName} 加入群组...`, `Inviting ${deviceName} to join the group...`);

    // Listen for invite.create.response
    this.cleanupScanListeners();
    this.scanMessageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
      if (type === 'invite.create.response') {
        const inviteCode: string = (data['inviteCode'] ?? '') as string;
        const relayUrl: string = (data['relayUrl'] ?? '') as string;
        if (inviteCode.length > 0 && relayUrl.length > 0) {
          hilog.info(DOMAIN, PAGE_TAG, 'Got invite code, sending to device %{public}s', httpUrl);
          this.scanStatus = this.t(`正在发送邀请给 ${deviceName}...`, `Sending invite to ${deviceName}...`);
          ConnectionManager.inviteToGroup(httpUrl, inviteCode, relayUrl,
            (success: boolean, message: string) => {
              if (success) {
                this.scanStatus = this.t(`已邀请 ${deviceName} 加入群组`, `Invited ${deviceName} to join the group`);
                this.cleanupScanListeners();
                this.refreshDeviceView();
              } else {
                this.parseError = this.t(`邀请发送失败: ${message}`, `Invite failed: ${message}`);
                this.scanStatus = this.t('邀请失败', 'Invite failed');
                this.cleanupScanListeners();
              }
            });
        }
      } else if (type === 'error') {
        const message: string = (data['message'] ?? '') as string;
        this.parseError = this.t(`邀请失败: ${message}`, `Invite failed: ${message}`);
        this.scanStatus = this.t('邀请失败', 'Invite failed');
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
      this.parseError = this.t('二维码缺少必要参数 (server/groupSecret)',
        'QR code missing required params (server/groupSecret)');
      return;
    }

    // If already in a group, migrate to scanned group and then invite previous relay into it.
    if (ConnectionManager.isInGroup()) {
      const currentGroupId: string = ConnectionManager.getGroupId();
      if (currentGroupId === groupId) {
        // Same group - already connected
        this.scanStatus = this.t('已在该群组中', 'Already in this group');
        return;
      }
      const currentRelayUrl: string = ConnectionManager.getCurrentGroupRelayUrl();
      if (this.preferInviteScannedIntoCurrentGroup(currentRelayUrl, server)) {
        const httpUrl: string = this.wsUrlToHttpUrl(server);
        if (httpUrl.length === 0) {
          this.parseError = this.t('无法解析设备HTTP地址', 'Unable to parse device HTTP address');
          return;
        }
        this.scanInviteHttpUrl = httpUrl;
        this.inviteDeviceName = params.get('serverDeviceId') ?? this.t('新设备', 'New Device');
        this.pendingInviteDeviceId = '';
        this.parseError = '';
        this.inviteDeviceToGroup();
        return;
      }
      const previousRelayHttp: string = this.wsUrlToHttpUrl(currentRelayUrl);
      if (previousRelayHttp.length === 0) {
        this.parseError = this.t('无法解析旧群组地址', 'Unable to resolve previous group relay address');
        return;
      }
      const targetName: string = params.get('serverDeviceId') ?? this.t('新设备', 'New Device');
      this.scanStatus = this.t(`正在切换到 ${targetName} 的新群组...`, `Switching to ${targetName} group...`);
      this.parseError = '';
      await this.executeBindQr(server, groupId, groupSecret, previousRelayHttp);
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

  private stripWsQuery(wsUrl: string): string {
    const qIdx: number = wsUrl.indexOf('?');
    return qIdx >= 0 ? wsUrl.substring(0, qIdx) : wsUrl;
  }

  private extractTokenFromWsUrl(wsUrl: string): string {
    const qIdx: number = wsUrl.indexOf('?');
    if (qIdx < 0) return '';
    const query: string = wsUrl.substring(qIdx + 1);
    const pairs: Array<string> = query.split('&');
    for (let i = 0; i < pairs.length; i++) {
      const part: string = pairs[i];
      const eq: number = part.indexOf('=');
      if (eq <= 0) continue;
      const key: string = part.substring(0, eq);
      if (key !== 'token') continue;
      const value: string = part.substring(eq + 1);
      return decodeURIComponent(value).trim();
    }
    return '';
  }

  private extractWsHost(wsUrl: string): string {
    let url: string = wsUrl.trim();
    const schemeIdx: number = url.indexOf('://');
    if (schemeIdx < 0) return '';
    url = url.substring(schemeIdx + 3);
    const slashIdx: number = url.indexOf('/');
    const hostPort: string = slashIdx >= 0 ? url.substring(0, slashIdx) : url;
    if (hostPort.length === 0) return '';

    if (hostPort.startsWith('[')) {
      const end: number = hostPort.indexOf(']');
      if (end <= 1) return '';
      return hostPort.substring(1, end).trim().toLowerCase();
    }

    const firstColon: number = hostPort.indexOf(':');
    const lastColon: number = hostPort.lastIndexOf(':');
    if (firstColon >= 0 && firstColon === lastColon) {
      return hostPort.substring(0, firstColon).trim().toLowerCase();
    }
    return hostPort.trim().toLowerCase();
  }

  private isPrivateOrLocalHost(host: string): boolean {
    const h: string = host.trim().toLowerCase();
    if (h.length === 0) return true;
    if (h === 'localhost' || h === '127.0.0.1' || h === '::1') return true;
    if (h.endsWith('.local')) return true;

    const ipv4Pattern: RegExp = /^(\d{1,3})(\.\d{1,3}){3}$/;
    if (ipv4Pattern.test(h)) {
      const parts: Array<number> = h.split('.').map((part: string) => Number(part));
      if (parts.length !== 4 || parts.some((part: number) => Number.isNaN(part) || part < 0 || part > 255)) {
        return false;
      }
      const a: number = parts[0];
      const b: number = parts[1];
      if (a === 10) return true;
      if (a === 172 && b >= 16 && b <= 31) return true;
      if (a === 192 && b === 168) return true;
      if (a === 169 && b === 254) return true;
      return false;
    }

    if (h.includes(':')) {
      if (h.startsWith('fc') || h.startsWith('fd') || h.startsWith('fe80') || h === '::1') {
        return true;
      }
    }
    return false;
  }

  private preferInviteScannedIntoCurrentGroup(currentRelayWsUrl: string, scannedRelayWsUrl: string): boolean {
    const currentHost: string = this.extractWsHost(currentRelayWsUrl);
    const scannedHost: string = this.extractWsHost(scannedRelayWsUrl);
    if (currentHost.length === 0 || scannedHost.length === 0) return false;
    const currentPrivate: boolean = this.isPrivateOrLocalHost(currentHost);
    const scannedPrivate: boolean = this.isPrivateOrLocalHost(scannedHost);
    return !currentPrivate && scannedPrivate;
  }

  private normalizeHttpUrl(httpUrl: string): string {
    let normalized: string = httpUrl.trim();
    while (normalized.endsWith('/')) {
      normalized = normalized.substring(0, normalized.length - 1);
    }
    return normalized.toLowerCase();
  }

  private autoInviteLegacyRelay(previousRelayHttp: string): void {
    const targetHttp: string = this.normalizeHttpUrl(previousRelayHttp);
    if (targetHttp.length === 0) return;

    this.scanStatus = this.t('已连接新群组，正在自动迁移旧群组设备...', 'Connected to the new group. Migrating previous group devices...');
    this.cleanupScanListeners();
    this.scanMessageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
      if (type === 'invite.create.response') {
        const inviteCode: string = (data['inviteCode'] ?? '') as string;
        const relayUrlFromServer: string = (data['relayUrl'] ?? '') as string;
        const relayUrl: string = ConnectionManager.getCurrentGroupRelayUrl();
        const effectiveRelayUrl: string = relayUrl.length > 0 ? relayUrl : relayUrlFromServer;
        if (inviteCode.length === 0 || effectiveRelayUrl.length === 0) {
          this.parseError = this.t('旧群组迁移失败：缺少邀请码或中继地址', 'Previous group migration failed: missing invite code or relay URL');
          this.scanStatus = this.t('旧群组迁移失败', 'Previous group migration failed');
          this.cleanupScanListeners();
          return;
        }
        ConnectionManager.inviteToGroup(
          targetHttp,
          inviteCode,
          effectiveRelayUrl,
          (success: boolean, message: string) => {
            if (success) {
              this.scanStatus = this.t(
                '已发送旧群组迁移邀请，等待设备加入...',
                'Migration invite sent. Waiting for previous group device to join...'
              );
              this.parseError = '';
              this.cleanupScanListeners();
            } else {
              this.parseError = this.t(`旧群组迁移失败: ${message}`, `Previous group migration failed: ${message}`);
              this.scanStatus = this.t('旧群组迁移失败', 'Previous group migration failed');
              this.cleanupScanListeners();
            }
          },
          ConnectionManager.getCurrentGroupSecret()
        );
      } else if (type === 'error') {
        const message: string = (data['message'] ?? '') as string;
        this.parseError = this.t(`旧群组迁移失败: ${message}`, `Previous group migration failed: ${message}`);
        this.scanStatus = this.t('旧群组迁移失败', 'Previous group migration failed');
        this.cleanupScanListeners();
      }
    });
    ConnectionManager.requestInviteCode();
  }

  private async executeBindQr(server: string, groupId: string, groupSecret: string, previousRelayHttp: string = ''): Promise<void> {
    // Save settings
    await PreferencesUtil.setLastServerUrl(server);
    await PreferencesUtil.setString(StorageKeys.GROUP_SECRET, groupSecret);

    this.scanStatus = this.t(
      `正在连接群组 ${server.substring(0, 40)}...`,
      `Connecting to group ${server.substring(0, 40)}...`
    );

    // Connect as group
    const connectUrl: string = this.buildConnectUrlWithToken(server, groupSecret);
    ConnectionManager.connectGroup(connectUrl, groupSecret, groupId);

    // Listen for group join result
    this.cleanupScanListeners();
    this.scanMessageListenerId = ConnectionManager.addOnMessage((type: string, _data: Record<string, Object>) => {
      if (type === 'group.join.accepted') {
        this.cleanupScanListeners();
        const effectiveGroupId: string = groupId.length > 0 ? groupId : ConnectionManager.getGroupId();
        this.scanStatus = '';
        this.parseError = '';
        this.scanResult = '';
        this.syncDeviceLists();
        this.isInGroup = true;
        this.currentMode = DeviceMode.GROUP;
        this.groupId = effectiveGroupId;
        // Bind mobile
        ConnectionManager.sendMobileBindRequest(effectiveGroupId);
        ConnectionManager.requestDeviceList();
        if (previousRelayHttp.length > 0) {
          this.autoInviteLegacyRelay(previousRelayHttp);
        }
      } else if (type === 'group.join.rejected') {
        this.cleanupScanListeners();
        this.parseError = this.t('加入群组被拒绝', 'Group join was rejected');
        this.scanStatus = this.t('加入群组失败', 'Failed to join group');
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
      this.parseError = this.t('登录码缺少必要参数', 'Login code missing required params');
      return;
    }

    this.scanResult = '';
    this.scanStatus = this.t('正在处理登录请求...', 'Processing login request...');
    // Listen for error responses from server
    this.cleanupScanListeners();
    this.scanMessageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
      if (type === 'error') {
        const code: string = (data['code'] ?? '') as string;
        const message: string = (data['message'] ?? '') as string;
        hilog.error(DOMAIN, PAGE_TAG, 'login scan error: code=%{public}s msg=%{public}s', code, message);
        if (code === 'not_bound_mobile') {
          this.scanStatus = this.t('登录失败：需要重新连接群组', 'Login failed: reconnect to the group');
          this.parseError = this.t('手机未在群组中注册，请返回设备管理页等待自动重连',
            'This phone is not registered in the group. Return to Device Center and wait for auto-reconnect.');
        } else if (code === 'login_session_not_found') {
          this.scanStatus = this.t('登录失败：登录会话已过期', 'Login failed: session expired');
          this.parseError = this.t('请刷新网页重新获取二维码', 'Please refresh the web page to get a new QR code');
        } else {
          this.scanStatus = this.t('登录失败', 'Login failed');
          this.parseError = message.length > 0 ? message : this.t('未知错误', 'Unknown error');
        }
        this.cleanupScanListeners();
      } else if (type === 'panel.login.approved') {
        this.scanStatus = this.t('网页登录已批准', 'Web login approved');
        this.cleanupScanListeners();
      }
    });

    // Check if connected to group and joined
    if (!ConnectionManager.isInGroup()) {
      this.scanStatus = this.t('正在重新连接群组...', 'Reconnecting to group...');
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
          this.scanStatus = this.t('已重新连接，正在发送登录确认...',
            'Reconnected. Sending login confirmation...');
          ConnectionManager.sendPanelLoginScan(requestId);
        }
      });
      return;
    }

    // Send panel.login.scan through ConnectionManager
    ConnectionManager.sendPanelLoginScan(requestId);
    this.scanStatus = this.t('已发送登录确认', 'Login confirmation sent.');
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

  private getBottomScanMargin(): number {
    const minOffset: number = 28;
    return Math.max(minOffset - this.safeAreaBottom, 0);
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
            Row() {
              Image($r('app.media.phoneshell_header'))
                .width(AppSizes.FONT_SIZE_SUBTITLE)
                .height(AppSizes.FONT_SIZE_SUBTITLE)
                .margin({ right: 8 })

              Column() {
                Text(AppStrings.APP_NAME)
                  .fontSize(20)
                  .lineHeight(21)
                  .fontColor(AppColors.TEXT_PRIMARY)
                  .fontWeight(FontWeight.Bold)
                  .maxLines(1)
                  .textOverflow({ overflow: TextOverflow.Ellipsis })

                Text(this.t('无缝切换你的终端会话', 'Seamless terminal session switch'))
                  .fontSize(11)
                  .lineHeight(12)
                  .fontColor(AppColors.TEXT_MUTED)
                  .maxLines(1)
                  .textOverflow({ overflow: TextOverflow.Ellipsis })
              }
              .alignItems(HorizontalAlign.Start)
            }
            .alignItems(VerticalAlign.Center)
            .justifyContent(FlexAlign.Start)
            .layoutWeight(1)
            .margin({ top: -2 })

            Row({ space: 8 }) {
              this.SettingsChip()
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
          .margin({ bottom: 4 })

        }
        .width('100%')
        .padding({
          left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
          right: AppSizes.PADDING_PAGE + this.safeAreaRight,
          top: 12,
          bottom: 6
        })
        .backgroundColor(AppColors.SURFACE_1)

        Divider().color(AppColors.DIVIDER).width('100%')

        // Content area
        this.MainContent()

        // Bottom scan button (group mode)
        if (this.isInGroup) {
          Column() {
            Divider().color(AppColors.DIVIDER).width('100%')
            Row() {
              Column() {
                Text(this.t('快捷操作', 'Quick Actions'))
                  .fontSize(12)
                  .fontColor(AppColors.TEXT_MUTED)
                  .fontFamily('monospace')
                Text(this.t('扫描二维码绑定/登录', 'Scan QR to bind/login'))
                  .fontSize(12)
                  .fontColor(AppColors.TEXT_SECONDARY)
              }
              .layoutWeight(1)

              Button(this.t('扫  码', 'Scan'))
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
          .padding({
            left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
            right: AppSizes.PADDING_PAGE + this.safeAreaRight,
            top: 10,
            bottom: 16
          })
          .margin({ bottom: this.getBottomScanMargin() })
        }
      }
      .width('100%')
      .height('100%')
      .padding({ top: this.safeAreaTop, bottom: this.safeAreaBottom })

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
      .padding({
        left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
        right: AppSizes.PADDING_PAGE + this.safeAreaRight,
        top: 16,
        bottom: 16
      })
    }
    .width('100%')
    .align(Alignment.TopStart)
    .layoutWeight(1)
  }

  @Builder
  GroupSection() {
    if (!this.isInGroup) {
      this.ScanConnectSection()
    } else {
      Column() {
        Row() {
          Column() {
            Text(this.t('群组设备', 'Group Devices'))
              .fontSize(AppSizes.FONT_SIZE_BODY)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
          }
          .alignItems(HorizontalAlign.Start)
          .layoutWeight(1)

          Row({ space: 8 }) {
            this.StatusChip(
              this.t(`${this.groupMembers.length} 成员`, `${this.groupMembers.length} MEMBERS`),
              AppColors.ACCENT
            )
          }
        }
        .width('100%')
        .margin({ bottom: 14 })

        if (this.getGroupDevices().length === 0) {
          Text(this.t('群组暂无设备', 'No devices in the group'))
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ bottom: 12 })
        } else {
          Column() {
            ForEach(this.getGroupDevices(), (device: DeviceItemModel) => {
              Column() {
                this.DeviceCard(device, false, false)
              }
              .margin({ bottom: 12 })
            }, (device: DeviceItemModel) => `group_${device.deviceId}_${device.isOnline ? '1' : '0'}`)
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
          Text(this.t('扫码连接服务器', 'Scan to Connect Server'))
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Medium)
          Text(this.t('对准 PC 端命令中心生成的二维码', 'Aim at the QR code generated by the PC command center'))
            .fontSize(12)
            .fontColor(AppColors.TEXT_SECONDARY)
        }
        .layoutWeight(1)

        this.StatusChip(this.t('连接', 'LINK'), AppColors.ACCENT_BLUE)
      }
      .width('100%')
      .margin({ bottom: 12 })

      Text(this.t('将手机加入群组并绑定控制权限。', 'Join the group and bind control permissions.'))
        .fontSize(AppSizes.FONT_SIZE_SMALL)
        .fontColor(AppColors.TEXT_SECONDARY)
        .margin({ bottom: 20 })

      Button(this.isScanning
        ? this.t('扫码中...', 'Scanning...')
        : this.t('开始扫码', 'Start Scan'))
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
    if (this.scanStatus.length > 0 || this.parseError.length > 0) {
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
            Text(this.t('待加入设备', 'Pending Devices'))
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
          }
          Blank()
          Text(this.t('长按移入群组', 'Long press to move into group'))
            .fontSize(12)
            .fontColor(AppColors.TEXT_MUTED)
        }
        .width('100%')
        .margin({ bottom: 12 })

        Column() {
          ForEach(this.getSingleDevices(), (device: DeviceItemModel) => {
            Column() {
              this.DeviceCard(device, true, true)
            }
            .margin({ bottom: 12 })
          }, (device: DeviceItemModel) => `pending_${device.deviceId}_${device.isOnline ? '1' : '0'}`)
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
  DeviceCard(device: DeviceItemModel, allowMoveToGroup: boolean, isPendingSingle: boolean) {
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

            Text(device.isOnline ? this.t('在线', 'Online') : this.t('离线', 'Offline'))
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
        Text(this.t('设备设置', 'Device Settings'))
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
          Text(this.t('名称:', 'Name:'))
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
          Text(this.t('系统:', 'OS:'))
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
          Text(this.t('角色:', 'Role:'))
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
        Button(this.t('重命名', 'Rename'))
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
          Button(this.t('踢出群组', 'Remove from Group'))
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
            Button(this.t('指定为服务器', 'Set as Server'))
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
          Button(this.t('断开连接', 'Disconnect'))
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
        Button(this.t('取消', 'Cancel'))
          .width('100%')
          .height(44)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_SECONDARY)
          .backgroundColor(Color.Transparent)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .onClick(() => { this.closeSettingsSheet(); })
      }
      .width('100%')
      .padding({
        left: 24 + this.safeAreaLeft,
        right: 24 + this.safeAreaRight,
        top: 24,
        bottom: 24 + this.safeAreaBottom
      })
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
        Text(this.t('授权请求', 'Authorization Request'))
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 16 })

        Text(this.authRequesterName.length > 0
          ? this.t(`${this.authRequesterName} 请求`, `${this.authRequesterName} request`)
          : this.t('收到授权请求', 'Authorization request received'))
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

        Text(this.t(`操作: ${this.getAuthActionLabel(this.authAction)}`,
          `Action: ${this.getAuthActionLabel(this.authAction)}`))
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.TEXT_SECONDARY)
          .margin({ bottom: 20 })

        Row({ space: 12 }) {
          Button(this.t('拒绝', 'Reject'))
            .width(120)
            .height(44)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.TEXT_MUTED)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => { this.rejectAuth(); })

          Button(this.t('批准', 'Approve'))
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
    .padding({ left: 24 + this.safeAreaLeft, right: 24 + this.safeAreaRight })
    .backgroundColor('#B0000000')
    .justifyContent(FlexAlign.Center)
  }

  @Builder
  InviteConfirmDialog() {
    Column() {
      Column() {
        Text(this.t('邀请设备加入群组', 'Invite Device to Group'))
          .fontSize(18)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 10 })

        Text(this.t(
          `是否将 ${this.inviteDeviceName} 邀请加入当前群组？`,
          `Invite ${this.inviteDeviceName} to join this group?`
        ))
          .fontSize(13)
          .fontColor(AppColors.TEXT_SECONDARY)
          .textAlign(TextAlign.Center)
          .margin({ bottom: 18 })

        Row({ space: 12 }) {
          Button(this.t('取消', 'Cancel'))
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

          Button(this.t('邀请加入', 'Invite'))
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
    .padding({ left: 24 + this.safeAreaLeft, right: 24 + this.safeAreaRight })
    .backgroundColor('#B0000000')
    .justifyContent(FlexAlign.Center)
  }

  @Builder
  SettingsChip() {
    Row() {
      Text(this.t('设置', 'Settings'))
        .fontSize(11)
        .fontColor(AppColors.ACCENT)
        .fontFamily('monospace')
    }
    .padding({ left: 10, right: 10, top: 4, bottom: 4 })
    .backgroundColor(AppColors.CHIP_BG)
    .borderWidth(1)
    .borderColor(AppColors.ACCENT)
    .borderRadius(AppSizes.BORDER_RADIUS_TAG)
    .onClick(() => {
      this.openSettingsPage();
    })
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
    if (this.currentMode === DeviceMode.GROUP) return this.t('群组', 'Group');
    if (this.currentMode === DeviceMode.SINGLE) return this.t('单连', 'Single');
    return this.t('待机', 'Standby');
  }

  private getModeColor(): string {
    if (this.currentMode === DeviceMode.GROUP) return AppColors.ACCENT;
    if (this.currentMode === DeviceMode.SINGLE) return AppColors.ACCENT_BLUE;
    return AppColors.TEXT_MUTED;
  }

  private getAuthActionLabel(action: string): string {
    if (action === 'panel.login') return this.t('面板登录', 'Panel Login');
    if (action === 'terminal.open.remote') return this.t('打开远程终端', 'Open Remote Terminal');
    return action;
  }

  private getSingleDevices(): Array<DeviceItemModel> {
    return this.singleDevices;
  }

  private getGroupDevices(): Array<DeviceItemModel> {
    return this.groupDevices;
  }

  private findGroupMember(deviceId: string): GroupMemberModel | undefined {
    for (let i: number = 0; i < this.groupMembers.length; i++) {
      if (this.groupMembers[i].deviceId === deviceId) {
        return this.groupMembers[i];
      }
    }
    return undefined;
  }

  private getRoleLabel(role: string): string {
    if (role === 'Server') return this.t('服务器', 'Server');
    if (role === 'Member') return this.t('成员', 'Member');
    if (role === 'Mobile') return this.t('手机', 'Mobile');
    if (role === 'Pending') return this.t('待加入', 'Pending');
    if (role === '单连') return this.t('待加入', 'Pending');
    return role;
  }

  private designateAsServer(): void {
    if (ConnectionManager.isInGroup()) {
      ConnectionManager.requestGroupServerChange(this.settingsDeviceId);
    } else {
      ConnectionManager.designateRelay(this.settingsDeviceId);
    }
    this.closeSettingsSheet();
  }

  private applyGroupExitLocal(): void {
    this.isInGroup = false;
    this.groupId = '';
    this.groupDevices = [];
    this.groupMembers = [];
    this.currentMode = this.singleDevices.length > 0 ? DeviceMode.SINGLE : DeviceMode.STANDALONE;
  }

  private removeDeviceFromGroupLocal(deviceId: string): void {
    if (deviceId.length === 0) return;
    this.groupDevices = this.groupDevices.filter((item: DeviceItemModel) => item.deviceId !== deviceId);
    this.groupMembers = this.groupMembers.filter((item: GroupMemberModel) => item.deviceId !== deviceId);
  }

  private moveDeviceToGroup(device: DeviceItemModel): void {
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
