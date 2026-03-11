import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';

const DOMAIN = 0x0000;
const PAGE_TAG = 'AuthorizationPage';

@Entry
@Component
struct AuthorizationPage {
  @State requestId: string = '';
  @State action: string = '';
  @State requesterName: string = '';
  @State description: string = '';
  @State hasResponded: boolean = false;
  @State responseText: string = '';
  private messageListenerId: number = -1;

  aboutToAppear(): void {
    // Get params passed from navigation
    const params: Record<string, Object> = this.getUIContext().getRouter().getParams() as Record<string, Object>;
    if (params !== null && params !== undefined) {
      this.requestId = (params['requestId'] as string) || '';
      this.action = (params['action'] as string) || '';
      this.requesterName = (params['requesterName'] as string) || '';
      this.description = (params['description'] as string) || '';
    }
    this.activatePage();
  }

  aboutToDisappear(): void {
    this.deactivatePage();
  }

  private activatePage(): void {
    if (this.messageListenerId === -1) {
      this.messageListenerId = WebSocketService.addOnMessage((type: string, data: Record<string, Object>) => {
        // Listen for additional auth requests (queue them or replace)
        if (type === 'auth.request') {
          this.requestId = (data['requestId'] as string) || '';
          this.action = (data['action'] as string) || '';
          this.requesterName = (data['requesterName'] as string) || '';
          this.description = (data['description'] as string) || '';
          this.hasResponded = false;
          this.responseText = '';
        }
      });
    }
  }

  private deactivatePage(): void {
    if (this.messageListenerId !== -1) {
      WebSocketService.removeOnMessage(this.messageListenerId);
      this.messageListenerId = -1;
    }
  }

  private approve(): void {
    if (this.requestId.length === 0 || this.hasResponded) {
      return;
    }
    hilog.info(DOMAIN, PAGE_TAG, 'approve requestId=%{public}s', this.requestId);
    WebSocketService.respondAuth(this.requestId, true);
    this.hasResponded = true;
    this.responseText = '已批准';
  }

  private reject(): void {
    if (this.requestId.length === 0 || this.hasResponded) {
      return;
    }
    hilog.info(DOMAIN, PAGE_TAG, 'reject requestId=%{public}s', this.requestId);
    WebSocketService.respondAuth(this.requestId, false);
    this.hasResponded = true;
    this.responseText = '已拒绝';
  }

  private getActionDisplayText(): string {
    if (this.action === 'terminal.open.remote') {
      return '打开远程终端';
    } else if (this.action === 'group.server.change') {
      return '更换服务器';
    } else if (this.action === 'group.kick') {
      return '踢出成员';
    } else if (this.action === 'group.secret.rotate') {
      return '轮换群组密钥';
    } else if (this.action === 'panel.login') {
      return '网页面板登录';
    }
    return this.action;
  }

  build() {
    Column() {
      // Header
      Row() {
        Button('< 返回')
          .height(32)
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.TEXT_SECONDARY)
          .backgroundColor(Color.Transparent)
          .onClick(() => {
            this.getUIContext().getRouter().back();
          })

        Blank()

        Text(AppStrings.AUTH_REQUEST_TITLE)
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Bold)

        Blank()
        // Spacer to balance the back button
        Text('').width(60)
      }
      .width('100%')
      .height(56)
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
      .alignItems(VerticalAlign.Center)

      Divider().color(AppColors.DIVIDER).width('100%')

      // Auth request content
      Column() {
        // Shield icon
        Text('🛡️')
          .fontSize(64)
          .margin({ top: 40, bottom: 24 })

        Text('授权请求')
          .fontSize(22)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Bold)
          .margin({ bottom: 8 })

        // Requester info
        Text(`来自: ${this.requesterName}`)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_SECONDARY)
          .margin({ bottom: 16 })

        // Action
        Column() {
          Text('操作')
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ bottom: 4 })

          Text(this.getActionDisplayText())
            .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
            .fontColor(AppColors.ACCENT)
            .fontWeight(FontWeight.Medium)
        }
        .width('100%')
        .padding(AppSizes.PADDING_CARD)
        .backgroundColor(AppColors.CARD_BACKGROUND)
        .borderRadius(AppSizes.BORDER_RADIUS_CARD)
        .margin({ bottom: 12 })

        // Description
        if (this.description.length > 0) {
          Column() {
            Text('详情')
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_SECONDARY)
              .margin({ bottom: 4 })

            Text(this.description)
              .fontSize(AppSizes.FONT_SIZE_BODY)
              .fontColor(AppColors.TEXT_PRIMARY)
          }
          .width('100%')
          .padding(AppSizes.PADDING_CARD)
          .backgroundColor(AppColors.CARD_BACKGROUND)
          .borderRadius(AppSizes.BORDER_RADIUS_CARD)
          .margin({ bottom: 24 })
        }

        // Response text
        if (this.hasResponded) {
          Text(this.responseText)
            .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
            .fontColor(AppColors.ACCENT)
            .fontWeight(FontWeight.Bold)
            .margin({ top: 20 })
        } else {
          // Buttons
          Row() {
            Button(AppStrings.AUTH_REJECT_BUTTON)
              .width('45%')
              .height(52)
              .fontSize(AppSizes.FONT_SIZE_BODY)
              .fontColor(AppColors.ERROR_RED)
              .backgroundColor(Color.Transparent)
              .borderWidth(1)
              .borderColor(AppColors.ERROR_RED)
              .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
              .onClick(() => {
                this.reject();
              })

            Blank().width(16)

            Button(AppStrings.AUTH_APPROVE_BUTTON)
              .width('45%')
              .height(52)
              .fontSize(AppSizes.FONT_SIZE_BODY)
              .fontColor('#FFFFFF')
              .backgroundColor(AppColors.ACCENT)
              .borderRadius(AppSizes.BORDER_RADIUS_BUTTON)
              .onClick(() => {
                this.approve();
              })
          }
          .width('100%')
          .justifyContent(FlexAlign.Center)
          .margin({ top: 20 })
        }
      }
      .width('100%')
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
      .alignItems(HorizontalAlign.Center)
      .layoutWeight(1)
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }
}
