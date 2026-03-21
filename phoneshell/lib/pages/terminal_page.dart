import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart' hide ConnectionState;
import 'package:webview_flutter/webview_flutter.dart';

import '../core/constants.dart';
import '../core/connection_manager.dart';
import '../core/i18n.dart';

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

  final List<Map<String, String>> _shortcutKeys = const [
    {'label': 'Ctrl+C', 'data': '\x03'},
    {'label': 'Ctrl+D', 'data': '\x04'},
    {'label': 'Ctrl+Z', 'data': '\x1A'},
    {'label': 'Tab', 'data': '\t'},
    {'label': 'Esc', 'data': '\x1B'},
    {'label': '↑', 'data': '\x1B[A'},
    {'label': '↓', 'data': '\x1B[B'},
    {'label': '←', 'data': '\x1B[D'},
    {'label': '→', 'data': '\x1B[C'},
    {'label': 'Home', 'data': '\x1B[H'},
    {'label': 'End', 'data': '\x1B[F'},
  ];

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
  }

  void _handleJsMessage(JavaScriptMessage message) {
    try {
      final decoded = jsonDecode(message.message);
      if (decoded is Map<String, dynamic>) {
        final type = decoded['type']?.toString() ?? '';
        final args = decoded['args'];
        if (type == 'onTerminalReady' && args is List && args.length >= 2) {
          _onTerminalReady(_toInt(args[0]), _toInt(args[1]));
        } else if (type == 'onTerminalResize' && args is List && args.length >= 2) {
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
      // Ignore parse errors
    }
  }

  int _toInt(dynamic value) {
    if (value is num) return value.toInt();
    return int.tryParse(value?.toString() ?? '') ?? 0;
  }

  void _activatePage() {
    connectionState = ConnectionManager.instance.getConnectionStateForDevice(ConnectionManager.instance.activeDeviceId);
    _viewedSessionId = ConnectionManager.instance.activeSessionId;
    sessionActive = _viewedSessionId.isNotEmpty;
    _historyPlanned = sessionActive && _viewedSessionId.isNotEmpty;

    if (_stateListenerId == -1) {
      _stateListenerId = ConnectionManager.instance.addOnStateChange((state, _) {
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
      _messageListenerId = ConnectionManager.instance.addOnMessage((type, data) {
        if (type == 'terminal.output') {
          if (_isCurrentSessionMessage(data)) {
            final output = data['data']?.toString() ?? '';
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
              showSessionClosedDialog = true;
            });
          }
        } else if (type == 'error') {
          final errorMsg = data['message']?.toString() ?? 'Error';
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
  }

  void _deactivatePage() {
    _flushTimer?.cancel();
    _flushTimer = null;
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
    final output = data;
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
    _runTerminalScript('window.phoneShell && window.phoneShell.write($payload);');
  }

  void _runTerminalScript(String script) {
    _controller.runJavaScript(script);
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
    if (!sessionActive || _lastReportedCols <= 0 || _lastReportedRows <= 0) return;
    ConnectionManager.instance.sendTerminalResize(_lastReportedCols, _lastReportedRows);
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
      historyLoadingVisible = terminalReady && _historyStarted && !_historyComplete && sessionActive;
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

    final chunk = data['data']?.toString() ?? '';
    final nextBeforeSeq = int.tryParse(data['nextBeforeSeq']?.toString() ?? '') ?? 0;
    final hasMore = data['hasMore'] == true || data['hasMore'] == 1 || data['hasMore'] == '1';

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
    final output = data;
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
    _runTerminalScript('window.phoneShell && (window.phoneShell.clear(), window.phoneShell.write($payload));');
  }

  bool _isCurrentSessionMessage(Map<String, dynamic> data) {
    final sessionId = data['sessionId']?.toString() ?? '';
    return sessionId.isNotEmpty && sessionId == _viewedSessionId;
  }

  void _sendShortcutKey(String data) {
    ConnectionManager.instance.sendTerminalInput(data);
    _runTerminalScript('window.phoneShell && window.phoneShell.focus();');
  }

  void _goBack() {
    Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final padding = MediaQuery.of(context).padding;
    final keyboardHeight = MediaQuery.of(context).viewInsets.bottom;
    final shortcutOffset = (keyboardHeight - padding.bottom).clamp(0.0, 9999.0);

    return Scaffold(
      resizeToAvoidBottomInset: false,
      backgroundColor: const Color(AppColors.terminalBg),
      body: Stack(
        children: [
          Padding(
            padding: EdgeInsets.only(top: padding.top, bottom: padding.bottom),
            child: Column(
              children: [
                Container(height: 2, color: const Color(AppColors.accent)),
                Container(
                  height: 54,
                  color: const Color(AppColors.surface1),
                  padding: EdgeInsets.symmetric(horizontal: AppSizes.paddingPage + padding.left),
                  child: Row(
                    children: [
                      GestureDetector(
                        onTap: _goBack,
                        child: Text(
                          _t('‹ 返回', '‹ Back'),
                          style: const TextStyle(fontSize: AppSizes.fontSizeSmall, color: Color(AppColors.accent)),
                        ),
                      ),
                      const SizedBox(width: 12),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            Text(
                              _t('终端', 'Terminal'),
                              style: const TextStyle(
                                fontSize: AppSizes.fontSizeBody,
                                color: Color(AppColors.textPrimary),
                                fontWeight: FontWeight.w500,
                              ),
                            ),
                            if (_viewedSessionId.isNotEmpty)
                              Text(
                                _viewedSessionId,
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: const TextStyle(fontSize: 11, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
                              ),
                          ],
                        ),
                      ),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                        decoration: BoxDecoration(
                          color: const Color(AppColors.chipBg),
                          borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                          border: Border.all(
                            color: Color(sessionActive ? AppColors.onlineGreen : AppColors.chipBorder),
                          ),
                        ),
                        child: Row(
                          children: [
                            Container(
                              width: 6,
                              height: 6,
                              margin: const EdgeInsets.only(right: 6),
                              decoration: BoxDecoration(
                                color: Color(sessionActive ? AppColors.onlineGreen : AppColors.offlineGray),
                                shape: BoxShape.circle,
                              ),
                            ),
                            Text(
                              sessionActive ? _t('在线', 'ACTIVE') : _t('离线', 'OFFLINE'),
                              style: TextStyle(
                                fontSize: 11,
                                color: Color(sessionActive ? AppColors.onlineGreen : AppColors.textMuted),
                                fontFamily: 'monospace',
                              ),
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
                Expanded(
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
                                style: const TextStyle(fontSize: AppSizes.fontSizeBody, color: Color(AppColors.textSecondary)),
                              ),
                            ),
                        ],
                      ),
                    ),
                  ),
                ),
                Container(
                  height: 74,
                  margin: EdgeInsets.only(bottom: shortcutOffset),
                  decoration: BoxDecoration(
                    color: const Color(AppColors.surface1),
                    border: Border.all(color: const Color(AppColors.cardBorder)),
                  ),
                  child: Column(
                    children: [
                      Padding(
                        padding: EdgeInsets.fromLTRB(12 + padding.left, 6, 12 + padding.right, 2),
                        child: Row(
                          children: [
                            Text(
                              _t('快捷键', 'SHORTCUT KEYS'),
                              style: const TextStyle(fontSize: 11, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
                            ),
                            const Spacer(),
                            Text(
                              _t('点击发送', 'TAP TO SEND'),
                              style: const TextStyle(fontSize: 11, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
                            ),
                          ],
                        ),
                      ),
                      Expanded(
                        child: SingleChildScrollView(
                          scrollDirection: Axis.horizontal,
                          padding: EdgeInsets.fromLTRB(12 + padding.left, 0, 12 + padding.right, 8),
                          child: Row(
                            children: _shortcutKeys.map((key) {
                              return Padding(
                                padding: const EdgeInsets.only(right: 8),
                                child: SizedBox(
                                  height: AppSizes.shortcutKeyHeight,
                                  child: OutlinedButton(
                                    onPressed: () => _sendShortcutKey(key['data'] ?? ''),
                                    style: OutlinedButton.styleFrom(
                                      side: const BorderSide(color: Color(AppColors.cardBorder)),
                                      backgroundColor: const Color(AppColors.highlight),
                                      shape: RoundedRectangleBorder(
                                        borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                                      ),
                                      padding: const EdgeInsets.symmetric(horizontal: 10),
                                    ),
                                    child: Text(
                                      key['label'] ?? '',
                                      style: const TextStyle(fontSize: 12, color: Color(AppColors.textPrimary)),
                                    ),
                                  ),
                                ),
                              );
                            }).toList(),
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
          if (showSessionClosedDialog) _sessionClosedDialog(padding),
        ],
      ),
    );
  }

  Widget _sessionClosedDialog(EdgeInsets padding) {
    return Positioned.fill(
      child: Container(
        color: const Color(0xB0000000),
        padding: EdgeInsets.symmetric(horizontal: 24 + padding.left, vertical: 24),
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
                  style: const TextStyle(fontSize: 18, color: Color(AppColors.textPrimary), fontWeight: FontWeight.w500),
                ),
                const SizedBox(height: 10),
                Text(
                  _t('点击下方按钮返回会话列表。', 'Tap the button below to return to the session list.'),
                  style: const TextStyle(fontSize: 13, color: Color(AppColors.textSecondary)),
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
                        borderRadius: BorderRadius.circular(AppSizes.borderRadiusButton),
                      ),
                    ),
                    child: Text(
                      _t('返回会话列表', 'Back to Sessions'),
                      style: const TextStyle(fontSize: AppSizes.fontSizeBody, color: Colors.white),
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
