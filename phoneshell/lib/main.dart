import 'package:flutter/material.dart';

import 'core/constants.dart';
import 'pages/about_page.dart';
import 'pages/device_manage_page.dart';
import 'pages/login_page.dart';
import 'pages/session_list_page.dart';
import 'pages/settings_page.dart';
import 'pages/terminal_page.dart';
import 'pages/usage_guide_page.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const PhoneShellApp());
}

class PhoneShellApp extends StatelessWidget {
  const PhoneShellApp({super.key});

  @override
  Widget build(BuildContext context) {
    final baseTheme = ThemeData.dark();
    return MaterialApp(
      title: 'PhoneShell',
      theme: baseTheme.copyWith(
        scaffoldBackgroundColor: const Color(AppColors.background),
        dividerColor: const Color(AppColors.divider),
        colorScheme: baseTheme.colorScheme.copyWith(
          primary: const Color(AppColors.accent),
          secondary: const Color(AppColors.accentBlue),
          error: const Color(AppColors.errorRed),
        ),
      ),
      debugShowCheckedModeBanner: false,
      initialRoute: 'pages/LoginPage',
      routes: {
        'pages/LoginPage': (_) => const LoginPage(),
        'pages/DeviceManagePage': (_) => const DeviceManagePage(),
        'pages/SettingsPage': (_) => const SettingsPage(),
        'pages/UsageGuidePage': (_) => const UsageGuidePage(),
        'pages/AboutPage': (_) => const AboutPage(),
        'pages/SessionListPage': (_) => const SessionListPage(),
        'pages/TerminalPage': (_) => const TerminalPage(),
      },
    );
  }
}
