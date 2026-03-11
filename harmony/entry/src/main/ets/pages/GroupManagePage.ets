import { hilog } from '@kit.PerformanceAnalysisKit';
import { AppColors, AppSizes, AppStrings, ConnectionState } from '../common/Constants';
import { WebSocketService } from '../services/WebSocketService';
import { GroupMemberInfo } from '../models/MessageTypes';

const DOMAIN = 0x0000;
const PAGE_TAG = 'GroupManagePage';

@Entry
@Component
struct GroupManagePage {
  @State groupMembers: Array<GroupMemberInfo> = [];
  @State groupId: string = '';
  @State isGroupJoined: boolean = false;
  private messageListenerId: number = -1;

  aboutToAppear(): void {
    this.activatePage();
  }

  aboutToDisappear(): void {
    this.deactivatePage();
  }

  onPageShow(): void {
    this.activatePage();
  }

  onPageHide(): void {
    this.deactivatePage();
  }

  private activatePage(): void {
    this.groupId = WebSocketService.getGroupId();
    this.isGroupJoined = WebSocketService.getIsGroupJoined();
    this.groupMembers = WebSocketService.getCachedGroupMembers();

    if (this.messageListenerId === -1) {
      this.messageListenerId = WebSocketService.addOnMessage((type: string, data: Record<string, Object>) => {
        if (type === 'group.member.list' || type === 'group.member.joined' || type === 'group.member.left') {
          this.groupMembers = WebSocketService.getCachedGroupMembers();
        } else if (type === 'group.join.accepted') {
          this.groupId = WebSocketService.getGroupId();
          this.isGroupJoined = true;
          this.groupMembers = WebSocketService.getCachedGroupMembers();
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

  private kickMember(deviceId: string): void {
    hilog.info(DOMAIN, PAGE_TAG, 'kickMember device=%{public}s', deviceId);
    WebSocketService.kickMember(deviceId);
  }

  private changeServer(newServerDeviceId: string): void {
    hilog.info(DOMAIN, PAGE_TAG, 'changeServer newServer=%{public}s', newServerDeviceId);
    WebSocketService.sendServerChangeRequest(newServerDeviceId);
  }

  private getRoleDisplayText(role: string): string {
    if (role === 'Server') {
      return '服务器';
    } else if (role === 'Mobile') {
      return '手机(管理员)';
    }
    return '成员';
  }

  private getRoleColor(role: string): string {
    if (role === 'Server') {
      return AppColors.ACCENT;
    } else if (role === 'Mobile') {
      return '#FFA500';
    }
    return AppColors.TEXT_SECONDARY;
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

        Text(AppStrings.GROUP_MANAGE_TITLE)
          .fontSize(AppSizes.FONT_SIZE_SUBTITLE)
          .fontColor(AppColors.TEXT_PRIMARY)
          .fontWeight(FontWeight.Bold)

        Blank()
        Text('').width(60) // Balance spacer
      }
      .width('100%')
      .height(56)
      .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })
      .alignItems(VerticalAlign.Center)

      Divider().color(AppColors.DIVIDER).width('100%')

      if (!this.isGroupJoined) {
        // Not in a group
        Column() {
          Text('📡')
            .fontSize(48)
            .margin({ bottom: 16 })
          Text(AppStrings.GROUP_STATUS_NOT_JOINED)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_SECONDARY)
        }
        .width('100%')
        .layoutWeight(1)
        .justifyContent(FlexAlign.Center)
        .alignItems(HorizontalAlign.Center)
      } else {
        // Group info
        Column() {
          // Group ID
          Column() {
            Text('群组 ID')
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_SECONDARY)
              .margin({ bottom: 4 })
            Text(this.groupId)
              .fontSize(AppSizes.FONT_SIZE_SMALL)
              .fontColor(AppColors.TEXT_PRIMARY)
              .fontFamily('monospace')
              .maxLines(1)
              .textOverflow({ overflow: TextOverflow.Ellipsis })
          }
          .width('100%')
          .padding(AppSizes.PADDING_CARD)
          .backgroundColor(AppColors.CARD_BACKGROUND)
          .borderRadius(AppSizes.BORDER_RADIUS_CARD)
          .margin({ top: 12, bottom: 12 })
          .alignItems(HorizontalAlign.Start)

          // Member count
          Text(`${AppStrings.GROUP_MEMBERS_TITLE} (${this.groupMembers.length})`)
            .fontSize(AppSizes.FONT_SIZE_BODY)
            .fontColor(AppColors.TEXT_PRIMARY)
            .fontWeight(FontWeight.Bold)
            .margin({ bottom: 8 })
            .width('100%')
        }
        .width('100%')
        .padding({ left: AppSizes.PADDING_PAGE, right: AppSizes.PADDING_PAGE })

        // Member list
        List({ space: 8 }) {
          ForEach(this.groupMembers, (member: GroupMemberInfo) => {
            ListItem() {
              Row() {
                // Online indicator
                Circle()
                  .width(AppSizes.STATUS_DOT_SIZE)
                  .height(AppSizes.STATUS_DOT_SIZE)
                  .fill(member.isOnline ? AppColors.ONLINE_GREEN : AppColors.OFFLINE_GRAY)
                  .margin({ right: 10 })

                // Member info
                Column() {
                  Text(member.displayName)
                    .fontSize(AppSizes.FONT_SIZE_BODY)
                    .fontColor(AppColors.TEXT_PRIMARY)
                    .maxLines(1)
                    .textOverflow({ overflow: TextOverflow.Ellipsis })

                  Row() {
                    Text(member.os)
                      .fontSize(12)
                      .fontColor(AppColors.TEXT_SECONDARY)
                      .margin({ right: 8 })

                    Text(this.getRoleDisplayText(member.role))
                      .fontSize(12)
                      .fontColor(this.getRoleColor(member.role))
                      .fontWeight(FontWeight.Medium)
                  }
                  .margin({ top: 2 })
                }
                .alignItems(HorizontalAlign.Start)
                .layoutWeight(1)

                // Kick button (only for non-Server, non-Mobile members)
                if (member.role !== 'Server' && member.role !== 'Mobile') {
                  Column() {
                    if (member.isOnline) {
                      Button('设为服务器')
                        .height(28)
                        .fontSize(11)
                        .fontColor(AppColors.ACCENT)
                        .backgroundColor(Color.Transparent)
                        .borderWidth(1)
                        .borderColor(AppColors.ACCENT)
                        .borderRadius(AppSizes.BORDER_RADIUS_TAG)
                        .margin({ bottom: 4 })
                        .onClick(() => {
                          this.changeServer(member.deviceId);
                        })
                    }
                    Button('踢出')
                      .height(28)
                      .fontSize(12)
                      .fontColor(AppColors.ERROR_RED)
                      .backgroundColor(Color.Transparent)
                      .borderWidth(1)
                      .borderColor(AppColors.ERROR_RED)
                      .borderRadius(AppSizes.BORDER_RADIUS_TAG)
                      .onClick(() => {
                        this.kickMember(member.deviceId);
                      })
                  }
                }
              }
              .width('100%')
              .padding(AppSizes.PADDING_CARD)
              .backgroundColor(AppColors.CARD_BACKGROUND)
              .borderRadius(AppSizes.BORDER_RADIUS_CARD)
            }
          }, (member: GroupMemberInfo, index: number) => `${member.deviceId}_${index}`)
        }
        .width('100%')
        .padding({
          left: AppSizes.PADDING_PAGE,
          right: AppSizes.PADDING_PAGE,
          top: 4,
          bottom: 12
        })
        .layoutWeight(1)
      }
    }
    .width('100%')
    .height('100%')
    .backgroundColor(AppColors.BACKGROUND)
  }
}
