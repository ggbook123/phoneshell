import { Context } from '@kit.AbilityKit';
import { AppColors, AppSizes } from '../common/Constants';
import { I18n } from '../common/I18n';
import { SafeAreaUtil } from '../common/SafeArea';

@Entry
@Component
struct UsageGuidePage {
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
              Text(this.t('操作说明', 'Usage Guide'))
                .fontSize(AppSizes.FONT_SIZE_BODY)
                .fontColor(AppColors.TEXT_PRIMARY)
                .fontWeight(FontWeight.Medium)

              Text(this.t('从连接到控制的完整流程', 'From connection to control'))
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
              this.t('快速开始', 'Quick Start'),
              this.t('三步完成第一次连接', 'Three steps to first connection'),
              [
                this.t('到https://github.com/ggbook123/phoneshell下载pc端',
                  'Download the PC app from https://github.com/ggbook123/phoneshell'),
                this.t('PC端点击启动按钮（第一次请以右键管理员打开）',
                  'Click Start in the PC app (first run: open as administrator).'),
                this.t('PC端防火墙打开对应端口', 'Open the required firewall ports on the PC.'),
                this.t('手机扫码即可连接', 'Scan the QR code on your phone to connect.')
              ],
              AppColors.ACCENT
            )

            this.SectionCard(
              this.t('设备与群组', 'Devices & Group'),
              this.t('单连与群组的切换逻辑', 'Switching between single and group'),
              [
                this.t('扫码自动加入群组', 'Scan to join a group automatically.'),
                this.t('群组之间可以互连', 'Groups can connect with each other.'),
                this.t('互连时只需扫码一下即可。', 'For interconnection, just scan once.')
              ],
              AppColors.ACCENT_BLUE
            )

            this.SectionCard(
              this.t('授权与安全', 'Auth & Safety'),
              this.t('关键操作需要确认', 'Confirm critical actions'),
              [
                this.t('收到授权请求时先核对设备名。', 'Verify the device name before approving.'),
                this.t('群组密钥用于权限绑定，请妥善保管。', 'Keep group secrets safe for permission binding.'),
                this.t('登录失败可返回设备管理等待重连。', 'If login fails, return and wait for reconnection.')
              ],
              AppColors.ACCENT_PINK
            )

            this.SectionCard(
              this.t('远程终端', 'Remote Terminal'),
              this.t('操作流畅与快捷键支持', 'Smooth control with shortcuts'),
              [
                this.t('终端无缝切换到手机', 'Seamlessly switch the terminal to your phone.'),
                this.t('手机可以随时唤醒电脑终端', 'Your phone can wake the PC terminal anytime.'),
                this.t('终端即时同步', 'The terminal syncs in real time.')
              ],
              AppColors.ACCENT_YELLOW
            )

            this.SectionCard(
              this.t('小贴士', 'Tips'),
              this.t('提升稳定性与体验', 'Stability and experience'),
              [
                this.t('保持手机与 PC 网络稳定一致。', 'Keep phone and PC on a stable network.'),
                this.t('后台恢复后可能自动重连。', 'Auto reconnection can occur after resume.'),
                this.t('尽量避免重复扫描同一二维码。', 'Avoid repeated scans of the same QR.')
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

        Text('GUIDE')
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
