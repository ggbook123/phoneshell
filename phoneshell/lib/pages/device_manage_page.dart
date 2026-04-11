import 'dart:async';

import 'package:flutter/material.dart' hide ConnectionState;

import '../core/auth_manager.dart';
import '../core/constants.dart';
import '../core/connection_manager.dart';
import '../core/i18n.dart';
import '../core/models.dart';
import '../core/preferences_util.dart';
import '../widgets/phoneshell_header.dart';
import 'scan_page.dart';
import 'settings_page.dart';

class DeviceItemModel {
  String deviceId = '';
  String displayName = '';
  String os = '';
  bool isOnline = false;
  List<String> availableShells = [];
}

class GroupMemberModel {
  String deviceId = '';
  String displayName = '';
  String os = '';
  String role = '';
  bool isOnline = false;
  List<String> availableShells = [];
}

class DeviceManagePage extends StatefulWidget {
  const DeviceManagePage({super.key});

  @override
  State<DeviceManagePage> createState() => _DeviceManagePageState();
}

class _DeviceManagePageState extends State<DeviceManagePage> {
  List<DeviceItemModel> groupDevices = [];
  List<DeviceItemModel> singleDevices = [];
  List<GroupMemberModel> groupMembers = [];
  DeviceMode currentMode = DeviceMode.standalone;
  bool isInGroup = false;
  String groupId = '';

  String scanResult = '';
  String scanStatus = '';
  bool isScanning = false;
  String parseError = '';
  bool showInviteConfirmDialog = false;
  String inviteDeviceName = '';

  bool showSettingsSheet = false;
  String settingsDeviceId = '';
  String settingsDisplayName = '';
  String settingsOs = '';
  String settingsEditName = '';
  String settingsRole = '';
  bool settingsIsGroupDevice = false;
  final TextEditingController _settingsNameController = TextEditingController();
  bool showDeviceRenameDialog = false;
  String renameDeviceId = '';
  String renameDeviceTitle = '';
  final TextEditingController _renameDeviceController = TextEditingController();

  bool showAuthDialog = false;
  String authRequestId = '';
  String authRequesterName = '';
  String authDescription = '';
  String authAction = '';

  int pendingAuthCount = 0;

  int deviceListCallbackId = -1;
  int modeChangeCallbackId = -1;
  int messageCallbackId = -1;
  int authDialogCallbackId = -1;
  String pendingInviteDeviceId = '';
  String pendingInviteHttpUrl = '';
  int scanMessageListenerId = -1;
  int scanStateListenerId = -1;
  String scanInviteHttpUrl = '';
  String scanInviteDeviceId = '';
  String pendingServerChangeDeviceId = '';

  @override
  void initState() {
    super.initState();
    _activatePage();
  }

  @override
  void dispose() {
    _deactivatePage();
    _settingsNameController.dispose();
    _renameDeviceController.dispose();
    super.dispose();
  }

  String _t(String zh, String en) => I18n.tCurrent(zh, en);

  Future<void> _openSettingsPage() async {
    final navigator = Navigator.of(context);
    bool? shouldInitialize;
    try {
      shouldInitialize = await navigator.pushNamed<bool>('pages/SettingsPage');
    } catch (_) {
      if (!mounted) return;
      shouldInitialize = await navigator.push<bool>(
        MaterialPageRoute(builder: (_) => const SettingsPage()),
      );
    }
    if (!mounted || shouldInitialize != true) return;
    setState(_applyGroupExitLocal);
  }

  void _activatePage() {
    currentMode = ConnectionManager.instance.currentMode;
    _syncDeviceLists();
    pendingAuthCount = AuthManager.instance.getPendingCount();

    if (deviceListCallbackId == -1) {
      deviceListCallbackId = ConnectionManager.instance.addOnDeviceListChanged((
        _,
      ) {
        setState(() {
          _syncDeviceLists();
        });
      });
    }

    if (modeChangeCallbackId == -1) {
      modeChangeCallbackId = ConnectionManager.instance.addOnModeChange((mode) {
        setState(() {
          currentMode = mode;
          _syncDeviceLists();
        });
      });
    }

    if (messageCallbackId == -1) {
      messageCallbackId = ConnectionManager.instance.addOnMessage((type, data) {
        if (type == 'invite.create.response') {
          final inviteCode = (data['inviteCode'] ?? '') as String;
          final relayUrl = (data['relayUrl'] ?? '') as String;
          if (pendingInviteDeviceId.isNotEmpty &&
              pendingInviteHttpUrl.isNotEmpty) {
            final httpUrl = pendingInviteHttpUrl;
            pendingInviteDeviceId = '';
            pendingInviteHttpUrl = '';
            ConnectionManager.instance.inviteToGroup(
              httpUrl,
              inviteCode,
              relayUrl,
              null,
            );
          }
        } else if (type == 'group.join.accepted') {
          setState(_syncDeviceLists);
        } else if (type == 'group.dissolved' || type == 'device.kicked') {
          setState(_applyGroupExitLocal);
        } else if (type == 'group.member.list' ||
            type == 'group.member.joined' ||
            type == 'group.member.left') {
          setState(_syncDeviceLists);
          if (type == 'group.member.joined') {
            final member = data['member'] as Map<String, dynamic>? ?? {};
            final joinedId = (member['deviceId'] ?? '') as String;
            _tryRequestServerChange(joinedId);
          } else if (type == 'group.member.list') {
            _tryRequestServerChange();
          }
        }
      });
    }

    if (authDialogCallbackId == -1) {
      authDialogCallbackId = AuthManager.instance.addOnShowDialog((request) {
        setState(() {
          authRequestId = request.requestId;
          authRequesterName = request.requesterName;
          authDescription = request.description;
          authAction = request.action;
          showAuthDialog = true;
          pendingAuthCount = AuthManager.instance.getPendingCount();
        });
      });
    }
  }

  void _deactivatePage() {
    if (deviceListCallbackId != -1) {
      ConnectionManager.instance.removeOnDeviceListChanged(
        deviceListCallbackId,
      );
      deviceListCallbackId = -1;
    }
    if (modeChangeCallbackId != -1) {
      ConnectionManager.instance.removeOnModeChange(modeChangeCallbackId);
      modeChangeCallbackId = -1;
    }
    if (messageCallbackId != -1) {
      ConnectionManager.instance.removeOnMessage(messageCallbackId);
      messageCallbackId = -1;
    }
    if (authDialogCallbackId != -1) {
      AuthManager.instance.removeOnShowDialog(authDialogCallbackId);
      authDialogCallbackId = -1;
    }
    _cleanupScanListeners();
  }

