import { common, Want } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { promptAction } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings } from '../common/Constants';
import { AppLanguage, I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';
import { PreferencesUtil } from '../services/PreferencesUtil';
import { ConnectionManager } from '../services/ConnectionManager';
import { AuthManager } from '../services/AuthManager';

const DOMAIN = 0xFF12;
const PAGE_TAG = 'LoginPage';

@Entry
@Component
struct LoginPage {
  @State isReady: boolean = false;
  @State language: AppLanguage = I18n.getLanguage();
  @State safeAreaTop: number = 0;
  @State safeAreaBottom: number = 0;
  @State safeAreaLeft: number = 0;
  @State safeAreaRight: number = 0;

  aboutToAppear(): void {
    this.syncSafeArea();
    this.initApp();
  }

  private async syncSafeArea(): Promise<void> {
    const insets = await SafeAreaUtil.resolveInsets(this.getUIContext());
    this.safeAreaTop = insets.top;
    this.safeAreaBottom = insets.bottom;
    this.safeAreaLeft = insets.left;
    this.safeAreaRight = insets.right;
  }

  private async initApp(): Promise<void> {
    const context: common.Context | undefined = this.getUIContext().getHostContext();
    if (context !== undefined) {
      await PreferencesUtil.init(context);
      await I18n.syncFromPreferences();
      this.language = I18n.getLanguage();
      const mobileId: string = PreferencesUtil.getMobileDeviceId(context);
      ConnectionManager.setMobileDeviceId(mobileId);
    }

    // Initialize AuthManager
    AuthManager.init();

    // Restore saved connections
    await ConnectionManager.restoreSavedConnections();

    this.isReady = true;
  }

  private enterApp(): void {
    try {
      this.getUIContext().getRouter().pushUrl({ url: 'pages/DeviceManagePage' });
    } catch (_e) {
      // Ignore navigation errors
    }
  }

  private t(zhText: string, enText: string): string {
    return I18n.t(this.language, zhText, enText);
  }

  private getReadyStatusText(): string {
    return this.isReady ? this.t('系统就绪', 'System Ready') : this.t('正在初始化...', 'Initializing...');
  }

  private normalizeUrl(url: string): string {
    const rawUrl: string = url.trim();
    if (rawUrl.startsWith('www.')) {
      return `https://${rawUrl}`;
    }
    return rawUrl;
  }

  private async openExternalUrl(url: string): Promise<void> {
    const normalizedUrl: string = this.normalizeUrl(url);
    if (normalizedUrl.length === 0) {
      return;
    }
    try {
      const abilityContext: common.UIAbilityContext = getContext(this) as common.UIAbilityContext;
      const want: Want = {
        action: 'ohos.want.action.viewData',
        entities: ['entity.system.browsable'],
        uri: normalizedUrl,
        parameters: {
          'ohos.ability.params.showDefaultPicker': true
        }
      };
      await abilityContext.startAbility(want);
      hilog.info(DOMAIN, PAGE_TAG, 'openExternalUrl success %{public}s', normalizedUrl);
    } catch (err) {
      const error: BusinessError = err as BusinessError;
      hilog.error(DOMAIN, PAGE_TAG, 'openExternalUrl failed code=%{public}d msg=%{public}s url=%{public}s',
        error.code ?? -1, error.message ?? '', normalizedUrl);
      promptAction.showToast({
        message: this.t('打开链接失败，请检查设备浏览器。', 'Failed to open link. Please check browser availability.')
      });
    }
  }

  @Builder
  BrandHeader() {
    Column() {
      Row({ space: 14 }) {
        Stack({ alignContent: Alignment.Center }) {
          Column() {
          }
            .width(84)
            .height(84)

          Image($r('app.media.phoneshell'))
            .width(84)
            .height(84)
        }

        Column() {
          Text(AppStrings.APP_NAME)
            .fontSize(24)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Bold)

          Text(this.t('移动端远程终端入口', 'Mobile Remote Shell Gateway'))
            .fontSize(12)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ top: 4, bottom: 8 })

          Row() {
            Circle()
              .width(7)
              .height(7)
              .fill(this.isReady ? AppColors.ONLINE_GREEN : AppColors.ACCENT_YELLOW)
              .margin({ right: 6 })

            Text(this.getReadyStatusText())
              .fontSize(11)
              .fontColor(this.isReady ? AppColors.ONLINE_GREEN : AppColors.ACCENT_YELLOW)
              .fontFamily('monospace')
          }
          .padding({ left: 8, right: 8, top: 4, bottom: 4 })
          .backgroundColor(AppColors.HIGHLIGHT)
          .borderWidth(1)
          .borderColor(this.isReady ? AppColors.ONLINE_GREEN : AppColors.ACCENT_YELLOW)
          .borderRadius(AppSizes.BORDER_RADIUS_TAG)
        }
        .alignItems(HorizontalAlign.Start)
        .layoutWeight(1)
      }
      .width('100%')

      Text(this.t(
        '原生终端体验，任何设备无缝切换',
        'Native terminal experience, seamless switching across any device.'
      ))
        .fontSize(13)
        .fontColor(AppColors.TEXT_SECONDARY)
        .lineHeight(19)
        .margin({ top: 14 })
        .width('100%')
    }
    .width('100%')
    .padding(18)
    .backgroundColor(AppColors.SURFACE_1)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(16)
    .margin({ bottom: 14 })
  }

  @Builder
  IntroCard() {
    Column() {
      Row() {
        Text(this.t('启动前说明', 'Before You Start'))
          .fontSize(12)
          .fontColor(AppColors.TEXT_MUTED)
          .fontFamily('monospace')
          .layoutWeight(1)
      }
      .width('100%')
      .margin({ bottom: 10 })

      Blank()
        .height(2)
        .width('100%')
        .backgroundColor(AppColors.ACCENT)
        .margin({ bottom: 12 })

      this.InfoLine('01', this.t('PC上点击启动按钮', 'Click the Start button on the PC'))
      this.InfoLine('02', this.t('手机扫码连接', 'Scan with your phone to connect'))
      this.InfoLine('03', this.t('无缝切换，不中断的进行终端会话', 'Seamless switching, keep terminal sessions uninterrupted'))
    }
    .width('100%')
    .padding(16)
    .backgroundColor(AppColors.SURFACE_2)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .margin({ bottom: 16 })
  }

  @Builder
  InfoLine(indexLabel: string, text: string) {
    Row() {
      Text(indexLabel)
        .fontSize(11)
        .fontColor(AppColors.ACCENT_BLUE)
        .fontFamily('monospace')
        .margin({ right: 10 })

      Text(text)
        .fontSize(13)
        .fontColor(AppColors.TEXT_PRIMARY)
        .layoutWeight(1)
    }
    .width('100%')
    .margin({ bottom: 10 })
  }

  @Builder
  LaunchPanel() {
    Column() {
      Row() {
        Text(this.t('启动应用', 'Launch App'))
          .fontSize(13)
          .fontColor(AppColors.TEXT_MUTED)
          .fontFamily('monospace')
          .layoutWeight(1)

        Text(this.isReady ? this.t('可进入', 'Ready') : this.t('加载中', 'Loading'))
          .fontSize(12)
          .fontColor(this.isReady ? AppColors.ONLINE_GREEN : AppColors.TEXT_MUTED)
      }
      .width('100%')
      .margin({ bottom: 12 })

      Button(this.t('进入设备管理', 'Enter Device Center'))
        .width('100%')
        .height(50)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor('#FFFFFF')
        .backgroundColor(this.isReady ? AppColors.ACCENT : AppColors.ACCENT_PRESSED)
        .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
        .enabled(this.isReady)
        .onClick(() => {
          this.enterApp();
        })
    }
    .width('100%')
    .padding(16)
    .backgroundColor(AppColors.SURFACE_1)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
  }

  @Builder
  LanguagePanel() {
    Row() {
      Text(this.t('语言', 'Language'))
        .fontSize(12)
        .fontColor(AppColors.TEXT_MUTED)
      Blank().layoutWeight(1)
      Row({ space: 6 }) {
        Text(this.t('中文', 'Chinese'))
          .fontSize(12)
          .fontColor(this.language === 'zh' ? AppColors.BACKGROUND : AppColors.TEXT_SECONDARY)
          .padding({ left: 12, right: 12, top: 6, bottom: 6 })
          .backgroundColor(this.language === 'zh' ? AppColors.ACCENT : AppColors.SURFACE_3)
          .borderRadius(12)
          .onClick(() => {
            this.language = 'zh';
            I18n.setLanguage('zh');
          })
        Text(this.t('英文', 'English'))
          .fontSize(12)
          .fontColor(this.language === 'en' ? AppColors.BACKGROUND : AppColors.TEXT_SECONDARY)
          .padding({ left: 12, right: 12, top: 6, bottom: 6 })
          .backgroundColor(this.language === 'en' ? AppColors.ACCENT : AppColors.SURFACE_3)
          .borderRadius(12)
          .onClick(() => {
            this.language = 'en';
            I18n.setLanguage('en');
          })
      }
      .padding(4)
      .backgroundColor(AppColors.SURFACE_2)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(14)
    }
    .width('100%')
    .margin({ top: 14, bottom: 10 })
  }

  @Builder
  FooterInfo() {
    Column({ space: 8 }) {
      Row() {
        Text('PhoneShell Mobile')
          .fontSize(11)
          .fontColor(AppColors.TEXT_MUTED)
        Blank().layoutWeight(1)
        Text('v1.0.1')
          .fontSize(11)
          .fontColor(AppColors.TEXT_MUTED)
      }
      .width('100%')

      Row({ space: 10 }) {
        this.FooterLinkButton(
          this.t('官网', 'Official'),
          AppColors.ACCENT,
          () => {
            this.openExternalUrl('https://www.phoneshell.sbs');
          }
        )

        this.FooterLinkButton(
          'GitHub',
          AppColors.ACCENT_BLUE,
          () => {
            this.openExternalUrl('https://github.com/ggbook123/phoneshell');
          }
        )
      }
      .width('100%')
      .justifyContent(FlexAlign.Start)
    }
    .width('100%')
    .alignItems(HorizontalAlign.Start)
  }

  @Builder
  FooterLinkButton(title: string, accent: string, onTap: () => void) {
    Row({ space: 6 }) {
      Text(title)
        .fontSize(11)
        .fontColor(accent)
        .fontWeight(FontWeight.Medium)

      Text('OPEN')
        .fontSize(9)
        .fontColor(AppColors.TEXT_MUTED)
        .fontFamily('monospace')
    }
    .padding({ left: 10, right: 10, top: 6, bottom: 6 })
    .backgroundColor(AppColors.SURFACE_2)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(10)
    .onClick(() => {
      onTap();
    })
  }

  build() {
    Stack({ alignContent: Alignment.TopStart }) {
      Column() {
        Blank()
          .height(200)
          .width('100%')
          .backgroundColor(AppColors.SURFACE_1)
          .opacity(0.94)
        Blank()
          .height(120)
          .width('100%')
          .backgroundColor(AppColors.ACCENT_DIM)
          .opacity(0.28)
        Blank().layoutWeight(1)
      }
      .width('100%')
      .height('100%')

      Circle()
        .width(220)
        .height(220)
        .fill(AppColors.GLOW)
        .opacity(0.18)
        .align(Alignment.TopEnd)
        .margin({ top: -70, right: -80 })

      Circle()
        .width(160)
        .height(160)
        .fill(AppColors.ACCENT_DIM)
        .opacity(0.2)
        .align(Alignment.BottomStart)
        .margin({ left: -60, bottom: 40 })

      Scroll() {
        Column() {
          this.BrandHeader()
          this.IntroCard()
          this.LaunchPanel()
          this.LanguagePanel()
          this.FooterInfo()
        }
        .width('100%')
        .alignItems(HorizontalAlign.Start)
        .padding({
          left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
          right: AppSizes.PADDING_PAGE + this.safeAreaRight,
          top: 24 + this.safeAreaTop,
          bottom: 28 + this.safeAreaBottom
        })
      }
      .width('100%')
      .height('100%')
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }
}
