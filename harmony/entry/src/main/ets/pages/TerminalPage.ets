import { webview } from '@kit.ArkWeb';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, ConnectionState } from '../common/Constants';
import { I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';
import { ConnectionManager } from '../services/ConnectionManager';
import window from '@ohos.window';

const DOMAIN = 0x0000;
const PAGE_TAG = 'TerminalPage';

interface ShortcutKey {
  label: string;
  data: string;
}

interface QuickPanelTabItem {
  id: string;
  zhLabel: string;
  enLabel: string;
}

interface QuickPanelExplorerEntryModel {
  name: string;
  fullPath: string;
  isDirectory: boolean;
  isParent: boolean;
}

interface QuickPanelExplorerTreeRowModel {
  key: string;
  name: string;
  fullPath: string;
  isDirectory: boolean;
  isParent: boolean;
  canExpand: boolean;
  expanded: boolean;
  depth: number;
}

interface QuickPanelFolderModel {
  id: string;
  name: string;
}

interface QuickPanelCommandModel {
  id: string;
  folderId: string;
  name: string;
  commandText: string;
  description: string;
}

interface KeyboardAvoidAreaChangeInfo {
  type: window.AvoidAreaType;
  area: window.AvoidArea;
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
  @State terminalHint: string = I18n.t(I18n.getLanguage(), '正在初始化终端...', 'Initializing terminal...');
  @State historyLoadingVisible: boolean = false;
  @State showSessionClosedDialog: boolean = false;
  @State safeAreaTop: number = 0;
  @State safeAreaBottom: number = 0;
  @State safeAreaLeft: number = 0;
  @State safeAreaRight: number = 0;
  @State windowHeightVp: number = 0;
  @State keyboardHeight: number = 0;
  @State showQuickPanel: boolean = false;
  @State quickPanelTab: string = 'quick_commands';
  @State quickPanelInputText: string = '';
  @State quickPanelRecentInputs: Array<string> = [];
  @State quickPanelExplorerPath: string = '';
  @State quickPanelExplorerVirtualRoot: boolean = false;
  @State quickPanelExplorerEntries: Array<QuickPanelExplorerEntryModel> = [];
  @State quickPanelSelectedExplorerPath: string = '';
  @State quickPanelFolders: Array<QuickPanelFolderModel> = [];
  @State quickPanelCommands: Array<QuickPanelCommandModel> = [];
  @State quickPanelSelectedFolderId: string = '';
  @State quickPanelExpandedPaths: Array<string> = [];
  @State quickPanelExplorerTreeVersion: number = 0;
  @State quickPanelLoading: boolean = false;
  @State quickPanelLoadError: string = '';
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
  private quickPanelSubmitTimers: Array<number> = [];
  private quickPanelSubmitDelayMs: number = 50;
  private lastReportedCols: number = 0;
  private lastReportedRows: number = 0;
  private viewedSessionId: string = '';
  private keyboardWindow: window.Window | null = null;
  private keyboardAvoidAreaCallback: ((data: KeyboardAvoidAreaChangeInfo) => void) | null = null;
  private quickPanelExplorerChildrenMap: Map<string, Array<QuickPanelExplorerEntryModel>> = new Map();
  private quickPanelLastRequestPath: string = '';
  private quickPanelLastRequestIsExpand: boolean = false;
  private quickPanelTabItems: Array<QuickPanelTabItem> = [
    { id: 'explorer', zhLabel: '资源管理器', enLabel: 'Explorer' } as QuickPanelTabItem,
    { id: 'quick_commands', zhLabel: '快捷指令', enLabel: 'Quick Commands' } as QuickPanelTabItem,
    { id: 'recent_inputs', zhLabel: '历史指令', enLabel: 'Recent Inputs' } as QuickPanelTabItem
  ];
  private row2ShortcutKeys: Array<ShortcutKey> = [
    { label: 'Ctrl+C', data: '\x03' } as ShortcutKey,
    { label: 'Ctrl+D', data: '\x04' } as ShortcutKey,
    { label: 'Esc', data: '\x1B' } as ShortcutKey
  ];

  private normalizeString(value: string | undefined | null, fallback: string = ''): string {
    if (typeof value === 'string') {
      return value;
    }
    return fallback;
  }

  private t(zhText: string, enText: string): string {
    return I18n.t(I18n.getLanguage(), zhText, enText);
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

  private getMessageObjectArray(data: Record<string, Object>, key: string): Array<Record<string, Object>> {
    const value: Object | undefined = data[key];
    if (!Array.isArray(value)) {
      return [];
    }

    const result: Array<Record<string, Object>> = [];
    const rawArray: Array<Object> = value as Array<Object>;
    for (let i: number = 0; i < rawArray.length; i++) {
      const item: Object = rawArray[i];
      if (item !== null && typeof item === 'object') {
        result.push(item as Record<string, Object>);
      }
    }
    return result;
  }

  private parseQuickPanelExplorerEntries(data: Record<string, Object>): Array<QuickPanelExplorerEntryModel> {
    const rawEntries: Array<Record<string, Object>> = this.getMessageObjectArray(data, 'explorerEntries');
    let entries: Array<QuickPanelExplorerEntryModel> = [];
    for (let i: number = 0; i < rawEntries.length; i++) {
      const item: Record<string, Object> = rawEntries[i];
      const fullPath: string = this.getMessageString(item, 'fullPath');
      if (fullPath.length === 0) {
        continue;
      }

      let entry: QuickPanelExplorerEntryModel = {
        name: this.getMessageString(item, 'name', fullPath),
        fullPath: fullPath,
        isDirectory: this.getMessageBoolean(item, 'isDirectory', false),
        isParent: this.getMessageBoolean(item, 'isParent', false)
      } as QuickPanelExplorerEntryModel;
      entries.push(entry);
    }
    return entries;
  }

  private parseQuickPanelFolders(data: Record<string, Object>): Array<QuickPanelFolderModel> {
    const rawFolders: Array<Record<string, Object>> = this.getMessageObjectArray(data, 'quickCommandFolders');
    let folders: Array<QuickPanelFolderModel> = [];
    for (let i: number = 0; i < rawFolders.length; i++) {
      const item: Record<string, Object> = rawFolders[i];
      const id: string = this.getMessageString(item, 'id');
      const name: string = this.getMessageString(item, 'name', id);
      if (id.length === 0 || name.length === 0) {
        continue;
      }
      let folder: QuickPanelFolderModel = {
        id: id,
        name: name
      } as QuickPanelFolderModel;
      folders.push(folder);
    }
    return folders;
  }

  private parseQuickPanelCommands(data: Record<string, Object>): Array<QuickPanelCommandModel> {
    const rawCommands: Array<Record<string, Object>> = this.getMessageObjectArray(data, 'quickCommands');
    let commands: Array<QuickPanelCommandModel> = [];
    for (let i: number = 0; i < rawCommands.length; i++) {
      const item: Record<string, Object> = rawCommands[i];
      const id: string = this.getMessageString(item, 'id');
      const folderId: string = this.getMessageString(item, 'folderId');
      const name: string = this.getMessageString(item, 'name');
      const commandText: string = this.getMessageString(item, 'commandText');
      const description: string = this.getMessageString(item, 'description');
      if (id.length === 0 || folderId.length === 0 || name.length === 0) {
        continue;
      }

      let command: QuickPanelCommandModel = {
        id: id,
        folderId: folderId,
        name: name,
        commandText: commandText,
        description: description
      } as QuickPanelCommandModel;
      commands.push(command);
    }
    return commands;
  }

  aboutToAppear(): void {
    hilog.info(DOMAIN, PAGE_TAG, 'aboutToAppear');
    this.syncSafeArea();
    this.registerKeyboardAvoidArea();
    // Reset state for fresh terminal view each time we enter
    this.pendingTerminalOutput = '';
    this.resetHistoryState();
    this.terminalReady = false;
    this.terminalHint = this.t('正在初始化终端...', 'Initializing terminal...');
    this.historyLoadingVisible = false;
    this.showSessionClosedDialog = false;
    this.showQuickPanel = false;
    this.quickPanelTab = 'quick_commands';
    this.quickPanelInputText = '';
    this.quickPanelExplorerPath = '';
    this.quickPanelExplorerVirtualRoot = false;
    this.quickPanelExplorerEntries = [];
    this.quickPanelSelectedExplorerPath = '';
    this.quickPanelFolders = [];
    this.quickPanelCommands = [];
    this.quickPanelSelectedFolderId = '';
    this.quickPanelRecentInputs = [];
    this.quickPanelExpandedPaths = [];
    this.quickPanelExplorerTreeVersion = 0;
    this.quickPanelExplorerChildrenMap.clear();
    this.quickPanelLastRequestPath = '';
    this.quickPanelLastRequestIsExpand = false;
    this.quickPanelLoading = false;
    this.quickPanelLoadError = '';
    this.viewedSessionId = this.normalizeString(ConnectionManager.getActiveSessionId());
    this.activatePage();
  }

  private async syncSafeArea(): Promise<void> {
    const insets = await SafeAreaUtil.resolveInsets(this.getUIContext());
    this.safeAreaTop = insets.top;
    this.safeAreaBottom = insets.bottom;
    this.safeAreaLeft = insets.left;
    this.safeAreaRight = insets.right;
  }

  private async registerKeyboardAvoidArea(): Promise<void> {
    if (this.keyboardWindow !== null) {
      return;
    }
    const uiContext = this.getUIContext();
    const context = uiContext.getHostContext();
    if (context === undefined) {
      return;
    }

    try {
      const mainWindow: window.Window = await window.getLastWindow(context);
      this.keyboardWindow = mainWindow;
      try {
        const windowHeightPx: number = mainWindow.getWindowProperties().windowRect.height;
        const measuredWindowHeightVp: number = uiContext.px2vp(windowHeightPx);
        if (measuredWindowHeightVp > 0) {
          this.windowHeightVp = measuredWindowHeightVp;
        }
      } catch (_err) {
        // Keep fallback sizing when window metrics are unavailable.
      }
      const keyboardArea: window.AvoidArea = mainWindow.getWindowAvoidArea(window.AvoidAreaType.TYPE_KEYBOARD);
      this.keyboardHeight = uiContext.px2vp(keyboardArea.bottomRect.height);

      this.keyboardAvoidAreaCallback = (data: KeyboardAvoidAreaChangeInfo) => {
        if (data.type !== window.AvoidAreaType.TYPE_KEYBOARD) {
          return;
        }
        this.keyboardHeight = uiContext.px2vp(data.area.bottomRect.height);
      };

      mainWindow.on('avoidAreaChange', this.keyboardAvoidAreaCallback);
    } catch (err) {
      hilog.warn(DOMAIN, PAGE_TAG, 'Failed to register keyboard avoid area: %{public}s', JSON.stringify(err));
    }
  }

  private unregisterKeyboardAvoidArea(): void {
    if (this.keyboardWindow !== null && this.keyboardAvoidAreaCallback !== null) {
      try {
        this.keyboardWindow.off('avoidAreaChange', this.keyboardAvoidAreaCallback);
      } catch (_err) {
        // Ignore errors during page transitions.
      }
    }
    this.keyboardWindow = null;
    this.keyboardAvoidAreaCallback = null;
    this.windowHeightVp = 0;
    this.keyboardHeight = 0;
  }

  private clearQuickPanelSubmitTimers(): void {
    for (let i: number = 0; i < this.quickPanelSubmitTimers.length; i++) {
      clearTimeout(this.quickPanelSubmitTimers[i]);
    }
    this.quickPanelSubmitTimers = [];
  }

  private getShortcutBarOffset(): number {
    const offset: number = this.keyboardHeight - this.safeAreaBottom;
    return offset > 0 ? offset : 0;
  }

  private getQuickPanelSheetBottomOffset(): number {
    if (this.keyboardHeight > 0) {
      // The system already moves the overlay with keyboard avoid area.
      // Keep zero extra offset so the sheet sits on keyboard top edge.
      return 0;
    }
    return this.safeAreaBottom;
  }

  private clampNumber(value: number, min: number, max: number): number {
    if (value < min) {
      return min;
    }
    if (value > max) {
      return max;
    }
    return value;
  }

  private getQuickPanelSheetHeight(): number | string {
    if (this.keyboardHeight <= 0 || this.windowHeightVp <= 0) {
      return '65%';
    }
    const availableHeight: number = this.windowHeightVp - this.keyboardHeight - this.safeAreaTop;
    if (availableHeight <= 0) {
      return '50%';
    }
    const preferredHeight: number = this.windowHeightVp * 0.65;
    if (availableHeight < 160) {
      return availableHeight;
    }
    return this.clampNumber(preferredHeight, 160, availableHeight);
  }

  aboutToDisappear(): void {
    hilog.warn(DOMAIN, PAGE_TAG, 'aboutToDisappear sessionActive=%{public}s',
      this.sessionActive ? 'true' : 'false');
    this.deactivatePage();
    this.unregisterKeyboardAvoidArea();

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
          this.terminalHint = this.t('连接已断开', 'Disconnected');
          this.historyLoadingVisible = false;
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
            this.terminalHint = this.t('PC 端已关闭当前会话', 'Session closed on PC');
            this.historyLoadingVisible = false;
            this.showQuickPanel = false;
            this.showSessionClosedDialog = true;
          }
        } else if (type === 'quickpanel.sync') {
          const deviceId: string = this.getMessageString(data, 'deviceId');
          const activeDeviceId: string = this.normalizeString(ConnectionManager.getActiveDeviceId());
          if (deviceId.length > 0 && activeDeviceId.length > 0 && deviceId !== activeDeviceId) {
            return;
          }
          this.applyQuickPanelSync(data);
        } else if (type === 'error') {
          const errorMsg: string = this.getMessageString(data, 'message', 'Error');
          const errorCode: string = this.getMessageString(data, 'code');
          if (errorCode.startsWith('quickpanel.sync')) {
            this.quickPanelLastRequestPath = '';
            this.quickPanelLastRequestIsExpand = false;
            this.quickPanelLoading = false;
            this.quickPanelLoadError = errorMsg;
            return;
          }
          const errorPrefix: string = this.t('[错误]', '[Error]');
          this.enqueueTerminalOutput(`\r\n${errorPrefix} ${errorMsg}\r\n`);
        }
      });
    }

    if (this.terminalReady) {
      this.requestRemoteResize();
      this.startHistoryLoadIfNeeded();
      this.flushPendingTerminalOutput();
    }

    // Pull latest quick panel data once per page activation.
    this.requestQuickPanelSync('', false);
  }

  private deactivatePage(): void {
    if (this.flushTimer !== -1) {
      clearTimeout(this.flushTimer);
      this.flushTimer = -1;
    }
    this.clearQuickPanelSubmitTimers();

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
    this.terminalHint = this.t('等待终端输出...', 'Waiting for terminal output...');
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
    this.historyLoadingVisible = false;
  }

  private syncHistoryLoadingOverlay(): void {
    this.historyLoadingVisible = this.terminalReady &&
      this.historyStarted &&
      !this.historyComplete &&
      this.sessionActive;
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
    this.syncHistoryLoadingOverlay();
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
      this.syncHistoryLoadingOverlay();
      this.requestHistoryPage();
      return;
    }

    this.historyComplete = true;
    this.applyHistoryBuffer();
    this.syncHistoryLoadingOverlay();
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

  private onTopRowSendKey(): void {
    // Send key should execute current terminal input (Enter).
    this.sendShortcutKey('\r');
  }

  private toggleQuickPanel(): void {
    this.showQuickPanel = !this.showQuickPanel;
    if (this.showQuickPanel) {
      this.requestQuickPanelSync('');
    }
    if (!this.showQuickPanel) {
      this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
    }
  }

  private closeQuickPanel(): void {
    this.showQuickPanel = false;
    this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  private setQuickPanelTab(tabId: string): void {
    this.quickPanelTab = tabId;
    if (tabId === 'quick_commands') {
      this.ensureQuickPanelFolderSelection();
    }
  }

  private fillQuickPanelInput(value: string): void {
    const normalized: string = this.normalizeString(value);
    this.quickPanelInputText = normalized;
  }

  private addQuickPanelRecentInput(value: string): void {
    const normalized: string = this.normalizeString(value).trim();
    if (normalized.length === 0) {
      return;
    }

    const next: Array<string> = [normalized];
    for (let i: number = 0; i < this.quickPanelRecentInputs.length; i++) {
      const item: string = this.quickPanelRecentInputs[i];
      if (item !== normalized) {
        next.push(item);
      }
      if (next.length >= 18) {
        break;
      }
    }
    this.quickPanelRecentInputs = next;
  }

  private getQuickPanelTargetDeviceId(): string {
    return this.normalizeString(ConnectionManager.getActiveDeviceId());
  }

  private requestQuickPanelSync(
    explorerPath: string,
    showLoading: boolean = true,
    isExpandRequest: boolean = false
  ): void {
    const deviceId: string = this.getQuickPanelTargetDeviceId();
    if (deviceId.length === 0) {
      if (!showLoading) {
        return;
      }
      this.quickPanelLoadError = this.t('未找到目标设备', 'Target device not found');
      this.quickPanelLoading = false;
      return;
    }

    if (showLoading) {
      this.quickPanelLoading = true;
    }
    this.quickPanelLoadError = '';
    this.quickPanelLastRequestPath = explorerPath;
    this.quickPanelLastRequestIsExpand = isExpandRequest;
    ConnectionManager.requestQuickPanelSync(deviceId, explorerPath);
  }

  private normalizeExplorerPathKey(path: string): string {
    return this.normalizeString(path).trim().toLowerCase();
  }

  private areExplorerPathsEqual(left: string, right: string): boolean {
    return this.normalizeExplorerPathKey(left) === this.normalizeExplorerPathKey(right);
  }

  private hasExplorerChildrenCache(path: string): boolean {
    const key: string = this.normalizeExplorerPathKey(path);
    if (key.length === 0) {
      return false;
    }
    return this.quickPanelExplorerChildrenMap.has(key);
  }

  private getExplorerChildrenCache(path: string): Array<QuickPanelExplorerEntryModel> {
    const key: string = this.normalizeExplorerPathKey(path);
    if (key.length === 0) {
      return [];
    }
    const cached: Array<QuickPanelExplorerEntryModel> | undefined = this.quickPanelExplorerChildrenMap.get(key);
    if (cached === undefined) {
      return [];
    }
    return cached;
  }

  private extractExplorerChildren(entries: Array<QuickPanelExplorerEntryModel>): Array<QuickPanelExplorerEntryModel> {
    let result: Array<QuickPanelExplorerEntryModel> = [];
    for (let i: number = 0; i < entries.length; i++) {
      const entry: QuickPanelExplorerEntryModel = entries[i];
      if (!entry.isParent) {
        result.push(entry);
      }
    }
    return result;
  }

  private setExplorerChildrenCache(path: string, entries: Array<QuickPanelExplorerEntryModel>): void {
    const key: string = this.normalizeExplorerPathKey(path);
    if (key.length === 0) {
      return;
    }
    this.quickPanelExplorerChildrenMap.set(key, this.extractExplorerChildren(entries));
    this.quickPanelExplorerTreeVersion += 1;
  }

  private clearExplorerTreeCache(): void {
    this.quickPanelExpandedPaths = [];
    this.quickPanelExplorerChildrenMap.clear();
    this.quickPanelExplorerTreeVersion += 1;
  }

  private isExplorerPathExpanded(path: string): boolean {
    const key: string = this.normalizeExplorerPathKey(path);
    if (key.length === 0) {
      return false;
    }
    for (let i: number = 0; i < this.quickPanelExpandedPaths.length; i++) {
      if (this.normalizeExplorerPathKey(this.quickPanelExpandedPaths[i]) === key) {
        return true;
      }
    }
    return false;
  }

  private setExplorerPathExpanded(path: string, expanded: boolean): void {
    const key: string = this.normalizeExplorerPathKey(path);
    if (key.length === 0) {
      return;
    }

    let next: Array<string> = [];
    let found: boolean = false;
    for (let i: number = 0; i < this.quickPanelExpandedPaths.length; i++) {
      const currentPath: string = this.quickPanelExpandedPaths[i];
      if (this.normalizeExplorerPathKey(currentPath) === key) {
        found = true;
        if (expanded) {
          next.push(currentPath);
        }
      } else {
        next.push(currentPath);
      }
    }

    if (expanded && !found) {
      next.push(path);
    }
    this.quickPanelExpandedPaths = next;
  }

  private getQuickPanelExplorerRootEntries(): Array<QuickPanelExplorerEntryModel> {
    return this.extractExplorerChildren(this.quickPanelExplorerEntries);
  }

  private appendExplorerTreeRows(
    rows: Array<QuickPanelExplorerTreeRowModel>,
    entry: QuickPanelExplorerEntryModel,
    depth: number
  ): void {
    const expanded: boolean = entry.isDirectory && this.isExplorerPathExpanded(entry.fullPath);
    let row: QuickPanelExplorerTreeRowModel = {
      key: `${entry.fullPath}_${depth}_${this.quickPanelExplorerTreeVersion}`,
      name: entry.name,
      fullPath: entry.fullPath,
      isDirectory: entry.isDirectory,
      isParent: entry.isParent,
      canExpand: entry.isDirectory,
      expanded: expanded,
      depth: depth
    } as QuickPanelExplorerTreeRowModel;
    rows.push(row);

    if (!expanded || !entry.isDirectory) {
      return;
    }

    const children: Array<QuickPanelExplorerEntryModel> = this.getExplorerChildrenCache(entry.fullPath);
    for (let i: number = 0; i < children.length; i++) {
      this.appendExplorerTreeRows(rows, children[i], depth + 1);
    }
  }

  private getQuickPanelExplorerTreeRows(): Array<QuickPanelExplorerTreeRowModel> {
    let rows: Array<QuickPanelExplorerTreeRowModel> = [];
    const roots: Array<QuickPanelExplorerEntryModel> = this.getQuickPanelExplorerRootEntries();
    for (let i: number = 0; i < roots.length; i++) {
      this.appendExplorerTreeRows(rows, roots[i], 0);
    }
    return rows;
  }

  private ensureQuickPanelFolderSelection(): void {
    if (this.quickPanelFolders.length === 0) {
      this.quickPanelSelectedFolderId = '';
      return;
    }
    if (this.quickPanelSelectedFolderId.length === 0) {
      this.quickPanelSelectedFolderId = this.quickPanelFolders[0].id;
      return;
    }

    for (let i: number = 0; i < this.quickPanelFolders.length; i++) {
      if (this.quickPanelFolders[i].id === this.quickPanelSelectedFolderId) {
        return;
      }
    }
    this.quickPanelSelectedFolderId = this.quickPanelFolders[0].id;
  }

  private applyQuickPanelSync(data: Record<string, Object>): void {
    const explorerPath: string = this.getMessageString(data, 'explorerPath');
    const explorerVirtualRoot: boolean = this.getMessageBoolean(data, 'explorerVirtualRoot', false);
    const explorerEntries: Array<QuickPanelExplorerEntryModel> = this.parseQuickPanelExplorerEntries(data);
    const matchesPendingExpandRequest: boolean = this.quickPanelLastRequestIsExpand &&
      this.quickPanelLastRequestPath.length > 0 &&
      this.areExplorerPathsEqual(this.quickPanelLastRequestPath, explorerPath);
    const looksLikeExpandedChildResponse: boolean = this.isExplorerPathExpanded(explorerPath) &&
      !this.areExplorerPathsEqual(explorerPath, this.quickPanelExplorerPath);
    const isExpandResponse: boolean = matchesPendingExpandRequest || looksLikeExpandedChildResponse;

    if (isExpandResponse) {
      this.setExplorerChildrenCache(explorerPath, explorerEntries);
    } else {
      this.quickPanelExplorerPath = explorerPath;
      this.quickPanelExplorerVirtualRoot = explorerVirtualRoot;
      this.quickPanelExplorerEntries = explorerEntries;
      this.quickPanelSelectedExplorerPath = '';
      this.clearExplorerTreeCache();
    }

    this.quickPanelFolders = this.parseQuickPanelFolders(data);
    this.quickPanelCommands = this.parseQuickPanelCommands(data);

    const recentRaw: Object | undefined = data['recentInputs'];
    let recentInputs: Array<string> = [];
    if (Array.isArray(recentRaw)) {
      const recentArray: Array<Object> = recentRaw as Array<Object>;
      for (let i: number = 0; i < recentArray.length; i++) {
        const item: Object = recentArray[i];
        if (typeof item === 'string') {
          const normalized: string = (item as string).trim();
          if (normalized.length > 0) {
            recentInputs.push(normalized);
          }
        }
      }
    }
    this.quickPanelRecentInputs = recentInputs;
    this.quickPanelLastRequestPath = '';
    this.quickPanelLastRequestIsExpand = false;
    this.quickPanelLoading = false;
    this.quickPanelLoadError = '';
    this.ensureQuickPanelFolderSelection();
  }

  private onExplorerTreeRowClick(row: QuickPanelExplorerTreeRowModel): void {
    this.quickPanelSelectedExplorerPath = row.fullPath;
  }

  private onExplorerDisclosureClick(row: QuickPanelExplorerTreeRowModel): void {
    this.quickPanelSelectedExplorerPath = row.fullPath;
    this.toggleExplorerDirectory(row.fullPath);
  }

  private isExplorerRowSelected(row: QuickPanelExplorerTreeRowModel): boolean {
    return this.areExplorerPathsEqual(this.quickPanelSelectedExplorerPath, row.fullPath);
  }

  private copySelectedExplorerPathToInput(): void {
    const selected: string = this.normalizeString(this.quickPanelSelectedExplorerPath).trim();
    if (selected.length === 0) {
      return;
    }
    this.fillQuickPanelInput(selected);
  }

  private toggleExplorerDirectory(path: string): void {
    if (path.length === 0) {
      return;
    }
    if (this.isExplorerPathExpanded(path)) {
      this.setExplorerPathExpanded(path, false);
      return;
    }

    this.setExplorerPathExpanded(path, true);
    if (!this.hasExplorerChildrenCache(path)) {
      this.requestQuickPanelSync(path, false, true);
    }
  }

  private openExplorerRoot(): void {
    this.requestQuickPanelSync('This PC', true, false);
  }

  private navigateExplorerUp(): void {
    for (let i: number = 0; i < this.quickPanelExplorerEntries.length; i++) {
      const entry: QuickPanelExplorerEntryModel = this.quickPanelExplorerEntries[i];
      if (entry.isParent && entry.isDirectory) {
        this.requestQuickPanelSync(entry.fullPath, true, false);
        return;
      }
    }
    this.openExplorerRoot();
  }

  private getVisibleQuickCommands(): Array<QuickPanelCommandModel> {
    if (this.quickPanelSelectedFolderId.length === 0) {
      return this.quickPanelCommands;
    }

    let result: Array<QuickPanelCommandModel> = [];
    for (let i: number = 0; i < this.quickPanelCommands.length; i++) {
      const command: QuickPanelCommandModel = this.quickPanelCommands[i];
      if (command.folderId === this.quickPanelSelectedFolderId) {
        result.push(command);
      }
    }
    return result;
  }

  private sendQuickPanelInput(): void {
    const raw: string = this.normalizeString(this.quickPanelInputText);
    if (raw.trim().length === 0) {
      return;
    }

    // Run behavior: send text first, then submit Enter after a short delay.
    const targetSessionId: string = this.normalizeString(ConnectionManager.getActiveSessionId());
    ConnectionManager.sendTerminalInput(raw, targetSessionId);
    const submitTimer: number = setTimeout(() => {
      let nextTimers: Array<number> = [];
      for (let i: number = 0; i < this.quickPanelSubmitTimers.length; i++) {
        if (this.quickPanelSubmitTimers[i] !== submitTimer) {
          nextTimers.push(this.quickPanelSubmitTimers[i]);
        }
      }
      this.quickPanelSubmitTimers = nextTimers;
      ConnectionManager.sendTerminalInput('\r', targetSessionId);
      this.runTerminalScript('window.phoneShell && window.phoneShell.focus();');
    }, this.quickPanelSubmitDelayMs) as number;
    this.quickPanelSubmitTimers.push(submitTimer);

    const deviceId: string = this.getQuickPanelTargetDeviceId();
    if (deviceId.length > 0) {
      ConnectionManager.appendQuickPanelRecentInput(deviceId, raw);
    }
    this.addQuickPanelRecentInput(raw);
    this.quickPanelInputText = '';
  }

  private goBack(): void {
    // Don't close the terminal session — it stays alive on the PC.
    // Just navigate back to the session list.
    this.getUIContext().getRouter().back();
  }

  build() {
    Stack({ alignContent: Alignment.Center }) {
      Column() {
        this.HeaderBar()
        this.TerminalViewport()
        if (!this.showQuickPanel) {
          this.CollapsedShortcutBar()
        }
      }
      .width('100%')
      .height('100%')
      .padding({ top: this.safeAreaTop, bottom: this.safeAreaBottom })
      .backgroundColor(AppColors.TERMINAL_BG)

      if (this.showQuickPanel) {
        this.QuickPanelOverlay()
      }

      if (this.showSessionClosedDialog) {
        this.SessionClosedDialog()
      }
    }
    .width('100%')
    .height('100%')
  }

  @Builder
  HeaderBar() {
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
        Text(this.t('终端', 'Terminal'))
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)

        if (this.viewedSessionId.length > 0) {
          Text(this.viewedSessionId)
            .fontSize(11)
            .fontColor(AppColors.TEXT_MUTED)
            .fontFamily('monospace')
            .maxLines(1)
            .textOverflow({ overflow: TextOverflow.Ellipsis })
        }
      }
      .layoutWeight(1)
      .alignItems(HorizontalAlign.Start)

      Row() {
        Circle()
          .width(6)
          .height(6)
          .fill(this.sessionActive ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
          .margin({ right: 6 })

        Text(this.sessionActive ? this.t('在线', 'ACTIVE') : this.t('离线', 'OFFLINE'))
          .fontSize(11)
          .fontColor(this.sessionActive ? AppColors.ONLINE_GREEN : AppColors.TEXT_MUTED)
          .fontFamily('monospace')
      }
      .padding({ left: 8, right: 8, top: 4, bottom: 4 })
      .backgroundColor(AppColors.CHIP_BG)
      .borderWidth(1)
      .borderColor(this.sessionActive ? AppColors.ONLINE_GREEN : AppColors.CHIP_BORDER)
      .borderRadius(AppSizes.BORDER_RADIUS_TAG)
    }
    .width('100%')
    .height(54)
    .padding({
      left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
      right: AppSizes.PADDING_PAGE + this.safeAreaRight
    })
    .backgroundColor(AppColors.SURFACE_1)
    .alignItems(VerticalAlign.Center)
  }

  @Builder
  TerminalViewport() {
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

      if (!this.terminalReady || this.historyLoadingVisible) {
        Column() {
          Text(!this.terminalReady
            ? this.terminalHint
            : this.t('内容较多，加载中，请等待', 'Loading history, please wait'))
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_SECONDARY)
        }
        .width('100%')
        .height('100%')
        .backgroundColor(AppColors.TERMINAL_BG)
        .justifyContent(FlexAlign.Center)
        .alignItems(HorizontalAlign.Center)
      }
    }
    .width('100%')
    .layoutWeight(1)
    .padding({
      left: 8 + this.safeAreaLeft,
      right: 8 + this.safeAreaRight,
      top: 8,
      bottom: 8
    })
    .backgroundColor(AppColors.TERMINAL_BG)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
  }

  @Builder
  CollapsedShortcutBar() {
    Column() {
      Row() {
        Blank().layoutWeight(1)

        Button(this.t('快捷面板', 'Quick Panel'))
          .height(34)
          .width(116)
          .fontSize(12)
          .fontColor('#FFFFFF')
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .onClick(() => {
            this.toggleQuickPanel();
          })

        Blank().layoutWeight(1)

        Row({ space: 6 }) {
          this.ShortcutButton('↑', '\x1B[A', 44)
          Button(this.t('发送', 'Send'))
            .height(32)
            .fontSize(12)
            .fontColor(AppColors.TEXT_PRIMARY)
            .backgroundColor(AppColors.HIGHLIGHT)
            .borderWidth(1)
            .borderColor(AppColors.CARD_BORDER)
            .borderRadius(AppSizes.BORDER_RADIUS_TAG)
            .padding({ left: 10, right: 10 })
            .onClick(() => {
              this.onTopRowSendKey();
            })
        }
      }
      .width('100%')
      .padding({
        left: 12 + this.safeAreaLeft,
        right: 12 + this.safeAreaRight,
        top: 6,
        bottom: 4
      })

      Row() {
        Row({ space: 6 }) {
          ForEach(this.row2ShortcutKeys, (key: ShortcutKey) => {
            this.ShortcutButton(key.label, key.data, 56)
          }, (key: ShortcutKey, index: number) => `${key.label}_${index}`)
        }

        Blank().width(8)

        Row({ space: 6 }) {
          this.ShortcutButton('←', '\x1B[D', 44)
          this.ShortcutButton('↓', '\x1B[B', 44)
          this.ShortcutButton('→', '\x1B[C', 44)
        }
      }
      .width('100%')
      .padding({
        left: 12 + this.safeAreaLeft,
        right: 12 + this.safeAreaRight,
        bottom: 8
      })
    }
    .width('100%')
    .height(96)
    .margin({ bottom: this.getShortcutBarOffset() })
    .backgroundColor(AppColors.SURFACE_1)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
  }

  @Builder
  ShortcutButton(label: string, data: string, minWidth: number) {
    Button(label)
      .height(32)
      .width(minWidth)
      .fontSize(12)
      .fontColor(AppColors.TEXT_PRIMARY)
      .backgroundColor(AppColors.HIGHLIGHT)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(AppSizes.BORDER_RADIUS_TAG)
      .padding({ left: 6, right: 6 })
      .onClick(() => {
        this.sendShortcutKey(data);
      })
  }

  @Builder
  QuickPanelOverlay() {
    Column() {
      Blank()
        .width('100%')
        .layoutWeight(1)
        .backgroundColor('#70000000')
        .onClick(() => {
          this.closeQuickPanel();
        })

      this.QuickPanelSheet()
    }
    .width('100%')
    .height('100%')
    .padding({ top: this.safeAreaTop })
  }

  @Builder
  QuickPanelSheet() {
    Column() {
      Row() {
        Blank()
          .width(36)
          .height(4)
          .borderRadius(2)
          .backgroundColor(AppColors.TEXT_MUTED)
      }
      .width('100%')
      .justifyContent(FlexAlign.Center)
      .margin({ bottom: 10 })

      Row({ space: 8 }) {
        ForEach(this.quickPanelTabItems, (tab: QuickPanelTabItem) => {
          this.QuickPanelTabButton(tab)
        }, (tab: QuickPanelTabItem) => `quick_panel_tab_${tab.id}`)
      }
      .width('100%')
      .margin({ bottom: 10 })

      this.QuickPanelContent()

      Row({ space: 8 }) {
        Button('⌄')
          .width(44)
          .height(40)
          .fontSize(16)
          .fontColor(AppColors.TEXT_PRIMARY)
          .backgroundColor(AppColors.HIGHLIGHT)
          .borderWidth(1)
          .borderColor(AppColors.CARD_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .onClick(() => {
            this.closeQuickPanel();
          })

        TextInput({
          text: $$this.quickPanelInputText,
          placeholder: this.t('输入命令并发送运行', 'Type command to run')
        })
          .type(InputType.Normal)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_PRIMARY)
          .backgroundColor(AppColors.INPUT_BG)
          .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
          .padding({ left: 12, right: 12 })
          .height(40)
          .layoutWeight(1)

        Button(this.t('发送', 'Send'))
          .width(64)
          .height(40)
          .fontSize(14)
          .fontColor('#FFFFFF')
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .onClick(() => {
            this.sendQuickPanelInput();
          })
      }
      .width('100%')
      .margin({ top: 10 })
    }
    .width('100%')
    .height(this.getQuickPanelSheetHeight())
    .padding({
      left: 12 + this.safeAreaLeft,
      right: 12 + this.safeAreaRight,
      top: 10,
      bottom: 10
    })
    .margin({ bottom: this.getQuickPanelSheetBottomOffset() })
    .backgroundColor(AppColors.SURFACE_2)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(16)
  }

  @Builder
  QuickPanelTabButton(tab: QuickPanelTabItem) {
    Button(this.t(tab.zhLabel, tab.enLabel))
      .height(34)
      .fontSize(12)
      .fontColor(this.quickPanelTab === tab.id ? '#FFFFFF' : AppColors.TEXT_SECONDARY)
      .backgroundColor(this.quickPanelTab === tab.id ? AppColors.ACCENT : AppColors.HIGHLIGHT)
      .borderWidth(1)
      .borderColor(this.quickPanelTab === tab.id ? AppColors.ACCENT : AppColors.CARD_BORDER)
      .borderRadius(AppSizes.BORDER_RADIUS_TAG)
      .layoutWeight(1)
      .onClick(() => {
        this.setQuickPanelTab(tab.id);
      })
  }

  @Builder
  QuickPanelContent() {
    if (this.quickPanelLoading) {
      Column() {
        Text(this.t('同步快捷面板中...', 'Syncing quick panel...'))
          .fontSize(13)
          .fontColor(AppColors.TEXT_SECONDARY)
      }
      .width('100%')
      .layoutWeight(1)
      .justifyContent(FlexAlign.Center)
      .alignItems(HorizontalAlign.Center)
    } else if (this.quickPanelLoadError.length > 0) {
      Column() {
        Text(this.quickPanelLoadError)
          .fontSize(13)
          .fontColor(AppColors.ERROR_RED)
          .margin({ bottom: 10 })
          .textAlign(TextAlign.Center)
        Button(this.t('重试', 'Retry'))
          .height(34)
          .fontSize(12)
          .fontColor('#FFFFFF')
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
          .onClick(() => {
            this.requestQuickPanelSync(this.quickPanelExplorerPath);
          })
      }
      .width('100%')
      .layoutWeight(1)
      .justifyContent(FlexAlign.Center)
      .alignItems(HorizontalAlign.Center)
    } else if (this.quickPanelTab === 'explorer') {
      this.QuickPanelExplorerTab()
    } else if (this.quickPanelTab === 'quick_commands') {
      this.QuickPanelQuickCommandsTab()
    } else {
      this.QuickPanelRecentInputsTab()
    }
  }

  @Builder
  QuickPanelExplorerTab() {
    Column() {
      Row({ space: 8 }) {
        Button('⟲')
          .width(40)
          .height(30)
          .fontSize(14)
          .fontColor(AppColors.TEXT_PRIMARY)
          .backgroundColor(AppColors.HIGHLIGHT)
          .borderWidth(1)
          .borderColor(AppColors.CARD_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
          .onClick(() => {
            this.requestQuickPanelSync(this.quickPanelExplorerPath);
          })
        Button('↑')
          .width(40)
          .height(30)
          .fontSize(14)
          .fontColor(AppColors.TEXT_PRIMARY)
          .backgroundColor(AppColors.HIGHLIGHT)
          .borderWidth(1)
          .borderColor(AppColors.CARD_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
          .onClick(() => {
            this.navigateExplorerUp();
          })
        Button(this.t('根目录', 'Root'))
          .height(30)
          .fontSize(12)
          .fontColor(AppColors.TEXT_PRIMARY)
          .backgroundColor(AppColors.HIGHLIGHT)
          .borderWidth(1)
          .borderColor(AppColors.CARD_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
          .padding({ left: 10, right: 10 })
          .onClick(() => {
            this.openExplorerRoot();
          })
        Text(this.quickPanelExplorerPath)
          .layoutWeight(1)
          .fontSize(11)
          .fontColor(AppColors.TEXT_MUTED)
          .fontFamily('monospace')
          .maxLines(1)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
      }
      .width('100%')
      .margin({ bottom: 8 })

      Scroll() {
        Column({ space: 8 }) {
          if (this.getQuickPanelExplorerTreeRows().length === 0) {
            Column() {
              Text(this.t('目录为空', 'Folder is empty'))
                .fontSize(13)
                .fontColor(AppColors.TEXT_SECONDARY)
            }
            .width('100%')
            .height(120)
            .justifyContent(FlexAlign.Center)
            .alignItems(HorizontalAlign.Center)
          } else {
            ForEach(this.getQuickPanelExplorerTreeRows(), (row: QuickPanelExplorerTreeRowModel) => {
              this.QuickPanelExplorerRow(row);
            }, (row: QuickPanelExplorerTreeRowModel, index: number) => `${row.key}_${index}`)
          }
        }
        .width('100%')
        .padding({ left: 2, right: 2, top: 2, bottom: 2 })
      }
      .width('100%')
      .layoutWeight(1)
      .scrollBar(BarState.Auto)

      Row({ space: 8 }) {
        Text(this.quickPanelSelectedExplorerPath.length > 0
          ? this.quickPanelSelectedExplorerPath
          : this.t('请选择文件或文件夹', 'Select a file or folder'))
          .layoutWeight(1)
          .fontSize(11)
          .fontColor(this.quickPanelSelectedExplorerPath.length > 0
            ? AppColors.TEXT_MUTED
            : AppColors.TEXT_SECONDARY)
          .fontFamily('monospace')
          .maxLines(1)
          .textOverflow({ overflow: TextOverflow.Ellipsis })

        Button(this.t('复制路径', 'Copy Path'))
          .height(30)
          .fontSize(12)
          .fontColor('#FFFFFF')
          .backgroundColor(this.quickPanelSelectedExplorerPath.length > 0
            ? AppColors.ACCENT
            : AppColors.CARD_BORDER)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
          .padding({ left: 12, right: 12 })
          .enabled(this.quickPanelSelectedExplorerPath.length > 0)
          .onClick(() => {
            this.copySelectedExplorerPathToInput();
          })
      }
      .width('100%')
      .margin({ top: 8 })
      .justifyContent(FlexAlign.End)
    }
    .width('100%')
    .layoutWeight(1)
  }

  @Builder
  QuickPanelQuickCommandsTab() {
    Column() {
      Scroll() {
        Row({ space: 8 }) {
          ForEach(this.quickPanelFolders, (folder: QuickPanelFolderModel) => {
            Button(folder.name)
              .height(30)
              .fontSize(12)
              .fontColor(this.quickPanelSelectedFolderId === folder.id ? '#FFFFFF' : AppColors.TEXT_SECONDARY)
              .backgroundColor(this.quickPanelSelectedFolderId === folder.id ? AppColors.ACCENT : AppColors.HIGHLIGHT)
              .borderWidth(1)
              .borderColor(this.quickPanelSelectedFolderId === folder.id ? AppColors.ACCENT : AppColors.CARD_BORDER)
              .borderRadius(AppSizes.BORDER_RADIUS_TAG)
              .padding({ left: 10, right: 10 })
              .onClick(() => {
                this.quickPanelSelectedFolderId = folder.id;
              })
          }, (folder: QuickPanelFolderModel, index: number) => `${folder.id}_${index}`)
        }
      }
      .width('100%')
      .scrollable(ScrollDirection.Horizontal)
      .scrollBar(BarState.Off)
      .margin({ bottom: 8 })

      Scroll() {
        Column({ space: 8 }) {
          if (this.getVisibleQuickCommands().length === 0) {
            Column() {
              Text(this.t('该目录暂无快捷指令', 'No quick commands in this folder'))
                .fontSize(13)
                .fontColor(AppColors.TEXT_SECONDARY)
            }
            .width('100%')
            .height(120)
            .justifyContent(FlexAlign.Center)
            .alignItems(HorizontalAlign.Center)
          } else {
            ForEach(this.getVisibleQuickCommands(), (command: QuickPanelCommandModel) => {
              this.QuickPanelCommandRow(command);
            }, (command: QuickPanelCommandModel, index: number) => `${command.id}_${index}`)
          }
        }
        .width('100%')
        .padding({ left: 2, right: 2, top: 2, bottom: 2 })
      }
      .width('100%')
      .layoutWeight(1)
      .scrollBar(BarState.Auto)
    }
    .width('100%')
    .layoutWeight(1)
  }

  @Builder
  QuickPanelRecentInputsTab() {
    Scroll() {
      Column({ space: 8 }) {
        if (this.quickPanelRecentInputs.length === 0) {
          Column() {
            Text(this.t('暂无历史指令', 'No recent inputs'))
              .fontSize(13)
              .fontColor(AppColors.TEXT_SECONDARY)
          }
          .width('100%')
          .height(120)
          .justifyContent(FlexAlign.Center)
          .alignItems(HorizontalAlign.Center)
        } else {
          ForEach(this.quickPanelRecentInputs, (item: string) => {
            this.QuickPanelRecentRow(item);
          }, (item: string, index: number) => `recent_item_${item}_${index}`)
        }
      }
      .width('100%')
      .padding({ left: 2, right: 2, top: 2, bottom: 2 })
    }
    .width('100%')
    .layoutWeight(1)
    .scrollBar(BarState.Auto)
  }

  @Builder
  QuickPanelExplorerRow(row: QuickPanelExplorerTreeRowModel) {
    Row() {
      Blank().width(12 * row.depth)

      if (row.canExpand) {
        Text(row.expanded ? '▼' : '▶')
          .width(16)
          .fontSize(11)
          .fontColor(AppColors.TEXT_MUTED)
          .textAlign(TextAlign.Center)
          .margin({ right: 4 })
          .onClick(() => {
            this.onExplorerDisclosureClick(row);
          })
      } else {
        Blank().width(16).margin({ right: 4 })
      }

      Text(row.isDirectory ? (row.expanded ? '📂' : '📁') : '📄')
        .width(18)
        .fontSize(14)
        .textAlign(TextAlign.Center)
        .margin({ right: 6 })

      Text(row.name)
        .layoutWeight(1)
        .fontSize(13)
        .fontColor(AppColors.TEXT_PRIMARY)
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
    }
    .width('100%')
    .height(34)
    .padding({ left: 8, right: 8 })
    .backgroundColor(this.isExplorerRowSelected(row) ? AppColors.HIGHLIGHT : AppColors.SURFACE_1)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(6)
    .alignItems(VerticalAlign.Center)
    .onClick(() => {
      this.onExplorerTreeRowClick(row);
    })
  }

  @Builder
  QuickPanelCommandRow(command: QuickPanelCommandModel) {
    Row() {
      Column() {
        Text(command.name)
          .fontSize(13)
          .fontColor(AppColors.TEXT_PRIMARY)
          .maxLines(1)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
        Text(command.commandText)
          .fontSize(11)
          .fontColor(AppColors.TEXT_MUTED)
          .fontFamily('monospace')
          .maxLines(1)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
          .margin({ top: 3 })
      }
      .alignItems(HorizontalAlign.Start)
      .layoutWeight(1)

      Button(this.t('插入', 'Insert'))
        .height(30)
        .fontSize(12)
        .fontColor(AppColors.TEXT_PRIMARY)
        .backgroundColor(AppColors.HIGHLIGHT)
        .borderWidth(1)
        .borderColor(AppColors.CARD_BORDER)
        .borderRadius(AppSizes.BORDER_RADIUS_TAG)
        .padding({ left: 12, right: 12 })
        .onClick(() => {
          this.fillQuickPanelInput(command.commandText);
        })
    }
    .width('100%')
    .padding({ left: 10, right: 10, top: 8, bottom: 8 })
    .backgroundColor(AppColors.SURFACE_1)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
  }

  @Builder
  QuickPanelRecentRow(input: string) {
    Row() {
      Text(input)
        .fontSize(13)
        .fontColor(AppColors.TEXT_PRIMARY)
        .fontFamily('monospace')
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
        .layoutWeight(1)

      Button(this.t('插入', 'Insert'))
        .height(30)
        .fontSize(12)
        .fontColor(AppColors.TEXT_PRIMARY)
        .backgroundColor(AppColors.HIGHLIGHT)
        .borderWidth(1)
        .borderColor(AppColors.CARD_BORDER)
        .borderRadius(AppSizes.BORDER_RADIUS_TAG)
        .padding({ left: 12, right: 12 })
        .onClick(() => {
          this.fillQuickPanelInput(input);
        })
    }
    .width('100%')
    .padding({ left: 10, right: 10, top: 8, bottom: 8 })
    .backgroundColor(AppColors.SURFACE_1)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
  }

  @Builder
  SessionClosedDialog() {
    Column() {
      Column() {
        Text(this.t('PC 端已关闭当前会话', 'Session closed on PC'))
          .fontSize(18)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 10 })

        Text(this.t('点击下方按钮返回会话列表。', 'Tap the button below to return to the session list.'))
          .fontSize(13)
          .fontColor(AppColors.TEXT_SECONDARY)
          .margin({ bottom: 18 })

        Row() {
          Button(this.t('返回会话列表', 'Back to Sessions'))
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
}