  void _cleanupScanListeners() {
    if (scanMessageListenerId != -1) {
      ConnectionManager.instance.removeOnMessage(scanMessageListenerId);
      scanMessageListenerId = -1;
    }
    if (scanStateListenerId != -1) {
      ConnectionManager.instance.removeOnStateChange(scanStateListenerId);
      scanStateListenerId = -1;
    }
  }

  void _syncDeviceLists() {
    groupDevices = _buildDeviceModels(
      ConnectionManager.instance.getGroupDevices(),
      groupDevices,
    );
    singleDevices = _buildDeviceModels(
      ConnectionManager.instance.getSingleDevices(),
      singleDevices,
    );
    groupMembers = _buildGroupMemberModels(
      ConnectionManager.instance.getGroupMembers(),
      groupMembers,
    );
    isInGroup = ConnectionManager.instance.isInGroup();
    groupId = ConnectionManager.instance.getGroupId();
  }

  void _tryRequestServerChange([String? joinedDeviceId]) {
    if (pendingServerChangeDeviceId.isEmpty) return;
    final targetId = pendingServerChangeDeviceId;
    final alreadyJoined =
        joinedDeviceId == targetId ||
        ConnectionManager.instance.getGroupMembers().any(
          (m) => m.deviceId == targetId,
        );
    if (!alreadyJoined) return;

    pendingServerChangeDeviceId = '';
    ConnectionManager.instance.requestGroupServerChange(targetId);
    setState(() {
      scanStatus = _t('正在切换服务器...', 'Switching server...');
    });
  }

  List<DeviceItemModel> _buildDeviceModels(
    List<DeviceInfo> raw,
    List<DeviceItemModel> current,
  ) {
    final existing = <String, DeviceItemModel>{};
    for (final item in current) {
      if (item.deviceId.isNotEmpty) existing[item.deviceId] = item;
    }

    final result = <DeviceItemModel>[];
    for (final info in raw) {
      if (info.deviceId.isEmpty) continue;
      final model = existing[info.deviceId] ?? DeviceItemModel();
      model.deviceId = info.deviceId;
      model.displayName = info.displayName;
      model.os = info.os;
      model.isOnline = info.isOnline;
      model.availableShells = info.availableShells.isNotEmpty
          ? List<String>.from(info.availableShells)
          : [];
      result.add(model);
    }
    return result;
  }

  List<GroupMemberModel> _buildGroupMemberModels(
    List<GroupMemberInfo> raw,
    List<GroupMemberModel> current,
  ) {
    final existing = <String, GroupMemberModel>{};
    for (final item in current) {
      if (item.deviceId.isNotEmpty) existing[item.deviceId] = item;
    }

    final result = <GroupMemberModel>[];
    for (final info in raw) {
      if (info.deviceId.isEmpty) continue;
      final model = existing[info.deviceId] ?? GroupMemberModel();
      model.deviceId = info.deviceId;
      model.displayName = info.displayName;
      model.os = info.os;
      model.role = info.role;
      model.isOnline = info.isOnline;
      model.availableShells = info.availableShells.isNotEmpty
          ? List<String>.from(info.availableShells)
          : [];
      result.add(model);
    }
    return result;
  }

  void _onDeviceClick(DeviceItemModel device) {
    if (!device.isOnline || device.deviceId.isEmpty) return;
    Navigator.of(context).pushNamed(
      'pages/SessionListPage',
      arguments: {
        'deviceId': device.deviceId,
        'displayName': device.displayName,
        'availableShells': device.availableShells,
      },
    );
  }

  void _openDeviceSettings(DeviceItemModel device, bool isPendingSingle) {
    settingsDeviceId = device.deviceId;
    settingsDisplayName = device.displayName;
    settingsOs = device.os;
    settingsEditName = device.displayName;
    _settingsNameController.text = device.displayName;

    settingsIsGroupDevice = !isPendingSingle && currentMode == DeviceMode.group;
    if (settingsIsGroupDevice) {
      final member = _findGroupMember(device.deviceId);
      settingsRole = member != null ? member.role : 'Member';
    } else {
      settingsRole = 'Pending';
    }

    setState(() {
      showSettingsSheet = true;
    });
  }

  void _closeSettingsSheet() {
    setState(() {
      showSettingsSheet = false;
      settingsDeviceId = '';
      _settingsNameController.text = '';
    });
  }

  void _openDeviceRenameDialog() {
    renameDeviceId = settingsDeviceId;
    renameDeviceTitle = settingsDisplayName;
    _renameDeviceController.text = settingsDisplayName;
    setState(() {
      showDeviceRenameDialog = true;
    });
  }

  void _closeDeviceRenameDialog() {
    setState(() {
      showDeviceRenameDialog = false;
      renameDeviceId = '';
      renameDeviceTitle = '';
      _renameDeviceController.text = '';
    });
  }

  void _confirmDeviceRename() {
    final trimmed = _renameDeviceController.text.trim();
    if (trimmed.isEmpty || renameDeviceId.isEmpty) {
      _closeDeviceRenameDialog();
      return;
    }
    if (trimmed != settingsDisplayName) {
      ConnectionManager.instance.updateDeviceDisplayName(
        renameDeviceId,
        trimmed,
      );
      _updateDeviceNameLocal(renameDeviceId, trimmed);
      settingsDisplayName = trimmed;
      _settingsNameController.text = trimmed;
    }
    _closeDeviceRenameDialog();
    _closeSettingsSheet();
  }

  void _updateDeviceNameLocal(String deviceId, String newName) {
    if (deviceId.isEmpty || newName.isEmpty) return;
    for (final item in groupDevices) {
      if (item.deviceId == deviceId) item.displayName = newName;
    }
    for (final item in singleDevices) {
      if (item.deviceId == deviceId) item.displayName = newName;
    }
    for (final item in groupMembers) {
      if (item.deviceId == deviceId) item.displayName = newName;
    }
  }

  void _kickDeviceFromGroup() {
    ConnectionManager.instance.kickDevice(settingsDeviceId);
    _removeDeviceFromGroupLocal(settingsDeviceId);
    _closeSettingsSheet();
  }

  void _disconnectSingleDevice() {
    ConnectionManager.instance.disconnectDevice(settingsDeviceId);
    _closeSettingsSheet();
  }

  void _approveAuth() {
    AuthManager.instance.approve(authRequestId);
    setState(() {
      showAuthDialog = false;
      pendingAuthCount = AuthManager.instance.getPendingCount();
    });
  }

  void _rejectAuth() {
    AuthManager.instance.reject(authRequestId);
    setState(() {
      showAuthDialog = false;
      pendingAuthCount = AuthManager.instance.getPendingCount();
    });
  }

  void _refreshDeviceView() {
    setState(() {
      currentMode = ConnectionManager.instance.currentMode;
      _syncDeviceLists();
    });
  }

