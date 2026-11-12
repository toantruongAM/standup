const mode = (window.STANDUP && window.STANDUP.mode) || 'exit';

const first = document.getElementById('first');
const second = document.getElementById('second');
const error = document.getElementById('error');

const COPY = {
  setup: ['passcode.welcomeTitle', 'passcode.welcome'],
  settings: ['passcode.settingsTitle', 'passcode.settingsPrompt'],
  exit: ['passcode.closeTitle', 'passcode.closePrompt'],
};

loadStrings().then(() => {
  document.getElementById('title').textContent = t(COPY[mode][0]);
  document.getElementById('intro').textContent = t(COPY[mode][1]);
  first.placeholder = t(mode === 'setup' ? 'passcode.new' : 'passcode.passcode');
  second.hidden = mode !== 'setup';
});

document.getElementById('form').addEventListener('submit', async (event) => {
  event.preventDefault();
  error.textContent = '';
  if (mode === 'setup') {
    if (first.value !== second.value) {
      error.textContent = t('passcode.mismatch');
      return;
    }
    try {
      await invoke('set_passcode', { current: '', passcode: first.value });
    } catch (message) {
      error.textContent = message;
    }
    return;
  }
  const accepted = await invoke('submit_passcode', { mode, passcode: first.value });
  if (!accepted) {
    error.textContent = t('passcode.wrong');
    first.select();
  }
});

document.getElementById('cancel').addEventListener('click', () => invoke('close_self'));
