import { webview } from '@kit.ArkWeb';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState } from '../common/Constants';
import { ConnectionManager } from '../services/ConnectionManager';

const DOMAIN = 0x0000;
const PAGE_TAG = 'TerminalPage';

interface ShortcutKey {
  label: string;
  data: string;
}

class TerminalBridge {
  onReadyCallback: (cols: number, rows: number) => void = (_cols: number, _rows: number) => {};
  onResizeCallback: (cols: number, rows: number) => void = (_cols: number, _rows: number) => {};
  onInputCallback: (data: string) => void = (_data: string) => {};
  onInteractionCallback: () => void = () => {};

  onTerminalReady(cols: number, rows: number): void {
    this.onReadyCallback(cols, rows);
  }

  onTerminalResize(cols: number, rows: number): void {
    this.onResizeCallback(cols, rows);
  }

  sendInput(data: string): void {
    this.onInputCallback(data);
  }

  onTerminalInteraction(): void {
    this.onInteractionCallback();
  }

  logMessage(_message: string): void {
  }
}

@Entry
@Component
struct TerminalPage {
  @State sessionActive: boolean = true;
  @State connectionState: ConnectionState = ConnectionState.CONNECTED;
  @State terminalReady: boolean = false;
  @State terminalHint: string = '正在初始化终端...';
  @State showSessionClosedDialog: boolean = false;
  private stateListenerId: number = -1;
  private messageListenerId: number = -1;
  private terminalController: webview.WebviewController = new webview.WebviewController();
  private terminalBridge: TerminalBridge = new TerminalBridge();
  private terminalPageUrl: string = 'resource://rawfile/terminal/index.html';
  private maxOutputLength: number = 200000;
  private historyPageChars: number = 20000;
  private pendingTerminalOutput: string = '';
  private pendingHistoryOutput: string = '';
  private pendingHistoryReplace: string = '';
  private historyChunks: Array<string> = [];
  private historyBeforeSeq: number = 0;
  private historyLoading: boolean = false;
  private historyComplete: boolean = false;
  private historyStarted: boolean = false;
  private historyPlanned: boolean = false;
  private historySessionId: string = '';
  private historyDeviceId: string = '';
  private flushTimer: number = -1;
  private lastReportedCols: number = 0;
  private lastReportedRows: number = 0;
  private viewedSessionId: string = '';

  private normalizeString(value: string | undefined | null, fallback: string = ''): string {
    if (typeof value === 'string') {
      return value;
    }
    return fallback;
  }

  private getMessageString(data: Record<string, Object>, key: string, fallback: string = ''): string {
    const value: Object | undefined = data[key];
    if (typeof value === 'string') {
      return value as string;
    }
    return fallback;
  }

  private getMessageNumber(data: Record<string, Object>, key: string, fallback: number = 0): number {
    const value: Object | undefined = data[key];
    if (typeof value === 'number') {
      return value as number;
    }
    if (typeof value === 'string') {
      const parsed: number = Number(value as string);
      return Number.isNaN(parsed) ? fallback : parsed;
    }
    return fallback;
  }

  private getMessageBoolean(data: Record<string, Object>, key: string, fallback: boolean = false): boolean {
    const value: Object | undefined = data[key];
    if (typeof value === 'boolean') {
      return value as boolean;
    }
    if (typeof value === 'number') {
      return (value as number) !== 0;
    }
    if (typeof value === 'string') {
      const raw: string = (value as string).toLowerCase();
      if (raw === 'true' || raw === '1') return true;
      if (raw === 'false' || raw === '0') return false;
    }
    return fallback;
  }

