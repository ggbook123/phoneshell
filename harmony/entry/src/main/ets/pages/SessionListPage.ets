import { LengthMetrics } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, ConnectionState } from '../common/Constants';
import { I18n } from '../common/I18n';
import { ConnectionManager } from '../services/ConnectionManager';

const DOMAIN = 0x0000;
const PAGE_TAG = 'SessionListPage';

@Observed
class SessionItemModel {
  sessionId: string = '';
  shellId: string = '';
  title: string = '';
}

interface PageParams {
  deviceId: string;
  displayName: string;
  availableShells: Array<string>;
}

@Entry
@Component
struct SessionListPage {
  @State sessions: Array<SessionItemModel> = [];
  @State connectionState: ConnectionState = ConnectionState.CONNECTED;
  @State isRefreshing: boolean = false;
  @State deviceId: string = '';
  @State displayName: string = '';
  @State availableShells: Array<string> = [];
  @State selectedShell: string = '';
  @State showRenameDialog: boolean = false;
  @State renameSessionId: string = '';
  @State renameSessionTitle: string = '';
  @State renameInput: string = '';
  @State showCloseDialog: boolean = false;
  @State closeSessionId: string = '';
  @State closeSessionTitle: string = '';
  private stateListenerId: number = -1;
  private messageListenerId: number = -1;
  private pendingOpenRequestedAt: number = 0;
  private pendingRenameTitles: Map<string, string> = new Map();
  private pendingRenameTimes: Map<string, number> = new Map();
  private pendingRenameTtlMs: number = 10000;

  private normalizeString(value: string | undefined | null, fallback: string = ''): string {
    if (typeof value === 'string') {
      return value;
    }
    return fallback;
  }

  private t(zhText: string, enText: string): string {
    return I18n.t(I18n.getLanguage(), zhText, enText);
  }

  private recordPendingRename(sessionId: string, title: string): void {
    if (sessionId.length === 0 || title.length === 0) {
      return;
    }
    this.pendingRenameTitles.set(sessionId, title);
    this.pendingRenameTimes.set(sessionId, Date.now());
  }

