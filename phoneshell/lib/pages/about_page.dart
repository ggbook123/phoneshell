import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/constants.dart';
import '../core/i18n.dart';

class AboutPage extends StatelessWidget {
  const AboutPage({super.key});

  String _t(String zh, String en) => I18n.tCurrent(zh, en);

  void _goBack(BuildContext context) {
    Navigator.of(context).pop();
  }

  String _extractUrl(String text) {
    final match = RegExp(r'https?://\\S+').firstMatch(text);
    return match?.group(0) ?? '';
  }

  Future<void> _openUrl(String url) async {
    final uri = Uri.tryParse(url);
    if (uri == null) return;
    await launchUrl(uri, mode: LaunchMode.externalApplication);
  }

  Widget _buildLine(String line) {
    final url = _extractUrl(line);
    final isLink = url.isNotEmpty;
    final style = TextStyle(
      fontSize: 12,
      color: Color(isLink ? AppColors.accentBlue : AppColors.textSecondary),
      height: 18 / 12,
      decoration: isLink ? TextDecoration.underline : TextDecoration.none,
    );
    final text = Text(line, style: style);
    if (!isLink) return text;
    return GestureDetector(
      onTap: () => _openUrl(url),
      child: text,
    );
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
                      Container(height: 2, color: const Color(AppColors.accentBlue)),
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
                                  style: const TextStyle(fontSize: AppSizes.fontSizeSmall, color: Color(AppColors.accentBlue)),
                                ),
                              ),
                              const SizedBox(width: 12),
                              Expanded(
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  mainAxisAlignment: MainAxisAlignment.center,
                                  children: [
                                    Text(
                                      _t('软件介绍', 'About'),
                                      style: const TextStyle(
                                        fontSize: AppSizes.fontSizeBody,
                                        color: Color(AppColors.textPrimary),
                                        fontWeight: FontWeight.w500,
                                      ),
                                    ),
                                    Text(
                                      _t('产品定位与能力概览', 'Product overview'),
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
                      24,
                    ),
                    children: [
                      _sectionCard(
                        title: _t('软件介绍', 'About'),
                        subtitle: _t('使用说明', 'Instructions'),
                        lines: [
                          _t('PhoneShell在手机上获得电脑端原生 Shell 体验，通过远程到电脑端随时随地 AI 编程。',
                              'PhoneShell brings a native desktop shell to your phone, enabling AI coding anywhere via remote access.'),
                          _t('请于到github上下载电脑端：https://github.com/ggbook123/phoneshell',
                              'Download the desktop app from GitHub: https://github.com/ggbook123/phoneshell'),
                          _t('第一次请以管理员身份打开电脑端，电脑端点击启动，防火墙打开对应端口，手机扫码连接即可。',
                              'First run: open the desktop app as administrator, click Start, allow the firewall ports, then scan the QR code on your phone.'),
                          _t('软件版本：v1.0.1', 'Version: v1.0.1'),
                        ],
                        accent: const Color(AppColors.accent),
                        tag: 'ABOUT',
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

  Widget _sectionCard({
    required String title,
    required String subtitle,
    required List<String> lines,
    required Color accent,
    required String tag,
  }) {
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface2),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Container(height: 2, color: accent.withOpacity(0.7)),
          const SizedBox(height: 10),
          Row(
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: const TextStyle(
                        fontSize: AppSizes.fontSizeBody,
                        color: Color(AppColors.textPrimary),
                        fontWeight: FontWeight.w500,
                      ),
                    ),
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
                child: Text(tag, style: TextStyle(fontSize: 10, color: accent, fontFamily: 'monospace')),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Column(
            children: lines
                .map((line) => Padding(
                      padding: const EdgeInsets.only(bottom: 6),
                      child: Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Container(
                            width: 6,
                            height: 6,
                            margin: const EdgeInsets.only(right: 8, top: 6),
                            decoration: BoxDecoration(color: accent, shape: BoxShape.circle),
                          ),
                          Expanded(
                            child: _buildLine(line),
                          ),
                        ],
                      ),
                    ))
                .toList(),
          ),
        ],
      ),
    );
  }
}
