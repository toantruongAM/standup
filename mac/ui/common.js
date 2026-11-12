// Shared by every page: the bridge to the app, and the app's text in the chosen language.
const { invoke } = window.__TAURI__.core;
const LOCALES = { en: 'en-US', es: 'es-ES', vi: 'vi-VN' };
let STRINGS = {};
let LANG = 'en';

/** Text for a key, filling in {0}, {1}, ... */
function t(key, ...args) {
  const text = STRINGS[key] || key;
  return text.replace(/\{(\d)\}/g, (_, i) => String(args[Number(i)]));
}

/** Loads the text and fills every element marked data-i18n or data-i18n-placeholder. */
async function loadStrings() {
  const result = await invoke('ui_strings');
  STRINGS = result.strings;
  LANG = result.lang;
  document.documentElement.lang = LANG;
  document.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = t(el.dataset.i18n); });
  document.querySelectorAll('[data-i18n-placeholder]').forEach((el) => { el.placeholder = t(el.dataset.i18nPlaceholder); });
}
