const byId = (id) => document.getElementById(id);
const error = byId('error');

function showPool(a) {
  byId('pool').textContent = t('advanced.pool', a.built_in_pictures, a.your_pictures);
}

async function load() {
  await loadStrings();
  const a = await invoke('get_advanced');
  byId('idle').value = a.idle_minutes;
  byId('snoozes').value = a.max_snoozes;
  const select = byId('language');
  for (const option of a.languages) {
    const item = document.createElement('option');
    item.value = option.code;
    item.textContent = option.name;
    item.selected = option.code === a.language;
    select.appendChild(item);
  }
  showPool(a);

  // Every change saves at once. A new language reloads this window, so its check shows after.
  autoSave([byId('idle'), byId('snoozes'), select], save);
  if (sessionStorage.getItem('flash') === 'language') {
    sessionStorage.removeItem('flash');
    flashMark(markFor(select));
  }
}

async function save(input) {
  const idle = byId('idle');
  const snoozes = byId('snoozes');
  if (!numberOk(idle)) {
    error.textContent = t('advanced.invalidIdle');
    return false;
  }
  if (!numberOk(snoozes)) return false;
  if (input.id === 'language') sessionStorage.setItem('flash', 'language');
  try {
    await invoke('save_advanced', { idle: Number(idle.value), snoozes: Math.round(Number(snoozes.value)), language: byId('language').value });
    error.textContent = '';
    return true;
  } catch (message) {
    sessionStorage.removeItem('flash');
    error.textContent = message;
    return false;
  }
}

function toBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

function isReadable(file) {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const image = new Image();
    image.onload = () => { URL.revokeObjectURL(url); resolve(true); };
    image.onerror = () => { URL.revokeObjectURL(url); resolve(false); };
    image.src = url;
  });
}

byId('add').addEventListener('click', () => byId('picker').click());

byId('picker').addEventListener('change', async (event) => {
  const rejected = [];
  for (const file of event.target.files) {
    try {
      if (!(await isReadable(file))) throw new Error('unreadable');
      await invoke('add_picture', { name: file.name, data: toBase64(await file.arrayBuffer()) });
    } catch (_) {
      rejected.push(file.name);
    }
  }
  event.target.value = '';
  error.textContent = rejected.length ? `${t('advanced.rejected')} ${rejected.join(', ')}` : '';
  showPool(await invoke('get_advanced'));
});

byId('open').addEventListener('click', () => invoke('open_pictures_folder'));

load();
