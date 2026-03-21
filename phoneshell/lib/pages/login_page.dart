import 'package:flutter/material.dart';

import '../core/auth_manager.dart';
import '../core/constants.dart';
import '../core/connection_manager.dart';
import '../core/i18n.dart';
import '../core/preferences_util.dart';

class LoginPage extends StatefulWidget {
  const LoginPage({super.key});

  @override
  State<LoginPage> createState() => _LoginPageState();
}

class _LoginPageState extends State<LoginPage> {
  bool _isReady = false;
  AppLanguage _language = I18n.getLanguage();

  @override
  void initState() {
    super.initState();
    _initApp();
  }

  Future<void> _initApp() async {
    await PreferencesUtil.init();
    await I18n.syncFromPreferences();
    _language = I18n.getLanguage();

    final mobileId = PreferencesUtil.getMobileDeviceId();
    ConnectionManager.instance.setMobileDeviceId(mobileId);

    AuthManager.instance.init();
    await ConnectionManager.instance.restoreSavedConnections();

    if (mounted) {
      setState(() {
        _isReady = true;
      });
    }
  }

  String _t(String zh, String en) {
    return I18n.t(_language, zh, en);
  }

  void _enterApp() {
    Navigator.of(context).pushNamed('pages/DeviceManagePage');
  }

