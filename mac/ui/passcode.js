const { invoke } = window.__TAURI__.core;
const mode = (window.STANDUP && window.STANDUP.mode) || 'exit';

const first = document.getElementById('first');
const second = document.getElementById('second');
const error = document.getElementById('error');

const copy = {
  setup: ['Welcome to StandUp', "Choose a passcode. You'll need it to change settings or close the app."],
  settings: ['Settings', 'Enter your passcode to change settings.'],
  exit: ['Close StandUp', 'Enter your passcode to close StandUp.'],
};
document.getElementById('title').textContent = copy[mode][0];
document.getElementById('intro').textContent = copy[mode][1];
first.placeholder = mode === 'setup' ? 'New passcode' : 'Passcode';
second.hidden = mode !== 'setup';

document.getElementById('form').addEventListener('submit', async (event) => {
  event.preventDefault();
  error.textContent = '';
  if (mode === 'setup') {
    if (first.value !== second.value) {
      error.textContent = "The two passcodes don't match.";
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
    error.textContent = 'That passcode is not right.';
    first.select();
  }
});

document.getElementById('cancel').addEventListener('click', () => invoke('close_self'));
