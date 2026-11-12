const byId = (id) => document.getElementById(id);
const error = byId('error');
const NUMBERS = ['work', 'rest', 'longEvery', 'longRest', 'snoozeTime'];

async function load() {
  await loadStrings();
  const s = await invoke('get_settings');
  byId('work').value = s.work_minutes;
  byId('rest').value = s.break_minutes;
  byId('longEvery').value = s.long_every_minutes;
  byId('longRest').value = s.long_break_minutes;
  byId('snoozeTime').value = s.snooze_minutes;
  byId('autostart').checked = s.start_with_mac;

  // Every change saves at once. The timer takes the new times when this window closes.
  autoSave([...NUMBERS.map(byId), byId('autostart')], save);
}

async function save() {
  if (!NUMBERS.every((id) => numberOk(byId(id)))) {
    error.textContent = t('settings.invalid');
    return false;
  }
  try {
    await invoke('save_settings', {
      work: Number(byId('work').value),
      rest: Number(byId('rest').value),
      longEvery: Number(byId('longEvery').value),
      longRest: Number(byId('longRest').value),
      snooze: Number(byId('snoozeTime').value),
      autostart: byId('autostart').checked,
    });
    error.textContent = '';
    return true;
  } catch (message) {
    error.textContent = message;
    return false;
  }
}

byId('advanced').addEventListener('click', () => invoke('open_advanced'));

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

load();