  private shortcutKeys: Array<ShortcutKey> = [
    { label: 'Ctrl+C', data: '\x03' } as ShortcutKey,
    { label: 'Ctrl+D', data: '\x04' } as ShortcutKey,
    { label: 'Ctrl+Z', data: '\x1A' } as ShortcutKey,
    { label: 'Tab', data: '\t' } as ShortcutKey,
    { label: 'Esc', data: '\x1B' } as ShortcutKey,
    { label: '↑', data: '\x1B[A' } as ShortcutKey,
    { label: '↓', data: '\x1B[B' } as ShortcutKey,
    { label: '←', data: '\x1B[D' } as ShortcutKey,
    { label: '→', data: '\x1B[C' } as ShortcutKey,
    { label: 'Home', data: '\x1B[H' } as ShortcutKey,
    { label: 'End', data: '\x1B[F' } as ShortcutKey
  ];

  aboutToAppear(): void {
    hilog.info(DOMAIN, PAGE_TAG, 'aboutToAppear');
    // Reset state for fresh terminal view each time we enter
    this.pendingTerminalOutput = '';
    this.resetHistoryState();
    this.terminalReady = false;
    this.terminalHint = '正在初始化终端...';
    this.showSessionClosedDialog = false;
    this.viewedSessionId = this.normalizeString(ConnectionManager.getActiveSessionId());
    this.activatePage();
  }

  aboutToDisappear(): void {
    hilog.warn(DOMAIN, PAGE_TAG, 'aboutToDisappear sessionActive=%{public}s',
      this.sessionActive ? 'true' : 'false');
    this.deactivatePage();

    // In multi-session mode, do NOT close the terminal when navigating away.
    // The session stays alive on the PC side.
    // Only close if session is already inactive (disconnected/error).
  }

  onPageShow(): void {
    hilog.info(DOMAIN, PAGE_TAG, 'onPageShow');
    this.activatePage();
  }

  onPageHide(): void {
    hilog.warn(DOMAIN, PAGE_TAG, 'onPageHide');
    this.deactivatePage();
  }

  private activatePage(): void {
    this.terminalBridge.onReadyCallback = (cols: number, rows: number) => {
      this.onTerminalReady(cols, rows);
    };
    this.terminalBridge.onResizeCallback = (cols: number, rows: number) => {
      this.onTerminalResize(cols, rows);
    };
    this.terminalBridge.onInputCallback = (data: string) => {
      ConnectionManager.sendTerminalInput(data);
    };
    this.terminalBridge.onInteractionCallback = () => {
      this.requestRemoteResize();
      this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
    };

    this.connectionState = ConnectionManager.getConnectionStateForDevice(ConnectionManager.getActiveDeviceId());
    this.viewedSessionId = this.normalizeString(ConnectionManager.getActiveSessionId(), this.viewedSessionId);
    this.sessionActive = this.viewedSessionId.length > 0;
    this.historyPlanned = this.sessionActive && this.viewedSessionId.length > 0;
    hilog.info(DOMAIN, PAGE_TAG, 'activatePage connected=%{public}d sessionActive=%{public}s session=%{public}s',
      this.connectionState, this.sessionActive ? 'true' : 'false', this.viewedSessionId);

    if (this.stateListenerId === -1) {
      this.stateListenerId = ConnectionManager.addOnStateChange((state: ConnectionState, _deviceId: string) => {
        hilog.info(DOMAIN, PAGE_TAG, 'state listener %{public}d', state);
        this.connectionState = state;
        if (state === ConnectionState.DISCONNECTED) {
          this.sessionActive = false;
          this.terminalHint = '连接已断开';
        }
      });
    }

    if (this.messageListenerId === -1) {
      this.messageListenerId = ConnectionManager.addOnMessage((type: string, data: Record<string, Object>) => {
        if (type === 'terminal.output') {
          if (this.isCurrentSessionMessage(data)) {
            const output: string = this.getMessageString(data, 'data');
            if (this.historyPlanned && !this.historyComplete) {
              this.appendPendingHistoryOutput(output);
            } else {
              this.enqueueTerminalOutput(output);
            }
          }
        } else if (type === 'terminal.history.response') {
          this.handleHistoryResponse(data);
        } else if (type === 'terminal.closed') {
          if (this.isCurrentSessionMessage(data)) {
            this.sessionActive = false;
            this.terminalHint = 'PC 端已关闭当前会话';
            this.showSessionClosedDialog = true;
          }
        } else if (type === 'error') {
          const errorMsg: string = this.getMessageString(data, 'message', 'Error');
          this.enqueueTerminalOutput(`\r\n[错误] ${errorMsg}\r\n`);
        }
      });
    }

    if (this.terminalReady) {
      this.requestRemoteResize();
      this.startHistoryLoadIfNeeded();
      this.flushPendingTerminalOutput();
    }
  }