  private resolveSessionTitle(sessionId: string, serverTitle: string): string {
    const pending: string | undefined = this.pendingRenameTitles.get(sessionId);
    if (pending === undefined) {
      return serverTitle;
    }
    const requestedAt: number = this.pendingRenameTimes.get(sessionId) ?? 0;
    if (requestedAt > 0 && (Date.now() - requestedAt) > this.pendingRenameTtlMs) {
      this.pendingRenameTitles.delete(sessionId);
      this.pendingRenameTimes.delete(sessionId);
      return serverTitle;
    }
    if (serverTitle.length > 0 && serverTitle === pending) {
      this.pendingRenameTitles.delete(sessionId);
      this.pendingRenameTimes.delete(sessionId);
      return serverTitle;
    }
    return pending;
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
    let result: Array<SessionItemModel> = [];
    const existing: Map<string, SessionItemModel> = new Map();
    for (let i: number = 0; i < this.sessions.length; i++) {
      const item: SessionItemModel = this.sessions[i];
      if (item.sessionId.length > 0) {
        existing.set(item.sessionId, item);
      }
    }
    if (Array.isArray(sessionsRaw)) {
      const rawItems: Array<Object> = sessionsRaw as Array<Object>;
      for (let i: number = 0; i < rawItems.length; i++) {
        const item: Record<string, Object> = rawItems[i] as Record<string, Object>;
        const sessionId: string = this.normalizeString(item['sessionId'] as string | undefined);
        const shellId: string = this.normalizeString(item['shellId'] as string | undefined);
        let title: string = this.normalizeString(item['title'] as string | undefined, `Session ${i + 1}`);
        if (sessionId.length > 0) {
          title = this.resolveSessionTitle(sessionId, title);
          let model: SessionItemModel | undefined = existing.get(sessionId);
          if (model === undefined) {
            model = new SessionItemModel();
            model.sessionId = sessionId;
          }
          model.shellId = shellId;
          model.title = title;
          result.push(model);
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

  private onSessionClick(session: SessionItemModel): void {
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

  private openRenameDialog(session: SessionItemModel): void {
    this.renameSessionId = session.sessionId;
    this.renameSessionTitle = session.title;
    this.renameInput = session.title;
    this.showRenameDialog = true;
  }

  private closeRenameDialog(): void {
    this.showRenameDialog = false;
    this.renameSessionId = '';
    this.renameSessionTitle = '';
    this.renameInput = '';
  }

  private confirmRename(): void {
    const trimmed: string = this.renameInput.trim();
    if (trimmed.length === 0 || this.renameSessionId.length === 0) {
      this.closeRenameDialog();
      return;
    }
    const sessionId: string = this.renameSessionId;
    this.recordPendingRename(sessionId, trimmed);
    ConnectionManager.renameSession(this.deviceId, sessionId, trimmed);
    this.updateSessionTitleLocal(sessionId, trimmed);
    this.closeRenameDialog();
    this.refreshSessions();
  }

  private updateSessionTitleLocal(sessionId: string, title: string): void {
    for (let i: number = 0; i < this.sessions.length; i++) {
      const item: SessionItemModel = this.sessions[i];
      if (item.sessionId === sessionId) {
        item.title = title;
        return;
      }
    }
  }

  private openCloseDialog(session: SessionItemModel): void {
    this.closeSessionId = session.sessionId;
    this.closeSessionTitle = session.title;
    this.showCloseDialog = true;
  }

  private closeCloseDialog(): void {
    this.showCloseDialog = false;
    this.closeSessionId = '';
    this.closeSessionTitle = '';
  }

  private confirmClose(): void {
    if (this.closeSessionId.length === 0) {
      this.closeCloseDialog();
      return;
    }
    ConnectionManager.closeTerminalSession(this.closeSessionId);
    this.removeSessionLocal(this.closeSessionId);
    this.closeCloseDialog();
    this.refreshSessions();
  }

  private removeSessionLocal(sessionId: string): void {
    this.pendingRenameTitles.delete(sessionId);
    this.pendingRenameTimes.delete(sessionId);
    let updated: Array<SessionItemModel> = [];
    for (let i: number = 0; i < this.sessions.length; i++) {
      const item: SessionItemModel = this.sessions[i];
      if (item.sessionId !== sessionId) {
        updated.push(item);
      }
    }
    this.sessions = updated;
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
    Stack({ alignContent: Alignment.TopStart }) {
      Column() {
        // Top bar
        Column() {
        Blank()
          .height(2)
          .width('100%')
          .backgroundColor(AppColors.ACCENT)

        Row() {
          Text(this.t('‹ 返回', '‹ Back'))
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

            Text(this.t('会话列表', 'Session List'))
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
            Text(this.connectionState === ConnectionState.CONNECTED
              ? this.t('在线', 'Online')
              : this.t('断开', 'Disconnected'))
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
                Text(this.t('暂无活动会话', 'NO ACTIVE SESSIONS'))
                  .fontSize(14)
                  .fontColor(AppColors.TEXT_MUTED)
                  .fontFamily('monospace')
                  .margin({ bottom: 8 })

                Text(this.t('当前没有活动会话', 'No active sessions at the moment'))
                  .fontSize(AppSizes.FONT_SIZE_BODY)
                  .fontColor(AppColors.TEXT_SECONDARY)
                  .margin({ bottom: 6 })

                Text(this.t('下拉刷新列表', 'Pull down to refresh'))
                  .fontSize(12)
                  .fontColor(AppColors.TEXT_MUTED)
              }
              .width('100%')
              .height(260)
              .justifyContent(FlexAlign.Center)
              .alignItems(HorizontalAlign.Center)
            }
          } else {
            ForEach(this.sessions, (session: SessionItemModel) => {
              ListItem() {
                this.SessionCard(session)
              }
            }, (session: SessionItemModel) => session.sessionId)
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

        Text(this.t('新建会话', 'NEW SESSION'))
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

        Button(this.t('新建会话', 'New Session'))
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

      if (this.showRenameDialog) {
        this.RenameDialog()
      }

      if (this.showCloseDialog) {
        this.CloseConfirmDialog()
      }
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }

  @Builder
  SessionCard(session: SessionItemModel) {
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

        Column() {
          Button() {
            Image($r('app.media.ic_close'))
              .width(12)
              .height(12)
          }
            .width(24)
            .height(24)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.ERROR_RED)
            .borderRadius(6)
            .padding(0)
            .onClick(() => {
              this.openCloseDialog(session);
            })

          Row() {
            Circle()
              .width(6)
              .height(6)
              .fill(AppColors.ONLINE_GREEN)
              .margin({ right: 6 })
            Text(this.t('活动中', 'ACTIVE'))
              .fontSize(10)
              .fontColor(AppColors.ONLINE_GREEN)
              .fontFamily('monospace')
          }
          .padding({ left: 8, right: 8, top: 4, bottom: 4 })
          .backgroundColor(AppColors.CHIP_BG)
          .borderWidth(1)
          .borderColor(AppColors.ONLINE_GREEN)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
          .margin({ top: 8 })
        }
        .alignItems(HorizontalAlign.End)
      }
      .width('100%')
    }
    .width('100%')
    .padding(AppSizes.PADDING_CARD)
    .backgroundColor(AppColors.CARD_BACKGROUND)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .gesture(
      LongPressGesture({ repeat: false, duration: 600 })
        .onAction(() => {
          this.openRenameDialog(session);
        })
    )
    .onClick(() => {
      this.onSessionClick(session);
    })
  }

  @Builder
  RenameDialog() {
    Column() {
      Column() {
        Text(this.t('重命名会话', 'Rename Session'))
          .fontSize(18)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 8 })

        if (this.renameSessionTitle.length > 0) {
          Text(this.renameSessionTitle)
            .fontSize(12)
            .fontColor(AppColors.TEXT_MUTED)
            .fontFamily('monospace')
            .margin({ bottom: 16 })
            .textAlign(TextAlign.Center)
            .width('100%')
        }

        Row() {
          Text(this.t('名称:', 'Name:'))
            .fontSize(12)
            .fontColor(AppColors.TEXT_SECONDARY)
            .width(48)

          TextInput({ text: $$this.renameInput })
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
              this.closeRenameDialog();
            })

          Button(this.t('确认', 'Confirm'))
            .width(120)
            .height(40)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor('#FFFFFF')
            .backgroundColor(AppColors.ACCENT)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => {
              this.confirmRename();
            })
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
  CloseConfirmDialog() {
    Column() {
      Column() {
        Text(this.t('关闭会话', 'Close Session'))
          .fontSize(18)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 10 })

        Text(this.closeSessionTitle.length > 0
          ? this.t(`确认关闭 ${this.closeSessionTitle}？`, `Close ${this.closeSessionTitle}?`)
          : this.t('确认关闭当前会话？', 'Close current session?'))
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
              this.closeCloseDialog();
            })

          Button(this.t('确认关闭', 'Close'))
            .width(140)
            .height(40)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor('#FFFFFF')
            .backgroundColor(AppColors.ERROR_RED)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => {
              this.confirmClose();
            })
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
}
