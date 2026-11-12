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
byId('cancel').addEventListener('click', () => invoke('close_self'));

byId('form').addEventListener('submit', async (event) => {
  event.preventDefault();
  error.textContent = '';
  try {
    const snoozes = Math.round(Number(byId('snoozes').value));
    await invoke('save_advanced', { idle: Number(byId('idle').value), snoozes: Math.max(0, Math.min(10, snoozes)), language: byId('language').value });
    await invoke('close_self');
  } catch (message) {
    error.textContent = message;
  }
});

load();
