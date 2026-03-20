import 'package:flutter/material.dart' hide ConnectionState;
import 'package:flutter_svg/flutter_svg.dart';

import '../core/constants.dart';
import '../core/connection_manager.dart';
import '../core/i18n.dart';
import '../core/models.dart';

class SessionListPage extends StatefulWidget {
  const SessionListPage({super.key});

  @override
  State<SessionListPage> createState() => _SessionListPageState();
}

class _SessionListPageState extends State<SessionListPage> {
  List<SessionItem> sessions = [];
  ConnectionState connectionState = ConnectionState.connected;
  bool isRefreshing = false;
  String deviceId = '';
  String displayName = '';
  List<String> availableShells = [];
  String selectedShell = '';

  bool showRenameDialog = false;
  String renameSessionId = '';
  String renameSessionTitle = '';
  bool showCloseDialog = false;
  String closeSessionId = '';
  String closeSessionTitle = '';

  int _stateListenerId = -1;
  int _messageListenerId = -1;
  int _pendingOpenRequestedAt = 0;
  final Map<String, String> _pendingRenameTitles = {};
  final Map<String, int> _pendingRenameTimes = {};
  final int _pendingRenameTtlMs = 10000;
  final TextEditingController _renameController = TextEditingController();
  bool _initialized = false;

