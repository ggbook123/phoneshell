import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

import 'constants.dart';
import 'device_connection.dart';
import 'models.dart';
import 'preferences_util.dart';

class ConnectionManager {
  ConnectionManager._();
  static final ConnectionManager instance = ConnectionManager._();

  final Map<String, DeviceConnection> _singleConnections = {};
  final Map<String, DeviceInfo> _singleDeviceInfos = {};
  final Map<String, String> _singleDeviceHttpUrls = {};
  final Map<String, int> _singleMessageCallbackIds = {};
  final Map<String, int> _singleStateCallbackIds = {};

  DeviceConnection? _groupConnection;
  int _groupMessageCallbackId = -1;
  int _groupStateCallbackId = -1;
  List<DeviceInfo> _groupDevices = [];
  List<GroupMemberInfo> _groupMembers = [];
  String _groupId = '';
  String _groupSecret = '';
  bool _isGroupJoined = false;

  DeviceMode _currentMode = DeviceMode.standalone;

  String _mobileDeviceId = '';

  final Map<String, SessionState> _sessions = {};
  String _activeSessionId = '';
  String _activeDeviceId = '';
  final int _maxBufferedTerminalOutput = 200000;

  final Map<int, void Function(List<DeviceInfo>)> _deviceListCallbacks = {};
  final Map<int, void Function(String, String, String, String, String)>
  _authRequestCallbacks = {};
  final Map<int, void Function(DeviceMode)> _modeChangeCallbacks = {};
  final Map<int, void Function(String, Map<String, dynamic>)>
  _messageCallbacks = {};
  final Map<int, void Function(ConnectionState, String)> _stateCallbacks = {};
  int _nextCallbackId = 1;

  DeviceMode get currentMode => _currentMode;
  String get activeSessionId => _activeSessionId;
  String get activeDeviceId => _activeDeviceId;

  void setMobileDeviceId(String id) {
    _mobileDeviceId = id;
  }

  int addOnMessage(void Function(String, Map<String, dynamic>) callback) {
    final id = _nextCallbackId++;
    _messageCallbacks[id] = callback;
    return id;
  }

  void removeOnMessage(int id) {
    _messageCallbacks.remove(id);
  }

  int addOnStateChange(void Function(ConnectionState, String) callback) {
    final id = _nextCallbackId++;
    _stateCallbacks[id] = callback;
    return id;
  }

  void removeOnStateChange(int id) {
    _stateCallbacks.remove(id);
  }

  int addOnDeviceListChanged(void Function(List<DeviceInfo>) callback) {
    final id = _nextCallbackId++;
    _deviceListCallbacks[id] = callback;
    return id;
  }

  void removeOnDeviceListChanged(int id) {
    _deviceListCallbacks.remove(id);
  }

  int addOnAuthRequest(
    void Function(String, String, String, String, String) callback,
  ) {
    final id = _nextCallbackId++;
    _authRequestCallbacks[id] = callback;
    return id;
  }

  void removeOnAuthRequest(int id) {
    _authRequestCallbacks.remove(id);
  }

  int addOnModeChange(void Function(DeviceMode) callback) {
    final id = _nextCallbackId++;
    _modeChangeCallbacks[id] = callback;
    return id;
  }

  void removeOnModeChange(int id) {
    _modeChangeCallbacks.remove(id);
  }

  void connectSingle(
    String wsUrl,
    String deviceId,
    String displayName,
    String httpUrl,
  ) {
    if (deviceId.isEmpty || wsUrl.isEmpty) return;

    if (_singleConnections.containsKey(deviceId)) {
      disconnectDevice(deviceId);
    }

    final conn = DeviceConnection(deviceId, displayName);
    final msgId = conn.addOnMessage(
      (type, data) => _handleSingleMessage(deviceId, type, data),
    );
    final stateId = conn.addOnStateChange(
      (state) => _handleSingleStateChange(deviceId, state),
    );

    _singleMessageCallbackIds[deviceId] = msgId;
    _singleStateCallbackIds[deviceId] = stateId;
    _singleConnections[deviceId] = conn;

    final info = DeviceInfo(
      deviceId: deviceId,
      displayName: displayName,
      os: '',
      isOnline: false,
      availableShells: [],
    );
    _singleDeviceInfos[deviceId] = info;
    _singleDeviceHttpUrls[deviceId] = httpUrl;
    _saveSingleDevice(deviceId, displayName, wsUrl, httpUrl);

    if (_currentMode == DeviceMode.standalone) {
      _currentMode = DeviceMode.single;
      _emitModeChange(DeviceMode.single);
    }

    _emitDeviceListChanged();
    conn.connect(wsUrl);
  }

