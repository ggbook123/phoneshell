import 'dart:ui' as ui;

import 'package:flutter/foundation.dart';

import 'constants.dart';
import 'preferences_util.dart';

enum AppLanguage { zh, en }

class I18n {
  static AppLanguage _currentLanguage = AppLanguage.zh;
  static bool _hasLocalOverride = false;
  static final ValueNotifier<AppLanguage> languageNotifier = ValueNotifier(_currentLanguage);

  static AppLanguage getLanguage() => _currentLanguage;

  static Future<AppLanguage> syncFromPreferences() async {
    if (_hasLocalOverride) {
      await PreferencesUtil.setString(StorageKeys.appLanguage, _languageToCode(_currentLanguage));
      return _currentLanguage;
    }
    final saved = await PreferencesUtil.getString(StorageKeys.appLanguage);
    if (saved == 'en') {
      _currentLanguage = AppLanguage.en;
    } else if (saved == 'zh') {
      _currentLanguage = AppLanguage.zh;
    } else {
      _currentLanguage = _systemLanguage();
    }
    languageNotifier.value = _currentLanguage;
    return _currentLanguage;
  }

  static void setLanguage(AppLanguage language) {
    _currentLanguage = language;
    _hasLocalOverride = true;
    languageNotifier.value = _currentLanguage;
    PreferencesUtil.setString(StorageKeys.appLanguage, _languageToCode(language));
  }

  static String t(AppLanguage language, String zhText, String enText) {
    return language == AppLanguage.en ? enText : zhText;
  }

  static String tCurrent(String zhText, String enText) {
    return t(_currentLanguage, zhText, enText);
  }

  static String _languageToCode(AppLanguage language) {
    return language == AppLanguage.en ? 'en' : 'zh';
  }

  static AppLanguage _systemLanguage() {
    final locale = ui.PlatformDispatcher.instance.locale;
    final code = locale.languageCode.toLowerCase();
    if (code == 'zh') return AppLanguage.zh;
    if (code == 'en') return AppLanguage.en;
    return AppLanguage.en;
  }
}
