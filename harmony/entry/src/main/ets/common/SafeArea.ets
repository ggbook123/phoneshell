import { Context } from '@kit.AbilityKit';
import { UIContext } from '@kit.ArkUI';
import { hilog } from '@kit.PerformanceAnalysisKit';
import window from '@ohos.window';

const DOMAIN = 0x0000;
const TAG = 'SafeAreaUtil';

export interface SafeAreaInsets {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

export class SafeAreaUtil {
  static async resolveInsets(uiContext: UIContext): Promise<SafeAreaInsets> {
    const context: Context | undefined = uiContext.getHostContext();
    if (context === undefined) {
      return { top: 0, bottom: 0, left: 0, right: 0 };
    }

    try {
      const mainWindow: window.Window = await window.getLastWindow(context);
      const systemArea: window.AvoidArea = mainWindow.getWindowAvoidArea(window.AvoidAreaType.TYPE_SYSTEM);
      const cutoutArea: window.AvoidArea = mainWindow.getWindowAvoidArea(window.AvoidAreaType.TYPE_CUTOUT);

      const topPx: number = Math.max(systemArea.topRect.height, cutoutArea.topRect.height);
      const bottomPx: number = Math.max(systemArea.bottomRect.height, cutoutArea.bottomRect.height);
      const leftPx: number = Math.max(systemArea.leftRect.width, cutoutArea.leftRect.width);
      const rightPx: number = Math.max(systemArea.rightRect.width, cutoutArea.rightRect.width);

      return {
        top: uiContext.px2vp(topPx),
        bottom: uiContext.px2vp(bottomPx),
        left: uiContext.px2vp(leftPx),
        right: uiContext.px2vp(rightPx)
      };
    } catch (err) {
      hilog.warn(DOMAIN, TAG, 'Failed to resolve safe area: %{public}s', JSON.stringify(err));
      return { top: 0, bottom: 0, left: 0, right: 0 };
    }
  }
}
