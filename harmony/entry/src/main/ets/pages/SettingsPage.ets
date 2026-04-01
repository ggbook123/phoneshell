import { AppColors, AppSizes } from '../common/Constants';
import { I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';

@Entry
@Component
struct SettingsPage {
  @State safeAreaTop: number = 0;
  @State safeAreaBottom: number = 0;
  @State safeAreaLeft: number = 0;
  @State safeAreaRight: number = 0;

  aboutToAppear(): void {
    this.syncSafeArea();
  }

  private async syncSafeArea(): Promise<void> {
    const insets = await SafeAreaUtil.resolveInsets(this.getUIContext());
    this.safeAreaTop = insets.top;
    this.safeAreaBottom = insets.bottom;
    this.safeAreaLeft = insets.left;
    this.safeAreaRight = insets.right;
  }

  private t(zhText: string, enText: string): string {
    return I18n.t(I18n.getLanguage(), zhText, enText);
  }

  private goBack(): void {
    this.getUIContext().getRouter().back();
  }

  private openUsageGuide(): void {
    try {
      this.getUIContext().getRouter().pushUrl({ url: 'pages/UsageGuidePage' });
    } catch (_e) {
      // Ignore navigation errors
    }
  }

  private openAboutPage(): void {
    try {
      this.getUIContext().getRouter().pushUrl({ url: 'pages/AboutPage' });
    } catch (_e) {
      // Ignore navigation errors
    }
  }

  build() {
    Stack({ alignContent: Alignment.TopStart }) {
      Column() {
        Blank()
          .height(140)
          .width('100%')
          .backgroundColor(AppColors.ACCENT_DIM)
          .opacity(0.2)
        Blank().layoutWeight(1)
      }
      .width('100%')
      .height('100%')

      Column() {
        Column() {
          Blank()
            .height(2)
            .width('100%')
            .backgroundColor(AppColors.ACCENT)

          Row() {
            Text(this.t('‹ 返回', '‹ Back'))
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.ACCENT)
              .onClick(() => {
                this.goBack();
              })
              .padding({ right: 12 })

            Column() {
              Text(this.t('设置', 'Settings'))
                .fontSize(AppSizes.FONT_SIZE_BODY)
                .fontColor(AppColors.TEXT_PRIMARY)
                .fontWeight(FontWeight.Medium)

              Text(this.t('操作说明与软件介绍', 'Usage Guide + About'))
                .fontSize(11)
                .fontColor(AppColors.TEXT_MUTED)
                .fontFamily('monospace')
            }
            .alignItems(HorizontalAlign.Start)
            .layoutWeight(1)
          }
          .width('100%')
          .height(56)
          .padding({
            left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
            right: AppSizes.PADDING_PAGE + this.safeAreaRight
          })
          .alignItems(VerticalAlign.Center)
        }
        .backgroundColor(AppColors.SURFACE_1)
        .width('100%')

        Divider().color(AppColors.DIVIDER).width('100%')

        List({ space: 12 }) {
          ListItem() {
            this.SettingsItem(
              '01',
              this.t('操作说明', 'Usage Guide'),
              this.t('使用流程与功能提示', 'How to use the app'),
              AppColors.ACCENT,
              () => { this.openUsageGuide(); }
            )
          }
          ListItem() {
            this.SettingsItem(
              '02',
              this.t('软件介绍', 'About'),
              this.t('版本、目标与许可', 'Version and overview'),
              AppColors.ACCENT_BLUE,
              () => { this.openAboutPage(); }
            )
          }
        }
        .padding({
          left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
          right: AppSizes.PADDING_PAGE + this.safeAreaRight,
          top: 16,
          bottom: 16
        })
        .width('100%')
        .layoutWeight(1)
      }
      .width('100%')
      .height('100%')
      .padding({ top: this.safeAreaTop, bottom: this.safeAreaBottom })
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }

  @Builder
  SettingsItem(indexLabel: string, title: string, subtitle: string, accent: string, onOpen: () => void) {
    Column() {
      Blank()
        .height(2)
        .width('100%')
        .backgroundColor(accent)
        .opacity(0.6)
        .margin({ bottom: 10 })

      Column({ space: 10 }) {
        Row() {
          Text(indexLabel)
            .fontSize(11)
            .fontColor(accent)
            .fontFamily('monospace')
            .padding({ left: 10, right: 10, top: 6, bottom: 6 })
            .backgroundColor(AppColors.HIGHLIGHT)
            .borderWidth(1)
            .borderColor(accent)
            .borderRadius(AppSizes.BORDER_RADIUS_TAG)

          Blank().layoutWeight(1)

          Text('OPEN')
            .fontSize(10)
            .fontColor(accent)
            .fontFamily('monospace')
            .padding({ left: 8, right: 8, top: 4, bottom: 4 })
            .backgroundColor(AppColors.CHIP_BG)
            .borderWidth(1)
            .borderColor(accent)
            .borderRadius(AppSizes.BORDER_RADIUS_TAG)
        }
        .width('100%')

        Column({ space: 4 }) {
          Text(title)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)

          Text(subtitle)
            .fontSize(11)
            .fontColor(AppColors.TEXT_MUTED)
            .fontFamily('monospace')
        }
        .width('100%')
        .alignItems(HorizontalAlign.Start)
      }
      .width('100%')
      .alignItems(HorizontalAlign.Start)
    }
    .width('100%')
    .padding({ left: 16, right: 16, top: 14, bottom: 14 })
    .backgroundColor(AppColors.SURFACE_2)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
    .onClick(() => {
      onOpen();
    })
  }
}
