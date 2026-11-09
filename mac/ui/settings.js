const { invoke } = window.__TAURI__.core;
const byId = (id) => document.getElementById(id);
const error = byId('error');

async function load() {
  const s = await invoke('get_settings');
  byId('work').value = s.work_minutes;
  byId('rest').value = s.break_minutes;
  byId('snoozeTime').value = s.snooze_minutes;
  byId('autostart').checked = s.start_with_mac;
  showPool(s);
}

function showPool(s) {
  byId('pool').textContent = `Picture pool: ${s.built_in_pictures} built-in, ${s.your_pictures} of yours`;
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
  error.textContent = rejected.length ? `Couldn't add: ${rejected.join(', ')}` : '';
  showPool(await invoke('get_settings'));
});

byId('open').addEventListener('click', () => invoke('open_pictures_folder'));
byId('cancel').addEventListener('click', () => invoke('close_self'));

byId('change').addEventListener('click', async () => {
  error.textContent = '';
  if (byId('next').value !== byId('again').value) {
    error.textContent = "The two new passcodes don't match.";
    return;
  }
  try {
    await invoke('set_passcode', { current: byId('current').value, passcode: byId('next').value });
    ['current', 'next', 'again'].forEach((id) => { byId(id).value = ''; });
    error.textContent = 'Passcode changed.';
  } catch (message) {
    error.textContent = message;
  }
});

byId('form').addEventListener('submit', async (event) => {
  event.preventDefault();
  error.textContent = '';
  try {
    await invoke('save_settings', {
      work: Number(byId('work').value),
      rest: Number(byId('rest').value),
      snooze: Number(byId('snoozeTime').value),
      autostart: byId('autostart').checked,
    });
    await invoke('close_self');
  } catch (message) {
    error.textContent = message;
  }
});

load();