  private deactivatePage(): void {
    if (this.flushTimer !== -1) {
      clearTimeout(this.flushTimer);
      this.flushTimer = -1;
    }

    if (this.stateListenerId !== -1) {
      ConnectionManager.removeOnStateChange(this.stateListenerId);
      this.stateListenerId = -1;
    }

    if (this.messageListenerId !== -1) {
      ConnectionManager.removeOnMessage(this.messageListenerId);
      this.messageListenerId = -1;
    }

    // Reset bridge callbacks to no-op to prevent stale references (Bug #9)
    this.terminalBridge.onReadyCallback = (_cols: number, _rows: number) => {};
    this.terminalBridge.onResizeCallback = (_cols: number, _rows: number) => {};
    this.terminalBridge.onInputCallback = (_data: string) => {};
    this.terminalBridge.onInteractionCallback = () => {};
  }

  private enqueueTerminalOutput(data: string): void {
    const output: string = this.normalizeString(data);
    if (output.length === 0) {
      return;
    }

    this.pendingTerminalOutput += output;
    if (this.pendingTerminalOutput.length > this.maxOutputLength) {
      this.pendingTerminalOutput = this.pendingTerminalOutput.substring(
        this.pendingTerminalOutput.length - this.maxOutputLength
      );
    }

    this.scheduleFlush();
  }

  private scheduleFlush(): void {
    if (!this.terminalReady || this.flushTimer !== -1) {
      return;
    }
    this.flushTimer = setTimeout(() => {
      this.flushTimer = -1;
      this.flushPendingTerminalOutput();
    }, 16) as number;
  }

  private flushPendingTerminalOutput(): void {
    if (!this.terminalReady || this.pendingTerminalOutput.length === 0) {
      return;
    }
    if (this.historyPlanned && !this.historyComplete) {
      return;
    }

    const payload: string = JSON.stringify(this.pendingTerminalOutput);
    this.pendingTerminalOutput = '';
    this.runTerminalScript(`window.phoneShell && window.phoneShell.write(${payload});`);
  }

  private runTerminalScript(script: string): void {
    try {
      this.terminalController.runJavaScript(script);
    } catch (_err) {
      // Ignore WebView script errors during page transitions.
    }
  }

