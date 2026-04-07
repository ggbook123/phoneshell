import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;

import 'package:flutter/material.dart' hide ConnectionState;
import 'package:webview_flutter/webview_flutter.dart';

import '../core/constants.dart';
import '../core/connection_manager.dart';
import '../core/i18n.dart';
import '../widgets/phoneshell_header.dart';

class TerminalPage extends StatefulWidget {
  const TerminalPage({super.key});

  @override
  State<TerminalPage> createState() => _TerminalPageState();
}

class _TerminalPageState extends State<TerminalPage> {
  bool sessionActive = true;
  ConnectionState connectionState = ConnectionState.connected;
  bool terminalReady = false;
  String terminalHint = I18n.tCurrent('正在初始化终端...', 'Initializing terminal...');
  bool historyLoadingVisible = false;
  bool showSessionClosedDialog = false;

  int _stateListenerId = -1;
  int _messageListenerId = -1;

  late final WebViewController _controller;

  final int _maxOutputLength = 200000;
  final int _historyPageChars = 20000;
  String _pendingTerminalOutput = '';
  String _pendingHistoryOutput = '';
  String _pendingHistoryReplace = '';
  final List<String> _historyChunks = [];
  int _historyBeforeSeq = 0;
  bool _historyLoading = false;
  bool _historyComplete = false;
  bool _historyStarted = false;
  bool _historyPlanned = false;
  String _historySessionId = '';
  String _historyDeviceId = '';
  Timer? _flushTimer;
  int _lastReportedCols = 0;
  int _lastReportedRows = 0;
  String _viewedSessionId = '';

  static const int _quickPanelSubmitDelayMs = 50;
  static const int _quickPanelSlashCharDelayMs = 12;
  static const int _quickPanelSlashSubmitDelayMs = 120;
  static const String _quickPanelRootLabel = 'This PC';

  final List<_QuickPanelTabItem> _quickPanelTabs = const [
    _QuickPanelTabItem(id: 'explorer', zhLabel: '资源管理器', enLabel: 'Explorer'),
    _QuickPanelTabItem(
      id: 'quick_commands',
      zhLabel: '快捷指令',
      enLabel: 'Quick Commands',
    ),
    _QuickPanelTabItem(
      id: 'recent_inputs',
      zhLabel: '历史指令',
      enLabel: 'Recent Inputs',
    ),
  ];

  final List<_ShortcutKey> _row2ShortcutKeys = const [
    _ShortcutKey(label: 'Ctrl+C', data: '\x03'),
    _ShortcutKey(label: 'Ctrl+D', data: '\x04'),
    _ShortcutKey(label: 'Esc', data: '\x1B'),
  ];

  final TextEditingController _quickPanelInputController =
      TextEditingController();
  final FocusNode _quickPanelInputFocusNode = FocusNode();
  final List<Timer> _quickPanelPendingTimers = [];

  bool _showQuickPanel = false;
  String _quickPanelTab = 'quick_commands';
  List<String> _quickPanelRecentInputs = [];
  String _quickPanelExplorerPath = '';
  List<_QuickPanelExplorerEntry> _quickPanelExplorerEntries = [];
  String _quickPanelSelectedExplorerPath = '';
  List<_QuickPanelFolder> _quickPanelFolders = [];
  List<_QuickPanelCommand> _quickPanelCommands = [];
  String _quickPanelSelectedFolderId = '';
  final Set<String> _quickPanelExpandedPathKeys = <String>{};
  final Map<String, List<_QuickPanelExplorerEntry>>
  _quickPanelExplorerChildrenMap = {};
  String _quickPanelLastRequestPath = '';
  bool _quickPanelLastRequestIsExpand = false;
  bool _quickPanelLoading = false;
  String _quickPanelLoadError = '';

  String _t(String zh, String en) => I18n.tCurrent(zh, en);

  @override
  void initState() {
    super.initState();
    _controller = WebViewController()
      ..setJavaScriptMode(JavaScriptMode.unrestricted)
      ..setBackgroundColor(const Color(AppColors.terminalBg))
      ..addJavaScriptChannel('nativeApp', onMessageReceived: _handleJsMessage)
      ..loadFlutterAsset('assets/terminal/index.html');

    _resetTerminalState();
    _activatePage();
  }

  @override
  void dispose() {
    _deactivatePage();
    _flushTimer?.cancel();
    _quickPanelInputController.dispose();
    _quickPanelInputFocusNode.dispose();
    super.dispose();
  }

  void _resetTerminalState() {
    _pendingTerminalOutput = '';
    _resetHistoryState();
    terminalReady = false;
    terminalHint = _t('正在初始化终端...', 'Initializing terminal...');
    historyLoadingVisible = false;
    showSessionClosedDialog = false;
    _viewedSessionId = ConnectionManager.instance.activeSessionId;
    _resetQuickPanelState();
  }

  void _resetQuickPanelState() {
    _showQuickPanel = false;
    _quickPanelTab = 'quick_commands';
    _quickPanelInputController.clear();
    _quickPanelRecentInputs = [];
    _quickPanelExplorerPath = '';
    _quickPanelExplorerEntries = [];
    _quickPanelSelectedExplorerPath = '';
    _quickPanelFolders = [];
    _quickPanelCommands = [];
    _quickPanelSelectedFolderId = '';
    _quickPanelExpandedPathKeys.clear();
    _quickPanelExplorerChildrenMap.clear();
    _quickPanelLastRequestPath = '';
    _quickPanelLastRequestIsExpand = false;
    _quickPanelLoading = false;
    _quickPanelLoadError = '';
    _clearQuickPanelPendingTimers();
  }

  void _handleJsMessage(JavaScriptMessage message) {
    try {
      final decoded = jsonDecode(message.message);
      if (decoded is Map<String, dynamic>) {
        final type = decoded['type']?.toString() ?? '';
        final args = decoded['args'];
        if (type == 'onTerminalReady' && args is List && args.length >= 2) {
          _onTerminalReady(_toInt(args[0]), _toInt(args[1]));
        } else if (type == 'onTerminalResize' &&
            args is List &&
            args.length >= 2) {
          _onTerminalResize(_toInt(args[0]), _toInt(args[1]));
        } else if (type == 'sendInput' && args is List && args.isNotEmpty) {
          final data = args[0]?.toString() ?? '';
          ConnectionManager.instance.sendTerminalInput(data);
        } else if (type == 'onTerminalInteraction') {
          _requestRemoteResize();
          _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
        }
      }
    } catch (_) {
      // Ignore parse errors.
    }
  }

  int _toInt(dynamic value) {
    if (value is num) return value.toInt();
    return int.tryParse(value?.toString() ?? '') ?? 0;
  }

  String _normalizeString(dynamic value, [String fallback = '']) {
    if (value is String) return value;
    return fallback;
  }

  bool _toBool(dynamic value, [bool fallback = false]) {
    if (value is bool) return value;
    if (value is num) return value != 0;
    if (value is String) {
      final raw = value.toLowerCase();
      if (raw == 'true' || raw == '1') return true;
      if (raw == 'false' || raw == '0') return false;
    }
    return fallback;
  }

  List<Map<String, dynamic>> _toObjectList(dynamic value) {
    if (value is! List) return const [];
    final result = <Map<String, dynamic>>[];
    for (final item in value) {
      if (item is Map<String, dynamic>) {
        result.add(item);
      }
    }
    return result;
  }

