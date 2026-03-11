import { AppColors, AppSizes, AppStrings, ConnectionState, StorageKeys } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';
import { PreferencesUtil } from '../services/PreferencesUtil';

@Entry
@Component
struct Index {
  @State serverUrl: string = '';
  @State groupSecret: string = '';
  @State connectionState: ConnectionState = ConnectionState.DISCONNECTED;
  @State connectionHistory: Array<string> = [];
  @State errorMessage: string = '';
  private stateListenerId: number = -1;
  private messageListenerId: number = -1;
  private hasAutoNavigated: boolean = false;

  aboutToAppear(): void {
    this.refreshPreferencesAndMaybeNavigate();
    this.activatePage();
  }

  aboutToDisappear(): void {
    this.deactivatePage();
  }

  onPageShow(): void {
    this.refreshPreferencesAndMaybeNavigate();
    this.activatePage();
  }

  onPageHide(): void {
    this.deactivatePage();
  }

  private async initPreferences(): Promise<void> {
    const context: Context | undefined = this.getUIContext().getHostContext();
    if (context !== undefined) {
      await PreferencesUtil.init(context);
    }
    this.connectionHistory = await PreferencesUtil.getConnectionHistory();
    const lastUrl: string = await PreferencesUtil.getLastServerUrl();
    if (lastUrl.length > 0) {
      this.serverUrl = lastUrl;
    }
    const savedSecret: string = await PreferencesUtil.getString(StorageKeys.GROUP_SECRET);
    if (savedSecret.length > 0) {
      this.groupSecret = savedSecret;
    }
  }

  private refreshPreferencesAndMaybeNavigate(): void {
    this.initPreferences().then(() => {
      this.connectionState = WebSocketService.getConnectionState();
      this.maybeNavigateOnConnected();
    });
  }

  private async saveAndNavigate(): Promise<void> {
    await PreferencesUtil.addConnectionHistory(this.serverUrl);
    await PreferencesUtil.setLastServerUrl(this.serverUrl);
    if (this.groupSecret.length > 0) {
      await PreferencesUtil.setString(StorageKeys.GROUP_SECRET, this.groupSecret);
    }
    this.connectionHistory = await PreferencesUtil.getConnectionHistory();
    this.getUIContext().getRouter().pushUrl({ url: 'pages/DeviceListPage' });
  }

