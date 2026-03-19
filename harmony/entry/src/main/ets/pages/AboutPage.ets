import { AppColors, AppSizes } from '../common/Constants';
import { I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';

@Entry
@Component
struct AboutPage {
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
            .backgroundColor(AppColors.ACCENT_BLUE)

          Row() {
            Text(this.t('‹ 返回', '‹ Back'))
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.ACCENT_BLUE)
              .onClick(() => {
                this.goBack();
              })
              .padding({ right: 12 })

            Column() {
              Text(this.t('软件介绍', 'About'))
                .fontSize(AppSizes.FONT_SIZE_BODY)
                .fontColor(AppColors.TEXT_PRIMARY)
                .fontWeight(FontWeight.Medium)

              Text(this.t('产品定位与能力概览', 'Product overview'))
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

        Scroll() {
          Column({ space: 12 }) {
            this.SectionCard(
              this.t('产品定位', 'Positioning'),
              this.t('把桌面终端装进口袋', 'Bring your desktop shell to your pocket'),
              [
                this.t('PhoneShell 让手机成为 PC 终端的轻量入口。', 'PhoneShell turns your phone into a lightweight entry for PC shells.'),
                this.t('面向设备管理与远程控制，减少来回切换成本。', 'Focuses on device management and remote control efficiency.'),
                this.t('强调快捷、安全与低干扰的操作体验。', 'Designed for fast, secure, and low-friction control.')
              ],
              AppColors.ACCENT
            )

            this.SectionCard(
              this.t('核心能力', 'Core Capabilities'),
              this.t('连接、管理、控制', 'Connect, manage, control'),
              [
                this.t('扫码或地址连接，快速进入设备列表。', 'Connect via QR or address to reach device list fast.'),
                this.t('群组管理多设备，统一授权与状态感知。', 'Group mode manages multi-devices with unified auth.'),
                this.t('远程终端与快捷键提升操作效率。', 'Remote terminal and shortcuts boost efficiency.')
              ],
              AppColors.ACCENT_BLUE
            )

            this.SectionCard(
              this.t('安全提示', 'Safety Notes'),
              this.t('重要操作需要确认', 'Confirm important actions'),
              [
                this.t('敏感操作会触发授权请求，请认真核对。', 'Sensitive actions prompt authorization; verify carefully.'),
                this.t('建议在可信网络环境中使用。', 'Use within trusted network environments.'),
                this.t('保持群组密钥私密，避免外泄。', 'Keep group secrets private to avoid leakage.')
              ],
              AppColors.ACCENT_PINK
            )

            this.SectionCard(
              this.t('当前重点', 'Current Focus'),
              this.t('稳定连接与清晰反馈', 'Stable links and clear feedback'),
              [
                this.t('本版本聚焦连接稳定与控制反馈。', 'This version focuses on stable connection and feedback.'),
                this.t('持续优化跨设备协作与权限流。', 'Ongoing improvements for multi-device collaboration.'),
                this.t('欢迎反馈以完善体验细节。', 'Feedback is welcome to refine details.')
              ],
              AppColors.ACCENT_YELLOW
            )
          }
          .width('100%')
          .padding({
            left: AppSizes.PADDING_PAGE + this.safeAreaLeft,
            right: AppSizes.PADDING_PAGE + this.safeAreaRight,
            top: 16,
            bottom: 24
          })
        }
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
  SectionCard(title: string, subtitle: string, lines: Array<string>, accent: string) {
    Column() {
      Blank()
        .height(2)
        .width('100%')
        .backgroundColor(accent)
        .opacity(0.7)
        .margin({ bottom: 10 })

      Row() {
        Column() {
          Text(title)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Medium)

          Text(subtitle)
            .fontSize(11)
            .fontColor(AppColors.TEXT_MUTED)
            .fontFamily('monospace')
        }
        .layoutWeight(1)

        Text('ABOUT')
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

      Column({ space: 6 }) {
        ForEach(lines, (line: string, index: number) => {
          Row() {
            Circle()
              .width(6)
              .height(6)
              .fill(accent)
              .margin({ right: 8, top: 6 })
            Text(line)
              .fontSize(12)
              .fontColor(AppColors.TEXT_SECONDARY)
              .lineHeight(18)
          }
          .width('100%')
        }, (_line: string, index: number) => `line_${index}`)
      }
      .width('100%')
      .margin({ top: 12 })
    }
    .width('100%')
    .padding(16)
    .backgroundColor(AppColors.SURFACE_2)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
  }
}