  Future<void> _startScan() async {
    if (isScanning) return;
    _cleanupScanListeners();
    setState(() {
      isScanning = true;
      scanStatus = _t('正在启动扫码...', 'Starting scan...');
      parseError = '';
      scanResult = '';
    });

    final result = await Navigator.of(
      context,
    ).push<String>(MaterialPageRoute(builder: (_) => const ScanPage()));

    if (!mounted) return;

    setState(() {
      isScanning = false;
    });

    if (result == null || result.isEmpty) {
      return;
    }

    setState(() {
      scanResult = result;
      scanStatus = _t('扫码成功', 'Scan successful');
    });

    await _handleScanResult(result);
  }

  Future<void> _handleScanResult(String payload) async {
    if (!payload.startsWith('phoneshell://')) {
      setState(() {
        parseError = _t('无效的二维码格式', 'Invalid QR code format');
      });
      return;
    }

    final params = _parseQrParams(payload);

    if (payload.startsWith('phoneshell://connect')) {
      _handleConnectQr(params);
    } else if (payload.startsWith('phoneshell://login')) {
      _handleLoginQr(params);
    } else if (payload.startsWith('phoneshell://bind')) {
      await _handleBindQr(params);
    } else {
      setState(() {
        parseError = _t('未知的二维码类型', 'Unknown QR code type');
      });
    }
  }

  void _handleConnectQr(Map<String, String> params) {
    final wsUrl = params['ws'] ?? '';
    final httpUrl = params['http'] ?? '';
    final deviceId = params['deviceId'] ?? '';
    final displayName = params['displayName'] ?? deviceId;

    if (wsUrl.isEmpty || deviceId.isEmpty) {
      setState(() {
        parseError = _t(
          '二维码缺少必要参数 (ws/deviceId)',
          'QR code missing required params (ws/deviceId)',
        );
      });
      return;
    }

    if (ConnectionManager.instance.isInGroup()) {
      if (httpUrl.isEmpty) {
        setState(() {
          parseError = _t(
            '该设备不支持邀请加入群组（缺少HTTP地址）',
            'This device cannot be invited (missing HTTP address)',
          );
        });
        return;
      }
      setState(() {
        inviteDeviceName = displayName;
        scanInviteHttpUrl = httpUrl;
        scanInviteDeviceId = '';
        showInviteConfirmDialog = true;
      });
      return;
    }

    setState(() {
      scanStatus = _t('正在连接 $displayName...', 'Connecting to $displayName...');
    });

    ConnectionManager.instance.connectSingle(
      wsUrl,
      deviceId,
      displayName,
      httpUrl,
    );

    setState(() {
      scanStatus = _t('已添加设备 $displayName', 'Added device $displayName');
    });

    _refreshDeviceView();
  }

  void _inviteDeviceToGroup() {
    setState(() {
      showInviteConfirmDialog = false;
    });
    final httpUrl = scanInviteHttpUrl;
    final deviceName = inviteDeviceName;
    if (httpUrl.isEmpty) return;

    setState(() {
      scanStatus = _t(
        '正在邀请 $deviceName 加入群组...',
        'Inviting $deviceName to join the group...',
      );
    });

    _cleanupScanListeners();
    scanMessageListenerId = ConnectionManager.instance.addOnMessage((
      type,
      data,
    ) {
      if (type == 'invite.create.response') {
        final inviteCode = (data['inviteCode'] ?? '') as String;
        final relayUrl = (data['relayUrl'] ?? '') as String;
        if (inviteCode.isNotEmpty && relayUrl.isNotEmpty) {
          setState(() {
            scanStatus = _t(
              '正在发送邀请给 $deviceName...',
              'Sending invite to $deviceName...',
            );
          });
          ConnectionManager.instance.inviteToGroup(
            httpUrl,
            inviteCode,
            relayUrl,
            (success, message) {
              if (!mounted) return;
              if (success) {
                setState(() {
                  scanStatus = _t(
                    '已邀请 $deviceName 加入群组',
                    'Invited $deviceName to join the group',
                  );
                });
                if (scanInviteDeviceId.isNotEmpty) {
                  pendingServerChangeDeviceId = scanInviteDeviceId;
                  scanInviteDeviceId = '';
                  _tryRequestServerChange();
                }
                _cleanupScanListeners();
                _refreshDeviceView();
              } else {
                setState(() {
                  parseError = _t(
                    '邀请发送失败: $message',
                    'Invite failed: $message',
                  );
                  scanStatus = _t('邀请失败', 'Invite failed');
                });
                scanInviteDeviceId = '';
                _cleanupScanListeners();
              }
            },
          );
        }
      } else if (type == 'error') {
        final message = (data['message'] ?? '') as String;
        setState(() {
          parseError = _t('邀请失败: $message', 'Invite failed: $message');
          scanStatus = _t('邀请失败', 'Invite failed');
        });
        scanInviteDeviceId = '';
        _cleanupScanListeners();
      }
    });

    ConnectionManager.instance.requestInviteCode();
  }

  Future<void> _handleBindQr(Map<String, String> params) async {
    final server = params['server'] ?? '';
    final groupId = params['groupId'] ?? '';
    final groupSecret = params['groupSecret'] ?? '';

    if (server.isEmpty || groupSecret.isEmpty) {
      setState(() {
        parseError = _t(
          '二维码缺少必要参数 (server/groupSecret)',
          'QR code missing required params (server/groupSecret)',
        );
      });
      return;
    }

    if (ConnectionManager.instance.isInGroup()) {
      final currentGroupId = ConnectionManager.instance.getGroupId();
      if (currentGroupId == groupId) {
        setState(() {
          scanStatus = _t('已在该群组中', 'Already in this group');
        });
        return;
      }
      setState(() {
        scanStatus = _t('正在切换到新服务器...', 'Switching to new server...');
        parseError = '';
      });
      ConnectionManager.instance.requestGroupServerChangeExternal(
        server,
        groupId,
        groupSecret,
      );
      return;
    }

    await _executeBindQr(server, groupId, groupSecret);
  }

  String _wsUrlToHttpUrl(String wsUrl) {
    var url = wsUrl;
    final qIdx = url.indexOf('?');
    if (qIdx >= 0) url = url.substring(0, qIdx);
    if (url.startsWith('wss://')) {
      url = 'https://' + url.substring(6);
    } else if (url.startsWith('ws://')) {
      url = 'http://' + url.substring(5);
    }
    if (url.endsWith('/ws/')) {
      url = url.substring(0, url.length - 4);
    } else if (url.endsWith('/ws')) {
      url = url.substring(0, url.length - 3);
    }
    if (url.endsWith('/')) {
      url = url.substring(0, url.length - 1);
    }
    return url;
  }

