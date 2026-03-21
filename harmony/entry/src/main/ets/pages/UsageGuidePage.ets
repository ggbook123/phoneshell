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
                this.t('确保 PC 端已启动并保持在线。', 'Make sure the PC client is running and online.'),
                this.t('在设备列表页扫码或输入地址连接。', 'Connect via QR or address from the device list page.'),
                this.t('进入会话列表，选择 Shell 创建终端。', 'Enter the session list and create a shell.')
              ],
              AppColors.ACCENT
            )

            this.SectionCard(
              this.t('设备与群组', 'Devices & Group'),
              this.t('单连与群组的切换逻辑', 'Switching between single and group'),
              [
                this.t('单连适合快速连接单台设备。', 'Single mode is for quick one-device control.'),
                this.t('群组可管理多设备并集中授权。', 'Group mode manages multiple devices with centralized auth.'),
                this.t('长按在线设备可尝试加入群组。', 'Long-press an online device to add it to the group.')
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
                this.t('选择 Shell 后新建会话进入终端。', 'Pick a shell and create a session.'),
                this.t('底部快捷键区支持常用操作。', 'Use the shortcut bar for common actions.'),
                this.t('无响应时可下拉刷新会话列表。', 'Pull to refresh if a session becomes unresponsive.')
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
