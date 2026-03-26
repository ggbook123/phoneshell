import { Context } from '@kit.AbilityKit';
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

  private extractUrl(text: string): string {
    const match: RegExpMatchArray | null = text.match(/https?:\/\/\S+/);
    return match ? match[0] : '';
  }

  private openUrl(url: string): void {
    const hostContext: Context | undefined = this.getUIContext().getHostContext();
    if (hostContext === undefined) {
      return;
    }
    try {
      hostContext.startAbility({ action: 'ohos.want.action.viewData', uri: url });
    } catch (_e) {
      // Ignore
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
              this.t('软件介绍', 'About'),
              this.t('使用说明', 'Instructions'),
              [
                this.t('PhoneShell在手机上获得电脑端原生 Shell 体验，通过远程到电脑端随时随地 AI 编程。',
                  'PhoneShell brings a native desktop shell to your phone, enabling AI coding anywhere via remote access.'),
                this.t('请于到github上下载电脑端：https://github.com/ggbook123/phoneshell',
                  'Download the desktop app from GitHub: https://github.com/ggbook123/phoneshell'),
                this.t('第一次请以管理员身份打开电脑端，电脑端点击启动，防火墙打开对应端口，手机扫码连接即可。',
                  'First run: open the desktop app as administrator, click Start, allow the firewall ports, then scan the QR code on your phone.'),
                this.t('软件版本：v1.0.0', 'Version: v1.0.0')
              ],
              AppColors.ACCENT
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
          const linkUrl: string = this.extractUrl(line);
          Row() {
            Circle()
              .width(6)
              .height(6)
              .fill(accent)
              .margin({ right: 8, top: 6 })
            if (linkUrl.length > 0) {
              Text(line)
                .fontSize(12)
                .fontColor(AppColors.ACCENT_BLUE)
                .lineHeight(18)
                .textDecoration({ type: TextDecorationType.Underline })
                .onClick(() => {
                  this.openUrl(linkUrl);
                })
            } else {
              Text(line)
                .fontSize(12)
                .fontColor(AppColors.TEXT_SECONDARY)
                .lineHeight(18)
            }
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