  Future<void> _executeBindQr(
    String server,
    String groupId,
    String groupSecret,
  ) async {
    await PreferencesUtil.setLastServerUrl(server);
    await PreferencesUtil.setString(StorageKeys.groupSecret, groupSecret);

    setState(() {
      final shortServer = server.substring(
        0,
        server.length > 40 ? 40 : server.length,
      );
      scanStatus = _t(
        '正在连接群组 $shortServer...',
        'Connecting to group $shortServer...',
      );
    });

    final connectUrl = _buildConnectUrlWithToken(server, groupSecret);
    ConnectionManager.instance.connectGroup(connectUrl, groupSecret, groupId);

    _cleanupScanListeners();
    scanMessageListenerId = ConnectionManager.instance.addOnMessage((type, _) {
      if (type == 'group.join.accepted') {
        _cleanupScanListeners();
        setState(() {
          scanStatus = '';
          parseError = '';
          scanResult = '';
          _syncDeviceLists();
          isInGroup = true;
          currentMode = DeviceMode.group;
          this.groupId = groupId.isNotEmpty
              ? groupId
              : ConnectionManager.instance.getGroupId();
        });
        ConnectionManager.instance.sendMobileBindRequest(groupId);
        ConnectionManager.instance.requestDeviceList();
      } else if (type == 'group.join.rejected') {
        _cleanupScanListeners();
        setState(() {
          parseError = _t('加入群组被拒绝', 'Group join was rejected');
          scanStatus = _t('加入群组失败', 'Failed to join group');
        });
      }
    });

    scanStateListenerId = ConnectionManager.instance.addOnStateChange((
      state,
      _,
    ) {
      if (state == ConnectionState.connected) {
        ConnectionManager.instance.sendGroupJoinRequest(groupSecret);
      }
    });

    if (ConnectionManager.instance.getConnectionStateForDevice('group') ==
        ConnectionState.connected) {
      ConnectionManager.instance.sendGroupJoinRequest(groupSecret);
    }
  }

  void _handleLoginQr(Map<String, String> params) {
    final server = params['server'] ?? '';
    final requestId = params['requestId'] ?? '';

    if (server.isEmpty || requestId.isEmpty) {
      setState(() {
        parseError = _t('登录码缺少必要参数', 'Login code missing required params');
      });
      return;
    }

    setState(() {
      scanResult = '';
      scanStatus = _t('正在处理登录请求...', 'Processing login request...');
    });

    _cleanupScanListeners();
    scanMessageListenerId = ConnectionManager.instance.addOnMessage((
      type,
      data,
    ) {
      if (type == 'error') {
        final code = (data['code'] ?? '') as String;
        final message = (data['message'] ?? '') as String;
        if (code == 'not_bound_mobile') {
          setState(() {
            scanStatus = _t(
              '登录失败：需要重新连接群组',
              'Login failed: reconnect to the group',
            );
            parseError = _t(
              '手机未在群组中注册，请返回设备管理页等待自动重连',
              'This phone is not registered in the group. Return to Device Center and wait for auto-reconnect.',
            );
          });
        } else if (code == 'login_session_not_found') {
          setState(() {
            scanStatus = _t('登录失败：登录会话已过期', 'Login failed: session expired');
            parseError = _t(
              '请刷新网页重新获取二维码',
              'Please refresh the web page to get a new QR code',
            );
          });
        } else {
          setState(() {
            scanStatus = _t('登录失败', 'Login failed');
            parseError = message.isNotEmpty
                ? message
                : _t('未知错误', 'Unknown error');
          });
        }
        _cleanupScanListeners();
      } else if (type == 'panel.login.approved') {
        setState(() {
          scanStatus = _t('网页登录已批准', 'Web login approved');
        });
        _cleanupScanListeners();
      }
    });

    if (!ConnectionManager.instance.isInGroup()) {
      setState(() {
        scanStatus = _t('正在重新连接群组...', 'Reconnecting to group...');
      });
      scanStateListenerId = ConnectionManager.instance.addOnStateChange((
        state,
        _,
      ) {
        if (state == ConnectionState.connected) {
          // Wait for join accepted
        }
      });
      int? joinListenerId;
      joinListenerId = ConnectionManager.instance.addOnMessage((type, _) {
        if (type == 'group.join.accepted') {
          ConnectionManager.instance.removeOnMessage(joinListenerId!);
          setState(() {
            scanStatus = _t(
              '已重新连接，正在发送登录确认...',
              'Reconnected. Sending login confirmation...',
            );
          });
          ConnectionManager.instance.sendPanelLoginScan(requestId);
        }
      });
      return;
    }

    ConnectionManager.instance.sendPanelLoginScan(requestId);
    setState(() {
      scanStatus = _t('已发送登录确认', 'Login confirmation sent.');
    });
  }

  String _buildConnectUrlWithToken(String baseUrl, String groupSecret) {
    final trimmedSecret = groupSecret.trim();
    if (trimmedSecret.isEmpty) return baseUrl;
    if (baseUrl.contains('token=')) return baseUrl;
    final token = Uri.encodeComponent(trimmedSecret);
    if (baseUrl.contains('?')) return '$baseUrl&token=$token';
    return '$baseUrl?token=$token';
  }

  Map<String, String> _parseQrParams(String payload) {
    final result = <String, String>{};
    final idx = payload.indexOf('?');
    if (idx < 0) return result;
    final query = payload.substring(idx + 1);
    final pairs = query.split('&');
    for (final pair in pairs) {
      final eq = pair.indexOf('=');
      if (eq > 0) {
        final key = pair.substring(0, eq);
        final value = Uri.decodeComponent(pair.substring(eq + 1));
        result[key] = value;
      }
    }
    return result;
  }

  void _applyGroupExitLocal() {
    isInGroup = false;
    groupId = '';
    groupDevices = [];
    groupMembers = [];
    currentMode = singleDevices.isNotEmpty
        ? DeviceMode.single
        : DeviceMode.standalone;
    pendingServerChangeDeviceId = '';
  }

  void _removeDeviceFromGroupLocal(String deviceId) {
    if (deviceId.isEmpty) return;
    groupDevices = groupDevices.where((d) => d.deviceId != deviceId).toList();
    groupMembers = groupMembers.where((m) => m.deviceId != deviceId).toList();
  }

  void _moveDeviceToGroup(DeviceItemModel device) {
    if (!ConnectionManager.instance.isInGroup()) {
      ConnectionManager.instance.designateRelay(device.deviceId);
    } else {
      final record = ConnectionManager.instance.getSingleDeviceRecord(
        device.deviceId,
      );
      final httpUrl = record?.httpUrl ?? '';
      if (httpUrl.isEmpty) return;
      pendingInviteDeviceId = device.deviceId;
      pendingInviteHttpUrl = httpUrl;
      ConnectionManager.instance.requestInviteCode();
    }
  }

