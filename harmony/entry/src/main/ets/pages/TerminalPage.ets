import { AppColors, AppSizes, AppStrings, ConnectionState } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';

// Shortcut key definition
interface ShortcutKey {
  label: string;
  data: string;
}

@Entry
@Component
struct TerminalPage {
  @State terminalOutput: string = '';
  @State inputText: string = '';
  @State sessionActive: boolean = true;
  @State connectionState: ConnectionState = ConnectionState.CONNECTED;
  @State deviceName: string = '';
  private stateListenerId: number = -1;
  private messageListenerId: number = -1;

  private outputScroller: Scroller = new Scroller();
  private maxOutputLength: number = 50000;

  // Shortcut keys for the toolbar
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
    this.activatePage();
  }

  aboutToDisappear(): void {
    this.deactivatePage();

    // Close terminal session when leaving the page
    if (this.sessionActive) {
      WebSocketService.closeTerminal();
    }
  }

  onPageShow(): void {
    this.activatePage();
  }

  onPageHide(): void {
    this.deactivatePage();
  }

  private activatePage(): void {
    this.connectionState = WebSocketService.getConnectionState();
    this.sessionActive = WebSocketService.getActiveSessionId().length > 0;
    const cachedOutput: string = WebSocketService.getBufferedTerminalOutput();
    if (this.terminalOutput.length === 0 && cachedOutput.length > 0) {
      this.appendOutput(cachedOutput);
    }

    if (this.stateListenerId === -1) {
      this.stateListenerId = WebSocketService.addOnStateChange((state: ConnectionState) => {
        this.connectionState = state;
        if (state === ConnectionState.DISCONNECTED) {
          this.sessionActive = false;
        }
      });
    }

    if (this.messageListenerId === -1) {
      this.messageListenerId = WebSocketService.addOnMessage((type: string, data: Record<string, Object>) => {
        if (type === 'terminal.output') {
          const outputData: Object | undefined = data['data'];
          if (outputData !== undefined && outputData !== null) {
            this.appendOutput(outputData as string);
          }
        } else if (type === 'terminal.closed') {
          this.sessionActive = false;
        } else if (type === 'error') {
          const msgValue: Object | undefined = data['message'];
          const errorMsg: string = (msgValue !== undefined && msgValue !== null) ? msgValue as string : 'Error';
          this.appendOutput('\r\n[错误] ' + errorMsg + '\r\n');
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

  private appendOutput(data: string): void {
    // Strip ANSI escape sequences for plain-text rendering
    const stripped: string = this.stripAnsi(data);
    this.terminalOutput += stripped;

    // Trim if too long
    if (this.terminalOutput.length > this.maxOutputLength) {
      this.terminalOutput = this.terminalOutput.substring(this.terminalOutput.length - this.maxOutputLength);
    }

    // Auto-scroll to bottom
    setTimeout(() => {
      this.outputScroller.scrollEdge(Edge.Bottom);
    }, 50);
  }

  private stripAnsi(text: string): string {
    // Remove common ANSI escape sequences
    let result: string = '';
    let i: number = 0;
    while (i < text.length) {
      if (text.charAt(i) === '\x1B' && i + 1 < text.length && text.charAt(i + 1) === '[') {
        // Skip CSI sequence: ESC [ ... final_byte
        i += 2;
        while (i < text.length) {
          const code: number = text.charCodeAt(i);
          i++;
          // Final byte is in range 0x40-0x7E
          if (code >= 0x40 && code <= 0x7E) {
            break;
          }
        }
      } else if (text.charAt(i) === '\x1B') {
        // Skip other ESC sequences (2 char)
        i += 2;
      } else if (text.charAt(i) === '\r') {
        // Convert \r\n to \n, or standalone \r to \n
        if (i + 1 < text.length && text.charAt(i + 1) === '\n') {
          result += '\n';
          i += 2;
        } else {
          result += '\n';
          i++;
        }
      } else {
        result += text.charAt(i);
        i++;
      }
    }
    return result;
  }

  private sendInput(): void {
    const text: string = this.inputText;
    if (text.length === 0) {
      // Send just Enter
      WebSocketService.sendTerminalInput('\r');
      return;
    }
    WebSocketService.sendTerminalInput(text + '\r');
    this.inputText = '';
  }

  private sendShortcutKey(data: string): void {
    WebSocketService.sendTerminalInput(data);
  }

  private goBack(): void {
    if (this.sessionActive) {
      WebSocketService.closeTerminal();
    }
    this.getUIContext().getRouter().back();
  }

  build() {
    Column() {
      // Top status bar (semi-transparent)
      Row() {
        // Back button
        Text('< 返回')
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.ACCENT)
          .onClick(() => {
            this.goBack();
          })
          .padding({ right: 16 })

        // Device name + session status
        Text(AppStrings.TERMINAL_TITLE)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .layoutWeight(1)

        // Session status indicator
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

      // Terminal output area
      Scroll(this.outputScroller) {
        Text(this.terminalOutput.length > 0 ? this.terminalOutput : '等待终端输出...')
          .fontSize(AppSizes.FONT_SIZE_TERMINAL)
          .fontColor(this.terminalOutput.length > 0 ? AppColors.TERMINAL_TEXT : AppColors.TEXT_SECONDARY)
          .fontFamily('monospace')
          .width('100%')
          .padding(12)
          .textAlign(TextAlign.Start)
          .copyOption(CopyOptions.LocalDevice)
      }
      .width('100%')
      .layoutWeight(1)
      .backgroundColor(AppColors.TERMINAL_BG)
      .scrollBar(BarState.Auto)
      .edgeEffect(EdgeEffect.None)

      // Shortcut key toolbar
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

      // Input bar
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