  private onTerminalReady(cols: number, rows: number): void {
    this.terminalReady = true;
    this.terminalHint = '等待终端输出...';
    this.handleTerminalResize(cols, rows);
    this.startHistoryLoadIfNeeded();
    this.applyPendingHistoryReplace();
    this.flushPendingTerminalOutput();
    this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  private onTerminalResize(cols: number, rows: number): void {
    this.handleTerminalResize(cols, rows);
  }

  private handleTerminalResize(cols: number, rows: number): void {
    if (cols <= 0 || rows <= 0) {
      return;
    }

    if (this.lastReportedCols === cols && this.lastReportedRows === rows) {
      return;
    }

    this.lastReportedCols = cols;
    this.lastReportedRows = rows;
    this.requestRemoteResize();
  }

  private requestRemoteResize(): void {
    if (!this.sessionActive || this.lastReportedCols <= 0 || this.lastReportedRows <= 0) {
      return;
    }

    ConnectionManager.sendTerminalResize(this.lastReportedCols, this.lastReportedRows);
  }

  private resetHistoryState(): void {
    this.historyPlanned = false;
    this.historyStarted = false;
    this.historyLoading = false;
    this.historyComplete = false;
    this.historyBeforeSeq = 0;
    this.historyChunks = [];
    this.pendingHistoryOutput = '';
    this.pendingHistoryReplace = '';
    this.historySessionId = '';
    this.historyDeviceId = '';
  }

  private startHistoryLoadIfNeeded(): void {
    if (!this.historyPlanned || this.historyStarted) {
      return;
    }
    if (!this.sessionActive || this.viewedSessionId.length === 0) {
      return;
    }
    if (!this.terminalReady) {
      return;
    }

    this.historyStarted = true;
    this.historySessionId = this.viewedSessionId;
    this.historyDeviceId = this.normalizeString(ConnectionManager.getActiveDeviceId());
    this.historyLoading = false;
    this.historyComplete = false;
    this.historyBeforeSeq = 0;
    this.historyChunks = [];
    this.pendingHistoryReplace = '';

    this.requestHistoryPage();
  }

  private requestHistoryPage(): void {
    if (this.historyLoading || this.historyComplete) {
      return;
    }
    if (this.historySessionId.length === 0 || this.historyDeviceId.length === 0) {
      return;
    }
    this.historyLoading = true;
    ConnectionManager.requestTerminalHistory(
      this.historyDeviceId,
      this.historySessionId,
      this.historyBeforeSeq,
      this.historyPageChars
    );
  }

  private handleHistoryResponse(data: Record<string, Object>): void {
    if (!this.historyPlanned || !this.isCurrentSessionMessage(data)) {
      return;
    }
    this.historyLoading = false;

    const chunk: string = this.getMessageString(data, 'data');
    const nextBeforeSeq: number = this.getMessageNumber(data, 'nextBeforeSeq', 0);
    const hasMore: boolean = this.getMessageBoolean(data, 'hasMore', false);

    if (chunk.length > 0) {
      this.historyChunks.unshift(chunk);
    }

    if (hasMore) {
      this.historyBeforeSeq = nextBeforeSeq;
      this.requestHistoryPage();
      return;
    }

    this.historyComplete = true;
    this.applyHistoryBuffer();
  }

  private appendPendingHistoryOutput(data: string): void {
    const output: string = this.normalizeString(data);
    if (output.length === 0) {
      return;
    }

    this.pendingHistoryOutput += output;
    if (this.pendingHistoryOutput.length > this.maxOutputLength) {
      this.pendingHistoryOutput = this.pendingHistoryOutput.substring(
        this.pendingHistoryOutput.length - this.maxOutputLength
      );
    }
  }

  private applyHistoryBuffer(): void {
    const history: string = this.historyChunks.join('');
    this.historyChunks = [];
    const merged: string = history + this.pendingHistoryOutput;
    this.pendingHistoryOutput = '';

    if (!this.terminalReady) {
      this.pendingHistoryReplace = merged;
      return;
    }

    this.replaceTerminalBuffer(merged);
  }

  private applyPendingHistoryReplace(): void {
    if (this.pendingHistoryReplace.length === 0) {
      return;
    }
    const payload: string = this.pendingHistoryReplace;
    this.pendingHistoryReplace = '';
    this.replaceTerminalBuffer(payload);
  }

  private replaceTerminalBuffer(data: string): void {
    if (!this.terminalReady) {
      this.pendingHistoryReplace = data;
      return;
    }
    if (data.length === 0) {
      this.runTerminalScript('window.phoneShell && window.phoneShell.clear();');
      return;
    }
    const payload: string = JSON.stringify(data);
    this.runTerminalScript(`window.phoneShell && (window.phoneShell.clear(), window.phoneShell.write(${payload}));`);
  }

  private isCurrentSessionMessage(data: Record<string, Object>): boolean {
    const sessionId: string = this.getMessageString(data, 'sessionId');
    return sessionId.length > 0 && sessionId === this.viewedSessionId;
  }

  private sendShortcutKey(data: string): void {
    ConnectionManager.sendTerminalInput(data);
    this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  private goBack(): void {
    // Don't close the terminal session — it stays alive on the PC.
    // Just navigate back to the session list.
    this.getUIContext().getRouter().back();
  }

  build() {
    Stack({ alignContent: Alignment.Center }) {
      Column() {
        Row() {
          Text('< 返回')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.ACCENT)
            .onClick(() => {
              this.goBack();
            })
            .padding({ right: 16 })

          Text(AppStrings.TERMINAL_TITLE)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Medium)
            .layoutWeight(1)

          Row() {
            Circle()
              .width(AppSizes.STATUS_DOT_SIZE)
              .height(AppSizes.STATUS_DOT_SIZE)
              .fill(this.sessionActive ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
              .margin({ right: 6 })

            Text(this.sessionActive ? '活动' : '已断开')
              .fontSize(12)
              .fontColor(this.sessionActive ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
          }
        }
        .width('100%')
        .height(48)
        .padding({ left: 16, right: 16 })
        .backgroundColor(AppColors.STATUS_BAR_BG)
        .alignItems(VerticalAlign.Center)

        Stack({ alignContent: Alignment.Center }) {
          Web({ src: this.terminalPageUrl, controller: this.terminalController })
            .width('100%')
            .height('100%')
            .backgroundColor(AppColors.TERMINAL_BG)
            .javaScriptAccess(true)
            .domStorageAccess(true)
            .javaScriptProxy({
              object: this.terminalBridge,
              name: 'nativeApp',
              methodList: ['onTerminalReady', 'onTerminalResize', 'sendInput', 'onTerminalInteraction', 'logMessage'],
              controller: this.terminalController
            })

          if (!this.terminalReady) {
            Column() {
              Text(this.terminalHint)
                .fontSize(AppSizes.FONT_SIZE_BODY)
                .fontColor(AppColors.TEXT_SECONDARY)
            }
            .width('100%')
            .height('100%')
            .backgroundColor(AppColors.TERMINAL_BG)
            .justifyContent(FlexAlign.Center)
          }
        }
        .width('100%')
        .layoutWeight(1)
        .backgroundColor(AppColors.TERMINAL_BG)

        Scroll() {
          Row({ space: 8 }) {
            ForEach(this.shortcutKeys, (key: ShortcutKey) => {
              Button(key.label)
                .height(AppSizes.SHORTCUT_KEY_HEIGHT)
                .fontSize(12)
                .fontColor(AppColors.TEXT_PRIMARY)
                .backgroundColor(AppColors.INPUT_BG)
                .borderRadius(AppSizes.BORDER_RADIUS_TAG)
                .padding({ left: 10, right: 10 })
                .onClick(() => {
                  this.sendShortcutKey(key.data);
                })
            }, (key: ShortcutKey, index: number) => `${key.label}_${index}`)
          }
          .padding({ left: 12, right: 12 })
        }
        .scrollable(ScrollDirection.Horizontal)
        .scrollBar(BarState.Off)
        .width('100%')
        .height(52)
        .backgroundColor(AppColors.TOOLBAR_BG)
        .align(Alignment.Center)
      }
      .width('100%')
      .height('100%')
      .backgroundColor(AppColors.TERMINAL_BG)

      if (this.showSessionClosedDialog) {
        Column() {
          Column() {
            Text('PC 端已关闭当前会话')
              .fontSize(18)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontWeight(FontWeight.Medium)
              .margin({ bottom: 10 })

            Text('点击下方按钮返回会话列表。')
              .fontSize(13)
              .fontColor(AppColors.TEXT_SECONDARY)
              .margin({ bottom: 18 })

            Row() {
              Button('返回会话列表')
                .width(180)
                .height(42)
                .fontSize(AppSizes.FONT_SIZE_BODY)
                .fontColor('#FFFFFF')
                .backgroundColor(AppColors.ACCENT)
                .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
                .onClick(() => {
                  this.goBack();
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