  GroupMemberModel? _findGroupMember(String deviceId) {
    for (final member in groupMembers) {
      if (member.deviceId == deviceId) return member;
    }
    return null;
  }

  String _getOsIcon(String os) {
    final lower = os.toLowerCase();
    if (lower.contains('windows')) return '🖥';
    if (lower.contains('linux')) return '🐧';
    if (lower.contains('mac') || lower.contains('darwin')) return '🍎';
    return '💻';
  }

  String _getRoleLabel(String role) {
    if (role == 'Server') return _t('服务器', 'Server');
    if (role == 'Member') return _t('成员', 'Member');
    if (role == 'Mobile') return _t('手机', 'Mobile');
    if (role == 'Pending' || role == '单连') return _t('待加入', 'Pending');
    return role;
  }

  String _getAuthActionLabel(String action) {
    if (action == 'panel.login') return _t('面板登录', 'Panel Login');
    if (action == 'terminal.open.remote')
      return _t('打开远程终端', 'Open Remote Terminal');
    return action;
  }

  String _getModeLabel() {
    if (currentMode == DeviceMode.group) return _t('群组', 'Group');
    if (currentMode == DeviceMode.single) return _t('单连', 'Single');
    return _t('待机', 'Standby');
  }

  Color _getModeColor() {
    if (currentMode == DeviceMode.group) return const Color(AppColors.accent);
    if (currentMode == DeviceMode.single)
      return const Color(AppColors.accentBlue);
    return const Color(AppColors.textMuted);
  }

  double _getBottomScanMargin(double safeBottom) {
    const minOffset = 28.0;
    return (minOffset - safeBottom).clamp(0, minOffset);
  }

