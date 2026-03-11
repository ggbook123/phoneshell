import { hilog } from '@kit.PerformanceAnalysisKit';
import { scanBarcode } from '@kit.ScanKit';
import { AppColors, AppSizes, AppStrings, ConnectionState } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';
import { PreferencesUtil } from '../services/PreferencesUtil';

const DOMAIN = 0x0000;
const PAGE_TAG = 'ScanPage';

@Entry
@Component
struct ScanPage {
  @State scanResult: string = '';
  @State scanStatus: string = '';
  @State isScanning: boolean = false;
  @State parseError: string = '';

  aboutToAppear(): void {
    hilog.info(DOMAIN, PAGE_TAG, 'ScanPage aboutToAppear');
  }

  private startScan(): void {
    this.isScanning = true;
    this.scanStatus = '正在启动扫码...';
    this.parseError = '';

    let options: scanBarcode.ScanOptions = {
      scanTypes: [],
      enableMultiMode: false,
      enableAlbum: true
    };

    scanBarcode.startScanForResult(getContext(this), options)
      .then((result: scanBarcode.ScanResult) => {
        this.isScanning = false;
        const value: string = result.originalValue;
        hilog.info(DOMAIN, PAGE_TAG, 'scan result=%{public}s', value);
        this.scanResult = value;
        this.scanStatus = '扫码成功';
        this.handleScanResult(value);
      })
      .catch((err: Error) => {
        this.isScanning = false;
        hilog.error(DOMAIN, PAGE_TAG, 'scan error=%{public}s', err.message);
        this.scanStatus = '扫码失败: ' + err.message;
      });
  }

  private handleScanResult(payload: string): void {
    // Expected format: phoneshell://bind?server=ws%3A%2F%2Fhost%3Aport%2Fws%2F&groupId=xxx&groupSecret=xxx
    if (!payload.startsWith('phoneshell://')) {
      this.parseError = '无效的二维码格式';
      return;
    }

    const params: Map<string, string> = this.parseQrParams(payload);
    const server: string = params.get('server') ?? '';
    const groupId: string = params.get('groupId') ?? '';
    const groupSecret: string = params.get('groupSecret') ?? '';

    // Debug: show parsed params
    hilog.info(DOMAIN, PAGE_TAG, 'parsed params: server=%{public}s groupId=%{public}s groupSecret=%{public}s',
      server, groupId, groupSecret.length > 0 ? groupSecret.substring(0, 8) + '...' : '(empty)');
    this.scanStatus = `解析: server=${server.length > 0 ? server.substring(0, 30) : '(空)'}, ` +
      `groupId=${groupId.length > 0 ? groupId.substring(0, 8) : '(空)'}, ` +
      `secret=${groupSecret.length > 0 ? '有' : '(空)'}`;

    if (server.length === 0 || groupSecret.length === 0) {
      this.parseError = `二维码缺少必要参数 (server=${server.length > 0 ? '有' : '无'}, secret=${groupSecret.length > 0 ? '有' : '无'})`;
      return;
    }

    hilog.info(DOMAIN, PAGE_TAG, 'connecting to server=%{public}s groupId=%{public}s', server, groupId);

    // Save settings
    PreferencesUtil.setServerAddress(getContext(this), server);
    PreferencesUtil.setGroupSecret(getContext(this), groupSecret);

    // Connect to server (append token for handshake auth)
    this.scanStatus = `正在连接 ${server.substring(0, 40)}...`;
    const connectUrl: string = this.buildConnectUrlWithToken(server, groupSecret);
    WebSocketService.connect(connectUrl);

    // After connection, join group and bind mobile
    let didHandleConnected: boolean = false;
    let stateListenerId: number = -1;
    let msgListenerId: number = -1;
    const connectTimeoutMs: number = 8000;
    const timeoutId: number = setTimeout(() => {
      if (!didHandleConnected) {
        this.scanStatus = '连接失败，请检查网络或服务器地址';
        this.parseError = '无法连接到服务器';
        if (stateListenerId !== -1) {
          WebSocketService.removeOnStateChange(stateListenerId);
        }
      }
    }, connectTimeoutMs);

    const handleConnected = () => {
      if (didHandleConnected) {
        return;
      }
      didHandleConnected = true;
      clearTimeout(timeoutId);
      if (stateListenerId !== -1) {
        WebSocketService.removeOnStateChange(stateListenerId);
      }

      this.scanStatus = '已连接，正在加入群组...';

      const mobileDeviceId: string = PreferencesUtil.getMobileDeviceId(getContext(this));
      const displayName: string = 'HarmonyOS Device';

      // Join group first
      WebSocketService.joinGroup(groupSecret, mobileDeviceId, displayName);

      // Listen for group join acceptance to bind mobile
      msgListenerId = WebSocketService.addOnMessage((type: string, _data: Record<string, Object>) => {
        if (type === 'group.join.accepted') {
          WebSocketService.removeOnMessage(msgListenerId);
          this.scanStatus = '已加入群组，正在绑定手机...';
          const joinedGroupId: string = WebSocketService.getGroupId();
          WebSocketService.bindMobile(joinedGroupId, mobileDeviceId, displayName);
          hilog.info(DOMAIN, PAGE_TAG, 'group joined, binding mobile');

          // Navigate back
          this.getUIContext().getRouter().back();
        } else if (type === 'group.join.rejected') {
          WebSocketService.removeOnMessage(msgListenerId);
          this.parseError = '加入群组被拒绝';
          this.scanStatus = '加入群组失败';
        } else if (type === 'error') {
          const errMsg: Object | undefined = _data['message'];
          this.parseError = `错误: ${typeof errMsg === 'string' ? errMsg : '未知'}`;
        }
      });
    };

    stateListenerId = WebSocketService.addOnStateChange((state: ConnectionState) => {
      if (state === ConnectionState.CONNECTED) {
        handleConnected();
      } else if (state === ConnectionState.DISCONNECTED && !didHandleConnected) {
        this.scanStatus = '连接断开，正在重试...';
      }
    });

    // Handle race where connection is already established before listener attaches
    if (WebSocketService.getConnectionState() === ConnectionState.CONNECTED) {
      handleConnected();
    }
  }