  void disconnectDevice(String deviceId) {
    if (deviceId.isEmpty) return;
    final conn = _singleConnections[deviceId];
    if (conn == null) return;

    final msgId = _singleMessageCallbackIds.remove(deviceId);
    if (msgId != null) {
      conn.removeOnMessage(msgId);
    }
    final stateId = _singleStateCallbackIds.remove(deviceId);
    if (stateId != null) {
      conn.removeOnStateChange(stateId);
    }

    conn.disconnect();
    _singleConnections.remove(deviceId);
    _singleDeviceInfos.remove(deviceId);
    _singleDeviceHttpUrls.remove(deviceId);
    _removeSingleDevice(deviceId);
    _removeSessionsForDevice(deviceId);

    if (_singleConnections.isEmpty && _currentMode == DeviceMode.single) {
      _currentMode = DeviceMode.standalone;
      _emitModeChange(DeviceMode.standalone);
    }

    _emitDeviceListChanged();
  }

  void connectGroup(String wsUrl, String groupSecret, String groupId) {
    if (wsUrl.isEmpty) return;
    disconnectGroup();

    final connectUrl = _buildConnectUrlWithToken(wsUrl, groupSecret);

    _groupId = groupId;
    _groupSecret = groupSecret;

    final conn = DeviceConnection('group', 'Group Connection');
    _groupMessageCallbackId = conn.addOnMessage(
      (type, data) => _handleGroupMessage(type, data),
    );
    _groupStateCallbackId = conn.addOnStateChange(
      (state) => _handleGroupStateChange(state),
    );
    _groupConnection = conn;
    _currentMode = DeviceMode.group;
    _emitModeChange(DeviceMode.group);

    conn.connect(connectUrl);

    PreferencesUtil.setString(StorageKeys.groupRelayUrl, connectUrl);
    PreferencesUtil.setString(StorageKeys.groupId, groupId);
    PreferencesUtil.setString(StorageKeys.groupSecret, groupSecret);
  }

  void disconnectGroup() {
    final conn = _groupConnection;
    if (conn == null) return;

    if (_groupMessageCallbackId != -1) {
      conn.removeOnMessage(_groupMessageCallbackId);
      _groupMessageCallbackId = -1;
    }
    if (_groupStateCallbackId != -1) {
      conn.removeOnStateChange(_groupStateCallbackId);
      _groupStateCallbackId = -1;
    }

    conn.disconnect();
    _groupConnection = null;
    _groupDevices = [];
    _groupMembers = [];
    _isGroupJoined = false;
    _groupId = '';
    _groupSecret = '';

    PreferencesUtil.setString(StorageKeys.groupRelayUrl, '');
    PreferencesUtil.setString(StorageKeys.groupId, '');
    PreferencesUtil.setString(StorageKeys.groupSecret, '');

    if (_currentMode == DeviceMode.group) {
      _currentMode = _singleConnections.isNotEmpty
          ? DeviceMode.single
          : DeviceMode.standalone;
      _emitModeChange(_currentMode);
    }

    _emitDeviceListChanged();
  }

  void designateRelay(String deviceId) {
    final conn = _singleConnections[deviceId];
    if (conn == null || conn.connectionState != ConnectionState.connected) {
      return;
    }
    final msg = {'type': 'relay.designate'};
    conn.send(jsonEncode(msg));
  }

  Future<void> inviteToGroup(
    String httpUrl,
    String inviteCode,
    String relayUrl,
    void Function(bool success, String message)? onResult,
  ) async {
    if (httpUrl.isEmpty || inviteCode.isEmpty || relayUrl.isEmpty) {
      onResult?.call(false, 'Missing parameters');
      return;
    }
    final inviteUrl = httpUrl.endsWith('/')
        ? '${httpUrl}api/invite'
        : '$httpUrl/api/invite';
    final body = jsonEncode({
      'relayUrl': relayUrl,
      'inviteCode': inviteCode,
      'groupId': _groupId,
    });

    try {
      final resp = await http.post(
        Uri.parse(inviteUrl),
        headers: {'Content-Type': 'application/json'},
        body: body,
      );
      if (resp.statusCode >= 200 && resp.statusCode < 300) {
        onResult?.call(true, '');
      } else {
        onResult?.call(false, 'HTTP ${resp.statusCode}');
      }
    } catch (e) {
      onResult?.call(false, e.toString());
    }
  }