  @override
  Widget build(BuildContext context) {
    final padding = MediaQuery.of(context).padding;
    final safeBottom = padding.bottom;

    return Scaffold(
      body: Stack(
        children: [
          Column(
            children: [
              Container(
                height: 140,
                width: double.infinity,
                color: const Color(AppColors.accentDim).withOpacity(0.2),
              ),
              const Expanded(child: SizedBox()),
            ],
          ),
          Padding(
            padding: EdgeInsets.only(top: padding.top, bottom: padding.bottom),
            child: Column(
              children: [
                Container(
                  color: const Color(AppColors.surface1),
                  padding: EdgeInsets.fromLTRB(
                    AppSizes.paddingPage + padding.left,
                    12,
                    AppSizes.paddingPage + padding.right,
                    6,
                  ),
                  child: Column(
                    children: [
                      Row(
                        children: [
                          Transform.translate(
                            offset: const Offset(0, -2),
                            child: PhoneShellBrandBlock(
                              subtitle: _t(
                                '无缝切换你的终端会话',
                                'Seamless terminal session switch',
                              ),
                            ),
                          ),
                          const Spacer(),
                          Row(
                            children: [
                              _settingsChip(),
                              if (pendingAuthCount > 0) ...[
                                const SizedBox(width: 8),
                                SizedBox(
                                  height: 26,
                                  child: OutlinedButton(
                                    onPressed: () {
                                      AuthManager.instance.showPending();
                                    },
                                    style: OutlinedButton.styleFrom(
                                      side: const BorderSide(
                                        color: Color(AppColors.accentPink),
                                      ),
                                      backgroundColor: const Color(
                                        AppColors.highlight,
                                      ),
                                      padding: const EdgeInsets.symmetric(
                                        horizontal: 10,
                                      ),
                                      shape: RoundedRectangleBorder(
                                        borderRadius: BorderRadius.circular(
                                          AppSizes.borderRadiusTag,
                                        ),
                                      ),
                                    ),
                                    child: Text(
                                      'AUTH $pendingAuthCount',
                                      style: const TextStyle(
                                        fontSize: 11,
                                        color: Color(AppColors.accentPink),
                                      ),
                                    ),
                                  ),
                                ),
                              ],
                            ],
                          ),
                        ],
                      ),
                      const SizedBox(height: 4),
                    ],
                  ),
                ),
                Divider(color: const Color(AppColors.divider), height: 1),
                Expanded(child: _mainContent(padding)),
                if (isInGroup)
                  Container(
                    padding: EdgeInsets.fromLTRB(
                      AppSizes.paddingPage + padding.left,
                      10,
                      AppSizes.paddingPage + padding.right,
                      16,
                    ),
                    margin: EdgeInsets.only(
                      bottom: _getBottomScanMargin(safeBottom),
                    ),
                    child: Container(
                      padding: const EdgeInsets.all(12),
                      decoration: BoxDecoration(
                        color: const Color(AppColors.surface1),
                        borderRadius: BorderRadius.circular(
                          AppSizes.borderRadiusCard,
                        ),
                        border: Border.all(
                          color: const Color(AppColors.cardBorder),
                        ),
                      ),
                      child: Row(
                        children: [
                          Expanded(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(
                                  _t('快捷操作', 'Quick Actions'),
                                  style: const TextStyle(
                                    fontSize: 12,
                                    color: Color(AppColors.textMuted),
                                    fontFamily: 'monospace',
                                  ),
                                ),
                                Text(
                                  _t('扫描二维码绑定/登录', 'Scan QR to bind/login'),
                                  style: const TextStyle(
                                    fontSize: 12,
                                    color: Color(AppColors.textSecondary),
                                  ),
                                ),
                              ],
                            ),
                          ),
                          SizedBox(
                            height: 40,
                            child: ElevatedButton(
                              onPressed: _startScan,
                              style: ElevatedButton.styleFrom(
                                backgroundColor: const Color(AppColors.accent),
                                shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(
                                    AppSizes.borderRadiusButton,
                                  ),
                                ),
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 16,
                                ),
                              ),
                              child: Text(
                                _t('扫  码', 'Scan'),
                                style: const TextStyle(
                                  color: Colors.white,
                                  fontSize: 14,
                                ),
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
          ),
          if (showSettingsSheet) _settingsSheet(padding),
          if (showDeviceRenameDialog) _deviceRenameDialog(padding),
          if (showAuthDialog) _authDialog(padding),
          if (showInviteConfirmDialog) _inviteConfirmDialog(padding),
        ],
      ),
    );
  }

  Widget _mainContent(EdgeInsets padding) {
    return SingleChildScrollView(
      padding: EdgeInsets.fromLTRB(
        AppSizes.paddingPage + padding.left,
        16,
        AppSizes.paddingPage + padding.right,
        16,
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _groupSection(),
          const SizedBox(height: 16),
          _pendingSection(),
        ],
      ),
    );
  }

  Widget _groupSection() {
    if (!isInGroup) {
      return _scanConnectSection();
    }
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: Text(
                    _t('群组设备', 'Group Devices'),
                    style: const TextStyle(
                      fontSize: AppSizes.fontSizeBody,
                      color: Color(AppColors.textPrimary),
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ),
              ),
              Row(
                children: [
                  _statusChip(
                    _t(
                      '${groupMembers.length} 成员',
                      '${groupMembers.length} MEMBERS',
                    ),
                    const Color(AppColors.accent),
                  ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 14),
          if (groupDevices.isEmpty)
            Text(
              _t('群组暂无设备', 'No devices in the group'),
              style: const TextStyle(
                fontSize: AppSizes.fontSizeSmall,
                color: Color(AppColors.textSecondary),
              ),
            )
          else
            Column(
              children: groupDevices
                  .map(
                    (device) => Container(
                      margin: const EdgeInsets.only(bottom: 12),
                      child: _deviceCard(
                        device,
                        allowMoveToGroup: false,
                        isPendingSingle: false,
                      ),
                    ),
                  )
                  .toList(),
            ),
          _scanStatusPanel(),
        ],
      ),
    );
  }

  Widget _scanConnectSection() {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      _t('扫码连接服务器', 'Scan to Connect Server'),
                      style: const TextStyle(
                        fontSize: AppSizes.fontSizeBody,
                        color: Color(AppColors.textPrimary),
                        fontWeight: FontWeight.w500,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      _t(
                        '对准 PC 端命令中心生成的二维码',
                        'Aim at the QR code generated by the PC command center',
                      ),
                      style: const TextStyle(
                        fontSize: 12,
                        color: Color(AppColors.textSecondary),
                      ),
                    ),
                  ],
                ),
              ),
              _statusChip(_t('连接', 'LINK'), const Color(AppColors.accentBlue)),
            ],
          ),
          const SizedBox(height: 12),
          Text(
            _t(
              '将手机加入群组并绑定控制权限。',
              'Join the group and bind control permissions.',
            ),
            style: const TextStyle(
              fontSize: AppSizes.fontSizeSmall,
              color: Color(AppColors.textSecondary),
            ),
          ),
          const SizedBox(height: 20),
          SizedBox(
            width: double.infinity,
            height: 44,
            child: ElevatedButton(
              onPressed: isScanning ? null : _startScan,
              style: ElevatedButton.styleFrom(
                backgroundColor: const Color(AppColors.accent),
                disabledBackgroundColor: const Color(AppColors.accent),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(
                    AppSizes.borderRadiusButton,
                  ),
                ),
              ),
              child: Text(
                isScanning
                    ? _t('扫码中...', 'Scanning...')
                    : _t('开始扫码', 'Start Scan'),
                style: const TextStyle(
                  fontSize: AppSizes.fontSizeBody,
                  color: Colors.white,
                ),
              ),
            ),
          ),
          const SizedBox(height: 16),
          _scanStatusPanel(),
        ],
      ),
    );
  }

  Widget _scanStatusPanel() {
    if (scanStatus.isEmpty && parseError.isEmpty) {
      return const SizedBox.shrink();
    }
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.only(top: 8),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface2),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (scanStatus.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Text(
                scanStatus,
                style: const TextStyle(
                  fontSize: AppSizes.fontSizeSmall,
                  color: Color(AppColors.textSecondary),
                ),
              ),
            ),
          if (parseError.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Text(
                parseError,
                style: const TextStyle(
                  fontSize: AppSizes.fontSizeSmall,
                  color: Color(AppColors.errorRed),
                ),
              ),
            ),
        ],
      ),
    );
  }

  Widget _pendingSection() {
    if (singleDevices.isEmpty) {
      return const SizedBox.shrink();
    }
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text(
                _t('待加入设备', 'Pending Devices'),
                style: const TextStyle(
                  fontSize: AppSizes.fontSizeSmall,
                  color: Color(AppColors.textPrimary),
                  fontWeight: FontWeight.w500,
                ),
              ),
              const Spacer(),
              Text(
                _t('长按移入群组', 'Long press to move into group'),
                style: const TextStyle(
                  fontSize: 12,
                  color: Color(AppColors.textMuted),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Column(
            children: singleDevices
                .map(
                  (device) => Container(
                    margin: const EdgeInsets.only(bottom: 12),
                    child: _deviceCard(
                      device,
                      allowMoveToGroup: true,
                      isPendingSingle: true,
                    ),
                  ),
                )
                .toList(),
          ),
        ],
      ),
    );
  }

  Widget _deviceCard(
    DeviceItemModel device, {
    required bool allowMoveToGroup,
    required bool isPendingSingle,
  }) {
    return GestureDetector(
      onTap: () => _onDeviceClick(device),
      onLongPress: () {
        if (allowMoveToGroup && device.isOnline) {
          _moveDeviceToGroup(device);
        }
      },
      child: Container(
        width: double.infinity,
        padding: const EdgeInsets.all(AppSizes.paddingCard),
        decoration: BoxDecoration(
          color: const Color(AppColors.cardBackground),
          borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
          border: Border.all(color: const Color(AppColors.cardBorder)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              height: 2,
              width: double.infinity,
              margin: const EdgeInsets.only(bottom: 12),
              color: device.isOnline
                  ? const Color(AppColors.accent)
                  : const Color(AppColors.cardBorder),
            ),
            Row(
              children: [
                Container(
                  width: 38,
                  height: 38,
                  decoration: const BoxDecoration(
                    color: Color(AppColors.surface3),
                    shape: BoxShape.circle,
                  ),
                  alignment: Alignment.center,
                  child: Text(
                    _getOsIcon(device.os),
                    style: const TextStyle(fontSize: 18),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        device.displayName,
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
                        device.os,
                        style: const TextStyle(
                          fontSize: AppSizes.fontSizeSmall,
                          color: Color(AppColors.textSecondary),
                        ),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        'ID ${device.deviceId}',
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
                Column(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    GestureDetector(
                      onTap: () => _openDeviceSettings(device, isPendingSingle),
                      child: const Padding(
                        padding: EdgeInsets.all(6),
                        child: Text(
                          '⚙',
                          style: TextStyle(
                            fontSize: 18,
                            color: Color(AppColors.textSecondary),
                          ),
                        ),
                      ),
                    ),
                    Container(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 8,
                        vertical: 4,
                      ),
                      decoration: BoxDecoration(
                        color: const Color(AppColors.chipBg),
                        borderRadius: BorderRadius.circular(
                          AppSizes.borderRadiusTag,
                        ),
                        border: Border.all(
                          color: Color(
                            device.isOnline
                                ? AppColors.onlineGreen
                                : AppColors.chipBorder,
                          ),
                        ),
                      ),
                      child: Row(
                        children: [
                          Container(
                            width: 6,
                            height: 6,
                            margin: const EdgeInsets.only(right: 6),
                            decoration: BoxDecoration(
                              color: Color(
                                device.isOnline
                                    ? AppColors.onlineGreen
                                    : AppColors.offlineGray,
                              ),
                              shape: BoxShape.circle,
                            ),
                          ),
                          Text(
                            device.isOnline
                                ? _t('在线', 'Online')
                                : _t('离线', 'Offline'),
                            style: TextStyle(
                              fontSize: 11,
                              color: Color(
                                device.isOnline
                                    ? AppColors.onlineGreen
                                    : AppColors.textMuted,
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ],
            ),
            if (device.availableShells.isNotEmpty) ...[
              const SizedBox(height: 12),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: device.availableShells
                    .map(
                      (shell) => Container(
                        padding: const EdgeInsets.symmetric(
                          horizontal: 8,
                          vertical: 4,
                        ),
                        decoration: BoxDecoration(
                          color: const Color(AppColors.highlight),
                          borderRadius: BorderRadius.circular(
                            AppSizes.borderRadiusTag,
                          ),
                          border: Border.all(
                            color: const Color(AppColors.accentDim),
                          ),
                        ),
                        child: Text(
                          shell,
                          style: const TextStyle(
                            fontSize: 12,
                            color: Color(AppColors.accent),
                          ),
                        ),
                      ),
                    )
                    .toList(),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _settingsChip() {
    return GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: () {
        _openSettingsPage();
      },
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: const Color(AppColors.chipBg),
          borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
          border: Border.all(color: const Color(AppColors.accent)),
        ),
        child: Text(
          _t('设置', 'Settings'),
          style: const TextStyle(
            fontSize: 11,
            color: Color(AppColors.accent),
            fontFamily: 'monospace',
          ),
        ),
      ),
    );
  }

  Widget _statusChip(String label, Color color) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: const Color(AppColors.chipBg),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
        border: Border.all(color: color),
      ),
      child: Row(
        children: [
          Container(
            width: 6,
            height: 6,
            margin: const EdgeInsets.only(right: 6),
            decoration: BoxDecoration(color: color, shape: BoxShape.circle),
          ),
          Text(
            label,
            style: TextStyle(
              fontSize: 11,
              color: color,
              fontFamily: 'monospace',
            ),
          ),
        ],
      ),
    );
  }

  Widget _settingsSheet(EdgeInsets padding) {
    return Positioned.fill(
      child: Stack(
        children: [
          GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTap: _closeSettingsSheet,
            child: Container(color: const Color(0xB0000000)),
          ),
          Align(
            alignment: Alignment.bottomCenter,
            child: Container(
              width: double.infinity,
              padding: EdgeInsets.fromLTRB(
                24 + padding.left,
                24,
                24 + padding.right,
                24 + padding.bottom,
              ),
              decoration: BoxDecoration(
                color: const Color(AppColors.surface2),
                borderRadius: const BorderRadius.only(
                  topLeft: Radius.circular(16),
                  topRight: Radius.circular(16),
                ),
                border: Border.all(color: const Color(AppColors.cardBorder)),
              ),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    _t('设备设置', 'Device Settings'),
                    style: const TextStyle(
                      fontSize: AppSizes.fontSizeSubtitle,
                      color: Color(AppColors.textPrimary),
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                  const SizedBox(height: 6),
                  Text(
                    settingsDisplayName,
                    style: const TextStyle(
                      fontSize: 12,
                      color: Color(AppColors.textMuted),
                      fontFamily: 'monospace',
                    ),
                  ),
                  const SizedBox(height: 18),
                  Row(
                    children: [
                      SizedBox(
                        width: 48,
                        child: Text(
                          _t('名称:', 'Name:'),
                          style: const TextStyle(
                            fontSize: AppSizes.fontSizeSmall,
                            color: Color(AppColors.textSecondary),
                          ),
                        ),
                      ),
                      Expanded(
                        child: TextField(
                          controller: _settingsNameController,
                          onChanged: (value) => settingsEditName = value,
                          style: const TextStyle(
                            fontSize: AppSizes.fontSizeBody,
                            color: Color(AppColors.textPrimary),
                          ),
                          decoration: InputDecoration(
                            filled: true,
                            fillColor: const Color(AppColors.inputBg),
                            border: OutlineInputBorder(
                              borderRadius: BorderRadius.circular(
                                AppSizes.borderRadiusInput,
                              ),
                              borderSide: BorderSide.none,
                            ),
                            contentPadding: const EdgeInsets.symmetric(
                              horizontal: 12,
                              vertical: 10,
                            ),
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),
                  Row(
                    children: [
                      SizedBox(
                        width: 48,
                        child: Text(
                          _t('系统:', 'OS:'),
                          style: const TextStyle(
                            fontSize: AppSizes.fontSizeSmall,
                            color: Color(AppColors.textSecondary),
                          ),
                        ),
                      ),
                      Text(
                        settingsOs,
                        style: const TextStyle(
                          fontSize: AppSizes.fontSizeBody,
                          color: Color(AppColors.textPrimary),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),
                  Row(
                    children: [
                      SizedBox(
                        width: 48,
                        child: Text(
                          _t('角色:', 'Role:'),
                          style: const TextStyle(
                            fontSize: AppSizes.fontSizeSmall,
                            color: Color(AppColors.textSecondary),
                          ),
                        ),
                      ),
                      Text(
                        _getRoleLabel(settingsRole),
                        style: TextStyle(
                          fontSize: AppSizes.fontSizeBody,
                          color: settingsRole == 'Server'
                              ? const Color(AppColors.accent)
                              : const Color(AppColors.textPrimary),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 20),
                  SizedBox(
                    width: double.infinity,
                    height: 44,
                    child: ElevatedButton(
                      onPressed: _openDeviceRenameDialog,
                      style: ElevatedButton.styleFrom(
                        backgroundColor: const Color(AppColors.accent),
                        shape: RoundedRectangleBorder(
                          borderRadius: BorderRadius.circular(
                            AppSizes.borderRadiusButton,
                          ),
                        ),
                      ),
                      child: Text(
                        _t('重命名', 'Rename'),
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: AppSizes.fontSizeBody,
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(height: 8),
                  if (settingsIsGroupDevice) ...[
                    SizedBox(
                      width: double.infinity,
                      height: 44,
                      child: ElevatedButton(
                        onPressed: _kickDeviceFromGroup,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: const Color(AppColors.errorRed),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('踢出群组', 'Remove from Group'),
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: AppSizes.fontSizeBody,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(height: 8),
                    if (settingsRole != 'Server')
                      SizedBox(
                        width: double.infinity,
                        height: 44,
                        child: ElevatedButton(
                          onPressed: () {
                            ConnectionManager.instance.requestGroupServerChange(
                              settingsDeviceId,
                            );
                            _closeSettingsSheet();
                          },
                          style: ElevatedButton.styleFrom(
                            backgroundColor: const Color(AppColors.accentBlue),
                            shape: RoundedRectangleBorder(
                              borderRadius: BorderRadius.circular(
                                AppSizes.borderRadiusButton,
                              ),
                            ),
                          ),
                          child: Text(
                            _t('指定为服务器', 'Set as Server'),
                            style: const TextStyle(
                              color: Colors.white,
                              fontSize: AppSizes.fontSizeBody,
                            ),
                          ),
                        ),
                      ),
                    const SizedBox(height: 8),
                  ],
                  if (!settingsIsGroupDevice)
                    SizedBox(
                      width: double.infinity,
                      height: 44,
                      child: OutlinedButton(
                        onPressed: _disconnectSingleDevice,
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(
                            color: Color(AppColors.errorRed),
                          ),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('断开连接', 'Disconnect'),
                          style: const TextStyle(
                            color: Color(AppColors.errorRed),
                            fontSize: AppSizes.fontSizeBody,
                          ),
                        ),
                      ),
                    ),
                  const SizedBox(height: 8),
                  SizedBox(
                    width: double.infinity,
                    height: 44,
                    child: TextButton(
                      onPressed: _closeSettingsSheet,
                      style: TextButton.styleFrom(
                        foregroundColor: const Color(AppColors.textSecondary),
                      ),
                      child: Text(
                        _t('取消', 'Cancel'),
                        style: const TextStyle(fontSize: AppSizes.fontSizeBody),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _authDialog(EdgeInsets padding) {
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
                  _t('授权请求', 'Authorization Request'),
                  style: const TextStyle(
                    fontSize: AppSizes.fontSizeSubtitle,
                    color: Color(AppColors.textPrimary),
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 16),
                Text(
                  authRequesterName.isNotEmpty
                      ? _t(
                          '$authRequesterName 请求',
                          '$authRequesterName request',
                        )
                      : _t('收到授权请求', 'Authorization request received'),
                  style: const TextStyle(
                    fontSize: AppSizes.fontSizeBody,
                    color: Color(AppColors.textPrimary),
                  ),
                ),
                const SizedBox(height: 8),
                if (authDescription.isNotEmpty)
                  Text(
                    authDescription,
                    textAlign: TextAlign.center,
                    style: const TextStyle(
                      fontSize: AppSizes.fontSizeSmall,
                      color: Color(AppColors.textSecondary),
                    ),
                  ),
                const SizedBox(height: 8),
                Text(
                  _t(
                    '操作: ${_getAuthActionLabel(authAction)}',
                    'Action: ${_getAuthActionLabel(authAction)}',
                  ),
                  style: const TextStyle(
                    fontSize: AppSizes.fontSizeSmall,
                    color: Color(AppColors.textSecondary),
                  ),
                ),
                const SizedBox(height: 20),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    SizedBox(
                      width: 120,
                      height: 44,
                      child: OutlinedButton(
                        onPressed: _rejectAuth,
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(
                            color: Color(AppColors.textMuted),
                          ),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('拒绝', 'Reject'),
                          style: const TextStyle(
                            color: Color(AppColors.textPrimary),
                            fontSize: AppSizes.fontSizeBody,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    SizedBox(
                      width: 120,
                      height: 44,
                      child: ElevatedButton(
                        onPressed: _approveAuth,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: const Color(AppColors.accent),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('批准', 'Approve'),
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: AppSizes.fontSizeBody,
                          ),
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

  Widget _deviceRenameDialog(EdgeInsets padding) {
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
                  _t('重命名设备', 'Rename Device'),
                  style: const TextStyle(
                    fontSize: 18,
                    color: Color(AppColors.textPrimary),
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 8),
                if (renameDeviceTitle.isNotEmpty)
                  Text(
                    renameDeviceTitle,
                    style: const TextStyle(
                      fontSize: 12,
                      color: Color(AppColors.textMuted),
                      fontFamily: 'monospace',
                    ),
                  ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    SizedBox(
                      width: 48,
                      child: Text(
                        _t('名称:', 'Name:'),
                        style: const TextStyle(
                          fontSize: 12,
                          color: Color(AppColors.textSecondary),
                        ),
                      ),
                    ),
                    Expanded(
                      child: TextField(
                        controller: _renameDeviceController,
                        style: const TextStyle(
                          fontSize: AppSizes.fontSizeBody,
                          color: Color(AppColors.textPrimary),
                        ),
                        decoration: InputDecoration(
                          filled: true,
                          fillColor: const Color(AppColors.inputBg),
                          border: OutlineInputBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusInput,
                            ),
                            borderSide: BorderSide.none,
                          ),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 10,
                          ),
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
                        onPressed: _closeDeviceRenameDialog,
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(
                            color: Color(AppColors.textMuted),
                          ),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('取消', 'Cancel'),
                          style: const TextStyle(
                            color: Color(AppColors.textPrimary),
                            fontSize: AppSizes.fontSizeSmall,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    SizedBox(
                      width: 120,
                      height: 40,
                      child: ElevatedButton(
                        onPressed: _confirmDeviceRename,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: const Color(AppColors.accent),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('确认', 'Confirm'),
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: AppSizes.fontSizeSmall,
                          ),
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

  Widget _inviteConfirmDialog(EdgeInsets padding) {
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
                  _t('邀请设备加入群组', 'Invite Device to Group'),
                  style: const TextStyle(
                    fontSize: 18,
                    color: Color(AppColors.textPrimary),
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 10),
                Text(
                  _t(
                    '是否将 $inviteDeviceName 邀请加入当前群组？',
                    'Invite $inviteDeviceName to join this group?',
                  ),
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                    fontSize: 13,
                    color: Color(AppColors.textSecondary),
                  ),
                ),
                const SizedBox(height: 18),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    SizedBox(
                      width: 120,
                      height: 40,
                      child: OutlinedButton(
                        onPressed: () {
                          setState(() {
                            showInviteConfirmDialog = false;
                            scanInviteDeviceId = '';
                          });
                        },
                        style: OutlinedButton.styleFrom(
                          side: const BorderSide(
                            color: Color(AppColors.textMuted),
                          ),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('取消', 'Cancel'),
                          style: const TextStyle(
                            color: Color(AppColors.textPrimary),
                            fontSize: AppSizes.fontSizeSmall,
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 12),
                    SizedBox(
                      width: 140,
                      height: 40,
                      child: ElevatedButton(
                        onPressed: _inviteDeviceToGroup,
                        style: ElevatedButton.styleFrom(
                          backgroundColor: const Color(AppColors.accent),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(
                              AppSizes.borderRadiusButton,
                            ),
                          ),
                        ),
                        child: Text(
                          _t('邀请加入', 'Invite'),
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: AppSizes.fontSizeSmall,
                          ),
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
