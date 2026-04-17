import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'constants.dart';

class DeviceConnection {
  WebSocket? _socket;
  String _url = '';
  final String deviceId;
  String displayName;
  Timer? _reconnectTimer;
  int _reconnectAttempts = 0;
  final int _maxReconnectAttempts = 10;
  final int _reconnectBaseDelayMs = 1000;
  bool _shouldReconnect = false;
  int _connectionGeneration = 0;
  ConnectionState _state = ConnectionState.disconnected;

  final Map<int, void Function(String, Map<String, dynamic>)> _messageCallbacks = {};
  final Map<int, void Function(ConnectionState)> _stateCallbacks = {};
  int _nextCallbackId = 1;

  DeviceConnection(this.deviceId, this.displayName);

  ConnectionState get connectionState => _state;
  String get serverUrl => _url;

  void updateServerUrl(String url) {
    final trimmed = url.trim();
    if (trimmed.isEmpty) return;
    _url = trimmed;
  }

  int addOnMessage(void Function(String, Map<String, dynamic>) callback) {
    final id = _nextCallbackId++;
    _messageCallbacks[id] = callback;
    return id;
  }

  void removeOnMessage(int id) {
    _messageCallbacks.remove(id);
  }

  int addOnStateChange(void Function(ConnectionState) callback) {
    final id = _nextCallbackId++;
    _stateCallbacks[id] = callback;
    return id;
  }

  void removeOnStateChange(int id) {
    _stateCallbacks.remove(id);
  }

  void connect(String url) {
    if (_state == ConnectionState.connecting || _state == ConnectionState.connected) {
      disconnect();
    }
    _url = url.trim();
    if (_url.isEmpty) {
      _updateState(ConnectionState.disconnected);
      return;
    }
    _shouldReconnect = true;
    _reconnectAttempts = 0;
    _doConnect();
  }

  void disconnect() {
    _shouldReconnect = false;
    _connectionGeneration++;
    _clearReconnectTimer();
    final active = _socket;
    _socket = null;
    if (active != null) {
      _cleanupSocket(active);
    }
    _updateState(ConnectionState.disconnected);
  }

  void send(String json) {
    if (json.isEmpty) return;
    if (_socket != null && _state == ConnectionState.connected) {
      _socket!.add(json);
    }
  }

  void _cleanupSocket(WebSocket socket) {
    try {
      socket.close();
    } catch (_) {
      // Ignore
    }
  }

  void _doConnect() async {
    _connectionGeneration++;
    final generation = _connectionGeneration;

    if (_socket != null) {
      _cleanupSocket(_socket!);
      _socket = null;
    }

    _updateState(ConnectionState.connecting);

    try {
      final socket = await WebSocket.connect(_url);
      if (generation != _connectionGeneration) {
        socket.close();
        return;
      }
      _socket = socket;
      _reconnectAttempts = 0;
      _updateState(ConnectionState.connected);

      socket.listen(
        (dynamic data) {
          if (generation != _connectionGeneration) return;
          if (data is String) {
            _handleMessage(data);
          }
        },
        onError: (_) {
          if (generation != _connectionGeneration) return;
          _updateState(ConnectionState.disconnected);
          if (_shouldReconnect) _scheduleReconnect();
        },
        onDone: () {
          if (generation != _connectionGeneration) return;
          _updateState(ConnectionState.disconnected);
          if (_shouldReconnect) _scheduleReconnect();
        },
        cancelOnError: true,
      );
    } catch (_) {
      if (generation != _connectionGeneration) return;
      _updateState(ConnectionState.disconnected);
      if (_shouldReconnect) _scheduleReconnect();
    }
  }

  void _handleMessage(String raw) {
    if (raw.isEmpty) return;
    try {
      final decoded = jsonDecode(raw);
      if (decoded is Map<String, dynamic>) {
        final typeValue = decoded['type'];
        if (typeValue is String && typeValue.isNotEmpty) {
          _emitMessage(typeValue, decoded);
        }
      }
    } catch (_) {
      // Ignore parse errors
    }
  }

  void _updateState(ConnectionState state) {
    _state = state;
    for (final cb in _stateCallbacks.values.toList()) {
      try {
        cb(state);
      } catch (_) {
        // Ignore
      }
    }
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

  void _scheduleReconnect() {
    if (_reconnectAttempts >= _maxReconnectAttempts) {
      _shouldReconnect = false;
      return;
    }
    _clearReconnectTimer();
    final delay = (_reconnectBaseDelayMs * (1 << _reconnectAttempts)).clamp(0, 30000);
    _reconnectAttempts++;
    _reconnectTimer = Timer(Duration(milliseconds: delay), () {
      if (_shouldReconnect) _doConnect();
    });
  }

  void _clearReconnectTimer() {
    _reconnectTimer?.cancel();
    _reconnectTimer = null;
  }
}