  void requestInviteCode() {
    if (_groupConnection == null) return;
    if (_groupConnection!.connectionState != ConnectionState.connected) return;
    final msg = {'type': 'invite.create.request'};
    _groupConnection!.send(jsonEncode(msg));
  }

  void requestGroupServerChange(String newServerDeviceId) {
    if (newServerDeviceId.isEmpty) return;
    if (_groupConnection == null) return;
    if (_groupConnection!.connectionState != ConnectionState.connected) return;
    if (_mobileDeviceId.isEmpty) return;
    final msg = {
      'type': 'group.server.change.request',
      'newServerDeviceId': newServerDeviceId,
      'requesterId': _mobileDeviceId,
    };
    _groupConnection!.send(jsonEncode(msg));
  }

  void requestGroupServerChangeExternal(
    String newServerUrl,
    String groupId,
    String groupSecret,
  ) {
    if (newServerUrl.isEmpty || groupId.isEmpty || groupSecret.isEmpty) return;
    if (_groupConnection == null) return;
    if (_groupConnection!.connectionState != ConnectionState.connected) return;
    if (_mobileDeviceId.isEmpty) return;
    final msg = {
      'type': 'group.server.change.prepare',
      'newServerUrl': newServerUrl,
      'groupId': groupId,
      'groupSecret': groupSecret,
      'requesterId': _mobileDeviceId,
    };
    _groupConnection!.send(jsonEncode(msg));
  }

  void dissolveGroup() {
    if (_groupConnection == null) return;
    final msg = {'type': 'group.dissolve'};
    _groupConnection!.send(jsonEncode(msg));
  }

