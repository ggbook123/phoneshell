import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/constants.dart';
import '../core/i18n.dart';

class UsageGuidePage extends StatelessWidget {
  const UsageGuidePage({super.key});

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
                                      _t('操作说明', 'Usage Guide'),
                                      style: const TextStyle(
                                        fontSize: AppSizes.fontSizeBody,
                                        color: Color(AppColors.textPrimary),
                                        fontWeight: FontWeight.w500,
                                      ),
                                    ),
                                    Text(
                                      _t('从连接到控制的完整流程', 'From connection to control'),
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
                        title: _t('快速开始', 'Quick Start'),
                        subtitle: _t('三步完成第一次连接', 'Three steps to first connection'),
                        lines: [
                          _t('到https://github.com/ggbook123/phoneshell下载pc端',
                              'Download the PC app from https://github.com/ggbook123/phoneshell'),
                          _t('PC端点击启动按钮（第一次请以右键管理员打开）',
                              'Click Start in the PC app (first run: open as administrator).'),
                          _t('PC端防火墙打开对应端口', 'Open the required firewall ports on the PC.'),
                          _t('手机扫码即可连接', 'Scan the QR code on your phone to connect.'),
                        ],
                        accent: const Color(AppColors.accent),
                        tag: 'GUIDE',
                      ),
                      const SizedBox(height: 12),
                      _sectionCard(
                        title: _t('设备与群组', 'Devices & Group'),
                        subtitle: _t('单连与群组的切换逻辑', 'Switching between single and group'),
                        lines: [
                          _t('扫码自动加入群组', 'Scan to join a group automatically.'),
                          _t('群组之间可以互连', 'Groups can connect with each other.'),
                          _t('互连时只需扫码一下即可。', 'For interconnection, just scan once.'),
                        ],
                        accent: const Color(AppColors.accentBlue),
                        tag: 'GUIDE',
                      ),
                      const SizedBox(height: 12),
                      _sectionCard(
                        title: _t('授权与安全', 'Auth & Safety'),
                        subtitle: _t('关键操作需要确认', 'Confirm critical actions'),
                        lines: [
                          _t('收到授权请求时先核对设备名。', 'Verify the device name before approving.'),
                          _t('群组密钥用于权限绑定，请妥善保管。', 'Keep group secrets safe for permission binding.'),
                          _t('登录失败可返回设备管理等待重连。', 'If login fails, return and wait for reconnection.'),
                        ],
                        accent: const Color(AppColors.accentPink),
                        tag: 'GUIDE',
                      ),
                      const SizedBox(height: 12),
                      _sectionCard(
                        title: _t('远程终端', 'Remote Terminal'),
                        subtitle: _t('操作流畅与快捷键支持', 'Smooth control with shortcuts'),
                        lines: [
                          _t('终端无缝切换到手机', 'Seamlessly switch the terminal to your phone.'),
                          _t('手机可以随时唤醒电脑终端', 'Your phone can wake the PC terminal anytime.'),
                          _t('终端即时同步', 'The terminal syncs in real time.'),
                        ],
                        accent: const Color(AppColors.accentYellow),
                        tag: 'GUIDE',
                      ),
                      const SizedBox(height: 12),
                      _sectionCard(
                        title: _t('小贴士', 'Tips'),
                        subtitle: _t('提升稳定性与体验', 'Stability and experience'),
                        lines: [
                          _t('保持手机与 PC 网络稳定一致。', 'Keep phone and PC on a stable network.'),
                          _t('后台恢复后可能自动重连。', 'Auto reconnection can occur after resume.'),
                          _t('尽量避免重复扫描同一二维码。', 'Avoid repeated scans of the same QR.'),
                        ],
                        accent: const Color(AppColors.accent),
                        tag: 'GUIDE',
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
