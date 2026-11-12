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

/** Shows the green check after a save; it stays a moment, then fades (see .mark in common.css). */
function flashMark(mark) {
  if (!mark) return;
  clearTimeout(mark.fadeTimer);
  mark.classList.add('on');
  mark.fadeTimer = setTimeout(() => mark.classList.remove('on'), 1500);
}

/** The check mark that belongs to an input. */
function markFor(input) {
  return input.closest('label').querySelector('.mark');
}

/** True when a number box holds a number within its range (any step). */
function numberOk(input) {
  const v = input.validity;
  return input.value !== '' && !v.badInput && !v.rangeUnderflow && !v.rangeOverflow;
}

/**
 * Saves as you go: on every committed change, and after a short pause in typing.
 * save(input) returns a promise; the input's check mark lights when it resolves.
 * Enter confirms the box, Escape closes the window.
 */
function autoSave(inputs, save) {
  for (const input of inputs) {
    let pause;
    let saved = input.type === 'checkbox' ? input.checked : input.value;
    const commit = async () => {
      clearTimeout(pause);
      const value = input.type === 'checkbox' ? input.checked : input.value;
      if (value === saved) return;
      if (await save(input)) {
        saved = value;
        flashMark(markFor(input));
      }
    };
    input.addEventListener('change', commit);
    if (input.type === 'number') {
      input.addEventListener('input', () => { clearTimeout(pause); pause = setTimeout(commit, 700); });
    }
  }
  document.addEventListener('submit', (event) => {
    event.preventDefault();
    if (document.activeElement) document.activeElement.blur();
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') invoke('close_self');
  });
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
