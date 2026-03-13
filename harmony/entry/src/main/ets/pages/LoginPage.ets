import { AppColors, AppSizes, AppStrings } from '../common/Constants';
import { PreferencesUtil } from '../services/PreferencesUtil';
import { ConnectionManager } from '../services/ConnectionManager';
import { AuthManager } from '../services/AuthManager';

@Entry
@Component
struct LoginPage {
  @State isReady: boolean = false;

  aboutToAppear(): void {
    this.initApp();
  }

  private async initApp(): Promise<void> {
    const context: Context | undefined = this.getUIContext().getHostContext();
    if (context !== undefined) {
      await PreferencesUtil.init(context);
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

  build() {
    Column() {
      Blank().height(80)

      // App Logo
      Text('>')
        .fontSize(48)
        .fontColor(AppColors.ACCENT)
        .fontWeight(FontWeight.Bold)
        .fontFamily('monospace')
        .margin({ bottom: 8 })

      Text(AppStrings.APP_NAME)
        .fontSize(AppSizes.FONT_SIZE_TITLE)
        .fontColor(AppColors.TEXT_PRIMARY)
        .fontWeight(FontWeight.Bold)
        .margin({ bottom: 4 })

      Text('远程终端控制')
        .fontSize(AppSizes.FONT_SIZE_SMALL)
        .fontColor(AppColors.TEXT_SECONDARY)
        .margin({ bottom: 60 })

      // Account field (disabled, reserved for future)
      TextInput({ placeholder: '账号' })
        .type(InputType.Normal)
        .width('100%')
        .height(48)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor(AppColors.TEXT_SECONDARY)
        .placeholderColor(AppColors.TEXT_SECONDARY)
        .backgroundColor(AppColors.INPUT_BG)
        .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
        .padding({ left: 16, right: 16 })
        .margin({ bottom: 12 })
        .enabled(false)
        .opacity(0.5)

      // Password field (disabled, reserved for future)
      TextInput({ placeholder: '密码' })
        .type(InputType.Password)
        .width('100%')
        .height(48)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor(AppColors.TEXT_SECONDARY)
        .placeholderColor(AppColors.TEXT_SECONDARY)
        .backgroundColor(AppColors.INPUT_BG)
        .borderRadius(AppSizes.BORDER_RADIUS_INPUT)
        .padding({ left: 16, right: 16 })
        .margin({ bottom: 24 })
        .enabled(false)
        .opacity(0.5)

      // Confirm button
      Button('确  认')
        .width('100%')
        .height(48)
        .fontSize(AppSizes.FONT_SIZE_BODY)
        .fontColor('#FFFFFF')
        .backgroundColor(this.isReady ? AppColors.ACCENT : AppColors.ACCENT_PRESSED)
        .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
        .enabled(this.isReady)
        .onClick(() => {
          this.enterApp();
        })

      Blank().layoutWeight(1)

      // Version
      Text('v0.2.0')
        .fontSize(12)
        .fontColor(AppColors.TEXT_SECONDARY)
        .margin({ bottom: 24 })
    }
    .width('100%')
    .height('100%')
    .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
    .backgroundColor(AppColors.BACKGROUND)
    .alignItems(HorizontalAlign.Center)
  }
}