  void updateDeviceDisplayName(String deviceId, String newName) {
    final trimmed = newName.trim();
    if (deviceId.isEmpty || trimmed.isEmpty) return;
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'device.settings.update',
      'deviceId': deviceId,
      'displayName': trimmed,
    };
    conn.send(jsonEncode(msg));
  }

  void kickDevice(String deviceId) {
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {'type': 'device.kick', 'deviceId': deviceId};
    conn.send(jsonEncode(msg));
  }

  void sendAuthResponse(String requestId, bool approved) {
    final msg = {
      'type': 'auth.response',
      'requestId': requestId,
      'approved': approved,
    };
    final json = jsonEncode(msg);

    if (_groupConnection != null &&
        _groupConnection!.connectionState == ConnectionState.connected) {
      _groupConnection!.send(json);
    } else {
      final conn = _singleConnections[_activeDeviceId];
      if (conn != null) {
        conn.send(json);
      }
    }
  }

  void sendPanelLoginScan(String requestId) {
    final msg = {
      'type': 'panel.login.scan',
      'requestId': requestId,
      'mobileDeviceId': _mobileDeviceId,
    };
    final json = jsonEncode(msg);

    if (_groupConnection != null &&
        _groupConnection!.connectionState == ConnectionState.connected) {
      _groupConnection!.send(json);
    } else {
      for (final conn in _singleConnections.values) {
        if (conn.connectionState == ConnectionState.connected) {
          conn.send(json);
        }
      }
    }
  }

  void requestSessionList(String deviceId) {
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {'type': 'session.list.request', 'deviceId': deviceId};
    conn.send(jsonEncode(msg));
  }

  void renameSession(String deviceId, String sessionId, String title) {
    final trimmed = title.trim();
    if (deviceId.isEmpty || sessionId.isEmpty || trimmed.isEmpty) return;
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'session.rename',
      'deviceId': deviceId,
      'sessionId': sessionId,
      'title': trimmed,
    };
    conn.send(jsonEncode(msg));
  }

  void requestQuickPanelSync(String deviceId, {String explorerPath = ''}) {
    if (deviceId.isEmpty) return;
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'quickpanel.sync.request',
      'deviceId': deviceId,
      'explorerPath': explorerPath,
    };
    conn.send(jsonEncode(msg));
  }

  void appendQuickPanelRecentInput(String deviceId, String input) {
    final trimmed = input.trim();
    if (deviceId.isEmpty || trimmed.isEmpty) return;
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'quickpanel.recent.append',
      'deviceId': deviceId,
      'input': trimmed,
    };
    conn.send(jsonEncode(msg));
  }

  void openTerminal(String deviceId, String shellId) {
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    _activeDeviceId = deviceId;
    final msg = {
      'type': 'terminal.open',
      'deviceId': deviceId,
      'shellId': shellId,
    };
    conn.send(jsonEncode(msg));
  }

  void sendTerminalInput(String data, {String targetSessionId = ''}) {
    final sessionId = targetSessionId.isNotEmpty
        ? targetSessionId
        : _activeSessionId;
    if (sessionId.isEmpty || data.isEmpty) return;
    final session = _sessions[sessionId];
    final deviceId = session?.deviceId ?? _activeDeviceId;
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'terminal.input',
      'deviceId': deviceId,
      'sessionId': sessionId,
      'data': data,
    };
    conn.send(jsonEncode(msg));
  }

  void requestTerminalHistory(
    String deviceId,
    String sessionId,
    int beforeSeq,
    int maxChars,
  ) {
    if (deviceId.isEmpty || sessionId.isEmpty) return;
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'terminal.history.request',
      'deviceId': deviceId,
      'sessionId': sessionId,
      'beforeSeq': beforeSeq,
      'maxChars': maxChars,
    };
    conn.send(jsonEncode(msg));
  }

  void sendTerminalResize(int cols, int rows, {String targetSessionId = ''}) {
    final sessionId = targetSessionId.isNotEmpty
        ? targetSessionId
        : _activeSessionId;
    if (sessionId.isEmpty) return;
    final session = _sessions[sessionId];
    final deviceId = session?.deviceId ?? _activeDeviceId;
    final conn = _getConnectionForDevice(deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'terminal.resize',
      'deviceId': deviceId,
      'sessionId': sessionId,
      'cols': cols,
      'rows': rows,
    };
    conn.send(jsonEncode(msg));
  }

  void closeTerminalSession(String sessionId) {
    if (sessionId.isEmpty) return;
    final session = _sessions[sessionId];
    if (session == null) return;
    final conn = _getConnectionForDevice(session.deviceId);
    if (conn == null) return;
    final msg = {
      'type': 'terminal.close',
      'deviceId': session.deviceId,
      'sessionId': sessionId,
    };
    conn.send(jsonEncode(msg));
  }

  void setActiveSession(String deviceId, String sessionId) {
    _activeDeviceId = deviceId;
    _activeSessionId = sessionId;
    if (sessionId.isNotEmpty && !_sessions.containsKey(sessionId)) {
      final session = SessionState();
      session.sessionId = sessionId;
      session.deviceId = deviceId;
      _sessions[sessionId] = session;
    }
  }

  String getBufferedTerminalOutput() {
    final session = _sessions[_activeSessionId];
    return session?.bufferedOutput ?? '';
  }

  String getSessionBufferedOutput(String sessionId) {
    final session = _sessions[sessionId];
    return session?.bufferedOutput ?? '';
  }

  int getSessionCount() => _sessions.length;

  List<SessionState> getSessionList() => _sessions.values.toList();

  void switchSession(String sessionId) {
    final session = _sessions[sessionId];
    if (session != null) {
      _activeSessionId = session.sessionId;
      _activeDeviceId = session.deviceId;
    }
  }

  bool hasSession(String sessionId) => _sessions.containsKey(sessionId);

  List<DeviceInfo> getAllDevices() {
    if (_currentMode == DeviceMode.group) {
      return List<DeviceInfo>.from(_groupDevices);
    }
    return _singleDeviceInfos.values.toList();
  }

  List<DeviceInfo> getSingleDevices() => _singleDeviceInfos.values.toList();

  List<DeviceInfo> getGroupDevices() => List<DeviceInfo>.from(_groupDevices);

  List<GroupMemberInfo> getGroupMembers() =>
      List<GroupMemberInfo>.from(_groupMembers);

  String getGroupId() => _groupId;

  bool isInGroup() => _isGroupJoined;

  ConnectionState getConnectionStateForDevice(String deviceId) {
    final conn = _singleConnections[deviceId];
    if (conn != null) return conn.connectionState;
    if (_groupConnection != null) return _groupConnection!.connectionState;
    return ConnectionState.disconnected;
  }

  SingleDeviceRecord? getSingleDeviceRecord(String deviceId) {
    final conn = _singleConnections[deviceId];
    if (conn == null) return null;
    final record = SingleDeviceRecord();
    record.deviceId = deviceId;
    record.displayName = conn.displayName;
    record.wsUrl = conn.serverUrl;
    record.httpUrl = _singleDeviceHttpUrls[deviceId] ?? '';
    return record;
  }

  void sendGroupJoinRequest(String groupSecret, {String inviteCode = ''}) {
    if (_groupConnection == null) return;
    final msg = {
      'type': 'group.join.request',
      'groupSecret': groupSecret,
      'inviteCode': inviteCode,
      'deviceId': _mobileDeviceId,
      'displayName': 'my phone',
      'os': 'Android',
      'availableShells': <String>[],
    };
    _groupConnection!.send(jsonEncode(msg));
  }

  void sendMobileBindRequest(String groupId) {
    if (_groupConnection == null) return;
    final msg = {
      'type': 'mobile.bind.request',
      'groupId': groupId,
      'mobileDeviceId': _mobileDeviceId,
      'mobileDisplayName': 'my phone',
    };
    _groupConnection!.send(jsonEncode(msg));
  }

  void requestDeviceList() {
    if (_groupConnection != null &&
        _groupConnection!.connectionState == ConnectionState.connected) {
      final msg = {'type': 'device.list.request'};
      _groupConnection!.send(jsonEncode(msg));
    }
  }

  Future<void> restoreSavedConnections() async {
    try {
      final groupRelayUrl = await PreferencesUtil.getString(
        StorageKeys.groupRelayUrl,
      );
      final groupSecret = await PreferencesUtil.getString(
        StorageKeys.groupSecret,
      );
      final groupId = await PreferencesUtil.getString(StorageKeys.groupId);
      if (groupRelayUrl.isNotEmpty && groupSecret.isNotEmpty) {
        connectGroup(groupRelayUrl, groupSecret, groupId);
      }
    } catch (_) {
      // Ignore
    }

    try {
      final raw = await PreferencesUtil.getString(StorageKeys.singleDevices);
      if (raw.isEmpty) return;
      final decoded = jsonDecode(raw);
      if (decoded is List) {
        for (final item in decoded) {
          if (item is Map<String, dynamic>) {
            final record = SingleDeviceRecord.fromJson(item);
            if (record.deviceId.isNotEmpty && record.wsUrl.isNotEmpty) {
              connectSingle(
                record.wsUrl,
                record.deviceId,
                record.displayName,
                record.httpUrl,
              );
            }
          }
        }
      }
    } catch (_) {
      // Ignore
    }
  }

  DeviceConnection? _getConnectionForDevice(String deviceId) {
    final single = _singleConnections[deviceId];
    if (single != null) return single;
    if (_groupConnection != null) return _groupConnection;
    return null;
  }

  void _handleSingleMessage(
    String deviceId,
    String type,
    Map<String, dynamic> data,
  ) {
    if (type == 'device.list') {
      final devices = _normalizeDeviceList(data['devices']);
      if (devices.isNotEmpty) {
        _singleDeviceInfos[deviceId] = devices.first;
      }
      _emitDeviceListChanged();
    } else if (type == 'device.settings.updated') {
      final targetId = (data['deviceId'] ?? deviceId) as String;
      final newName = (data['displayName'] ?? '') as String;
      _updateDeviceNameInLists(targetId, newName);
    } else if (type == 'relay.designated') {
      final relayUrl = (data['relayUrl'] ?? '') as String;
      final newGroupId = (data['groupId'] ?? '') as String;
      final secret = (data['groupSecret'] ?? '') as String;
      if (relayUrl.isNotEmpty) {
        if (secret.isNotEmpty) {
          _groupSecret = secret;
          PreferencesUtil.setString(StorageKeys.groupSecret, secret);
        }
        _promoteToGroupConnection(deviceId, relayUrl, newGroupId);
        if (secret.isNotEmpty) {
          sendGroupJoinRequest(secret);
        }
      }
    } else if (type == 'auth.request') {
      _handleAuthRequest(data);
    } else if (type == 'terminal.opened' ||
        type == 'terminal.output' ||
        type == 'terminal.closed' ||
        type == 'session.list') {
      _handleTerminalMessage(type, data);
    }

    _emitMessage(type, data);
  }

  void _handleSingleStateChange(String deviceId, ConnectionState state) {
    final info = _singleDeviceInfos[deviceId];
    if (info != null) {
      _singleDeviceInfos[deviceId] = info.copyWith(
        isOnline: state == ConnectionState.connected,
      );
      _emitDeviceListChanged();
    }

    if (state == ConnectionState.connected) {
      final conn = _singleConnections[deviceId];
      if (conn != null) {
        conn.send(jsonEncode({'type': 'device.list.request'}));
      }
    }

    _emitStateChange(state, deviceId);
  }

  void _handleGroupMessage(String type, Map<String, dynamic> data) {
    if (type == 'device.list') {
      _groupDevices = _normalizeDeviceList(data['devices']);
      _emitDeviceListChanged();
    } else if (type == 'group.join.accepted') {
      _isGroupJoined = true;
      _groupId = (data['groupId'] ?? '') as String;
      _groupMembers = _normalizeGroupMembers(data['members']);
      PreferencesUtil.setString(StorageKeys.groupId, _groupId);
      final secret = (data['groupSecret'] ?? '') as String;
      if (secret.isNotEmpty && secret != _groupSecret) {
        _groupSecret = secret;
        PreferencesUtil.setString(StorageKeys.groupSecret, secret);
      }
      requestDeviceList();
    } else if (type == 'group.member.list') {
      _groupMembers = _normalizeGroupMembers(data['members']);
      requestDeviceList();
    } else if (type == 'group.member.joined') {
      final member = _normalizeGroupMember(data['member']);
      if (member != null) _groupMembers.add(member);
      requestDeviceList();
    } else if (type == 'group.member.left') {
      final leftId = (data['deviceId'] ?? '') as String;
      _groupMembers = _groupMembers.where((m) => m.deviceId != leftId).toList();
      requestDeviceList();
    } else if (type == 'group.server.change.commit') {
      final newUrl = (data['newServerUrl'] ?? '') as String;
      final newGroupId = (data['groupId'] ?? _groupId) as String;
      final newSecret = (data['groupSecret'] ?? '') as String;
      final effectiveSecret = newSecret.isNotEmpty ? newSecret : _groupSecret;
      if (newUrl.isNotEmpty && effectiveSecret.isNotEmpty) {
        final connectUrl = _buildConnectUrlWithToken(newUrl, effectiveSecret);
        connectGroup(connectUrl, effectiveSecret, newGroupId);
      }
    } else if (type == 'group.secret.rotate.done') {
      final newSecret = (data['newSecret'] ?? '') as String;
      if (newSecret.isNotEmpty) {
        _groupSecret = newSecret;
        PreferencesUtil.setString(StorageKeys.groupSecret, newSecret);
      }
    } else if (type == 'group.dissolved' || type == 'device.kicked') {
      disconnectGroup();
    } else if (type == 'device.settings.updated') {
      final targetId = (data['deviceId'] ?? '') as String;
      final newName = (data['displayName'] ?? '') as String;
      _updateDeviceNameInLists(targetId, newName);
    } else if (type == 'auth.request') {
      _handleAuthRequest(data);
    } else if (type == 'terminal.opened' ||
        type == 'terminal.output' ||
        type == 'terminal.closed' ||
        type == 'session.list') {
      _handleTerminalMessage(type, data);
    }

    _emitMessage(type, data);
  }

  void _handleGroupStateChange(ConnectionState state) {
    if (state == ConnectionState.connected && _groupConnection != null) {
      if (_groupSecret.isNotEmpty) {
        sendGroupJoinRequest(_groupSecret);
      }
    }
    _emitStateChange(state, 'group');
  }

  void _handleTerminalMessage(String type, Map<String, dynamic> data) {
    if (type == 'terminal.opened') {
      final sessionId = (data['sessionId'] ?? '') as String;
      final deviceId = (data['deviceId'] ?? '') as String;
      if (sessionId.isNotEmpty) {
        final session = SessionState();
        session.sessionId = sessionId;
        session.deviceId = deviceId;
        _sessions[sessionId] = session;
        _activeSessionId = sessionId;
        _activeDeviceId = deviceId;
      }
    } else if (type == 'terminal.output') {
      final sessionId = (data['sessionId'] ?? '') as String;
      final outputData = (data['data'] ?? '') as String;
      final session = _sessions[sessionId];
      if (session != null && outputData.isNotEmpty) {
        session.bufferedOutput += outputData;
        if (session.bufferedOutput.length > _maxBufferedTerminalOutput) {
          session.bufferedOutput = session.bufferedOutput.substring(
            session.bufferedOutput.length - _maxBufferedTerminalOutput,
          );
        }
      }
    } else if (type == 'terminal.closed') {
      final sessionId = (data['sessionId'] ?? '') as String;
      _sessions.remove(sessionId);
      if (_activeSessionId == sessionId) {
        _activeSessionId = '';
      }
    } else if (type == 'session.list') {
      final deviceId = (data['deviceId'] ?? '') as String;
      final rawSessions = data['sessions'];
      if (rawSessions is List) {
        for (final item in rawSessions) {
          if (item is Map<String, dynamic>) {
            final sid = (item['sessionId'] ?? '') as String;
            if (sid.isNotEmpty && !_sessions.containsKey(sid)) {
              final session = SessionState();
              session.sessionId = sid;
              session.deviceId = deviceId;
              session.shellId = (item['shellId'] ?? '') as String;
              _sessions[sid] = session;
            }
          }
        }
      }
    }
  }

  void _handleAuthRequest(Map<String, dynamic> data) {
    final requestId = (data['requestId'] ?? '') as String;
    final action = (data['action'] ?? '') as String;
    final requesterName = (data['requesterName'] ?? '') as String;
    final description = (data['description'] ?? '') as String;
    final targetDeviceId = (data['targetDeviceId'] ?? '') as String;
    if (requestId.isEmpty) return;
    for (final cb in _authRequestCallbacks.values.toList()) {
      try {
        cb(requestId, action, requesterName, description, targetDeviceId);
      } catch (_) {
        // Ignore
      }
    }
  }

  void _promoteToGroupConnection(
    String deviceId,
    String relayUrl,
    String newGroupId,
  ) {
    final conn = _singleConnections[deviceId];
    if (conn == null) return;

    final msgId = _singleMessageCallbackIds.remove(deviceId);
    if (msgId != null) conn.removeOnMessage(msgId);
    final stateId = _singleStateCallbackIds.remove(deviceId);
    if (stateId != null) conn.removeOnStateChange(stateId);

    _singleConnections.remove(deviceId);
    _singleDeviceInfos.remove(deviceId);

    _groupConnection = conn;
    _groupId = newGroupId;
    _currentMode = DeviceMode.group;

    _groupMessageCallbackId = conn.addOnMessage(
      (type, data) => _handleGroupMessage(type, data),
    );
    _groupStateCallbackId = conn.addOnStateChange(
      (state) => _handleGroupStateChange(state),
    );

    PreferencesUtil.setString(StorageKeys.groupRelayUrl, relayUrl);
    PreferencesUtil.setString(StorageKeys.groupId, newGroupId);
    _emitModeChange(DeviceMode.group);
  }

  void _removeSessionsForDevice(String deviceId) {
    final toRemove = <String>[];
    _sessions.forEach((key, session) {
      if (session.deviceId == deviceId) {
        toRemove.add(key);
      }
    });
    for (final sid in toRemove) {
      _sessions.remove(sid);
      if (_activeSessionId == sid) {
        _activeSessionId = '';
        _activeDeviceId = '';
      }
    }
  }

  void _updateDeviceNameInLists(String deviceId, String newName) {
    if (deviceId.isEmpty || newName.isEmpty) return;
    final info = _singleDeviceInfos[deviceId];
    if (info != null) {
      _singleDeviceInfos[deviceId] = info.copyWith(displayName: newName);
    }

    for (var i = 0; i < _groupDevices.length; i++) {
      if (_groupDevices[i].deviceId == deviceId) {
        final d = _groupDevices[i];
        _groupDevices[i] = DeviceInfo(
          deviceId: d.deviceId,
          displayName: newName,
          os: d.os,
          isOnline: d.isOnline,
          availableShells: d.availableShells,
        );
      }
    }

    for (var i = 0; i < _groupMembers.length; i++) {
      if (_groupMembers[i].deviceId == deviceId) {
        final m = _groupMembers[i];
        _groupMembers[i] = GroupMemberInfo(
          deviceId: m.deviceId,
          displayName: newName,
          os: m.os,
          role: m.role,
          isOnline: m.isOnline,
          availableShells: m.availableShells,
        );
      }
    }

    final conn = _singleConnections[deviceId];
    if (conn != null) {
      conn.displayName = newName;
    }

    _updateSingleDeviceRecordName(deviceId, newName);
    _emitDeviceListChanged();
  }

  void _updateSingleDeviceRecordName(String deviceId, String newName) {
    PreferencesUtil.getString(StorageKeys.singleDevices).then((raw) {
      if (raw.isEmpty) return;
      try {
        final decoded = jsonDecode(raw);
        if (decoded is! List) return;
        final records = decoded
            .whereType<Map<String, dynamic>>()
            .map((e) => SingleDeviceRecord.fromJson(e))
            .toList();
        var updated = false;
        for (final record in records) {
          if (record.deviceId == deviceId) {
            record.displayName = newName;
            updated = true;
          }
        }
        if (updated) {
          final json = jsonEncode(records.map((e) => e.toJson()).toList());
          PreferencesUtil.setString(StorageKeys.singleDevices, json);
        }
      } catch (_) {
        // Ignore parse errors
      }
    });
  }

  void _saveSingleDevice(
    String deviceId,
    String displayName,
    String wsUrl,
    String httpUrl,
  ) {
    PreferencesUtil.getString(StorageKeys.singleDevices).then((raw) {
      var records = <SingleDeviceRecord>[];
      if (raw.isNotEmpty) {
        try {
          final decoded = jsonDecode(raw);
          if (decoded is List) {
            records = decoded
                .whereType<Map<String, dynamic>>()
                .map((e) => SingleDeviceRecord.fromJson(e))
                .toList();
          }
        } catch (_) {}
      }
      records = records.where((r) => r.deviceId != deviceId).toList();
      final rec = SingleDeviceRecord();
      rec.deviceId = deviceId;
      rec.displayName = displayName;
      rec.wsUrl = wsUrl;
      rec.httpUrl = httpUrl;
      records.add(rec);
      final json = jsonEncode(records.map((e) => e.toJson()).toList());
      PreferencesUtil.setString(StorageKeys.singleDevices, json);
    });
  }

  void _removeSingleDevice(String deviceId) {
    PreferencesUtil.getString(StorageKeys.singleDevices).then((raw) {
      if (raw.isEmpty) return;
      try {
        final decoded = jsonDecode(raw);
        if (decoded is List) {
          final records = decoded
              .whereType<Map<String, dynamic>>()
              .map((e) => SingleDeviceRecord.fromJson(e))
              .where((r) => r.deviceId != deviceId)
              .toList();
          final json = jsonEncode(records.map((e) => e.toJson()).toList());
          PreferencesUtil.setString(StorageKeys.singleDevices, json);
        }
      } catch (_) {
        // Ignore
      }
    });
  }

  List<DeviceInfo> _normalizeDeviceList(dynamic value) {
    if (value is! List) return [];
    final result = <DeviceInfo>[];
    for (final item in value) {
      if (item is Map<String, dynamic>) {
        result.add(DeviceInfo.fromMap(item));
      }
    }
    return result;
  }

  List<GroupMemberInfo> _normalizeGroupMembers(dynamic value) {
    if (value is! List) return [];
    final result = <GroupMemberInfo>[];
    for (final item in value) {
      final member = _normalizeGroupMember(item);
      if (member != null) result.add(member);
    }
    return result;
  }

  GroupMemberInfo? _normalizeGroupMember(dynamic value) {
    if (value is! Map<String, dynamic>) return null;
    return GroupMemberInfo.fromMap(value);
  }

  void _emitMessage(String type, Map<String, dynamic> data) {
    for (final cb in _messageCallbacks.values.toList()) {
      try {
        cb(type, data);
      } catch (_) {
        // Ignore
      }
    }
  }

  void _emitStateChange(ConnectionState state, String deviceId) {
    for (final cb in _stateCallbacks.values.toList()) {
      try {
        cb(state, deviceId);
      } catch (_) {
        // Ignore
      }
    }
  }

  void _emitDeviceListChanged() {
    final devices = getAllDevices();
    for (final cb in _deviceListCallbacks.values.toList()) {
      try {
        cb(devices);
      } catch (_) {
        // Ignore
      }
    }
  }

  void _emitModeChange(DeviceMode mode) {
    for (final cb in _modeChangeCallbacks.values.toList()) {
      try {
        cb(mode);
      } catch (_) {
        // Ignore
      }
    }
  }

  String _buildConnectUrlWithToken(String baseUrl, String groupSecret) {
    final trimmedSecret = groupSecret.trim();
    if (trimmedSecret.isEmpty) return baseUrl;
    final token = Uri.encodeComponent(trimmedSecret);
    if (baseUrl.contains('token=')) {
      return baseUrl.replaceAll(RegExp(r'token=[^&]*'), 'token=$token');
    }
    if (baseUrl.contains('?')) return '$baseUrl&token=$token';
    return '$baseUrl?token=$token';
  }
}
