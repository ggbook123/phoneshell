import 'connection_manager.dart';

class AuthPendingRequest {
  String requestId = '';
  String action = '';
  String requesterName = '';
  String description = '';
  String targetDeviceId = '';
  int receivedAt = 0;
}

class AuthManager {
  AuthManager._();
  static final AuthManager instance = AuthManager._();

  final List<AuthPendingRequest> _pendingQueue = [];
  final Map<int, void Function(AuthPendingRequest)> _dialogCallbacks = {};
  final Map<int, void Function(String, bool)> _resolvedCallbacks = {};
  int _nextCallbackId = 1;
  int _authListenerId = -1;
  final int _maxPendingAgeMs = 300000;

  void init() {
    if (_authListenerId != -1) return;
    _authListenerId = ConnectionManager.instance.addOnAuthRequest(
      (requestId, action, requesterName, description, targetDeviceId) {
        _onAuthRequestReceived(requestId, action, requesterName, description, targetDeviceId);
      },
    );
  }

  void dispose() {
    if (_authListenerId != -1) {
      ConnectionManager.instance.removeOnAuthRequest(_authListenerId);
      _authListenerId = -1;
    }
    _pendingQueue.clear();
  }

  int addOnShowDialog(void Function(AuthPendingRequest) callback) {
    final id = _nextCallbackId++;
    _dialogCallbacks[id] = callback;
    return id;
  }

  void removeOnShowDialog(int id) {
    _dialogCallbacks.remove(id);
  }

  int addOnResolved(void Function(String, bool) callback) {
    final id = _nextCallbackId++;
    _resolvedCallbacks[id] = callback;
    return id;
  }

  void removeOnResolved(int id) {
    _resolvedCallbacks.remove(id);
  }

  void approve(String requestId) {
    ConnectionManager.instance.sendAuthResponse(requestId, true);
    _removeFromQueue(requestId);
    _emitResolved(requestId, true);
  }

  void reject(String requestId) {
    ConnectionManager.instance.sendAuthResponse(requestId, false);
    _removeFromQueue(requestId);
    _emitResolved(requestId, false);
  }

  int getPendingCount() {
    _pruneExpired();
    return _pendingQueue.length;
  }

  List<AuthPendingRequest> getPendingRequests() {
    _pruneExpired();
    return List<AuthPendingRequest>.from(_pendingQueue);
  }

  AuthPendingRequest? getNextPending() {
    _pruneExpired();
    if (_pendingQueue.isEmpty) return null;
    return _pendingQueue.first;
  }

  void showPending() {
    final next = getNextPending();
    if (next == null) return;
    _emitShowDialog(next);
  }

  void _onAuthRequestReceived(
    String requestId,
    String action,
    String requesterName,
    String description,
    String targetDeviceId,
  ) {
    for (final item in _pendingQueue) {
      if (item.requestId == requestId) return;
    }

    final req = AuthPendingRequest();
    req.requestId = requestId;
    req.action = action;
    req.requesterName = requesterName;
    req.description = description;
    req.targetDeviceId = targetDeviceId;
    req.receivedAt = DateTime.now().millisecondsSinceEpoch;

    _pendingQueue.add(req);
    _emitShowDialog(req);
  }

  void _removeFromQueue(String requestId) {
    _pendingQueue.removeWhere((r) => r.requestId == requestId);
  }

  void _pruneExpired() {
    final now = DateTime.now().millisecondsSinceEpoch;
    _pendingQueue.removeWhere((r) => (now - r.receivedAt) > _maxPendingAgeMs);
  }

  void _emitShowDialog(AuthPendingRequest request) {
    for (final cb in _dialogCallbacks.values.toList()) {
      try {
        cb(request);
      } catch (_) {
        // Ignore
      }
    }
  }

  void _emitResolved(String requestId, bool approved) {
    for (final cb in _resolvedCallbacks.values.toList()) {
      try {
        cb(requestId, approved);
      } catch (_) {
        // Ignore
      }
    }
  }
}
