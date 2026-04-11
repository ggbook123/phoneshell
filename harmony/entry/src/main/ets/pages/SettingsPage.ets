import { AppColors, AppSizes } from '../common/Constants';
import { I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';
import { ConnectionManager } from '../services/ConnectionManager';

@Entry
@Component
struct SettingsPage {
  @State safeAreaTop: number = 0;
  @State safeAreaBottom: number = 0;
  @State safeAreaLeft: number = 0;
  @State safeAreaRight: number = 0;
  @State showInitializeConfirmDialog: boolean = false;

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

  private openInitializeConfirmDialog(): void {
    this.showInitializeConfirmDialog = true;
  }

  private closeInitializeConfirmDialog(): void {
    this.showInitializeConfirmDialog = false;
  }

  private confirmInitialize(): void {
    ConnectionManager.dissolveGroup();
    ConnectionManager.markPendingGroupDissolveUiReset();
    this.closeInitializeConfirmDialog();
    this.goBack();
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

              Text(this.t('操作入口与系统初始化', 'App actions + initialization'))
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
          ListItem() {
            this.SettingsItem(
              '03',
              this.t('初始化', 'Initialize'),
              this.t('解散当前群组并恢复初始状态', 'Dissolve the current group and reset state'),
              AppColors.ERROR_RED,
              () => { this.openInitializeConfirmDialog(); }
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

      if (this.showInitializeConfirmDialog) {
        this.InitializeConfirmDialog()
      }
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

  @Builder
  InitializeConfirmDialog() {
    Column() {
      Column() {
        Text(this.t('确认初始化', 'Confirm Initialization'))
          .fontSize(18)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)
          .margin({ bottom: 10 })

        Text(this.t(
          '是否确认初始化？当前群组将被解散。',
          'Are you sure you want to initialize? The current group will be dissolved.'
        ))
          .fontSize(13)
          .fontColor(AppColors.TEXT_SECONDARY)
          .textAlign(TextAlign.Center)
          .margin({ bottom: 18 })

        Row({ space: 12 }) {
          Button(this.t('否', 'No'))
            .width(120)
            .height(40)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_PRIMARY)
            .backgroundColor(Color.Transparent)
            .borderWidth(1)
            .borderColor(AppColors.TEXT_MUTED)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => {
              this.closeInitializeConfirmDialog();
            })

          Button(this.t('是', 'Yes'))
            .width(140)
            .height(40)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor('#FFFFFF')
            .backgroundColor(AppColors.ERROR_RED)
            .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
            .onClick(() => {
              this.confirmInitialize();
            })
        }
        .justifyContent(FlexAlign.Center)
      }
      .width('100%')
      .padding(24)
      .backgroundColor(AppColors.SURFACE_2)
      .borderWidth(1)
      .borderColor(AppColors.CARD_BORDER)
      .borderRadius(16)
      .alignItems(HorizontalAlign.Center)
    }
    .width('100%')
    .height('100%')
    .padding({ left: 24 + this.safeAreaLeft, right: 24 + this.safeAreaRight })
    .backgroundColor('#B0000000')
    .justifyContent(FlexAlign.Center)
  }
}