  void _activatePage() {
    connectionState = ConnectionManager.instance.getConnectionStateForDevice(
      ConnectionManager.instance.activeDeviceId,
    );
    _viewedSessionId = ConnectionManager.instance.activeSessionId;
    sessionActive = _viewedSessionId.isNotEmpty;
    _historyPlanned = sessionActive && _viewedSessionId.isNotEmpty;

    if (_stateListenerId == -1) {
      _stateListenerId = ConnectionManager.instance.addOnStateChange((
        state,
        _,
      ) {
        if (!mounted) return;
        setState(() {
          connectionState = state;
        });
        if (state == ConnectionState.disconnected) {
          setState(() {
            sessionActive = false;
            terminalHint = _t('连接已断开', 'Disconnected');
            historyLoadingVisible = false;
          });
        }
      });
    }

    if (_messageListenerId == -1) {
      _messageListenerId = ConnectionManager.instance.addOnMessage((
        type,
        data,
      ) {
        if (!mounted) return;

        if (type == 'terminal.output') {
          if (_isCurrentSessionMessage(data)) {
            final output = _normalizeString(data['data']);
            if (_historyPlanned && !_historyComplete) {
              _appendPendingHistoryOutput(output);
            } else {
              _enqueueTerminalOutput(output);
            }
          }
        } else if (type == 'terminal.history.response') {
          _handleHistoryResponse(data);
        } else if (type == 'terminal.closed') {
          if (_isCurrentSessionMessage(data)) {
            setState(() {
              sessionActive = false;
              terminalHint = _t('PC 端已关闭当前会话', 'Session closed on PC');
              historyLoadingVisible = false;
              _showQuickPanel = false;
              showSessionClosedDialog = true;
            });
          }
        } else if (type == 'quickpanel.sync') {
          final deviceId = _normalizeString(data['deviceId']);
          final activeDeviceId = _normalizeString(
            ConnectionManager.instance.activeDeviceId,
          );
          if (deviceId.isNotEmpty &&
              activeDeviceId.isNotEmpty &&
              deviceId != activeDeviceId) {
            return;
          }
          setState(() {
            _applyQuickPanelSync(data);
          });
        } else if (type == 'error') {
          final errorMsg = _normalizeString(data['message'], 'Error');
          final errorCode = _normalizeString(data['code']);
          if (errorCode.startsWith('quickpanel.sync')) {
            setState(() {
              _quickPanelLastRequestPath = '';
              _quickPanelLastRequestIsExpand = false;
              _quickPanelLoading = false;
              _quickPanelLoadError = errorMsg;
            });
            return;
          }
          final errorPrefix = _t('[错误]', '[Error]');
          _enqueueTerminalOutput('\r\n$errorPrefix $errorMsg\r\n');
        }
      });
    }

    if (terminalReady) {
      _requestRemoteResize();
      _startHistoryLoadIfNeeded();
      _flushPendingTerminalOutput();
    }

    _requestQuickPanelSync('', showLoading: false);
  }

  void _deactivatePage() {
    _flushTimer?.cancel();
    _flushTimer = null;
    _clearQuickPanelPendingTimers();

    if (_stateListenerId != -1) {
      ConnectionManager.instance.removeOnStateChange(_stateListenerId);
      _stateListenerId = -1;
    }
    if (_messageListenerId != -1) {
      ConnectionManager.instance.removeOnMessage(_messageListenerId);
      _messageListenerId = -1;
    }
  }

  void _enqueueTerminalOutput(String data) {
    final output = _normalizeString(data);
    if (output.isEmpty) return;

    _pendingTerminalOutput += output;
    if (_pendingTerminalOutput.length > _maxOutputLength) {
      _pendingTerminalOutput = _pendingTerminalOutput.substring(
        _pendingTerminalOutput.length - _maxOutputLength,
      );
    }

    _scheduleFlush();
  }

  void _scheduleFlush() {
    if (!terminalReady || _flushTimer != null) return;
    _flushTimer = Timer(const Duration(milliseconds: 16), () {
      _flushTimer = null;
      _flushPendingTerminalOutput();
    });
  }

  void _flushPendingTerminalOutput() {
    if (!terminalReady || _pendingTerminalOutput.isEmpty) return;
    if (_historyPlanned && !_historyComplete) return;

    final payload = jsonEncode(_pendingTerminalOutput);
    _pendingTerminalOutput = '';
    _runTerminalScript(
      'window.phoneShell && window.phoneShell.write($payload);',
    );
  }

  void _runTerminalScript(String script) {
    try {
      _controller.runJavaScript(script);
    } catch (_) {
      // Ignore WebView script errors during transitions.
    }
  }

