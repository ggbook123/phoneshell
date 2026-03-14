import { LengthMetrics } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState, DeviceMode } from '../common/Constants';
import { ConnectionManager, SingleDeviceRecord } from '../services/ConnectionManager';
import { AuthManager, AuthPendingRequest } from '../services/AuthManager';
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
  @State devices: Array<DeviceInfo> = [];
  @State currentMode: DeviceMode = DeviceMode.STANDALONE;
  @State selectedTab: number = 0; // 0 = single, 1 = group
  @State isRefreshing: boolean = false;
  @State groupMembers: Array<GroupMemberInfo> = [];
  @State groupId: string = '';

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
    this.devices = ConnectionManager.getAllDevices();
    this.groupMembers = ConnectionManager.getGroupMembers();
    this.groupId = ConnectionManager.getGroupId();
    this.pendingAuthCount = AuthManager.getPendingCount();

    // Auto-select tab based on mode
    if (this.currentMode === DeviceMode.GROUP) {
      this.selectedTab = 1;
    }

    if (this.deviceListCallbackId === -1) {
      this.deviceListCallbackId = ConnectionManager.addOnDeviceListChanged((devices: Array<DeviceInfo>) => {
        this.devices = devices;
        this.groupMembers = ConnectionManager.getGroupMembers();
        this.isRefreshing = false;
      });
    }

    if (this.modeChangeCallbackId === -1) {
      this.modeChangeCallbackId = ConnectionManager.addOnModeChange((mode: DeviceMode) => {
        this.currentMode = mode;
        if (mode === DeviceMode.GROUP) {
          this.selectedTab = 1;
        }
        this.groupId = ConnectionManager.getGroupId();
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

  private openDeviceSettings(device: DeviceInfo): void {
    this.settingsDeviceId = device.deviceId;
    this.settingsDisplayName = device.displayName;
    this.settingsOs = device.os;
    this.settingsEditName = device.displayName;

    // Determine role from group members if in group mode
    this.settingsIsGroupDevice = this.currentMode === DeviceMode.GROUP;
    if (this.settingsIsGroupDevice) {
      const member: GroupMemberInfo | undefined = this.findGroupMember(device.deviceId);
      this.settingsRole = member !== undefined ? member.role : 'Member';
    } else {
      this.settingsRole = '单连';
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

  private navigateToScan(): void {
    try {
      this.getUIContext().getRouter().pushUrl({ url: 'pages/ScanPage' });
    } catch (_e) {
      // Ignore navigation errors
    }
  }

  build() {
    Stack() {
      Column() {
        // Top bar
        Row() {
          Text(AppStrings.APP_NAME)
            .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Bold)
            .layoutWeight(1)

          // Auth badge
          if (this.pendingAuthCount > 0) {
            Button(`${this.pendingAuthCount}`)
              .height(24)
              .fontSize(11)
              .fontColor('#FFFFFF')
              .backgroundColor(AppColors.ERROR_RED)
              .borderRadius(12)
              .padding({ left: 8, right: 8 })
              .margin({ right: 8 })
              .onClick(() => {
                AuthManager.showPending();
              })
          }
        }
        .width('100%')
        .height(56)
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
        .alignItems(VerticalAlign.Center)

        // Tab selector
        Row() {
          Button('单连模式')
            .height(36)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(this.selectedTab === 0 ? '#FFFFFF' : AppColors.TEXT_SECONDARY)
            .backgroundColor(this.selectedTab === 0 ? AppColors.ACCENT : Color.Transparent)
            .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
            .layoutWeight(1)
            .onClick(() => { this.selectedTab = 0; })

          Button('群组模式')
            .height(36)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(this.selectedTab === 1 ? '#FFFFFF' : AppColors.TEXT_SECONDARY)
            .backgroundColor(this.selectedTab === 1 ? AppColors.ACCENT : Color.Transparent)
            .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
            .layoutWeight(1)
            .onClick(() => { this.selectedTab = 1; })
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, bottom: 8 })
        .backgroundColor(AppColors.CARD_BACKGROUND)

        Divider().color(AppColors.DIVIDER).width('100%')

        // Content area
        if (this.selectedTab === 0) {
          this.SingleModeContent()
        } else {
          this.GroupModeContent()
        }

        // Bottom scan button
        Column() {
          Divider().color(AppColors.DIVIDER).width('100%')
          Button('扫  码')
            .width('100%')
            .height(48)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor('#FFFFFF')
            .backgroundColor(AppColors.ACCENT)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => { this.navigateToScan(); })
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 8, bottom: 16 })
      }
      .width('100%')
      .height('100%')
      .backgroundColor(AppColors.BACKGROUND)

      // Settings sheet overlay
      if (this.showSettingsSheet) {
        this.SettingsSheet()
      }

      // Auth dialog overlay
      if (this.showAuthDialog) {
        this.AuthDialog()
      }
    }
    .width('100%')
    .height('100%')
  }

  @Builder
  SingleModeContent() {
    if (this.getSingleDevices().length === 0) {
      Column() {
        Text('暂无连接设备')
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_SECONDARY)
          .margin({ bottom: 12 })

        Text('扫描设备二维码来添加设备')
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.TEXT_SECONDARY)
      }
      .width('100%')
      .layoutWeight(1)
      .justifyContent(FlexAlign.Center)
      .alignItems(HorizontalAlign.Center)
    } else {
      List({ space: 12 }) {
        ForEach(this.getSingleDevices(), (device: DeviceInfo) => {
          ListItem() {
            this.DeviceCard(device)
          }
        }, (device: DeviceInfo, index: number) => `single_${device.deviceId}_${index}`)
      }
      .width('100%')
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 12, bottom: 12 })
      .layoutWeight(1)
    }
  }

  @Builder
  GroupModeContent() {
    if (!ConnectionManager.isInGroup()) {
      Column() {
        Text('当前没有群组')
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_SECONDARY)
          .margin({ bottom: 8 })

        Text('可将单连设备拉入群组')
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.TEXT_SECONDARY)
          .margin({ bottom: 24 })

        Button('创建群组')
          .width(180)
          .height(44)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor('#FFFFFF')
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .enabled(this.getSingleDevices().length > 0)
          .onClick(() => { this.createGroupFromSingle(); })
      }
      .width('100%')
      .layoutWeight(1)
      .justifyContent(FlexAlign.Center)
      .alignItems(HorizontalAlign.Center)
    } else {
      Column() {
        // Group info header
        Row() {
          Text(`群组 · ${this.groupMembers.length} 成员`)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.ACCENT)
            .layoutWeight(1)

          Button('解散')
            .height(28)
            .fontSize(12)
            .fontColor(AppColors.ERROR_RED)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.ERROR_RED)
            .borderRadius(AppSizes.BORDER_RADIUS_TAG)
            .onClick(() => { ConnectionManager.dissolveGroup(); })
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 8, bottom: 8 })

        List({ space: 12 }) {
          ForEach(this.getGroupDevices(), (device: DeviceInfo) => {
            ListItem() {
              this.DeviceCard(device)
            }
          }, (device: DeviceInfo, index: number) => `group_${device.deviceId}_${index}`)
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 4, bottom: 12 })
        .layoutWeight(1)
      }
      .width('100%')
      .layoutWeight(1)
    }
  }

  @Builder
  DeviceCard(device: DeviceInfo) {
    Column() {
      Row() {
        Text(this.getOsIcon(device.os))
          .fontSize(24)
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
        }
        .alignItems(HorizontalAlign.Start)
        .layoutWeight(1)

        // Settings gear
        Text('⚙')
          .fontSize(20)
          .fontColor(AppColors.TEXT_SECONDARY)
          .padding(8)
          .onClick(() => { this.openDeviceSettings(device); })

        // Online status
        Row() {
          Circle()
            .width(AppSizes.STATUS_DOT_SIZE)
            .height(AppSizes.STATUS_DOT_SIZE)
            .fill(device.isOnline ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
            .margin({ right: 6 })

          Text(device.isOnline ? '在线' : '离线')
            .fontSize(12)
            .fontColor(device.isOnline ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
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
              .backgroundColor('#0F3460')
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
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .opacity(device.isOnline ? 1.0 : 0.5)
    .onClick(() => { this.onDeviceClick(device); })
    .gesture(
      LongPressGesture({ repeat: false, duration: 600 })
        .onAction(() => {
          if (device.isOnline && this.selectedTab === 0) {
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
        Text(`── ${this.settingsDisplayName} ──`)
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 20 })
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

        // Kick from group (group mode only)
        if (this.currentMode === DeviceMode.GROUP) {
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
              .backgroundColor('#0F3460')
              .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
              .margin({ bottom: 8 })
              .onClick(() => { this.designateAsServer(); })
          }
        }

        // Disconnect (single mode only)
        if (this.currentMode !== DeviceMode.GROUP) {
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
      .backgroundColor(AppColors.CARD_BACKGROUND)
      .borderRadius({ topLeft: 16, topRight: 16 })
    }
    .width('100%')
    .height('100%')
    .backgroundColor('#90000000')
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
            .borderColor(AppColors.TEXT_SECONDARY)
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

  private getAuthActionLabel(action: string): string {
    if (action === 'panel.login') return '面板登录';
    if (action === 'terminal.open.remote') return '打开远程终端';
    return action;
  }

  private getSingleDevices(): Array<DeviceInfo> {
    if (this.currentMode === DeviceMode.GROUP) return [];
    return this.devices;
  }

  private getGroupDevices(): Array<DeviceInfo> {
    if (this.currentMode !== DeviceMode.GROUP) return [];
    return this.devices;
  }

  private createGroupFromSingle(): void {
    // Designate the first connected single device as relay
    const singleDevices: Array<DeviceInfo> = this.getSingleDevices();
    if (singleDevices.length === 0) return;

    // Pick first online device
    for (let i: number = 0; i < singleDevices.length; i++) {
      if (singleDevices[i].isOnline) {
        ConnectionManager.designateRelay(singleDevices[i].deviceId);
        return;
      }
    }
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
    if (role === '单连') return '单连';
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