  private normalizeServerUrl(rawUrl: string): string {
    let normalized: string = rawUrl.trim();
    if (normalized.length === 0) {
      return '';
    }

    if (normalized.startsWith('http://')) {
      normalized = 'ws://' + normalized.substring(7);
    } else if (normalized.startsWith('https://')) {
      normalized = 'wss://' + normalized.substring(8);
    } else if (!normalized.startsWith('ws://') && !normalized.startsWith('wss://')) {
      normalized = 'ws://' + normalized;
    }

    while (normalized.startsWith('ws://http://') || normalized.startsWith('ws://https://') ||
           normalized.startsWith('wss://http://') || normalized.startsWith('wss://https://') ||
           normalized.startsWith('ws://ws://') || normalized.startsWith('wss://wss://')) {
      const schemeSeparator: number = normalized.indexOf('://');
      normalized = normalized.substring(schemeSeparator + 3);
      if (!normalized.startsWith('ws://') && !normalized.startsWith('wss://') &&
          !normalized.startsWith('http://') && !normalized.startsWith('https://')) {
        normalized = 'ws://' + normalized;
      }
    }

    const schemeSeparator: number = normalized.indexOf('://');
    const scheme: string = normalized.substring(0, schemeSeparator);
    let rest: string = normalized.substring(schemeSeparator + 3);
    while (rest.startsWith('/')) {
      rest = rest.substring(1);
    }

    const slashIndex: number = rest.indexOf('/');
    if (slashIndex < 0) {
      rest = rest + '/ws/';
    } else {
      const hostPart: string = rest.substring(0, slashIndex);
      const pathPart: string = rest.substring(slashIndex);
      if (pathPart === '/' || pathPart.length === 0 || pathPart === '/ws' || pathPart === '/ws/') {
        rest = hostPart + '/ws/';
      } else if (pathPart.indexOf('/ws') !== 0) {
        rest = hostPart + '/ws/';
      }
    }

    return scheme + '://' + rest;
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

  private validateServerUrl(url: string): string {
    const schemeSeparator: number = url.indexOf('://');
    if (schemeSeparator < 0) {
      return '请输入正确的服务器地址';
    }

    let rest: string = url.substring(schemeSeparator + 3);
    const slashIndex: number = rest.indexOf('/');
    if (slashIndex >= 0) {
      rest = rest.substring(0, slashIndex);
    }

    const host: string = rest.split(':')[0];
    if (host === '0.0.0.0' || host === '127.0.0.1' || host === 'localhost' ||
        host === '*' || host === '+') {
      return '请填写 PC 的实际局域网 IP，不要填 0.0.0.0 或 localhost';
    }

    return '';
  }

  private connectToServer(): void {
    const normalizedUrl: string = this.normalizeServerUrl(this.serverUrl);
    if (normalizedUrl.length === 0) {
      this.errorMessage = '请输入服务器地址';
      return;
    }

    const validationMessage: string = this.validateServerUrl(normalizedUrl);
    if (validationMessage.length > 0) {
      this.errorMessage = validationMessage;
      return;
    }

    this.serverUrl = normalizedUrl;
    this.errorMessage = '';
    const connectUrl: string = this.buildConnectUrlWithToken(normalizedUrl, this.groupSecret);
    WebSocketService.connect(connectUrl);
  }

  private maybeNavigateOnConnected(): void {
    if (this.connectionState !== ConnectionState.CONNECTED) {
      return;
    }

    if (this.hasAutoNavigated) {
      return;
    }

    this.hasAutoNavigated = true;
    this.saveAndNavigate();
  }

  private activatePage(): void {
    this.connectionState = WebSocketService.getConnectionState();

    if (this.stateListenerId === -1) {
      this.stateListenerId = WebSocketService.addOnStateChange((state: ConnectionState) => {
        this.connectionState = state;
        if (state === ConnectionState.CONNECTED) {
          this.errorMessage = '';
          // After connecting, join group if a group secret is provided
          if (this.groupSecret.trim().length > 0) {
            const deviceId: string = PreferencesUtil.getMobileDeviceId(getContext(this));
            WebSocketService.joinGroup(this.groupSecret.trim(), deviceId, 'HarmonyOS Device');
          }
          this.maybeNavigateOnConnected();
        } else if (state === ConnectionState.DISCONNECTED) {
          this.hasAutoNavigated = false;
        }
      });
    }

    if (this.messageListenerId === -1) {
      this.messageListenerId = WebSocketService.addOnMessage((type: string, data: Record<string, Object>) => {
        if (type === 'error') {
          const msgValue: Object | undefined = data['message'];
          if (msgValue !== undefined && msgValue !== null) {
            this.errorMessage = msgValue as string;
          } else {
            this.errorMessage = '连接错误';
          }
        }
      });
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

  private getStatusText(): string {
    if (this.connectionState === ConnectionState.CONNECTING) {
      return AppStrings.STATUS_CONNECTING;
    } else if (this.connectionState === ConnectionState.CONNECTED) {
      return AppStrings.STATUS_CONNECTED;
    }
    return AppStrings.STATUS_DISCONNECTED;
  }

  private getStatusColor(): string {
    if (this.connectionState === ConnectionState.CONNECTING) {
      return '#FFA500';
    } else if (this.connectionState === ConnectionState.CONNECTED) {
      return AppColors.ONLINE_GREEN;
    }
    return AppColors.OFFLINE_GRAY;
  }

  build() {
    Column() {
      // Top spacer
      Blank().height(60)

      // App Logo area
      Text('>')
        .fontSize(48)
        .fontColor(AppColors.ACCENT)
        .fontWeight(FontWeight.Bold)
        .fontFamily('monospace')
        .margin({ bottom: 8 })

      Text(AppStrings.APP_NAME)
        .fontSize(AppSizes.FONT_SIZE_TITLE)
        .fontColor(AppColors.TEXT_PRIMARY)
        .fontWeight(FontWeight.Bold)
        .margin({ bottom: 4 })

      Text('远程终端控制')
        .fontSize(AppSizes.FONT_SIZE_SMALL)
        .fontColor(AppColors.TEXT_SECONDARY)
        .margin({ bottom: 40 })

      // Connection status indicator
      Row() {
        Circle()
          .width(AppSizes.STATUS_DOT_SIZE)
          .height(AppSizes.STATUS_DOT_SIZE)
          .fill(this.getStatusColor())
          .margin({ right: 8 })

        Text(this.getStatusText())
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(this.getStatusColor())
      }
      .margin({ bottom: 20 })

      // Server URL input
      TextInput({ text: $$this.serverUrl, placeholder: AppStrings.SERVER_ADDRESS_HINT })
        .type(InputType.Normal)
        .width('100%')
        .height(48)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor(AppColors.TEXT_PRIMARY)
        .placeholderColor(AppColors.TEXT_SECONDARY)
        .backgroundColor(AppColors.INPUT_BG)
        .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
        .padding({ left: 16, right: 16 })
        .margin({ bottom: 12 })

      // Group secret input
      TextInput({ text: $$this.groupSecret, placeholder: AppStrings.GROUP_SECRET_HINT })
        .type(InputType.Normal)
        .width('100%')
        .height(48)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor(AppColors.TEXT_PRIMARY)
        .placeholderColor(AppColors.TEXT_SECONDARY)
        .backgroundColor(AppColors.INPUT_BG)
        .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
        .padding({ left: 16, right: 16 })
        .margin({ bottom: 16 })

      // Error message
      if (this.errorMessage.length > 0) {
        Text(this.errorMessage)
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.ERROR_RED)
          .margin({ bottom: 12 })
          .width('100%')
          .textAlign(TextAlign.Start)
      }

      // Connect button
      Button(this.connectionState === ConnectionState.CONNECTING ? AppStrings.STATUS_CONNECTING : AppStrings.CONNECT_BUTTON)
        .width('100%')
        .height(48)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor('#FFFFFF')
        .backgroundColor(this.connectionState === ConnectionState.CONNECTING ? AppColors.ACCENT_PRESSED : AppColors.ACCENT)
        .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
        .enabled(this.connectionState !== ConnectionState.CONNECTING)
        .onClick(() => {
          this.connectToServer();
        })
        .margin({ bottom: 12 })

      // Scan connect button
      Button('扫码连接')
        .width('100%')
        .height(44)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor(AppColors.TEXT_PRIMARY)
        .backgroundColor(Color.Transparent)
        .borderWidth(1)
        .borderColor(AppColors.ACCENT)
        .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
        .onClick(() => {
          this.getUIContext().getRouter().pushUrl({ url: 'pages/ScanPage' });
        })
        .margin({ bottom: 32 })

      // History section
      if (this.connectionHistory.length > 0) {
        Row() {
          Text(AppStrings.HISTORY_TITLE)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
          Blank()
        }
        .width('100%')
        .margin({ bottom: 8 })

        List() {
          ForEach(this.connectionHistory, (url: string) => {
            ListItem() {
              Row() {
                Text(url)
                  .fontSize(AppSizes.FONT_SIZE_SMALL)
                  .fontColor(AppColors.TEXT_PRIMARY)
                  .layoutWeight(1)
                  .maxLines(1)
                  .textOverflow({ overflow: TextOverflow.Ellipsis })

                Text('x')
                  .fontSize(AppSizes.FONT_SIZE_SMALL)
                  .fontColor(AppColors.TEXT_SECONDARY)
                  .padding({ left: 12, right: 4, top: 4, bottom: 4 })
                  .onClick(() => {
                    this.removeHistoryItem(url);
                  })
              }
              .width('100%')
              .height(44)
              .padding({ left: 16, right: 12 })
              .backgroundColor(AppColors.CARD_BACKGROUND)
              .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
              .onClick(() => {
                this.serverUrl = url;
                this.connectToServer();
              })
            }
            .margin({ bottom: 8 })
          }, (url: string) => url)
        }
        .width('100%')
        .layoutWeight(1)
      } else {
        Blank().layoutWeight(1)
      }
    }
    .width('100%')
    .height('100%')
    .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
    .backgroundColor(AppColors.BACKGROUND)
    .alignItems(HorizontalAlign.Center)
  }

  private async removeHistoryItem(url: string): Promise<void> {
    await PreferencesUtil.removeConnectionHistory(url);
    this.connectionHistory = await PreferencesUtil.getConnectionHistory();
  }
}