  private buildConnectUrlWithToken(baseUrl: string, groupSecret: string): string {
    const trimmedSecret: string = groupSecret.trim();
    if (trimmedSecret.length === 0) {
      return baseUrl;
    }

    if (baseUrl.indexOf('token=') >= 0) {
      return baseUrl;
    }

    const token: string = encodeURIComponent(trimmedSecret);
    if (baseUrl.indexOf('?') >= 0) {
      return baseUrl + '&token=' + token;
    }
    return baseUrl + '?token=' + token;
  }

  private parseQrParams(payload: string): Map<string, string> {
    const result: Map<string, string> = new Map();
    const questionIdx: number = payload.indexOf('?');
    if (questionIdx < 0) {
      return result;
    }
    const queryStr: string = payload.substring(questionIdx + 1);
    const pairs: Array<string> = queryStr.split('&');
    for (let i: number = 0; i < pairs.length; i++) {
      const pair: string = pairs[i];
      const eqIdx: number = pair.indexOf('=');
      if (eqIdx > 0) {
        const key: string = pair.substring(0, eqIdx);
        const value: string = decodeURIComponent(pair.substring(eqIdx + 1));
        result.set(key, value);
      }
    }
    return result;
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

        Text('扫码连接')
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Bold)

        Blank()
        Text('').width(60)
      }
      .width('100%')
      .height(56)
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
      .alignItems(VerticalAlign.Center)

      Divider().color(AppColors.DIVIDER).width('100%')

      // Content
      Column() {
        Text('扫描服务器二维码')
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.TEXT_PRIMARY)
          .margin({ bottom: 8 })

        Text('使用 PC 端生成的 QR 码进行扫码连接，自动加入群组并绑定手机。')
          .fontSize(AppSizes.FONT_SIZE_SMALL)
          .fontColor(AppColors.TEXT_SECONDARY)
          .textAlign(TextAlign.Center)
          .margin({ bottom: 24 })
          .padding({ left: 20, right: 20 })

        Button(this.isScanning ? '扫码中...' : '开始扫码')
          .width(200)
          .height(44)
          .fontSize(AppSizes.FONT_SIZE_BODY)
          .fontColor(AppColors.BACKGROUND)
          .backgroundColor(AppColors.ACCENT)
          .borderRadius(AppSizes.BORDER_RADIUS_CARD)
          .enabled(!this.isScanning)
          .onClick(() => {
            this.startScan();
          })
          .margin({ bottom: 20 })

        if (this.scanStatus.length > 0) {
          Text(this.scanStatus)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.TEXT_SECONDARY)
            .margin({ bottom: 8 })
        }

        if (this.parseError.length > 0) {
          Text(this.parseError)
            .fontSize(AppSizes.FONT_SIZE_SMALL)
            .fontColor(AppColors.ERROR_RED)
            .margin({ bottom: 8 })
        }

        if (this.scanResult.length > 0) {
          Column() {
            Text('扫码结果')
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_SECONDARY)
              .margin({ bottom: 4 })
            Text(this.scanResult)
              .fontSize(12)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontFamily('monospace')
              .maxLines(3)
              .textOverflow({ overflow: TextOverflow.Ellipsis })
          }
          .width('100%')
          .padding(AppSizes.PADDING_CARD)
          .backgroundColor(AppColors.CARD_BACKGROUND)
          .borderRadius(AppSizes.BORDER_RADIUS_CARD)
          .alignItems(HorizontalAlign.Start)
        }
      }
      .width('100%')
      .layoutWeight(1)
      .justifyContent(FlexAlign.Center)
      .alignItems(HorizontalAlign.Center)
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }
}
