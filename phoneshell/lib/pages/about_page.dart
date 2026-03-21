import 'package:flutter/material.dart';

import '../core/constants.dart';
import '../core/i18n.dart';

class AboutPage extends StatelessWidget {
  const AboutPage({super.key});

  String _t(String zh, String en) => I18n.tCurrent(zh, en);

  void _goBack(BuildContext context) {
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
                        title: _t('产品定位', 'Positioning'),
                        subtitle: _t('把桌面终端装进口袋', 'Bring your desktop shell to your pocket'),
                        lines: [
                          _t('PhoneShell 让手机成为 PC 终端的轻量入口。', 'PhoneShell turns your phone into a lightweight entry for PC shells.'),
                          _t('面向设备管理与远程控制，减少来回切换成本。', 'Focuses on device management and remote control efficiency.'),
                          _t('强调快捷、安全与低干扰的操作体验。', 'Designed for fast, secure, and low-friction control.'),
                        ],
                        accent: const Color(AppColors.accent),
                        tag: 'ABOUT',
                      ),
                      const SizedBox(height: 12),
                      _sectionCard(
                        title: _t('核心能力', 'Core Capabilities'),
                        subtitle: _t('连接、管理、控制', 'Connect, manage, control'),
                        lines: [
                          _t('扫码或地址连接，快速进入设备列表。', 'Connect via QR or address to reach device list fast.'),
                          _t('群组管理多设备，统一授权与状态感知。', 'Group mode manages multi-devices with unified auth.'),
                          _t('远程终端与快捷键提升操作效率。', 'Remote terminal and shortcuts boost efficiency.'),
                        ],
                        accent: const Color(AppColors.accentBlue),
                        tag: 'ABOUT',
                      ),
                      const SizedBox(height: 12),
                      _sectionCard(
                        title: _t('安全提示', 'Safety Notes'),
                        subtitle: _t('重要操作需要确认', 'Confirm important actions'),
                        lines: [
                          _t('敏感操作会触发授权请求，请认真核对。', 'Sensitive actions prompt authorization; verify carefully.'),
                          _t('建议在可信网络环境中使用。', 'Use within trusted network environments.'),
                          _t('保持群组密钥私密，避免外泄。', 'Keep group secrets private to avoid leakage.'),
                        ],
                        accent: const Color(AppColors.accentPink),
                        tag: 'ABOUT',
                      ),
                      const SizedBox(height: 12),
                      _sectionCard(
                        title: _t('当前重点', 'Current Focus'),
                        subtitle: _t('稳定连接与清晰反馈', 'Stable links and clear feedback'),
                        lines: [
                          _t('本版本聚焦连接稳定与控制反馈。', 'This version focuses on stable connection and feedback.'),
                          _t('持续优化跨设备协作与权限流。', 'Ongoing improvements for multi-device collaboration.'),
                          _t('欢迎反馈以完善体验细节。', 'Feedback is welcome to refine details.'),
                        ],
                        accent: const Color(AppColors.accentYellow),
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
                            child: Text(
                              line,
                              style: const TextStyle(fontSize: 12, color: Color(AppColors.textSecondary), height: 18 / 12),
                            ),
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
