import { LengthMetrics } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState } from '../common/Constants';
import { ConnectionManager } from '../services/ConnectionManager';

const DOMAIN = 0x0000;
const PAGE_TAG = 'SessionListPage';

interface SessionItem {
  sessionId: string;
  shellId: string;
  title: string;
}

interface PageParams {
  deviceId: string;
  displayName: string;
  availableShells: Array<string>;
}

@Entry
@Component
struct SessionListPage {
  @State sessions: Array<SessionItem> = [];
  @State connectionState: ConnectionState = ConnectionState.CONNECTED;
  @State isRefreshing: boolean = false;
  @State deviceId: string = '';
  @State displayName: string = '';
  @State availableShells: Array<string> = [];
  @State selectedShell: string = '';
  private stateListenerId: number = -1;
  private messageListenerId: number = -1;
  private pendingOpenRequestedAt: number = 0;

  private normalizeString(value: string | undefined | null, fallback: string = ''): string {
    if (typeof value === 'string') {
      return value;
    }
    return fallback;
  }

  aboutToAppear(): void {
    // Get params from router
    const params: Object | undefined = this.getUIContext().getRouter().getParams() as Object | undefined;
    if (params !== undefined && params !== null) {
      const record: Record<string, Object> = params as Record<string, Object>;
      this.deviceId = this.normalizeString(record['deviceId'] as string | undefined);
      this.displayName = this.normalizeString(record['displayName'] as string | undefined, this.deviceId);
      const shells: Object | undefined = record['availableShells'];
      if (Array.isArray(shells)) {
        const rawShells: Array<Object> = shells as Array<Object>;
        let normalized: Array<string> = [];
        for (let i: number = 0; i < rawShells.length; i++) {
          const shell: Object = rawShells[i];
          if (typeof shell === 'string' && shell.length > 0) {
            normalized.push(shell as string);
          }
        }
        this.availableShells = normalized;
        if (normalized.length > 0) {
          this.selectedShell = normalized[0];
        }
      }
    }
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
    this.connectionState = ConnectionManager.getConnectionStateForDevice(this.deviceId);

    if (this.stateListenerId === -1) {
      this.stateListenerId = ConnectionManager.addOnStateChange((state: ConnectionState, deviceId: string) => {
        if (deviceId === this.deviceId || deviceId === 'group') {
          this.connectionState = state;
          if (state === ConnectionState.DISCONNECTED) {
            this.getUIContext().getRouter().back();
          } else if (state === ConnectionState.CONNECTED) {
            this.refreshSessions();
          }
        }
      });
    }

    if (this.messageListenerId === -1) {
      this.messageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
        if (type === 'session.list') {
          this.handleSessionList(data);
        } else if (type === 'terminal.opened') {
          const sessionId: Object | undefined = data['sessionId'];
          const deviceIdValue: Object | undefined = data['deviceId'];
          const resolvedSessionId: string =
            (typeof sessionId === 'string' && sessionId.length > 0) ? sessionId as string : '';
          const resolvedDeviceId: string =
            (typeof deviceIdValue === 'string' && deviceIdValue.length > 0)
              ? deviceIdValue as string
              : this.deviceId;
          if (resolvedSessionId.length > 0) {
            hilog.info(DOMAIN, PAGE_TAG, 'terminal.opened session=%{public}s device=%{public}s',
              resolvedSessionId, resolvedDeviceId);
            if (resolvedDeviceId === this.deviceId) {
              if (this.isPendingOpen()) {
                this.clearPendingOpen();
                ConnectionManager.setActiveSession(resolvedDeviceId, resolvedSessionId);
                try {
                  this.getUIContext().getRouter().pushUrl({ url: 'pages/TerminalPage' });
                } catch (_e) {
                  // Ignore navigation errors
                }
              } else {
                // Session created from another client; refresh list to show it.
                this.refreshSessions();
              }
            }
          }
        } else if (type === 'error') {
          this.clearPendingOpen();
          this.isRefreshing = false;
        }
      });
    }

    // Request session list for this device
    if (this.deviceId.length > 0 && this.connectionState === ConnectionState.CONNECTED) {
      this.isRefreshing = true;
      ConnectionManager.requestSessionList(this.deviceId);
    }
  }

  private deactivatePage(): void {
    if (this.stateListenerId !== -1) {
      ConnectionManager.removeOnStateChange(this.stateListenerId);
      this.stateListenerId = -1;
    }

    if (this.messageListenerId !== -1) {
      ConnectionManager.removeOnMessage(this.messageListenerId);
      this.messageListenerId = -1;
    }
    this.clearPendingOpen();
  }

  private handleSessionList(data: Record<string, Object>): void {
    const deviceId: string = this.normalizeString(data['deviceId'] as string | undefined);
    if (deviceId.length > 0 && deviceId !== this.deviceId) {
      return;
    }

    const sessionsRaw: Object | undefined = data['sessions'];
    let result: Array<SessionItem> = [];
    if (Array.isArray(sessionsRaw)) {
      const rawItems: Array<Object> = sessionsRaw as Array<Object>;
      for (let i: number = 0; i < rawItems.length; i++) {
        const item: Record<string, Object> = rawItems[i] as Record<string, Object>;
        const sessionId: string = this.normalizeString(item['sessionId'] as string | undefined);
        const shellId: string = this.normalizeString(item['shellId'] as string | undefined);
        const title: string = this.normalizeString(item['title'] as string | undefined, `Session ${i + 1}`);
        if (sessionId.length > 0) {
          result.push({
            sessionId: sessionId,
            shellId: shellId,
            title: title
          } as SessionItem);
        }
      }
    }
    this.sessions = result;
    this.isRefreshing = false;
    hilog.info(DOMAIN, PAGE_TAG, 'session list updated count=%{public}d', result.length);
  }

  private refreshSessions(): void {
    if (this.isRefreshing) {
      return;
    }
    if (this.deviceId.length === 0 || this.connectionState !== ConnectionState.CONNECTED) {
      this.isRefreshing = false;
      return;
    }
    this.isRefreshing = true;
    ConnectionManager.requestSessionList(this.deviceId);
  }

  private onSessionClick(session: SessionItem): void {
    hilog.info(DOMAIN, PAGE_TAG, 'connectToSession session=%{public}s', session.sessionId);
    ConnectionManager.setActiveSession(this.deviceId, session.sessionId);
    try {
      this.getUIContext().getRouter().pushUrl({ url: 'pages/TerminalPage' });
    } catch (_e) {
      // Ignore navigation errors
    }
  }

  private onNewSession(): void {
    const shellId: string = this.selectedShell;
    hilog.info(DOMAIN, PAGE_TAG, 'newSession device=%{public}s shell=%{public}s', this.deviceId, shellId);
    if (this.deviceId.length === 0 || this.connectionState !== ConnectionState.CONNECTED) {
      return;
    }
    this.markPendingOpen();
    ConnectionManager.openTerminal(this.deviceId, shellId);
  }

  private markPendingOpen(): void {
    this.pendingOpenRequestedAt = Date.now();
  }

  private clearPendingOpen(): void {
    this.pendingOpenRequestedAt = 0;
  }

  private isPendingOpen(): boolean {
    if (this.pendingOpenRequestedAt <= 0) {
      return false;
    }
    const ageMs: number = Date.now() - this.pendingOpenRequestedAt;
    if (ageMs > 10000) {
      this.clearPendingOpen();
      return false;
    }
    return true;
  }

  private goBack(): void {
    this.getUIContext().getRouter().back();
  }

  build() {
    Column() {
      // Top bar
      Column() {
        Blank()
          .height(2)
          .width('100%')
          .backgroundColor(AppColors.ACCENT)

        Row() {
          Text('‹ 返回')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.ACCENT)
            .onClick(() => {
              this.goBack();
            })
            .padding({ right: 12 })

          Column() {
            Text(this.displayName)
              .fontSize(AppSizes.FONT_SIZE_BODY)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
              .maxLines(1)
              .textOverflow({ overflow: TextOverflow.Ellipsis })

            Text(AppStrings.SESSION_LIST_TITLE)
              .fontSize(12)
              .fontColor(AppColors.TEXT_MUTED)
              .fontFamily('monospace')
          }
          .alignItems(HorizontalAlign.Start)
          .layoutWeight(1)

          Row() {
            Circle()
              .width(6)
              .height(6)
              .fill(this.connectionState === ConnectionState.CONNECTED ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
              .margin({ right: 6 })
            Text(this.connectionState === ConnectionState.CONNECTED ? '在线' : '断开')
              .fontSize(11)
              .fontColor(this.connectionState === ConnectionState.CONNECTED ? AppColors.ONLINE_GREEN : AppColors.TEXT_MUTED)
          }
          .padding({ left: 8, right: 8, top: 4, bottom: 4 })
          .backgroundColor(AppColors.CHIP_BG)
          .borderWidth(1)
          .borderColor(this.connectionState === ConnectionState.CONNECTED ? AppColors.ONLINE_GREEN : AppColors.CHIP_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
        }
        .width('100%')
        .height(56)
          .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
          .alignItems(VerticalAlign.Center)

        Row() {
          Text(`SESSIONS ${this.sessions.length}`)
            .fontSize(11)
            .fontColor(AppColors.TEXT_MUTED)
            .fontFamily('monospace')
            .layoutWeight(1)

          if (this.deviceId.length > 0) {
            Text(`DEVICE ${this.deviceId}`)
              .fontSize(11)
              .fontColor(AppColors.TEXT_SECONDARY)
              .fontFamily('monospace')
              .maxLines(1)
              .textOverflow({ overflow: TextOverflow.Ellipsis })
          }
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE, bottom: 8 })
      }
      .backgroundColor(AppColors.SURFACE_1)
      .width('100%')

      // Divider
      Divider()
        .color(AppColors.DIVIDER)
        .width('100%')

      // Session list
      Refresh({ refreshing: $$this.isRefreshing }) {
        List({ space: 12 }) {
          if (this.sessions.length === 0) {
            ListItem() {
              Column() {
                Text('NO ACTIVE SESSIONS')
                  .fontSize(14)
                  .fontColor(AppColors.TEXT_MUTED)
                  .fontFamily('monospace')
                  .margin({ bottom: 8 })

                Text(AppStrings.EMPTY_SESSION_LIST)
                  .fontSize(AppSizes.FONT_SIZE_BODY)
                  .fontColor(AppColors.TEXT_SECONDARY)
                  .margin({ bottom: 6 })

                Text('下拉刷新列表')
                  .fontSize(12)
                  .fontColor(AppColors.TEXT_MUTED)
              }
              .width('100%')
              .height(260)
              .justifyContent(FlexAlign.Center)
              .alignItems(HorizontalAlign.Center)
            }
          } else {
            ForEach(this.sessions, (session: SessionItem) => {
              ListItem() {
                this.SessionCard(session)
              }
            }, (session: SessionItem, index: number) => `${session.sessionId}_${index}`)
          }
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
        this.refreshSessions();
      })
      .layoutWeight(1)

      // New Session section at bottom
      Divider()
        .color(AppColors.DIVIDER)
        .width('100%')

      Column() {
        Blank()
          .height(2)
          .width('100%')
          .backgroundColor(AppColors.ACCENT_BLUE)
          .margin({ bottom: 10 })

        Text('NEW SESSION')
          .fontSize(12)
          .fontColor(AppColors.TEXT_MUTED)
          .fontFamily('monospace')
          .margin({ bottom: 8 })

        // Shell selector (if multiple shells available)
        if (this.availableShells.length > 1) {
          Flex({ wrap: FlexWrap.Wrap, space: { main: new LengthMetrics(8), cross: new LengthMetrics(8) } }) {
            ForEach(this.availableShells, (shell: string) => {
              Button(shell)
                .height(32)
                .fontSize(12)
                .fontColor(this.selectedShell === shell ? '#FFFFFF' : AppColors.ACCENT)
                .backgroundColor(this.selectedShell === shell ? AppColors.ACCENT : AppColors.HIGHLIGHT)
                .borderWidth(1)
                .borderColor(this.selectedShell === shell ? AppColors.ACCENT : AppColors.ACCENT_DIM)
                .borderRadius(AppSizes.BORDER_RADIUS_TAG)
                .padding({ left: 12, right: 12 })
                .onClick(() => {
                  this.selectedShell = shell;
                })
            }, (shell: string, index: number) => `shell_${shell}_${index}`)
          }
          .width('100%')
          .margin({ bottom: 12 })
        }

        Button(AppStrings.NEW_SESSION_BUTTON)
          .width('100%')
          .height(44)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor('#FFFFFF')
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .onClick(() => {
            this.onNewSession();
          })
      }
      .width('100%')
      .padding({
        left: AppSizes.PADDING_PAGE,
        right: AppSizes.PADDING_PAGE,
        top: 12,
        bottom: 16
      })
      .backgroundColor(AppColors.SURFACE_2)
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }

  @Builder
  SessionCard(session: SessionItem) {
    Column() {
      Blank()
        .height(2)
        .width('100%')
        .backgroundColor(AppColors.ACCENT)
        .margin({ bottom: 12 })

      Row() {
        // Session icon
        Stack({ alignContent: Alignment.Center }) {
          Circle()
            .width(32)
            .height(32)
            .fill(AppColors.SURFACE_3)
          Text('>')
            .fontSize(16)
            .fontColor(AppColors.ACCENT)
            .fontFamily('monospace')
            .fontWeight(FontWeight.Bold)
        }
        .margin({ right: 12 })

        Column() {
          Text(session.title)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Medium)
            .maxLines(1)
            .textOverflow({ overflow: TextOverflow.Ellipsis })

          Text(session.shellId)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ top: 4 })

          Text(`ID ${session.sessionId}`)
            .fontSize(11)
            .fontColor(AppColors.TEXT_MUTED)
            .fontFamily('monospace')
            .maxLines(1)
            .textOverflow({ overflow: TextOverflow.Ellipsis })
            .margin({ top: 4 })
        }
        .alignItems(HorizontalAlign.Start)
        .layoutWeight(1)

        // Online indicator
        Row() {
          Circle()
            .width(6)
            .height(6)
            .fill(AppColors.ONLINE_GREEN)
            .margin({ right: 6 })
          Text('ACTIVE')
            .fontSize(10)
            .fontColor(AppColors.ONLINE_GREEN)
            .fontFamily('monospace')
        }
        .padding({ left: 8, right: 8, top: 4, bottom: 4 })
        .backgroundColor(AppColors.CHIP_BG)
        .borderWidth(1)
        .borderColor(AppColors.ONLINE_GREEN)
        .borderRadius(AppSizes.BORDER_RADIUS_TAG)
      }
      .width('100%')
    }
    .width('100%')
    .padding(AppSizes.PADDING_CARD)
    .backgroundColor(AppColors.CARD_BACKGROUND)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .onClick(() => {
      this.onSessionClick(session);
    })
  }
}