  Widget _brandHeader() {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(18),
      margin: const EdgeInsets.only(bottom: 14),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: const Color(AppColors.cardBorder), width: 1),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              SizedBox(
                width: 84,
                height: 84,
                child: Image.asset('assets/images/phoneshell.png', fit: BoxFit.contain),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      AppStrings.appName,
                      style: const TextStyle(
                        fontSize: 24,
                        color: Color(AppColors.textPrimary),
                        fontWeight: FontWeight.bold,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      _t('移动端远程终端入口', 'Mobile Remote Shell Gateway'),
                      style: const TextStyle(
                        fontSize: 12,
                        color: Color(AppColors.textSecondary),
                      ),
                    ),
                    const SizedBox(height: 8),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                      decoration: BoxDecoration(
                        color: const Color(AppColors.highlight),
                        borderRadius: BorderRadius.circular(AppSizes.borderRadiusTag),
                        border: Border.all(
                          color: Color(_isReady ? AppColors.onlineGreen : AppColors.accentYellow),
                        ),
                      ),
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Container(
                            width: 7,
                            height: 7,
                            margin: const EdgeInsets.only(right: 6),
                            decoration: BoxDecoration(
                              color: Color(_isReady ? AppColors.onlineGreen : AppColors.accentYellow),
                              shape: BoxShape.circle,
                            ),
                          ),
                          Text(
                            _isReady ? _t('系统就绪', 'System Ready') : _t('正在初始化...', 'Initializing...'),
                            style: TextStyle(
                              fontSize: 11,
                              color: Color(_isReady ? AppColors.onlineGreen : AppColors.accentYellow),
                              fontFamily: 'monospace',
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 14),
          Text(
            _t(
              '原生终端体验，任何设备无缝切换',
              'Native terminal experience, seamless switching across any device.',
            ),
            style: const TextStyle(
              fontSize: 13,
              color: Color(AppColors.textSecondary),
              height: 19 / 13,
            ),
          ),
        ],
      ),
    );
  }

  Widget _introCard() {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(16),
      margin: const EdgeInsets.only(bottom: 16),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface2),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder), width: 1),
      ),
      child: Column(
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  _t('启动前说明', 'Before You Start'),
                  style: const TextStyle(
                    fontSize: 12,
                    color: Color(AppColors.textMuted),
                    fontFamily: 'monospace',
                  ),
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          Container(
            height: 2,
            width: double.infinity,
            margin: const EdgeInsets.only(bottom: 12),
            color: const Color(AppColors.accent),
          ),
          _infoLine('01', _t('PC上点击启动按钮', 'Click the Start button on the PC')),
          _infoLine('02', _t('手机扫码连接', 'Scan with your phone to connect')),
          _infoLine(
            '03',
            _t('无缝切换，不中断的进行终端会话', 'Seamless switching, keep terminal sessions uninterrupted'),
          ),
        ],
      ),
    );
  }

  Widget _infoLine(String index, String text) {
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.only(bottom: 10),
      child: Row(
        children: [
          Text(
            index,
            style: const TextStyle(
              fontSize: 11,
              color: Color(AppColors.accentBlue),
              fontFamily: 'monospace',
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              text,
              style: const TextStyle(fontSize: 13, color: Color(AppColors.textPrimary)),
            ),
          ),
        ],
      ),
    );
  }

  Widget _launchPanel() {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: const Color(AppColors.surface1),
        borderRadius: BorderRadius.circular(AppSizes.borderRadiusCard),
        border: Border.all(color: const Color(AppColors.cardBorder), width: 1),
      ),
      child: Column(
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  _t('启动应用', 'Launch App'),
                  style: const TextStyle(
                    fontSize: 13,
                    color: Color(AppColors.textMuted),
                    fontFamily: 'monospace',
                  ),
                ),
              ),
              Text(
                _isReady ? _t('可进入', 'Ready') : _t('加载中', 'Loading'),
                style: TextStyle(
                  fontSize: 12,
                  color: Color(_isReady ? AppColors.onlineGreen : AppColors.textMuted),
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          SizedBox(
            width: double.infinity,
            height: 50,
            child: ElevatedButton(
              onPressed: _isReady ? _enterApp : null,
              style: ElevatedButton.styleFrom(
                backgroundColor: Color(_isReady ? AppColors.accent : AppColors.accentPressed),
                disabledBackgroundColor: const Color(AppColors.accentPressed),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(AppSizes.borderRadiusButton),
                ),
              ),
              child: Text(
                _t('进入设备管理', 'Enter Device Center'),
                style: const TextStyle(
                  fontSize: AppSizes.fontSizeBody,
                  color: Colors.white,
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _languagePanel() {
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.only(top: 14, bottom: 10),
      child: Row(
        children: [
          Text(
            _t('语言', 'Language'),
            style: const TextStyle(fontSize: 12, color: Color(AppColors.textMuted)),
          ),
          const Spacer(),
          Container(
            padding: const EdgeInsets.all(4),
            decoration: BoxDecoration(
              color: const Color(AppColors.surface2),
              borderRadius: BorderRadius.circular(14),
              border: Border.all(color: const Color(AppColors.cardBorder)),
            ),
            child: Row(
              children: [
                _languageChip(
                  label: _t('中文', 'Chinese'),
                  selected: _language == AppLanguage.zh,
                  onTap: () {
                    setState(() {
                      _language = AppLanguage.zh;
                      I18n.setLanguage(AppLanguage.zh);
                    });
                  },
                ),
                _languageChip(
                  label: _t('英文', 'English'),
                  selected: _language == AppLanguage.en,
                  onTap: () {
                    setState(() {
                      _language = AppLanguage.en;
                      I18n.setLanguage(AppLanguage.en);
                    });
                  },
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  Widget _languageChip({required String label, required bool selected, required VoidCallback onTap}) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        margin: const EdgeInsets.symmetric(horizontal: 3),
        decoration: BoxDecoration(
          color: Color(selected ? AppColors.accent : AppColors.surface3),
          borderRadius: BorderRadius.circular(12),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontSize: 12,
            color: Color(selected ? AppColors.background : AppColors.textSecondary),
          ),
        ),
      ),
    );
  }

  Widget _footerInfo() {
    return Row(
      children: const [
        Text(
          'PhoneShell Mobile',
          style: TextStyle(fontSize: 11, color: Color(AppColors.textMuted)),
        ),
        Spacer(),
        Text(
          'v0.2.0',
          style: TextStyle(fontSize: 11, color: Color(AppColors.textMuted)),
        ),
      ],
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
                height: 200,
                width: double.infinity,
                color: const Color(AppColors.surface1).withOpacity(0.94),
              ),
              Container(
                height: 120,
                width: double.infinity,
                color: const Color(AppColors.accentDim).withOpacity(0.28),
              ),
              const Expanded(child: SizedBox()),
            ],
          ),
          Positioned(
            top: -70,
            right: -80,
            child: Container(
              width: 220,
              height: 220,
              decoration: BoxDecoration(
                color: const Color(AppColors.glow).withOpacity(0.18),
                shape: BoxShape.circle,
              ),
            ),
          ),
          Positioned(
            left: -60,
            bottom: 40,
            child: Container(
              width: 160,
              height: 160,
              decoration: BoxDecoration(
                color: const Color(AppColors.accentDim).withOpacity(0.2),
                shape: BoxShape.circle,
              ),
            ),
          ),
          SingleChildScrollView(
            padding: EdgeInsets.fromLTRB(
              AppSizes.paddingPage + padding.left,
              24 + padding.top,
              AppSizes.paddingPage + padding.right,
              28 + padding.bottom,
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _brandHeader(),
                _introCard(),
                _launchPanel(),
                _languagePanel(),
                _footerInfo(),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
