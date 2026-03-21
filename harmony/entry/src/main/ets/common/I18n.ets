import { PreferencesUtil } from '../services/PreferencesUtil';
import { StorageKeys } from './Constants';

export type AppLanguage = 'zh' | 'en';

export class I18n {
  private static currentLanguage: AppLanguage = 'zh';
  private static hasLocalOverride: boolean = false;

  static getLanguage(): AppLanguage {
    return I18n.currentLanguage;
  }

  static async syncFromPreferences(): Promise<AppLanguage> {
    if (I18n.hasLocalOverride) {
      void PreferencesUtil.setString(StorageKeys.APP_LANGUAGE, I18n.currentLanguage);
      return I18n.currentLanguage;
    }
    const saved: string = await PreferencesUtil.getString(StorageKeys.APP_LANGUAGE);
    if (saved === 'zh' || saved === 'en') {
      I18n.currentLanguage = saved as AppLanguage;
    }
    return I18n.currentLanguage;
  }

  static setLanguage(language: AppLanguage): void {
    I18n.currentLanguage = language;
    I18n.hasLocalOverride = true;
    void PreferencesUtil.setString(StorageKeys.APP_LANGUAGE, language);
  }

  static t(language: AppLanguage, zhText: string, enText: string): string {
    return language === 'en' ? enText : zhText;
  }
}