  void _onTerminalReady(int cols, int rows) {
    setState(() {
      terminalReady = true;
      terminalHint = _t('等待终端输出...', 'Waiting for terminal output...');
    });
    _handleTerminalResize(cols, rows);
    _startHistoryLoadIfNeeded();
    _applyPendingHistoryReplace();
    _flushPendingTerminalOutput();
    _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  void _onTerminalResize(int cols, int rows) {
    _handleTerminalResize(cols, rows);
  }

  void _handleTerminalResize(int cols, int rows) {
    if (cols <= 0 || rows <= 0) return;
    if (_lastReportedCols == cols && _lastReportedRows == rows) return;
    _lastReportedCols = cols;
    _lastReportedRows = rows;
    _requestRemoteResize();
  }

  void _requestRemoteResize() {
    if (!sessionActive || _lastReportedCols <= 0 || _lastReportedRows <= 0) {
      return;
    }
    ConnectionManager.instance.sendTerminalResize(
      _lastReportedCols,
      _lastReportedRows,
    );
  }

  void _resetHistoryState() {
    _historyPlanned = false;
    _historyStarted = false;
    _historyLoading = false;
    _historyComplete = false;
    _historyBeforeSeq = 0;
    _historyChunks.clear();
    _pendingHistoryOutput = '';
    _pendingHistoryReplace = '';
    _historySessionId = '';
    _historyDeviceId = '';
    historyLoadingVisible = false;
  }

  void _syncHistoryLoadingOverlay() {
    setState(() {
      historyLoadingVisible =
          terminalReady &&
          _historyStarted &&
          !_historyComplete &&
          sessionActive;
    });
  }

  void _startHistoryLoadIfNeeded() {
    if (!_historyPlanned || _historyStarted) return;
    if (!sessionActive || _viewedSessionId.isEmpty) return;
    if (!terminalReady) return;

    _historyStarted = true;
    _historySessionId = _viewedSessionId;
    _historyDeviceId = ConnectionManager.instance.activeDeviceId;
    _historyLoading = false;
    _historyComplete = false;
    _historyBeforeSeq = 0;
    _historyChunks.clear();
    _pendingHistoryReplace = '';

    _requestHistoryPage();
    _syncHistoryLoadingOverlay();
  }

  void _requestHistoryPage() {
    if (_historyLoading || _historyComplete) return;
    if (_historySessionId.isEmpty || _historyDeviceId.isEmpty) return;
    _historyLoading = true;
    ConnectionManager.instance.requestTerminalHistory(
      _historyDeviceId,
      _historySessionId,
      _historyBeforeSeq,
      _historyPageChars,
    );
  }

  void _handleHistoryResponse(Map<String, dynamic> data) {
    if (!_historyPlanned || !_isCurrentSessionMessage(data)) return;
    _historyLoading = false;

    final chunk = _normalizeString(data['data']);
    final nextBeforeSeq = _toInt(data['nextBeforeSeq']);
    final hasMore =
        data['hasMore'] == true ||
        data['hasMore'] == 1 ||
        data['hasMore'] == '1';

    if (chunk.isNotEmpty) {
      _historyChunks.insert(0, chunk);
    }

    if (hasMore) {
      _historyBeforeSeq = nextBeforeSeq;
      _syncHistoryLoadingOverlay();
      _requestHistoryPage();
      return;
    }

    _historyComplete = true;
    _applyHistoryBuffer();
    _syncHistoryLoadingOverlay();
  }

  void _appendPendingHistoryOutput(String data) {
    final output = _normalizeString(data);
    if (output.isEmpty) return;
    _pendingHistoryOutput += output;
    if (_pendingHistoryOutput.length > _maxOutputLength) {
      _pendingHistoryOutput = _pendingHistoryOutput.substring(
        _pendingHistoryOutput.length - _maxOutputLength,
      );
    }
  }

  void _applyHistoryBuffer() {
    final history = _historyChunks.join('');
    _historyChunks.clear();
    final merged = history + _pendingHistoryOutput;
    _pendingHistoryOutput = '';

    if (!terminalReady) {
      _pendingHistoryReplace = merged;
      return;
    }
    _replaceTerminalBuffer(merged);
  }

  void _applyPendingHistoryReplace() {
    if (_pendingHistoryReplace.isEmpty) return;
    final payload = _pendingHistoryReplace;
    _pendingHistoryReplace = '';
    _replaceTerminalBuffer(payload);
  }

  void _replaceTerminalBuffer(String data) {
    if (!terminalReady) {
      _pendingHistoryReplace = data;
      return;
    }
    if (data.isEmpty) {
      _runTerminalScript('window.phoneShell && window.phoneShell.clear();');
      return;
    }
    final payload = jsonEncode(data);
    _runTerminalScript(
      'window.phoneShell && (window.phoneShell.clear(), window.phoneShell.write($payload));',
    );
  }

  bool _isCurrentSessionMessage(Map<String, dynamic> data) {
    final sessionId = _normalizeString(data['sessionId']);
    return sessionId.isNotEmpty && sessionId == _viewedSessionId;
  }

  void _sendShortcutKey(String data) {
    ConnectionManager.instance.sendTerminalInput(data);
    _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  void _onTopRowSendKey() {
    _sendShortcutKey('\r');
  }

  void _removeQuickPanelPendingTimer(Timer timer) {
    _quickPanelPendingTimers.remove(timer);
  }

  void _scheduleQuickPanelPendingTimer(int delayMs, VoidCallback action) {
    late final Timer timer;
    timer = Timer(Duration(milliseconds: delayMs), () {
      _removeQuickPanelPendingTimer(timer);
      if (!mounted) return;
      action();
    });
    _quickPanelPendingTimers.add(timer);
  }

  void _clearQuickPanelPendingTimers() {
    for (final timer in _quickPanelPendingTimers) {
      timer.cancel();
    }
    _quickPanelPendingTimers.clear();
  }

  bool _startsWithSlashCommand(String text) {
    for (var i = 0; i < text.length; i++) {
      final ch = text.substring(i, i + 1);
      if (ch == ' ' || ch == '\t') {
        continue;
      }
      return ch == '/';
    }
    return false;
  }

  void _scheduleQuickPanelSubmitEnter(String targetSessionId, int delayMs) {
    _scheduleQuickPanelPendingTimer(delayMs, () {
      ConnectionManager.instance.sendTerminalInput(
        '\r',
        targetSessionId: targetSessionId,
      );
      _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
    });
  }

  double _getShortcutBarOffset(EdgeInsets padding, double keyboardHeight) {
    final offset = keyboardHeight - padding.bottom;
    return offset > 0 ? offset : 0;
  }

  double _getQuickPanelSheetBottomOffset(
    EdgeInsets padding,
    double keyboardHeight,
  ) {
    if (keyboardHeight > 0) {
      return _getShortcutBarOffset(padding, keyboardHeight);
    }
    return padding.bottom;
  }

  double _clampDouble(double value, double min, double max) {
    if (value < min) return min;
    if (value > max) return max;
    return value;
  }

  double _getQuickPanelSheetHeight(
    double windowHeight,
    EdgeInsets padding,
    double keyboardHeight,
  ) {
    if (windowHeight <= 0) {
      return 320;
    }
    if (keyboardHeight <= 0) {
      final preferred = windowHeight * 0.65;
      return _clampDouble(preferred, 280, windowHeight - padding.top - 24);
    }

    final availableHeight = windowHeight - keyboardHeight - padding.top;
    if (availableHeight <= 0) {
      return windowHeight * 0.5;
    }
    final preferredHeight = windowHeight * 0.65;
    if (availableHeight < 180) {
      return availableHeight;
    }
    return _clampDouble(preferredHeight, 180, availableHeight);
  }

  void _toggleQuickPanel() {
    setState(() {
      _showQuickPanel = !_showQuickPanel;
    });
    if (_showQuickPanel) {
      _requestQuickPanelSync('');
    } else {
      _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
    }
  }

  void _closeQuickPanel() {
    setState(() {
      _showQuickPanel = false;
    });
    _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  void _setQuickPanelTab(String tabId) {
    setState(() {
      _quickPanelTab = tabId;
      if (tabId == 'quick_commands') {
        _ensureQuickPanelFolderSelection();
      }
    });
  }

  void _fillQuickPanelInput(String value) {
    final normalized = _normalizeString(value);
    _quickPanelInputController.value = TextEditingValue(
      text: normalized,
      selection: TextSelection.collapsed(offset: normalized.length),
    );
    _quickPanelInputFocusNode.requestFocus();
  }

  void _addQuickPanelRecentInput(String value) {
    final normalized = _normalizeString(value).trim();
    if (normalized.isEmpty) return;

    final next = <String>[normalized];
    for (final item in _quickPanelRecentInputs) {
      if (item != normalized) {
        next.add(item);
      }
      if (next.length >= 18) {
        break;
      }
    }
    _quickPanelRecentInputs = next;
  }

  String _getQuickPanelTargetDeviceId() {
    return _normalizeString(ConnectionManager.instance.activeDeviceId);
  }

  void _requestQuickPanelSync(
    String explorerPath, {
    bool showLoading = true,
    bool isExpandRequest = false,
  }) {
    final deviceId = _getQuickPanelTargetDeviceId();
    if (deviceId.isEmpty) {
      if (showLoading && mounted) {
        setState(() {
          _quickPanelLoading = false;
          _quickPanelLoadError = _t('未找到目标设备', 'Target device not found');
        });
      }
      return;
    }

    if (showLoading && mounted) {
      setState(() {
        _quickPanelLoading = true;
        _quickPanelLoadError = '';
      });
    } else if (_quickPanelLoadError.isNotEmpty && mounted) {
      setState(() {
        _quickPanelLoadError = '';
      });
    } else {
      _quickPanelLoadError = '';
    }

    _quickPanelLastRequestPath = explorerPath;
    _quickPanelLastRequestIsExpand = isExpandRequest;
    ConnectionManager.instance.requestQuickPanelSync(
      deviceId,
      explorerPath: explorerPath,
    );
  }

  String _normalizeExplorerPathKey(String path) {
    return _normalizeString(path).trim().toLowerCase();
  }

  bool _areExplorerPathsEqual(String left, String right) {
    return _normalizeExplorerPathKey(left) == _normalizeExplorerPathKey(right);
  }

  bool _hasExplorerChildrenCache(String path) {
    final key = _normalizeExplorerPathKey(path);
    if (key.isEmpty) return false;
    return _quickPanelExplorerChildrenMap.containsKey(key);
  }

  List<_QuickPanelExplorerEntry> _getExplorerChildrenCache(String path) {
    final key = _normalizeExplorerPathKey(path);
    if (key.isEmpty) return const [];
    return _quickPanelExplorerChildrenMap[key] ?? const [];
  }

  List<_QuickPanelExplorerEntry> _extractExplorerChildren(
    List<_QuickPanelExplorerEntry> entries,
  ) {
    return entries.where((entry) => !entry.isParent).toList();
  }

  void _setExplorerChildrenCache(
    String path,
    List<_QuickPanelExplorerEntry> entries,
  ) {
    final key = _normalizeExplorerPathKey(path);
    if (key.isEmpty) return;
    _quickPanelExplorerChildrenMap[key] = _extractExplorerChildren(entries);
  }

  void _clearExplorerTreeCache() {
    _quickPanelExpandedPathKeys.clear();
    _quickPanelExplorerChildrenMap.clear();
  }

  bool _isExplorerPathExpanded(String path) {
    final key = _normalizeExplorerPathKey(path);
    if (key.isEmpty) return false;
    return _quickPanelExpandedPathKeys.contains(key);
  }

  void _setExplorerPathExpanded(String path, bool expanded) {
    final key = _normalizeExplorerPathKey(path);
    if (key.isEmpty) return;
    if (expanded) {
      _quickPanelExpandedPathKeys.add(key);
    } else {
      _quickPanelExpandedPathKeys.remove(key);
    }
  }

  List<_QuickPanelExplorerEntry> _parseQuickPanelExplorerEntries(
    Map<String, dynamic> data,
  ) {
    final rawEntries = _toObjectList(data['explorerEntries']);
    final entries = <_QuickPanelExplorerEntry>[];
    for (final item in rawEntries) {
      final fullPath = _normalizeString(item['fullPath']);
      if (fullPath.isEmpty) continue;
      entries.add(
        _QuickPanelExplorerEntry(
          name: _normalizeString(item['name'], fullPath),
          fullPath: fullPath,
          isDirectory: _toBool(item['isDirectory'], false),
          isParent: _toBool(item['isParent'], false),
        ),
      );
    }
    return entries;
  }

  List<_QuickPanelFolder> _parseQuickPanelFolders(Map<String, dynamic> data) {
    final rawFolders = _toObjectList(data['quickCommandFolders']);
    final folders = <_QuickPanelFolder>[];
    for (final item in rawFolders) {
      final id = _normalizeString(item['id']);
      final name = _normalizeString(item['name'], id);
      if (id.isEmpty || name.isEmpty) continue;
      folders.add(_QuickPanelFolder(id: id, name: name));
    }
    return folders;
  }

  List<_QuickPanelCommand> _parseQuickPanelCommands(Map<String, dynamic> data) {
    final rawCommands = _toObjectList(data['quickCommands']);
    final commands = <_QuickPanelCommand>[];
    for (final item in rawCommands) {
      final id = _normalizeString(item['id']);
      final folderId = _normalizeString(item['folderId']);
      final name = _normalizeString(item['name']);
      final commandText = _normalizeString(item['commandText']);
      final description = _normalizeString(item['description']);
      if (id.isEmpty || folderId.isEmpty || name.isEmpty) continue;
      commands.add(
        _QuickPanelCommand(
          id: id,
          folderId: folderId,
          name: name,
          commandText: commandText,
          description: description,
        ),
      );
    }
    return commands;
  }

  void _ensureQuickPanelFolderSelection() {
    if (_quickPanelFolders.isEmpty) {
      _quickPanelSelectedFolderId = '';
      return;
    }
    if (_quickPanelSelectedFolderId.isEmpty) {
      _quickPanelSelectedFolderId = _quickPanelFolders.first.id;
      return;
    }
    for (final folder in _quickPanelFolders) {
      if (folder.id == _quickPanelSelectedFolderId) {
        return;
      }
    }
    _quickPanelSelectedFolderId = _quickPanelFolders.first.id;
  }

  void _applyQuickPanelSync(Map<String, dynamic> data) {
    final explorerPath = _normalizeString(data['explorerPath']);
    final explorerEntries = _parseQuickPanelExplorerEntries(data);

    final matchesPendingExpandRequest =
        _quickPanelLastRequestIsExpand &&
        _quickPanelLastRequestPath.isNotEmpty &&
        _areExplorerPathsEqual(_quickPanelLastRequestPath, explorerPath);
    final looksLikeExpandedChildResponse =
        _isExplorerPathExpanded(explorerPath) &&
        !_areExplorerPathsEqual(explorerPath, _quickPanelExplorerPath);
    final isExpandResponse =
        matchesPendingExpandRequest || looksLikeExpandedChildResponse;

    if (isExpandResponse) {
      _setExplorerChildrenCache(explorerPath, explorerEntries);
    } else {
      _quickPanelExplorerPath = explorerPath;
      _quickPanelExplorerEntries = explorerEntries;
      _quickPanelSelectedExplorerPath = '';
      _clearExplorerTreeCache();
    }

    _quickPanelFolders = _parseQuickPanelFolders(data);
    _quickPanelCommands = _parseQuickPanelCommands(data);

    final recentInputs = <String>[];
    final recentRaw = data['recentInputs'];
    if (recentRaw is List) {
      for (final item in recentRaw) {
        if (item is String) {
          final normalized = item.trim();
          if (normalized.isNotEmpty) {
            recentInputs.add(normalized);
          }
        }
      }
    }
    _quickPanelRecentInputs = recentInputs;
    _quickPanelLastRequestPath = '';
    _quickPanelLastRequestIsExpand = false;
    _quickPanelLoading = false;
    _quickPanelLoadError = '';
    _ensureQuickPanelFolderSelection();
  }

  List<_QuickPanelExplorerEntry> _getQuickPanelExplorerRootEntries() {
    return _extractExplorerChildren(_quickPanelExplorerEntries);
  }

  void _appendExplorerTreeRows(
    List<_QuickPanelExplorerTreeRow> rows,
    _QuickPanelExplorerEntry entry,
    int depth,
  ) {
    final expanded =
        entry.isDirectory && _isExplorerPathExpanded(entry.fullPath);
    rows.add(
      _QuickPanelExplorerTreeRow(
        key: '${entry.fullPath}_$depth',
        name: entry.name,
        fullPath: entry.fullPath,
        isDirectory: entry.isDirectory,
        isParent: entry.isParent,
        canExpand: entry.isDirectory,
        expanded: expanded,
        depth: depth,
      ),
    );

    if (!expanded || !entry.isDirectory) return;

    final children = _getExplorerChildrenCache(entry.fullPath);
    for (final child in children) {
      _appendExplorerTreeRows(rows, child, depth + 1);
    }
  }

  List<_QuickPanelExplorerTreeRow> _getQuickPanelExplorerTreeRows() {
    final rows = <_QuickPanelExplorerTreeRow>[];
    final roots = _getQuickPanelExplorerRootEntries();
    for (final root in roots) {
      _appendExplorerTreeRows(rows, root, 0);
    }
    return rows;
  }

  void _onExplorerTreeRowTap(_QuickPanelExplorerTreeRow row) {
    setState(() {
      _quickPanelSelectedExplorerPath = row.fullPath;
    });
  }

  void _onExplorerDisclosureTap(_QuickPanelExplorerTreeRow row) {
    setState(() {
      _quickPanelSelectedExplorerPath = row.fullPath;
    });
    _toggleExplorerDirectory(row.fullPath);
  }

  bool _isExplorerRowSelected(_QuickPanelExplorerTreeRow row) {
    return _areExplorerPathsEqual(
      _quickPanelSelectedExplorerPath,
      row.fullPath,
    );
  }

  void _copySelectedExplorerPathToInput() {
    final selected = _normalizeString(_quickPanelSelectedExplorerPath).trim();
    if (selected.isEmpty) return;
    _fillQuickPanelInput(selected);
  }

  void _toggleExplorerDirectory(String path) {
    if (path.isEmpty) return;
    if (_isExplorerPathExpanded(path)) {
      setState(() {
        _setExplorerPathExpanded(path, false);
      });
      return;
    }

    setState(() {
      _setExplorerPathExpanded(path, true);
    });
    if (!_hasExplorerChildrenCache(path)) {
      _requestQuickPanelSync(path, showLoading: false, isExpandRequest: true);
    }
  }

  void _openExplorerRoot() {
    _requestQuickPanelSync(_quickPanelRootLabel);
  }

  void _navigateExplorerUp() {
    for (final entry in _quickPanelExplorerEntries) {
      if (entry.isParent && entry.isDirectory) {
        _requestQuickPanelSync(entry.fullPath);
        return;
      }
    }
    _openExplorerRoot();
  }

  List<_QuickPanelCommand> _getVisibleQuickCommands() {
    if (_quickPanelSelectedFolderId.isEmpty) {
      return _quickPanelCommands;
    }
    return _quickPanelCommands
        .where((command) => command.folderId == _quickPanelSelectedFolderId)
        .toList();
  }

  void _sendQuickPanelInput() {
    final raw = _quickPanelInputController.text;
    final commandText = raw.replaceAll('\r', '').replaceAll('\n', '');
    if (commandText.trim().isEmpty) return;

    final targetSessionId = _normalizeString(
      ConnectionManager.instance.activeSessionId,
    );
    if (_startsWithSlashCommand(commandText)) {
      for (var i = 0; i < commandText.length; i++) {
        final currentChar = commandText.substring(i, i + 1);
        final charDelayMs = i * _quickPanelSlashCharDelayMs;
        _scheduleQuickPanelPendingTimer(charDelayMs, () {
          ConnectionManager.instance.sendTerminalInput(
            currentChar,
            targetSessionId: targetSessionId,
          );
        });
      }
      final submitDelayMs =
          (commandText.length * _quickPanelSlashCharDelayMs) +
          _quickPanelSlashSubmitDelayMs;
      _scheduleQuickPanelSubmitEnter(targetSessionId, submitDelayMs);
    } else {
      ConnectionManager.instance.sendTerminalInput(
        commandText,
        targetSessionId: targetSessionId,
      );
      _scheduleQuickPanelSubmitEnter(targetSessionId, _quickPanelSubmitDelayMs);
    }

    final deviceId = _getQuickPanelTargetDeviceId();
    if (deviceId.isNotEmpty) {
      ConnectionManager.instance.appendQuickPanelRecentInput(
        deviceId,
        commandText,
      );
    }
    setState(() {
      _addQuickPanelRecentInput(commandText);
      _showQuickPanel = false;
    });
    _quickPanelInputController.clear();
    _quickPanelInputFocusNode.unfocus();
    _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  void _goBack() {
    Navigator.of(context).pop();
  }

  String _getActiveSessionWindowTitle() {
    final sessionId = _viewedSessionId.isNotEmpty
        ? _viewedSessionId
        : ConnectionManager.instance.activeSessionId;
    if (sessionId.isEmpty) {
      return _t('终端会话', 'Terminal Session');
    }
    final sessionTitle = ConnectionManager.instance
        .getSessionTitle(sessionId)
        .trim();
    if (sessionTitle.isNotEmpty) {
      return sessionTitle;
    }
    return sessionId;
  }

  @override
  Widget build(BuildContext context) {
    final media = MediaQuery.of(context);
    final padding = media.padding;
    final keyboardHeight = media.viewInsets.bottom;
    final windowHeight = media.size.height;
    final shortcutOffset = _getShortcutBarOffset(padding, keyboardHeight);

    return Scaffold(
      resizeToAvoidBottomInset: false,
      backgroundColor: const Color(AppColors.terminalBg),
      body: Stack(
        children: [
          Padding(
            padding: EdgeInsets.only(top: padding.top, bottom: padding.bottom),
            child: Column(
              children: [
                _buildHeaderBar(padding),
                _buildTerminalViewport(padding),
                if (!_showQuickPanel)
                  _buildCollapsedShortcutBar(padding, shortcutOffset),
              ],
            ),
          ),
          if (_showQuickPanel)
            _buildQuickPanelOverlay(padding, keyboardHeight, windowHeight),
          if (showSessionClosedDialog) _sessionClosedDialog(padding),
        ],
      ),
    );
  }

  Widget _buildHeaderBar(EdgeInsets padding) {
    return PhoneShellHeaderBar(
      subtitle: _t('无缝切换你的终端会话', 'Seamless terminal session switch'),
      height: 54,
      padding: EdgeInsets.fromLTRB(
        AppSizes.paddingPage + padding.left,
        0,
        AppSizes.paddingPage + padding.right,
        0,
      ),
      trailing: PhoneShellInfoChip(
        text: _getActiveSessionWindowTitle(),
        fontSize: 14,
      ),
    );
  }

  Widget _buildTerminalViewport(EdgeInsets padding) {
    return Expanded(
      child: Padding(
        padding: EdgeInsets.fromLTRB(8 + padding.left, 8, 8 + padding.right, 8),
        child: Container(
          decoration: BoxDecoration(
            color: const Color(AppColors.terminalBg),
            borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
            border: Border.all(color: const Color(AppColors.cardBorder)),
          ),
          child: Stack(
            children: [
              WebViewWidget(controller: _controller),
              if (!terminalReady || historyLoadingVisible)
                Container(
                  color: const Color(AppColors.terminalBg),
                  alignment: Alignment.center,
                  child: Text(
                    !terminalReady
                        ? terminalHint
                        : _t('内容较多，加载中，请等待', 'Loading history, please wait'),
                    style: const TextStyle(
                      fontSize: AppSizes.fontSizeBody,
                      color: Color(AppColors.textSecondary),
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _buildCollapsedShortcutBar(EdgeInsets padding, double shortcutOffset) {
    return Container(
      height: 96,
      margin: EdgeInsets.only(bottom: shortcutOffset),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Column(
        children: [
          Padding(
            padding: EdgeInsets.fromLTRB(
              12 + padding.left,
              6,
              12 + padding.right,
              4,
            ),
            child: Row(
              children: [
                const Expanded(child: SizedBox()),
                SizedBox(
                  width: 116,
                  height: 34,
                  child: ElevatedButton(
                    onPressed: _toggleQuickPanel,
                    style: ElevatedButton.styleFrom(
                      backgroundColor: const Color(AppColors.accent),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(
                          AppSizes.borderRadiusButton,
                        ),
                      ),
                      padding: EdgeInsets.zero,
                    ),
                    child: Text(
                      _t('快捷面板', 'Quick Panel'),
                      style: const TextStyle(fontSize: 12, color: Colors.white),
                    ),
                  ),
                ),
                const Expanded(child: SizedBox()),
                Row(
                  children: [
                    _buildShortcutButton('↑', '\x1B[A', 44),
                    const SizedBox(width: 6),
                    SizedBox(
                      height: 32,
                      child: OutlinedButton(
                        onPressed: _onTopRowSendKey,
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(
                            color: Color(AppColors.cardBorder),
                          ),
                          backgroundColor: const Color(AppColors.highlight),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusTag,
                            ),
                          ),
                          padding: const EdgeInsets.symmetric(horizontal: 10),
                        ),
                        child: Text(
                          _t('发送', 'Send'),
                          style: const TextStyle(
                            fontSize: 12,
                            color: Color(AppColors.textPrimary),
                          ),
                        ),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          Padding(
            padding: EdgeInsets.fromLTRB(
              12 + padding.left,
              0,
              12 + padding.right,
              8,
            ),
            child: Row(
              children: [
                Row(
                  children: _row2ShortcutKeys.map((key) {
                    return Padding(
                      padding: const EdgeInsets.only(right: 6),
                      child: _buildShortcutButton(key.label, key.data, 56),
                    );
                  }).toList(),
                ),
                const Spacer(),
                Row(
                  children: [
                    _buildShortcutButton('←', '\x1B[D', 44),
                    const SizedBox(width: 6),
                    _buildShortcutButton('↓', '\x1B[B', 44),
                    const SizedBox(width: 6),
                    _buildShortcutButton('→', '\x1B[C', 44),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildShortcutButton(String label, String data, double minWidth) {
    return SizedBox(
      height: 32,
      width: minWidth,
      child: OutlinedButton(
        onPressed: () => _sendShortcutKey(data),
        style: OutlinedButton.styleFrom(
          side: const BorderSide(color: Color(AppColors.cardBorder)),
          backgroundColor: const Color(AppColors.highlight),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 6),
        ),
        child: Text(
          label,
          style: const TextStyle(
            fontSize: 12,
            color: Color(AppColors.textPrimary),
          ),
        ),
      ),
    );
  }

  Widget _buildQuickPanelOverlay(
    EdgeInsets padding,
    double keyboardHeight,
    double windowHeight,
  ) {
    final sheetBottomOffset = _getQuickPanelSheetBottomOffset(
      padding,
      keyboardHeight,
    );
    final sheetHeight = _getQuickPanelSheetHeight(
      windowHeight,
      padding,
      keyboardHeight,
    );
    final clampedSheetHeight = math.max(180.0, sheetHeight);

    return Positioned.fill(
      child: Padding(
        padding: EdgeInsets.only(top: padding.top),
        child: Column(
          children: [
            Expanded(
              child: GestureDetector(
                onTap: _closeQuickPanel,
                behavior: HitTestBehavior.opaque,
                child: Container(color: const Color(0x70000000)),
              ),
            ),
            AnimatedContainer(
              duration: const Duration(milliseconds: 120),
              curve: Curves.easeOut,
              height: clampedSheetHeight,
              width: double.infinity,
              margin: EdgeInsets.fromLTRB(
                12 + padding.left,
                0,
                12 + padding.right,
                sheetBottomOffset,
              ),
              padding: const EdgeInsets.fromLTRB(10, 10, 10, 10),
              decoration: BoxDecoration(
                color: const Color(AppColors.surface2),
                borderRadius: BorderRadius.circular(16),
                border: Border.all(color: const Color(AppColors.cardBorder)),
              ),
              child: Column(
                children: [
                  Container(
                    width: 36,
                    height: 4,
                    margin: const EdgeInsets.only(bottom: 10),
                    decoration: BoxDecoration(
                      color: const Color(AppColors.textMuted),
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                  Row(
                    children: _quickPanelTabs.map((tab) {
                      return Expanded(
                        child: Padding(
                          padding: const EdgeInsets.symmetric(horizontal: 4),
                          child: _buildQuickPanelTabButton(tab),
                        ),
                      );
                    }).toList(),
                  ),
                  const SizedBox(height: 10),
                  Expanded(child: _buildQuickPanelContent()),
                  const SizedBox(height: 10),
                  Row(
                    children: [
                      SizedBox(
                        width: 44,
                        height: 40,
                        child: OutlinedButton(
                          onPressed: _closeQuickPanel,
                          style: OutlinedButton.styleFrom(
                            side: const BorderSide(
                              color: Color(AppColors.cardBorder),
                            ),
                            backgroundColor: const Color(AppColors.highlight),
                            shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(
                                AppSizes.borderRadiusButton,
                              ),
                            ),
                            padding: EdgeInsets.zero,
                          ),
                          child: const Text(
                            '⌄',
                            style: TextStyle(
                              fontSize: 16,
                              color: Color(AppColors.textPrimary),
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: TextField(
                          controller: _quickPanelInputController,
                          focusNode: _quickPanelInputFocusNode,
                          maxLines: 1,
                          textInputAction: TextInputAction.send,
                          onSubmitted: (_) => _sendQuickPanelInput(),
                          style: const TextStyle(
                            fontSize: AppSizes.fontSizeBody,
                            color: Color(AppColors.textPrimary),
                          ),
                          decoration: InputDecoration(
                            hintText: _t('输入命令并发送运行', 'Type command to run'),
                            hintStyle: const TextStyle(
                              fontSize: 14,
                              color: Color(AppColors.textMuted),
                            ),
                            filled: true,
                            fillColor: const Color(AppColors.inputBg),
                            contentPadding: const EdgeInsets.symmetric(
                              horizontal: 12,
                              vertical: 10,
                            ),
                            border: OutlineInputBorder(
                              borderRadius: BorderRadius.circular(
                                AppSizes.borderRadiusInput,
                              ),
                              borderSide: const BorderSide(
                                color: Color(AppColors.cardBorder),
                              ),
                            ),
                            enabledBorder: OutlineInputBorder(
                              borderRadius: BorderRadius.circular(
                                AppSizes.borderRadiusInput,
                              ),
                              borderSide: const BorderSide(
                                color: Color(AppColors.cardBorder),
                              ),
                            ),
                            focusedBorder: OutlineInputBorder(
                              borderRadius: BorderRadius.circular(
                                AppSizes.borderRadiusInput,
                              ),
                              borderSide: const BorderSide(
                                color: Color(AppColors.accent),
                              ),
                            ),
                          ),
                        ),
                      ),
                      const SizedBox(width: 8),
                      SizedBox(
                        width: 64,
                        height: 40,
                        child: ElevatedButton(
                          onPressed: _sendQuickPanelInput,
                          style: ElevatedButton.styleFrom(
                            backgroundColor: const Color(AppColors.accent),
                            shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(
                                AppSizes.borderRadiusButton,
                              ),
                            ),
                            padding: EdgeInsets.zero,
                          ),
                          child: Text(
                            _t('发送', 'Send'),
                            style: const TextStyle(
                              fontSize: 14,
                              color: Colors.white,
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildQuickPanelTabButton(_QuickPanelTabItem tab) {
    final selected = _quickPanelTab == tab.id;
    return SizedBox(
      height: 34,
      child: OutlinedButton(
        onPressed: () => _setQuickPanelTab(tab.id),
        style: OutlinedButton.styleFrom(
          side: BorderSide(
            color: Color(selected ? AppColors.accent : AppColors.cardBorder),
          ),
          backgroundColor: Color(
            selected ? AppColors.accent : AppColors.highlight,
          ),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 8),
        ),
        child: Text(
          _t(tab.zhLabel, tab.enLabel),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: TextStyle(
            fontSize: 12,
            color: selected
                ? Colors.white
                : const Color(AppColors.textSecondary),
          ),
        ),
      ),
    );
  }

  Widget _buildQuickPanelContent() {
    if (_quickPanelLoading) {
      return Center(
        child: Text(
          _t('同步快捷面板中...', 'Syncing quick panel...'),
          style: const TextStyle(
            fontSize: 13,
            color: Color(AppColors.textSecondary),
          ),
        ),
      );
    }
    if (_quickPanelLoadError.isNotEmpty) {
      return Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              _quickPanelLoadError,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 13,
                color: Color(AppColors.errorRed),
              ),
            ),
            const SizedBox(height: 10),
            SizedBox(
              height: 34,
              child: ElevatedButton(
                onPressed: () =>
                    _requestQuickPanelSync(_quickPanelExplorerPath),
                style: ElevatedButton.styleFrom(
                  backgroundColor: const Color(AppColors.accent),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(
                      AppSizes.borderRadiusButton,
                    ),
                  ),
                ),
                child: Text(
                  _t('重试', 'Retry'),
                  style: const TextStyle(fontSize: 12, color: Colors.white),
                ),
              ),
            ),
          ],
        ),
      );
    }
    if (_quickPanelTab == 'explorer') {
      return _buildQuickPanelExplorerTab();
    }
    if (_quickPanelTab == 'quick_commands') {
      return _buildQuickPanelQuickCommandsTab();
    }
    return _buildQuickPanelRecentInputsTab();
  }

  Widget _buildQuickPanelExplorerTab() {
    final rows = _getQuickPanelExplorerTreeRows();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            _buildExplorerActionButton(
              label: '⟲',
              onPressed: () => _requestQuickPanelSync(_quickPanelExplorerPath),
              width: 40,
            ),
            const SizedBox(width: 8),
            _buildExplorerActionButton(
              label: '↑',
              onPressed: _navigateExplorerUp,
              width: 40,
            ),
            const SizedBox(width: 8),
            _buildExplorerActionButton(
              label: _t('根目录', 'Root'),
              onPressed: _openExplorerRoot,
              width: 72,
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                _quickPanelExplorerPath,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontSize: 11,
                  color: Color(AppColors.textMuted),
                  fontFamily: 'monospace',
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
        Expanded(
          child: rows.isEmpty
              ? Center(
                  child: Text(
                    _t('目录为空', 'Folder is empty'),
                    style: const TextStyle(
                      fontSize: 13,
                      color: Color(AppColors.textSecondary),
                    ),
                  ),
                )
              : ListView.separated(
                  itemCount: rows.length,
                  separatorBuilder: (_, __) => const SizedBox(height: 8),
                  itemBuilder: (context, index) =>
                      _buildQuickPanelExplorerRow(rows[index]),
                ),
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            Expanded(
              child: Text(
                _quickPanelSelectedExplorerPath.isNotEmpty
                    ? _quickPanelSelectedExplorerPath
                    : _t('请选择文件或文件夹', 'Select a file or folder'),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontSize: 11,
                  color: Color(
                    _quickPanelSelectedExplorerPath.isNotEmpty
                        ? AppColors.textMuted
                        : AppColors.textSecondary,
                  ),
                  fontFamily: 'monospace',
                ),
              ),
            ),
            const SizedBox(width: 8),
            SizedBox(
              height: 30,
              child: ElevatedButton(
                onPressed: _quickPanelSelectedExplorerPath.isNotEmpty
                    ? _copySelectedExplorerPathToInput
                    : null,
                style: ElevatedButton.styleFrom(
                  backgroundColor: _quickPanelSelectedExplorerPath.isNotEmpty
                      ? const Color(AppColors.accent)
                      : const Color(AppColors.cardBorder),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(
                      AppSizes.borderRadiusTag,
                    ),
                  ),
                ),
                child: Text(
                  _t('复制路径', 'Copy Path'),
                  style: const TextStyle(fontSize: 12, color: Colors.white),
                ),
              ),
            ),
          ],
        ),
      ],
    );
  }

  Widget _buildExplorerActionButton({
    required String label,
    required VoidCallback onPressed,
    required double width,
  }) {
    return SizedBox(
      width: width,
      height: 30,
      child: OutlinedButton(
        onPressed: onPressed,
        style: OutlinedButton.styleFrom(
          side: const BorderSide(color: Color(AppColors.cardBorder)),
          backgroundColor: const Color(AppColors.highlight),
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 8),
        ),
        child: Text(
          label,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(
            fontSize: 12,
            color: Color(AppColors.textPrimary),
          ),
        ),
      ),
    );
  }

  Widget _buildQuickPanelExplorerRow(_QuickPanelExplorerTreeRow row) {
    final selected = _isExplorerRowSelected(row);
    return InkWell(
      onTap: () => _onExplorerTreeRowTap(row),
      borderRadius: BorderRadius.circular(6),
      child: Container(
        height: 34,
        padding: const EdgeInsets.symmetric(horizontal: 8),
        decoration: BoxDecoration(
          color: Color(selected ? AppColors.highlight : AppColors.surface1),
          borderRadius: BorderRadius.circular(6),
          border: Border.all(color: const Color(AppColors.cardBorder)),
        ),
        child: Row(
          children: [
            SizedBox(width: 12.0 * row.depth),
            if (row.canExpand)
              GestureDetector(
                onTap: () => _onExplorerDisclosureTap(row),
                behavior: HitTestBehavior.opaque,
                child: SizedBox(
                  width: 16,
                  child: Icon(
                    row.expanded ? Icons.expand_more : Icons.chevron_right,
                    size: 14,
                    color: const Color(AppColors.textMuted),
                  ),
                ),
              )
            else
              const SizedBox(width: 16),
            const SizedBox(width: 4),
            Icon(
              row.isDirectory
                  ? (row.expanded
                        ? Icons.folder_open_rounded
                        : Icons.folder_rounded)
                  : Icons.description_outlined,
              size: 16,
              color: const Color(AppColors.textSecondary),
            ),
            const SizedBox(width: 6),
            Expanded(
              child: Text(
                row.name,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontSize: 13,
                  color: Color(AppColors.textPrimary),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildQuickPanelQuickCommandsTab() {
    final commands = _getVisibleQuickCommands();
    return Column(
      children: [
        if (_quickPanelFolders.isNotEmpty)
          SizedBox(
            height: 36,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              itemCount: _quickPanelFolders.length,
              separatorBuilder: (_, __) => const SizedBox(width: 8),
              itemBuilder: (context, index) {
                final folder = _quickPanelFolders[index];
                final selected = folder.id == _quickPanelSelectedFolderId;
                return OutlinedButton(
                  onPressed: () {
                    setState(() {
                      _quickPanelSelectedFolderId = folder.id;
                    });
                  },
                  style: OutlinedButton.styleFrom(
                    side: BorderSide(
                      color: Color(
                        selected ? AppColors.accent : AppColors.cardBorder,
                      ),
                    ),
                    backgroundColor: Color(
                      selected ? AppColors.accent : AppColors.highlight,
                    ),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(
                        AppSizes.borderRadiusTag,
                      ),
                    ),
                    padding: const EdgeInsets.symmetric(horizontal: 10),
                  ),
                  child: Text(
                    folder.name,
                    style: TextStyle(
                      fontSize: 12,
                      color: selected
                          ? Colors.white
                          : const Color(AppColors.textSecondary),
                    ),
                  ),
                );
              },
            ),
          ),
        if (_quickPanelFolders.isNotEmpty) const SizedBox(height: 8),
        Expanded(
          child: commands.isEmpty
              ? Center(
                  child: Text(
                    _t('该目录暂无快捷指令', 'No quick commands in this folder'),
                    style: const TextStyle(
                      fontSize: 13,
                      color: Color(AppColors.textSecondary),
                    ),
                  ),
                )
              : ListView.separated(
                  itemCount: commands.length,
                  separatorBuilder: (_, __) => const SizedBox(height: 8),
                  itemBuilder: (context, index) =>
                      _buildQuickPanelCommandRow(commands[index]),
                ),
        ),
      ],
    );
  }

  Widget _buildQuickPanelCommandRow(_QuickPanelCommand command) {
    return Container(
      padding: const EdgeInsets.fromLTRB(10, 8, 10, 8),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  command.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 13,
                    color: Color(AppColors.textPrimary),
                  ),
                ),
                const SizedBox(height: 3),
                Text(
                  command.commandText,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 11,
                    color: Color(AppColors.textMuted),
                    fontFamily: 'monospace',
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: 8),
          SizedBox(
            height: 30,
            child: OutlinedButton(
              onPressed: () => _fillQuickPanelInput(command.commandText),
              style: OutlinedButton.styleFrom(
                side: const BorderSide(color: Color(AppColors.cardBorder)),
                backgroundColor: const Color(AppColors.highlight),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                ),
                padding: const EdgeInsets.symmetric(horizontal: 12),
              ),
              child: Text(
                _t('插入', 'Insert'),
                style: const TextStyle(
                  fontSize: 12,
                  color: Color(AppColors.textPrimary),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildQuickPanelRecentInputsTab() {
    return _quickPanelRecentInputs.isEmpty
        ? Center(
            child: Text(
              _t('暂无历史指令', 'No recent inputs'),
              style: const TextStyle(
                fontSize: 13,
                color: Color(AppColors.textSecondary),
              ),
            ),
          )
        : ListView.separated(
            itemCount: _quickPanelRecentInputs.length,
            separatorBuilder: (_, __) => const SizedBox(height: 8),
            itemBuilder: (context, index) =>
                _buildQuickPanelRecentRow(_quickPanelRecentInputs[index]),
          );
  }

  Widget _buildQuickPanelRecentRow(String input) {
    return Container(
      padding: const EdgeInsets.fromLTRB(10, 8, 10, 8),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Row(
        children: [
          Expanded(
            child: Text(
              input,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontSize: 13,
                color: Color(AppColors.textPrimary),
                fontFamily: 'monospace',
              ),
            ),
          ),
          const SizedBox(width: 8),
          SizedBox(
            height: 30,
            child: OutlinedButton(
              onPressed: () => _fillQuickPanelInput(input),
              style: OutlinedButton.styleFrom(
                side: const BorderSide(color: Color(AppColors.cardBorder)),
                backgroundColor: const Color(AppColors.highlight),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                ),
                padding: const EdgeInsets.symmetric(horizontal: 12),
              ),
              child: Text(
                _t('插入', 'Insert'),
                style: const TextStyle(
                  fontSize: 12,
                  color: Color(AppColors.textPrimary),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _sessionClosedDialog(EdgeInsets padding) {
    return Positioned.fill(
      child: Container(
        color: const Color(0xB0000000),
        padding: EdgeInsets.symmetric(
          horizontal: 24 + padding.left,
          vertical: 24,
        ),
        child: Center(
          child: Container(
            padding: const EdgeInsets.all(24),
            decoration: BoxDecoration(
              color: const Color(AppColors.surface2),
              borderRadius: BorderRadius.circular(16),
              border: Border.all(color: const Color(AppColors.cardBorder)),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  _t('PC 端已关闭当前会话', 'Session closed on PC'),
                  style: const TextStyle(
                    fontSize: 18,
                    color: Color(AppColors.textPrimary),
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 10),
                Text(
                  _t(
                    '点击下方按钮返回会话列表。',
                    'Tap the button below to return to the session list.',
                  ),
                  style: const TextStyle(
                    fontSize: 13,
                    color: Color(AppColors.textSecondary),
                  ),
                ),
                const SizedBox(height: 18),
                SizedBox(
                  width: 180,
                  height: 42,
                  child: ElevatedButton(
                    onPressed: _goBack,
                    style: ElevatedButton.styleFrom(
                      backgroundColor: const Color(AppColors.accent),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(
                          AppSizes.borderRadiusButton,
                        ),
                      ),
                    ),
                    child: Text(
                      _t('返回会话列表', 'Back to Sessions'),
                      style: const TextStyle(
                        fontSize: AppSizes.fontSizeBody,
                        color: Colors.white,
                      ),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _ShortcutKey {
  const _ShortcutKey({required this.label, required this.data});

  final String label;
  final String data;
}

class _QuickPanelTabItem {
  const _QuickPanelTabItem({
    required this.id,
    required this.zhLabel,
    required this.enLabel,
  });

  final String id;
  final String zhLabel;
  final String enLabel;
}

class _QuickPanelExplorerEntry {
  const _QuickPanelExplorerEntry({
    required this.name,
    required this.fullPath,
    required this.isDirectory,
    required this.isParent,
  });

  final String name;
  final String fullPath;
  final bool isDirectory;
  final bool isParent;
}

class _QuickPanelExplorerTreeRow {
  const _QuickPanelExplorerTreeRow({
    required this.key,
    required this.name,
    required this.fullPath,
    required this.isDirectory,
    required this.isParent,
    required this.canExpand,
    required this.expanded,
    required this.depth,
  });

  final String key;
  final String name;
  final String fullPath;
  final bool isDirectory;
  final bool isParent;
  final bool canExpand;
  final bool expanded;
  final int depth;
}

class _QuickPanelFolder {
  const _QuickPanelFolder({required this.id, required this.name});

  final String id;
  final String name;
}

class _QuickPanelCommand {
  const _QuickPanelCommand({
    required this.id,
    required this.folderId,
    required this.name,
    required this.commandText,
    required this.description,
  });

  final String id;
  final String folderId;
  final String name;
  final String commandText;
  final String description;
}
