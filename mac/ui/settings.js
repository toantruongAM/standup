const byId = (id) => document.getElementById(id);
const error = byId('error');

async function load() {
  await loadStrings();
  const s = await invoke('get_settings');
  byId('work').value = s.work_minutes;
  byId('rest').value = s.break_minutes;
  byId('longEvery').value = s.long_every_minutes;
  byId('longRest').value = s.long_break_minutes;
  byId('snoozeTime').value = s.snooze_minutes;
  byId('autostart').checked = s.start_with_mac;
}

byId('advanced').addEventListener('click', () => invoke('open_advanced'));
byId('cancel').addEventListener('click', () => invoke('close_self'));

byId('change').addEventListener('click', async () => {
  error.textContent = '';
  if (byId('next').value !== byId('again').value) {
    error.textContent = t('passcode.mismatch');
    return;
  }
  try {
    await invoke('set_passcode', { current: byId('current').value, passcode: byId('next').value });
    ['current', 'next', 'again'].forEach((id) => { byId(id).value = ''; });
    error.textContent = t('passcode.changed');
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
      longEvery: Number(byId('longEvery').value),
      longRest: Number(byId('longRest').value),
      snooze: Number(byId('snoozeTime').value),
      autostart: byId('autostart').checked,
    });
    await invoke('close_self');
  } catch (message) {
    error.textContent = message;
  }
});

load();
