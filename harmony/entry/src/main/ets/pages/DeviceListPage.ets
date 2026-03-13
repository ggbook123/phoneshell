import { LengthMetrics } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState, StorageKeys } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';
import { PreferencesUtil } from '../services/PreferencesUtil';
import { DeviceInfo, GroupMemberInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const PAGE_TAG = 'DeviceListPage';

interface AuthorizationPageParams {
  requestId: string;
  action: string;
  requesterName: string;
  description: string;
}

interface SessionListParams {
  deviceId: string;
  displayName: string;
  availableShells: Array<string>;
}

@Entry
@Component
struct DeviceListPage {
  @State devices: Array<DeviceInfo> = [];
  @State connectionState: ConnectionState = ConnectionState.CONNECTED;
  @State isRefreshing: boolean = false;
  @State isGroupJoined: boolean = false;
  @State groupMemberCount: number = 0;
  @State showUnbindDialog: boolean = false;
  @State pendingUnbindDeviceId: string = '';
  @State pendingUnbindDisplayName: string = '';
  private stateListenerId: number = -1;
  private messageListenerId: number = -1;

  private normalizeString(value: string | undefined | null, fallback: string = ''): string {
    if (typeof value === 'string') {
      return value;
    }
    return fallback;
  }

  private getDeviceId(device: DeviceInfo): string {
    return this.normalizeString(device.deviceId);
  }

  private getDeviceDisplayName(device: DeviceInfo): string {
    const deviceId: string = this.getDeviceId(device);
    return this.normalizeString(device.displayName, deviceId.length > 0 ? deviceId : 'Unknown device');
  }

  private getDeviceOs(device: DeviceInfo): string {
    return this.normalizeString(device.os, 'Unknown');
  }

  private getAvailableShells(device: DeviceInfo): Array<string> {
    const rawShells: Array<string> | undefined = device.availableShells as Array<string> | undefined;
    if (!Array.isArray(rawShells)) {
      return [];
    }

    let normalized: Array<string> = [];
    for (let i: number = 0; i < rawShells.length; i++) {
      const shell: string | undefined = rawShells[i];
      if (typeof shell === 'string' && shell.length > 0) {
        normalized.push(shell as string);
      }
    }

    return normalized;
  }

  private getServerDeviceId(): string {
    const members: Array<GroupMemberInfo> = WebSocketService.getCachedGroupMembers();
    for (let i: number = 0; i < members.length; i++) {
      const member: GroupMemberInfo = members[i];
      if (member.role === 'Server') {
        return this.normalizeString(member.deviceId);
      }
    }
    return '';
  }

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
    const shouldRequestDevices: boolean = this.stateListenerId === -1 || this.messageListenerId === -1;
    this.connectionState = WebSocketService.getConnectionState();
    this.devices = WebSocketService.getCachedDevices();
    this.isGroupJoined = WebSocketService.getIsGroupJoined();
    this.groupMemberCount = WebSocketService.getCachedGroupMembers().length;

    if (this.stateListenerId === -1) {
      this.stateListenerId = WebSocketService.addOnStateChange((state: ConnectionState) => {
        this.connectionState = state;
        if (state === ConnectionState.DISCONNECTED) {
          this.getUIContext().getRouter().back();
        }
      });
    }

    if (this.messageListenerId === -1) {
      this.messageListenerId = WebSocketService.addOnMessage((type: string, data: Record<string, Object>) => {
        if (type === 'device.list') {
          this.devices = WebSocketService.getCachedDevices();
          this.isRefreshing = false;
        } else if (type === 'error') {
          this.isRefreshing = false;
        } else if (type === 'auth.request') {
          // Navigate to authorization page
          const requestId: string = (data['requestId'] ?? '') as string;
          const action: string = (data['action'] ?? '') as string;
          const requesterName: string = (data['requesterName'] ?? '') as string;
          const description: string = (data['description'] ?? '') as string;
          const params: AuthorizationPageParams = {
            requestId: requestId,
            action: action,
            requesterName: requesterName,
            description: description
          };
          try {
            this.getUIContext().getRouter().pushUrl({
              url: 'pages/AuthorizationPage',
              params: params
            });
          } catch (_e) {
            // Ignore navigation errors
          }
        } else if (type === 'group.join.accepted') {
          this.isGroupJoined = true;
          this.groupMemberCount = WebSocketService.getCachedGroupMembers().length;
        } else if (type === 'group.member.joined' || type === 'group.member.left' || type === 'group.member.list') {
          this.groupMemberCount = WebSocketService.getCachedGroupMembers().length;
        }
      });
    }

    if (shouldRequestDevices && this.connectionState === ConnectionState.CONNECTED) {
      this.isRefreshing = true;
      WebSocketService.requestDeviceList();
    }
  }

  private deactivatePage(): void {
    if (this.stateListenerId !== -1) {
      WebSocketService.removeOnStateChange(this.stateListenerId);
      this.stateListenerId = -1;
    }

    if (this.messageListenerId !== -1) {
      WebSocketService.removeOnMessage(this.messageListenerId);
      this.messageListenerId = -1;
    }
  }

  private refreshDevices(): void {
    this.isRefreshing = true;
    WebSocketService.requestDeviceList();
  }

  private onDeviceClick(device: DeviceInfo): void {
    if (!device.isOnline) {
      return;
    }
    const deviceId: string = this.getDeviceId(device);
    if (deviceId.length === 0) {
      return;
    }
    // Navigate to session list page for this device
    hilog.info(DOMAIN, PAGE_TAG, 'navigateToSessionList device=%{public}s', deviceId);
    const params: SessionListParams = {
      deviceId: deviceId,
      displayName: this.getDeviceDisplayName(device),
      availableShells: this.getAvailableShells(device)
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

  private requestUnbind(device: DeviceInfo): void {
    const deviceId: string = this.getDeviceId(device);
    if (deviceId.length === 0) {
      return;
    }
    this.pendingUnbindDeviceId = deviceId;
    this.pendingUnbindDisplayName = this.getDeviceDisplayName(device);
    this.showUnbindDialog = true;
  }

  private cancelUnbind(): void {
    this.showUnbindDialog = false;
    this.pendingUnbindDeviceId = '';
    this.pendingUnbindDisplayName = '';
  }

  private confirmUnbind(): void {
    const deviceId: string = this.normalizeString(this.pendingUnbindDeviceId);
    this.showUnbindDialog = false;
    this.pendingUnbindDeviceId = '';
    this.pendingUnbindDisplayName = '';
    if (deviceId.length === 0) {
      return;
    }
    WebSocketService.unregisterDevice(deviceId);

    const serverDeviceId: string = this.getServerDeviceId();
    if (serverDeviceId.length > 0 && deviceId === serverDeviceId) {
      PreferencesUtil.setString(StorageKeys.GROUP_SECRET, '');
      WebSocketService.resetGroupState();
      WebSocketService.disconnect();
      return;
    }

    this.refreshDevices();
  }

  private getOsIcon(os: string): string {
    const osLower: string = os.toLowerCase();
    if (osLower.indexOf('windows') >= 0) {
      return '🖥';
    } else if (osLower.indexOf('linux') >= 0) {
      return '🐧';
    } else if (osLower.indexOf('mac') >= 0 || osLower.indexOf('darwin') >= 0) {
      return '🍎';
    }
    return '💻';
  }

  private disconnectAndGoBack(): void {
    WebSocketService.disconnect();
  }

  build() {
    Column() {
      // Top bar
      Row() {
        Text(AppStrings.DEVICE_LIST_TITLE)
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Bold)
          .layoutWeight(1)

        // Connection status dot
        Circle()
          .width(AppSizes.STATUS_DOT_SIZE)
          .height(AppSizes.STATUS_DOT_SIZE)
          .fill(this.connectionState === ConnectionState.CONNECTED ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
          .margin({ right: 12 })

        // Disconnect button
        Button(AppStrings.DISCONNECT_BUTTON)
          .height(32)
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.ERROR_RED)
          .backgroundColor(Color.Transparent)
          .borderWidth(1)
          .borderColor(AppColors.ERROR_RED)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
          .onClick(() => {
            this.disconnectAndGoBack();
          })
      }
      .width('100%')
      .height(56)
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
      .alignItems(VerticalAlign.Center)

      // Group management bar (shown when group is joined)
      if (this.isGroupJoined) {
        Row() {
          Text(`${AppStrings.GROUP_STATUS_JOINED} · ${this.groupMemberCount} 成员`)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.ACCENT)
            .layoutWeight(1)

          Button(AppStrings.GROUP_MANAGE_TITLE)
            .height(28)
            .fontSize(12)
            .fontColor(AppColors.ACCENT)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.ACCENT)
            .borderRadius(AppSizes.BORDER_RADIUS_TAG)
            .onClick(() => {
              try {
                this.getUIContext().getRouter().pushUrl({ url: 'pages/GroupManagePage' });
              } catch (_e) {
                // Ignore navigation errors
              }
            })
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, top: 8, bottom: 8 })
        .backgroundColor(AppColors.CARD_BACKGROUND)
        .alignItems(VerticalAlign.Center)
      }

      // Divider
      Divider()
        .color(AppColors.DIVIDER)
        .width('100%')

      // Device list or empty state
      if (this.devices.length === 0) {
        Column() {
          Text('📡')
            .fontSize(48)
            .margin({ bottom: 16 })

          Text(AppStrings.EMPTY_DEVICE_LIST)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ bottom: 24 })

          Button('刷新')
            .height(36)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.ACCENT)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.ACCENT)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => {
              this.refreshDevices();
            })
        }
        .width('100%')
        .layoutWeight(1)
        .justifyContent(FlexAlign.Center)
        .alignItems(HorizontalAlign.Center)
      } else {
        Refresh({ refreshing: $$this.isRefreshing }) {
          List({ space: 12 }) {
            ForEach(this.devices, (device: DeviceInfo) => {
              ListItem() {
                this.DeviceCard(device)
              }
            }, (device: DeviceInfo, index: number) => `${this.getDeviceId(device)}_${index}`)
          }
          .width('100%')
          .padding({
            left: AppSizes.PADDING_PAGE,
            right: AppSizes.PADDING_PAGE,
            top: 12,
            bottom: 12
          })
        }
        .onRefreshing(() => {
          this.refreshDevices();
        })
        .layoutWeight(1)
      }

      if (this.showUnbindDialog) {
        Column() {
          Column() {
            Text('解绑确认')
              .fontSize(18)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
              .margin({ bottom: 10 })

            Text(`确认解绑设备 "${this.pendingUnbindDisplayName}" 吗？解绑后需要重新扫码绑定。`)
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
                  this.cancelUnbind();
                })

              Button('确认解绑')
                .width(120)
                .height(40)
                .fontSize(AppSizes.FONT_SIZE_SMALL)
                .fontColor('#FFFFFF')
                .backgroundColor(AppColors.ERROR_RED)
                .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
                .onClick(() => {
                  this.confirmUnbind();
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
    .backgroundColor(AppColors.BACKGROUND)
  }

  @Builder
  DeviceCard(device: DeviceInfo) {
    Column() {
      Row() {
        // OS icon + device name
        Text(this.getOsIcon(this.getDeviceOs(device)))
          .fontSize(24)
          .margin({ right: 12 })

        Column() {
          Text(this.getDeviceDisplayName(device))
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Medium)
            .maxLines(1)
            .textOverflow({ overflow: TextOverflow.Ellipsis })

          Text(this.getDeviceOs(device))
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ top: 4 })
        }
        .alignItems(HorizontalAlign.Start)
        .layoutWeight(1)

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

      // Available shells as tags
      if (this.getAvailableShells(device).length > 0) {
        Flex({ wrap: FlexWrap.Wrap, space: { main: new LengthMetrics(8), cross: new LengthMetrics(8) } }) {
          ForEach(this.getAvailableShells(device), (shell: string) => {
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

      if (this.connectionState === ConnectionState.CONNECTED) {
        Row() {
          Button('解绑')
            .height(28)
            .fontSize(12)
            .fontColor(AppColors.ERROR_RED)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.ERROR_RED)
            .borderRadius(AppSizes.BORDER_RADIUS_TAG)
            .onClick(() => {
              this.requestUnbind(device);
            })
        }
        .width('100%')
        .justifyContent(FlexAlign.End)
        .margin({ top: 12 })
      }
    }
    .width('100%')
    .padding(AppSizes.PADDING_CARD)
    .backgroundColor(AppColors.CARD_BACKGROUND)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .opacity(device.isOnline ? 1.0 : 0.5)
    .onClick(() => {
      this.onDeviceClick(device);
    })
  }
}
