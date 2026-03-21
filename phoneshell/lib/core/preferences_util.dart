import 'dart:convert';
import 'package:shared_preferences/shared_preferences.dart';

import 'constants.dart';

class PreferencesUtil {
  static SharedPreferences? _prefs;
  static String _cachedMobileDeviceId = '';
  static String _pendingDeviceId = '';
  static const int _maxHistoryItems = 10;

  static Future<void> init() async {
    try {
      _prefs = await SharedPreferences.getInstance();
      final savedId = _prefs?.getString(StorageKeys.mobileDeviceId) ?? '';
      if (savedId.isNotEmpty) {
        _cachedMobileDeviceId = savedId;
      } else if (_pendingDeviceId.isNotEmpty) {
        _cachedMobileDeviceId = _pendingDeviceId;
        await _prefs?.setString(StorageKeys.mobileDeviceId, _pendingDeviceId);
      }
      _pendingDeviceId = '';
    } catch (_) {
      // Ignore init errors
    }
  }

  static Future<List<String>> getConnectionHistory() async {
    if (_prefs == null) return [];
    try {
      final raw = _prefs?.getString(StorageKeys.connectionHistory) ?? '[]';
      final decoded = jsonDecode(raw);
      if (decoded is List) {
        return decoded.map((e) => e.toString()).toList();
      }
      return [];
    } catch (_) {
      return [];
    }
  }

  static Future<void> addConnectionHistory(String url) async {
    if (_prefs == null) return;
    try {
      var history = await getConnectionHistory();
      history = history.where((item) => item != url).toList();
      history.insert(0, url);
      if (history.length > _maxHistoryItems) {
        history = history.sublist(0, _maxHistoryItems);
      }
      await _prefs?.setString(StorageKeys.connectionHistory, jsonEncode(history));
    } catch (_) {
      // Ignore
    }
  }

  static Future<void> removeConnectionHistory(String url) async {
    if (_prefs == null) return;
    try {
      var history = await getConnectionHistory();
      history = history.where((item) => item != url).toList();
      await _prefs?.setString(StorageKeys.connectionHistory, jsonEncode(history));
    } catch (_) {
      // Ignore
    }
  }

  static Future<String> getLastServerUrl() async {
    return getString(StorageKeys.lastServerUrl);
  }

  static Future<void> setLastServerUrl(String url) async {
    await setString(StorageKeys.lastServerUrl, url);
  }

  static Future<String> getString(String key) async {
    if (_prefs == null) return '';
    try {
      return _prefs?.getString(key) ?? '';
    } catch (_) {
      return '';
    }
  }

  static Future<void> setString(String key, String value) async {
    if (_prefs == null) return;
    try {
      await _prefs?.setString(key, value);
    } catch (_) {
      // Ignore
    }
  }

  static Future<String> getSingleDevices() async => getString(StorageKeys.singleDevices);

  static Future<void> setSingleDevices(String json) async => setString(StorageKeys.singleDevices, json);

  static Future<String> getGroupRelayUrl() async => getString(StorageKeys.groupRelayUrl);

  static Future<void> setGroupRelayUrl(String url) async => setString(StorageKeys.groupRelayUrl, url);

  static Future<String> getGroupId() async => getString(StorageKeys.groupId);

  static Future<void> setGroupId(String id) async => setString(StorageKeys.groupId, id);

  static String getMobileDeviceId() {
    if (_cachedMobileDeviceId.isNotEmpty) {
      return _cachedMobileDeviceId;
    }
    final timePart = DateTime.now().millisecondsSinceEpoch.toRadixString(36);
    final randPart = (DateTime.now().microsecondsSinceEpoch ^ timePart.hashCode)
        .toRadixString(36)
        .replaceAll('-', '')
        .padLeft(6, '0')
        .substring(0, 6);
    _cachedMobileDeviceId = 'mobile-$timePart-$randPart';
    _pendingDeviceId = _cachedMobileDeviceId;
    setString(StorageKeys.mobileDeviceId, _cachedMobileDeviceId);
    return _cachedMobileDeviceId;
  }
}
