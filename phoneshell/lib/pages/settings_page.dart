import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/constants.dart';
import '../core/i18n.dart';

class SettingsPage extends StatelessWidget {
  const SettingsPage({super.key});

  String _t(String zh, String en) => I18n.tCurrent(zh, en);

  void _goBack(BuildContext context) {
    Navigator.of(context).pop();
  }

  void _openUsageGuide(BuildContext context) {
    Navigator.of(context).pushNamed('pages/UsageGuidePage');
  }

  void _openAboutPage(BuildContext context) {
    Navigator.of(context).pushNamed('pages/AboutPage');
  }

  Future<void> _openPrivacyPolicy() async {
    final uri = Uri.tryParse(AppStrings.privacyPolicyUrl);
    if (uri == null) return;
    await launchUrl(uri, mode: LaunchMode.externalApplication);
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
                                onTap: () => _goBack(context),
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
                                      _t('设置', 'Settings'),
                                      style: const TextStyle(
                                        fontSize: AppSizes.fontSizeBody,
                                        color: Color(AppColors.textPrimary),
                                        fontWeight: FontWeight.w500,
                                      ),
                                    ),
                                    Text(
                                      _t('操作说明与软件介绍', 'Usage Guide + About'),
                                      style: const TextStyle(
                                        fontSize: 11,
                                        color: Color(AppColors.textMuted),
                                        fontFamily: 'monospace',
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
                  child: ListView(
                    padding: EdgeInsets.fromLTRB(
                      AppSizes.paddingPage + padding.left,
                      16,
                      AppSizes.paddingPage + padding.right,
                      16,
                    ),
                    children: [
                      _settingsItem(
                        context,
                        indexLabel: '01',
                        title: _t('操作说明', 'Usage Guide'),
                        subtitle: _t('使用流程与功能提示', 'How to use the app'),
                        accent: const Color(AppColors.accent),
                        onOpen: () => _openUsageGuide(context),
                      ),
                      const SizedBox(height: 12),
                      _settingsItem(
                        context,
                        indexLabel: '02',
                        title: _t('软件介绍', 'About'),
                        subtitle: _t('版本、目标与许可', 'Version and overview'),
                        accent: const Color(AppColors.accentBlue),
                        onOpen: () => _openAboutPage(context),
                      ),
                      const SizedBox(height: 12),
                      _settingsItem(
                        context,
                        indexLabel: '03',
                        title: _t('隐私政策', 'Privacy Policy'),
                        subtitle: _t('查看隐私条款', 'View privacy policy'),
                        accent: const Color(AppColors.accentPink),
                        onOpen: _openPrivacyPolicy,
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _settingsItem(
    BuildContext context, {
    required String indexLabel,
    required String title,
    required String subtitle,
    required Color accent,
    required VoidCallback onOpen,
  }) {
    return GestureDetector(
      onTap: onOpen,
      child: Container(
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
        decoration: BoxDecoration(
          color: const Color(AppColors.surface2),
          borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
          border: Border.all(color: const Color(AppColors.cardBorder)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Container(
              height: 2,
              margin: const EdgeInsets.only(bottom: 10),
              color: accent.withOpacity(0.6),
            ),
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
                  margin: const EdgeInsets.only(right: 12),
                  decoration: BoxDecoration(
                    color: const Color(AppColors.highlight),
                    borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                    border: Border.all(color: accent),
                  ),
                  child: Text(
                    indexLabel,
                    style: TextStyle(fontSize: 11, color: accent, fontFamily: 'monospace'),
                  ),
                ),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: const TextStyle(fontSize: AppSizes.fontSizeBody, color: Color(AppColors.textPrimary))),
                      Text(
                        subtitle,
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
                    border: Border.all(color: accent),
                  ),
                  child: Text(
                    'OPEN',
                    style: TextStyle(fontSize: 10, color: accent, fontFamily: 'monospace'),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
