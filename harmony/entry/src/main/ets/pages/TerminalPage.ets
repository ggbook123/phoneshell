import { webview } from '@kit.ArkWeb';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';

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
  @State inputText: string = '';
  @State sessionActive: boolean = true;
  @State connectionState: ConnectionState = ConnectionState.CONNECTED;
  @State terminalReady: boolean = false;
  @State terminalHint: string = '正在初始化终端...';
  private stateListenerId: number = -1;
  private messageListenerId: number = -1;
  private terminalController: webview.WebviewController = new webview.WebviewController();
  private terminalBridge: TerminalBridge = new TerminalBridge();
  private terminalPageUrl: string = 'resource://rawfile/terminal/index.html';
  private maxOutputLength: number = 50000;
  private pendingTerminalOutput: string = '';
  private seededCachedOutput: boolean = false;
  private lastReportedCols: number = 0;
  private lastReportedRows: number = 0;

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
    this.seededCachedOutput = false;
    this.pendingTerminalOutput = '';
    this.terminalReady = false;
    this.terminalHint = '正在初始化终端...';
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
      WebSocketService.sendTerminalInput(data);
    };
    this.terminalBridge.onInteractionCallback = () => {
      this.requestRemoteResize();
      this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
    };

    this.connectionState = WebSocketService.getConnectionState();
    this.sessionActive = WebSocketService.getActiveSessionId().length > 0;
    hilog.info(DOMAIN, PAGE_TAG, 'activatePage connected=%{public}d sessionActive=%{public}s session=%{public}s',
      this.connectionState, this.sessionActive ? 'true' : 'false', WebSocketService.getActiveSessionId());

    if (!this.seededCachedOutput) {
      this.seededCachedOutput = true;
      this.enqueueTerminalOutput(WebSocketService.getBufferedTerminalOutput());
    }

    if (this.stateListenerId === -1) {
      this.stateListenerId = WebSocketService.addOnStateChange((state: ConnectionState) => {
        hilog.info(DOMAIN, PAGE_TAG, 'state listener %{public}d', state);
        this.connectionState = state;
        if (state === ConnectionState.DISCONNECTED) {
          this.sessionActive = false;
          this.terminalHint = '连接已断开';
        }
      });
    }

    if (this.messageListenerId === -1) {
      this.messageListenerId = WebSocketService.addOnMessage((type: string, data: Record<string, Object>) => {
        hilog.info(DOMAIN, PAGE_TAG, 'message listener %{public}s', type);
        if (type === 'terminal.output') {
          this.enqueueTerminalOutput(this.getMessageString(data, 'data'));
        } else if (type === 'terminal.closed') {
          this.sessionActive = false;
          this.terminalHint = '终端已断开';
        } else if (type === 'error') {
          const errorMsg: string = this.getMessageString(data, 'message', 'Error');
          this.enqueueTerminalOutput(`\r\n[错误] ${errorMsg}\r\n`);
        }
      });
    }

    if (this.terminalReady) {
      this.flushPendingTerminalOutput();
      this.requestRemoteResize();
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

    this.flushPendingTerminalOutput();
  }

  private flushPendingTerminalOutput(): void {
    if (!this.terminalReady || this.pendingTerminalOutput.length === 0) {
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

    WebSocketService.resizeTerminal(this.lastReportedCols, this.lastReportedRows);
  }

  private sendInput(): void {
    const text: string = this.inputText;
    hilog.info(DOMAIN, PAGE_TAG, 'sendInput textLen=%{public}d', text.length);
    if (text.length === 0) {
      WebSocketService.sendTerminalInput('\r');
      return;
    }
    WebSocketService.sendTerminalInput(text + '\r');
    this.inputText = '';
  }

  private sendShortcutKey(data: string): void {
    WebSocketService.sendTerminalInput(data);
    this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  private goBack(): void {
    // Don't close the terminal session — it stays alive on the PC.
    // Just navigate back to the session list.
    this.getUIContext().getRouter().back();
  }

  build() {
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

      Row() {
        TextInput({ text: $$this.inputText, placeholder: AppStrings.INPUT_PLACEHOLDER })
          .type(InputType.Normal)
          .layoutWeight(1)
          .height(40)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TERMINAL_TEXT)
          .placeholderColor(AppColors.TEXT_SECONDARY)
          .backgroundColor(AppColors.TERMINAL_BG)
          .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
          .padding({ left: 12, right: 12 })
          .fontFamily('monospace')
          .onSubmit(() => {
            this.sendInput();
          })

        Button(AppStrings.SEND_BUTTON)
          .height(40)
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor('#FFFFFF')
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
          .margin({ left: 8 })
          .enabled(this.sessionActive)
          .onClick(() => {
            this.sendInput();
          })
      }
      .width('100%')
      .padding({ left: 12, right: 12, top: 8, bottom: 8 })
      .backgroundColor(AppColors.TOOLBAR_BG)
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.TERMINAL_BG)
  }
}
