import { ref } from 'vue';

export type LanguageCode = 'en' | 'zh';

const STORAGE_KEY = 'phoneshell_lang';

function detectDefaultLanguage(): LanguageCode {
  if (typeof navigator !== 'undefined') {
    const nav = navigator.language?.toLowerCase() ?? '';
    if (nav.startsWith('zh')) return 'zh';
  }
  return 'en';
}

function loadLanguage(): LanguageCode {
  if (typeof localStorage !== 'undefined') {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'en' || stored === 'zh') return stored;
  }
  return detectDefaultLanguage();
}

const language = ref<LanguageCode>(loadLanguage());

function applyDocumentLang(next: LanguageCode): void {
  if (typeof document === 'undefined') return;
  document.documentElement.lang = next === 'zh' ? 'zh' : 'en';
}

applyDocumentLang(language.value);

export function useLanguage() {
  function setLanguage(next: LanguageCode): void {
    language.value = next;
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(STORAGE_KEY, next);
    }
    applyDocumentLang(next);
  }

  return {
    language,
    setLanguage,
  };
}
