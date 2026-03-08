import { preferences } from '@kit.ArkData';
import { Context } from '@kit.AbilityKit';
import { StorageKeys } from '../common/Constants';

const PREFERENCES_NAME: string = 'phoneshell_prefs';
const MAX_HISTORY_ITEMS: number = 10;

class PreferencesUtilClass {
  private prefs: preferences.Preferences | null = null;

  async init(context: Context): Promise<void> {
    try {
      this.prefs = await preferences.getPreferences(context, PREFERENCES_NAME);
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
}

export const PreferencesUtil: PreferencesUtilClass = new PreferencesUtilClass();