  String _t(String zh, String en) => I18n.tCurrent(zh, en);

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (!_initialized) {
      final args = ModalRoute.of(context)?.settings.arguments;
      if (args is Map) {
        deviceId = args['deviceId']?.toString() ?? '';
        displayName = args['displayName']?.toString() ?? deviceId;
        final shells = args['availableShells'];
        if (shells is List) {
          availableShells = shells.map((e) => e.toString()).toList();
          if (availableShells.isNotEmpty) {
            selectedShell = availableShells.first;
          }
        }
      }
      _activatePage();
      _initialized = true;
    }
  }

  @override
  void dispose() {
    _deactivatePage();
    _renameController.dispose();
    super.dispose();
  }

  void _activatePage() {
    connectionState = ConnectionManager.instance.getConnectionStateForDevice(deviceId);

    if (_stateListenerId == -1) {
      _stateListenerId = ConnectionManager.instance.addOnStateChange((state, id) {
        if (id == deviceId || id == 'group') {
          setState(() {
            connectionState = state;
          });
          if (state == ConnectionState.disconnected) {
            Navigator.of(context).pop();
          } else if (state == ConnectionState.connected) {
            _refreshSessions();
          }
        }
      });
    }

    if (_messageListenerId == -1) {
      _messageListenerId = ConnectionManager.instance.addOnMessage((type, data) {
        if (type == 'session.list') {
          _handleSessionList(data);
        } else if (type == 'terminal.opened') {
          final sessionId = data['sessionId']?.toString() ?? '';
          final deviceIdValue = data['deviceId']?.toString() ?? deviceId;
          if (sessionId.isNotEmpty) {
            if (deviceIdValue == deviceId) {
              if (_isPendingOpen()) {
                _clearPendingOpen();
                ConnectionManager.instance.setActiveSession(deviceIdValue, sessionId);
                Navigator.of(context).pushNamed('pages/TerminalPage');
              } else {
                _refreshSessions();
              }
            }
          }
        } else if (type == 'error') {
          _clearPendingOpen();
          setState(() {
            isRefreshing = false;
          });
        }
      });
    }

    if (deviceId.isNotEmpty && connectionState == ConnectionState.connected) {
      setState(() {
        isRefreshing = true;
      });
      ConnectionManager.instance.requestSessionList(deviceId);
    }
  }

  void _deactivatePage() {
    if (_stateListenerId != -1) {
      ConnectionManager.instance.removeOnStateChange(_stateListenerId);
      _stateListenerId = -1;
    }
    if (_messageListenerId != -1) {
      ConnectionManager.instance.removeOnMessage(_messageListenerId);
      _messageListenerId = -1;
    }
    _clearPendingOpen();
  }

  void _recordPendingRename(String sessionId, String title) {
    if (sessionId.isEmpty || title.isEmpty) return;
    _pendingRenameTitles[sessionId] = title;
    _pendingRenameTimes[sessionId] = DateTime.now().millisecondsSinceEpoch;
  }

  String _resolveSessionTitle(String sessionId, String serverTitle) {
    final pending = _pendingRenameTitles[sessionId];
    if (pending == null) return serverTitle;
    final requestedAt = _pendingRenameTimes[sessionId] ?? 0;
    if (requestedAt > 0 && (DateTime.now().millisecondsSinceEpoch - requestedAt) > _pendingRenameTtlMs) {
      _pendingRenameTitles.remove(sessionId);
      _pendingRenameTimes.remove(sessionId);
      return serverTitle;
    }
    if (serverTitle.isNotEmpty && serverTitle == pending) {
      _pendingRenameTitles.remove(sessionId);
      _pendingRenameTimes.remove(sessionId);
      return serverTitle;
    }
    return pending;
  }

  void _handleSessionList(Map<String, dynamic> data) {
    final deviceIdValue = data['deviceId']?.toString() ?? '';
    if (deviceIdValue.isNotEmpty && deviceIdValue != deviceId) return;

    final sessionsRaw = data['sessions'];
    final result = <SessionItem>[];
    final existing = <String, SessionItem>{};
    for (final item in sessions) {
      if (item.sessionId.isNotEmpty) existing[item.sessionId] = item;
    }
    if (sessionsRaw is List) {
      for (var i = 0; i < sessionsRaw.length; i++) {
        final raw = sessionsRaw[i];
        if (raw is Map<String, dynamic>) {
          final sessionId = raw['sessionId']?.toString() ?? '';
          final shellId = raw['shellId']?.toString() ?? '';
          var title = raw['title']?.toString() ?? 'Session ${i + 1}';
          if (sessionId.isNotEmpty) {
            title = _resolveSessionTitle(sessionId, title);
            final model = existing[sessionId] ?? SessionItem();
            model.sessionId = sessionId;
            model.shellId = shellId;
            model.title = title;
            result.add(model);
          }
        }
      }
    }

    setState(() {
      sessions = result;
      isRefreshing = false;
    });
  }

  void _refreshSessions() {
    if (isRefreshing) return;
    if (deviceId.isEmpty || connectionState != ConnectionState.connected) {
      setState(() {
        isRefreshing = false;
      });
      return;
    }
    setState(() {
      isRefreshing = true;
    });
    ConnectionManager.instance.requestSessionList(deviceId);
  }

  void _onSessionClick(SessionItem session) {
    ConnectionManager.instance.setActiveSession(deviceId, session.sessionId);
    Navigator.of(context).pushNamed('pages/TerminalPage');
  }

  void _onNewSession() {
    if (deviceId.isEmpty || connectionState != ConnectionState.connected) return;
    _markPendingOpen();
    ConnectionManager.instance.openTerminal(deviceId, selectedShell);
  }

  void _openRenameDialog(SessionItem session) {
    renameSessionId = session.sessionId;
    renameSessionTitle = session.title;
    _renameController.text = session.title;
    setState(() {
      showRenameDialog = true;
    });
  }

  void _closeRenameDialog() {
    setState(() {
      showRenameDialog = false;
      renameSessionId = '';
      renameSessionTitle = '';
      _renameController.text = '';
    });
  }

  void _confirmRename() {
    final trimmed = _renameController.text.trim();
    if (trimmed.isEmpty || renameSessionId.isEmpty) {
      _closeRenameDialog();
      return;
    }
    final sessionId = renameSessionId;
    _recordPendingRename(sessionId, trimmed);
    ConnectionManager.instance.renameSession(deviceId, sessionId, trimmed);
    _updateSessionTitleLocal(sessionId, trimmed);
    _closeRenameDialog();
    _refreshSessions();
  }

  void _updateSessionTitleLocal(String sessionId, String title) {
    for (final item in sessions) {
      if (item.sessionId == sessionId) {
        item.title = title;
        break;
      }
    }
  }

  void _openCloseDialog(SessionItem session) {
    closeSessionId = session.sessionId;
    closeSessionTitle = session.title;
    setState(() {
      showCloseDialog = true;
    });
  }

  void _closeCloseDialog() {
    setState(() {
      showCloseDialog = false;
      closeSessionId = '';
      closeSessionTitle = '';
    });
  }

  void _confirmClose() {
    if (closeSessionId.isEmpty) {
      _closeCloseDialog();
      return;
    }
    ConnectionManager.instance.closeTerminalSession(closeSessionId);
    _removeSessionLocal(closeSessionId);
    _closeCloseDialog();
    _refreshSessions();
  }

  void _removeSessionLocal(String sessionId) {
    _pendingRenameTitles.remove(sessionId);
    _pendingRenameTimes.remove(sessionId);
    sessions = sessions.where((item) => item.sessionId != sessionId).toList();
  }

  void _markPendingOpen() {
    _pendingOpenRequestedAt = DateTime.now().millisecondsSinceEpoch;
  }

  void _clearPendingOpen() {
    _pendingOpenRequestedAt = 0;
  }

  bool _isPendingOpen() {
    if (_pendingOpenRequestedAt <= 0) return false;
    final age = DateTime.now().millisecondsSinceEpoch - _pendingOpenRequestedAt;
    if (age > 10000) {
      _clearPendingOpen();
      return false;
    }
    return true;
  }

  void _goBack() {
    Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final padding = MediaQuery.of(context).padding;
    return Scaffold(
      body: Stack(
        children: [
          Column(
            children: [
              Container(
                color: const Color(AppColors.surface1),
                child: Column(
                  children: [
                    Container(height: 2, color: const Color(AppColors.accent)),
                    SizedBox(
                      height: 56,
                      child: Padding(
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
                                    displayName,
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                    style: const TextStyle(
                                      fontSize: AppSizes.fontSizeBody,
                                      color: Color(AppColors.textPrimary),
                                      fontWeight: FontWeight.w500,
                                    ),
                                  ),
                                  Text(
                                    _t('会话列表', 'Session List'),
                                    style: const TextStyle(fontSize: 12, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
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
                                  color: Color(connectionState == ConnectionState.connected
                                      ? AppColors.onlineGreen
                                      : AppColors.chipBorder),
                                ),
                              ),
                              child: Row(
                                children: [
                                  Container(
                                    width: 6,
                                    height: 6,
                                    margin: const EdgeInsets.only(right: 6),
                                    decoration: BoxDecoration(
                                      color: Color(connectionState == ConnectionState.connected
                                          ? AppColors.onlineGreen
                                          : AppColors.offlineGray),
                                      shape: BoxShape.circle,
                                    ),
                                  ),
                                  Text(
                                    connectionState == ConnectionState.connected
                                        ? _t('在线', 'Online')
                                        : _t('断开', 'Disconnected'),
                                    style: TextStyle(
                                      fontSize: 11,
                                      color: Color(connectionState == ConnectionState.connected
                                          ? AppColors.onlineGreen
                                          : AppColors.textMuted),
                                    ),
                                  ),
                                ],
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              Divider(color: const Color(AppColors.divider), height: 1),
              Expanded(
                child: RefreshIndicator(
                  onRefresh: () async => _refreshSessions(),
                  child: ListView(
                    padding: EdgeInsets.fromLTRB(
                      AppSizes.paddingPage + padding.left,
                      12,
                      AppSizes.paddingPage + padding.right,
                      12,
                    ),
                    children: [
                      if (sessions.isEmpty)
                        SizedBox(
                          height: 260,
                          child: Column(
                            mainAxisAlignment: MainAxisAlignment.center,
                            children: [
                              Text(
                                _t('暂无活动会话', 'NO ACTIVE SESSIONS'),
                                style: const TextStyle(fontSize: 14, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
                              ),
                              const SizedBox(height: 8),
                              Text(
                                _t('当前没有活动会话', 'No active sessions at the moment'),
                                style: const TextStyle(fontSize: AppSizes.fontSizeBody, color: Color(AppColors.textSecondary)),
                              ),
                              const SizedBox(height: 6),
                              Text(
                                _t('下拉刷新列表', 'Pull down to refresh'),
                                style: const TextStyle(fontSize: 12, color: Color(AppColors.textMuted)),
                              ),
                            ],
                          ),
                        )
                      else
                        ...sessions.map((session) => Padding(
                              padding: const EdgeInsets.only(bottom: 12),
                              child: _sessionCard(session),
                            )),
                    ],
                  ),
                ),
              ),
              Divider(color: const Color(AppColors.divider), height: 1),
              Container(
                color: const Color(AppColors.surface2),
                padding: EdgeInsets.fromLTRB(
                  AppSizes.paddingPage + padding.left,
                  12,
                  AppSizes.paddingPage + padding.right,
                  16 + padding.bottom,
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Container(height: 2, color: const Color(AppColors.accentBlue), margin: const EdgeInsets.only(bottom: 10)),
                    Text(
                      _t('新建会话', 'NEW SESSION'),
                      style: const TextStyle(fontSize: 12, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
                    ),
                    const SizedBox(height: 8),
                    if (availableShells.length > 1)
                      Wrap(
                        spacing: 8,
                        runSpacing: 8,
                        children: availableShells.map((shell) {
                          final selected = selectedShell == shell;
                          return SizedBox(
                            height: 32,
                            child: OutlinedButton(
                              onPressed: () {
                                setState(() {
                                  selectedShell = shell;
                                });
                              },
                              style: OutlinedButton.styleFrom(
                                backgroundColor: selected ? const Color(AppColors.accent) : const Color(AppColors.highlight),
                                side: BorderSide(color: selected ? const Color(AppColors.accent) : const Color(AppColors.accentDim)),
                                shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                                ),
                                padding: const EdgeInsets.symmetric(horizontal: 12),
                              ),
                              child: Text(
                                shell,
                                style: TextStyle(fontSize: 12, color: selected ? Colors.white : const Color(AppColors.accent)),
                              ),
                            ),
                          );
                        }).toList(),
                      ),
                    if (availableShells.length > 1) const SizedBox(height: 12),
                    SizedBox(
                      width: double.infinity,
                      height: 44,
                      child: ElevatedButton(
                        onPressed: _onNewSession,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: const Color(AppColors.accent),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(AppSizes.borderRadiusButton),
                          ),
                        ),
                        child: Text(
                          _t('新建会话', 'New Session'),
                          style: const TextStyle(fontSize: AppSizes.fontSizeBody, color: Colors.white),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
          if (showRenameDialog) _renameDialog(padding),
          if (showCloseDialog) _closeConfirmDialog(padding),
        ],
      ),
    );
  }

  Widget _sessionCard(SessionItem session) {
    return GestureDetector(
      onTap: () => _onSessionClick(session),
      onLongPress: () => _openRenameDialog(session),
      child: Container(
        padding: const EdgeInsets.all(AppSizes.paddingCard),
        decoration: BoxDecoration(
          color: const Color(AppColors.cardBackground),
          borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
          border: Border.all(color: const Color(AppColors.cardBorder)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(height: 2, color: const Color(AppColors.accent), margin: const EdgeInsets.only(bottom: 12)),
            Row(
              children: [
                Container(
                  width: 32,
                  height: 32,
                  decoration: const BoxDecoration(color: Color(AppColors.surface3), shape: BoxShape.circle),
                  alignment: Alignment.center,
                  child: const Text('>', style: TextStyle(fontSize: 16, color: Color(AppColors.accent), fontFamily: 'monospace', fontWeight: FontWeight.bold)),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        session.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          fontSize: AppSizes.fontSizeBody,
                          color: Color(AppColors.textPrimary),
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        session.shellId,
                        style: const TextStyle(fontSize: AppSizes.fontSizeSmall, color: Color(AppColors.textSecondary)),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        'ID ${session.sessionId}',
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontSize: 11, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
                      ),
                    ],
                  ),
                ),
                Column(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    SizedBox(
                      width: 24,
                      height: 24,
                      child: OutlinedButton(
                        onPressed: () => _openCloseDialog(session),
                        style: OutlinedButton.styleFrom(
                          padding: EdgeInsets.zero,
                          side: const BorderSide(color: Color(AppColors.errorRed)),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6)),
                        ),
                        child: SvgPicture.asset('assets/images/ic_close.svg', width: 12, height: 12),
                      ),
                    ),
                    const SizedBox(height: 8),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                      decoration: BoxDecoration(
                        color: const Color(AppColors.chipBg),
                        borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                        border: Border.all(color: const Color(AppColors.onlineGreen)),
                      ),
                      child: Row(
                        children: const [
                          SizedBox(width: 6, height: 6, child: DecoratedBox(decoration: BoxDecoration(color: Color(AppColors.onlineGreen), shape: BoxShape.circle))),
                          SizedBox(width: 6),
                          Text('ACTIVE', style: TextStyle(fontSize: 10, color: Color(AppColors.onlineGreen), fontFamily: 'monospace')),
                        ],
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _renameDialog(EdgeInsets padding) {
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
                  _t('重命名会话', 'Rename Session'),
                  style: const TextStyle(fontSize: 18, color: Color(AppColors.textPrimary), fontWeight: FontWeight.w500),
                ),
                const SizedBox(height: 8),
                if (renameSessionTitle.isNotEmpty)
                  Text(
                    renameSessionTitle,
                    style: const TextStyle(fontSize: 12, color: Color(AppColors.textMuted), fontFamily: 'monospace'),
                  ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    const SizedBox(
                      width: 48,
                      child: Text('名称:', style: TextStyle(fontSize: 12, color: Color(AppColors.textSecondary))),
                    ),
                    Expanded(
                      child: TextField(
                        controller: _renameController,
                        style: const TextStyle(fontSize: AppSizes.fontSizeBody, color: Color(AppColors.textPrimary)),
                        decoration: InputDecoration(
                          filled: true,
                          fillColor: const Color(AppColors.inputBg),
                          border: OutlineInputBorder(
                            borderRadius: BorderRadius.circular(AppSizes.borderRadiusInput),
                            borderSide: BorderSide.none,
                          ),
                          contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                        ),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 18),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    SizedBox(
                      width: 120,
                      height: 40,
                      child: OutlinedButton(
                        onPressed: _closeRenameDialog,
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(color: Color(AppColors.textMuted)),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(AppSizes.borderRadiusButton)),
                        ),
                        child: Text(
                          _t('取消', 'Cancel'),
                          style: const TextStyle(color: Color(AppColors.textPrimary), fontSize: AppSizes.fontSizeSmall),
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    SizedBox(
                      width: 120,
                      height: 40,
                      child: ElevatedButton(
                        onPressed: _confirmRename,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: const Color(AppColors.accent),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(AppSizes.borderRadiusButton)),
                        ),
                        child: Text(
                          _t('确认', 'Confirm'),
                          style: const TextStyle(color: Colors.white, fontSize: AppSizes.fontSizeSmall),
                        ),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  Widget _closeConfirmDialog(EdgeInsets padding) {
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
                  _t('关闭会话', 'Close Session'),
                  style: const TextStyle(fontSize: 18, color: Color(AppColors.textPrimary), fontWeight: FontWeight.w500),
                ),
                const SizedBox(height: 10),
                Text(
                  closeSessionTitle.isNotEmpty
                      ? _t('确认关闭 $closeSessionTitle？', 'Close $closeSessionTitle?')
                      : _t('确认关闭当前会话？', 'Close current session?'),
                  textAlign: TextAlign.center,
                  style: const TextStyle(fontSize: 13, color: Color(AppColors.textSecondary)),
                ),
                const SizedBox(height: 18),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    SizedBox(
                      width: 120,
                      height: 40,
                      child: OutlinedButton(
                        onPressed: _closeCloseDialog,
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(color: Color(AppColors.textMuted)),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(AppSizes.borderRadiusButton)),
                        ),
                        child: Text(
                          _t('取消', 'Cancel'),
                          style: const TextStyle(color: Color(AppColors.textPrimary), fontSize: AppSizes.fontSizeSmall),
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    SizedBox(
                      width: 140,
                      height: 40,
                      child: ElevatedButton(
                        onPressed: _confirmClose,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: const Color(AppColors.errorRed),
                          shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(AppSizes.borderRadiusButton)),
                        ),
                        child: Text(
                          _t('确认关闭', 'Close'),
                          style: const TextStyle(color: Colors.white, fontSize: AppSizes.fontSizeSmall),
                        ),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
