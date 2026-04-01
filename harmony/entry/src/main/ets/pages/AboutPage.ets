import { common, Want } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { promptAction } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes } from '../common/Constants';
import { I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';

const DOMAIN = 0xFF12;
const PAGE_TAG = 'AboutPage';

@Observed
class AboutLineItem {
  line: string = '';
  linkUrl: string = '';
  key: string = '';
}

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
    const match: RegExpMatchArray | null = text.match(/(https?:\/\/\S+|www\.\S+)/i);
    if (match === null) {
      return '';
    }
    return this.normalizeUrl(match[0]);
  }

  private normalizeUrl(url: string): string {
    const rawUrl: string = url.trim().replace(/[),.;!?，。；！？]+$/, '');
    if (rawUrl.startsWith('www.')) {
      return `https://${rawUrl}`;
    }
    return rawUrl;
  }

  private async openUrl(url: string): Promise<void> {
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
      hilog.info(DOMAIN, PAGE_TAG, 'openUrl success %{public}s', normalizedUrl);
    } catch (err) {
      const error: BusinessError = err as BusinessError;
      hilog.error(DOMAIN, PAGE_TAG, 'openUrl failed code=%{public}d msg=%{public}s url=%{public}s',
        error.code ?? -1, error.message ?? '', normalizedUrl);
      promptAction.showToast({
        message: this.t('打开链接失败，请检查设备浏览器或查看日志。', 'Failed to open link. Check browser availability or logs.')
      });
    }
  }

  private buildLineItems(lines: Array<string>): Array<AboutLineItem> {
    const items: Array<AboutLineItem> = [];
    for (let i: number = 0; i < lines.length; i++) {
      const item: AboutLineItem = new AboutLineItem();
      item.line = lines[i];
      item.linkUrl = this.extractUrl(lines[i]);
      item.key = `line_${i}`;
      items.push(item);
    }
    return items;
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
                this.t('或者官网：https://www.phoneshell.sbs',
                  'Or use the official website: https://www.phoneshell.sbs'),
                this.t('第一次请以管理员身份打开电脑端，电脑端点击启动，防火墙打开对应端口，手机扫码连接即可。',
                  'First run: open the desktop app as administrator, click Start, allow the firewall ports, then scan the QR code on your phone.'),
                this.t('软件版本：v1.0.1', 'Version: v1.0.1')
              ],
              AppColors.ACCENT
            )
          }
          .width('100%')
          .alignItems(HorizontalAlign.Start)
          .justifyContent(FlexAlign.Start)
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
      .alignItems(HorizontalAlign.Start)
      .justifyContent(FlexAlign.Start)
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

      Column({ space: 8 }) {
        Text(title)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Medium)

        Text(subtitle)
          .fontSize(11)
          .fontColor(AppColors.TEXT_MUTED)
          .fontFamily('monospace')

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
      .alignItems(HorizontalAlign.Start)

      Column({ space: 6 }) {
        ForEach(this.buildLineItems(lines), (item: AboutLineItem) => {
          Row() {
            Circle()
              .width(6)
              .height(6)
              .fill(accent)
              .margin({ right: 8, top: 6 })
            if (item.linkUrl.length > 0) {
              Text(item.line)
                .fontSize(12)
                .fontColor(AppColors.ACCENT_BLUE)
                .lineHeight(18)
                .decoration({ type: TextDecorationType.Underline })
                .layoutWeight(1)
                .onClick(() => {
                  this.openUrl(item.linkUrl);
                })
            } else {
              Text(item.line)
                .fontSize(12)
                .fontColor(AppColors.TEXT_SECONDARY)
                .lineHeight(18)
                .layoutWeight(1)
            }
          }
          .width('100%')
          .alignItems(VerticalAlign.Top)
          .onClick(() => {
            if (item.linkUrl.length > 0) {
              this.openUrl(item.linkUrl);
            }
          })
        }, (item: AboutLineItem) => item.key)
      }
      .width('100%')
      .margin({ top: 12 })
    }
    .width('100%')
    .alignItems(HorizontalAlign.Start)
    .justifyContent(FlexAlign.Start)
    .padding(16)
    .backgroundColor(AppColors.SURFACE_2)
    .borderWidth(1)
    .borderColor(AppColors.CARD_BORDER)
    .borderRadius(AppSizes.BORDER_RADIUS_CARD)
  }
}
