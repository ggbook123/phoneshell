import { preferences } from '@kit.ArkData';
import { Context } from '@kit.AbilityKit';
import { StorageKeys } from '../common/Constants';

const PREFERENCES_NAME: string = 'phoneshell_prefs';
const MAX_HISTORY_ITEMS: number = 10;

class PreferencesUtilClass {
  private prefs: preferences.Preferences | null = null;
  private cachedMobileDeviceId: string = '';

  async init(context: Context): Promise<void> {
    try {
      this.prefs = await preferences.getPreferences(context, PREFERENCES_NAME);
      try {
        const savedId: string = await this.prefs.get(StorageKeys.MOBILE_DEVICE_ID, '') as string;
        if (savedId.length > 0) {
          this.cachedMobileDeviceId = savedId;
        }
      } catch (_e) {
        // Ignore read errors
      }
    } catch (e) {
      // Ignore init errors
    }
  }

  async getConnectionHistory(): Promise<Array<string>> {
    if (this.prefs === null) {
      return [];
    }
    try {
      const raw: string = await this.prefs.get(StorageKeys.CONNECTION_HISTORY, '[]') as string;
      return JSON.parse(raw) as Array<string>;
    } catch (e) {
      return [];
    }
  }

  async addConnectionHistory(url: string): Promise<void> {
    if (this.prefs === null) {
      return;
    }
    try {
      let history: Array<string> = await this.getConnectionHistory();

      // Remove duplicate if exists
      history = history.filter((item: string) => item !== url);

      // Add to front
      history.unshift(url);

      // Trim to max items
      if (history.length > MAX_HISTORY_ITEMS) {
        history = history.slice(0, MAX_HISTORY_ITEMS);
      }

      await this.prefs.put(StorageKeys.CONNECTION_HISTORY, JSON.stringify(history));
      await this.prefs.flush();
    } catch (e) {
      // Ignore write errors
    }
  }

  async removeConnectionHistory(url: string): Promise<void> {
    if (this.prefs === null) {
      return;
    }
    try {
      let history: Array<string> = await this.getConnectionHistory();
      history = history.filter((item: string) => item !== url);
      await this.prefs.put(StorageKeys.CONNECTION_HISTORY, JSON.stringify(history));
      await this.prefs.flush();
    } catch (e) {
      // Ignore write errors
    }
  }

  async getLastServerUrl(): Promise<string> {
    if (this.prefs === null) {
      return '';
    }
    try {
      return await this.prefs.get(StorageKeys.LAST_SERVER_URL, '') as string;
    } catch (e) {
      return '';
    }
  }

  async setLastServerUrl(url: string): Promise<void> {
    if (this.prefs === null) {
      return;
    }
    try {
      await this.prefs.put(StorageKeys.LAST_SERVER_URL, url);
      await this.prefs.flush();
    } catch (e) {
      // Ignore write errors
    }
  }

  async getString(key: string): Promise<string> {
    if (this.prefs === null) {
      return '';
    }
    try {
      return await this.prefs.get(key, '') as string;
    } catch (e) {
      return '';
    }
  }

  async setString(key: string, value: string): Promise<void> {
    if (this.prefs === null) {
      return;
    }
    try {
      await this.prefs.put(key, value);
      await this.prefs.flush();
    } catch (e) {
      // Ignore write errors
    }
  }

  setServerAddress(context: Context, url: string): void {
    this.setLastServerUrl(url);
  }

  setGroupSecret(context: Context, secret: string): void {
    this.setString(StorageKeys.GROUP_SECRET, secret);
  }

  getMobileDeviceId(context: Context): string {
    if (this.cachedMobileDeviceId.length > 0) {
      return this.cachedMobileDeviceId;
    }

    const timePart: string = Date.now().toString(36);
    const randPart: string = Math.random().toString(36).substring(2, 8);
    this.cachedMobileDeviceId = `mobile-${timePart}-${randPart}`;
    this.setString(StorageKeys.MOBILE_DEVICE_ID, this.cachedMobileDeviceId);
    return this.cachedMobileDeviceId;
  }
}

export const PreferencesUtil: PreferencesUtilClass = new PreferencesUtilClass();
