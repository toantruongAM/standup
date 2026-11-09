const { invoke } = window.__TAURI__.core;
const primary = !window.STANDUP || window.STANDUP.primary;
const MOVE_THRESHOLD = 3; // pixels; a bumped desk should not restart the timer

const clock = document.getElementById('clock');
const statusLine = document.getElementById('status');
const snooze = document.getElementById('snooze');

if (primary) {
  invoke('next_photo').then((url) => {
    if (url) document.getElementById('photo').src = url;
  });
  snooze.style.left = `${6 + Math.random() * 86}vw`;
} else {
  document.body.classList.add('secondary');
  document.getElementById('headline').textContent = 'Go walk. This screen can wait.';
}

async function refresh() {
  const state = await invoke('break_state');
  const seconds = Math.ceil(state.left_ms / 1000);
  clock.textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  statusLine.classList.toggle('warn', state.restarted);
  statusLine.textContent = state.restarted
    ? 'You touched something, so the timer restarted. Hands off!'
    : 'Any key or mouse movement restarts the timer.';
  snooze.hidden = !(primary && state.can_snooze);
}
refresh();
setInterval(refresh, 250);

let anchor = null;
window.addEventListener('mousemove', (event) => {
  // The first event only says where the pointer already is.
  if (!anchor) {
    anchor = { x: event.screenX, y: event.screenY };
    return;
  }
  if (Math.abs(event.screenX - anchor.x) + Math.abs(event.screenY - anchor.y) >= MOVE_THRESHOLD) {
    anchor = { x: event.screenX, y: event.screenY };
    invoke('activity');
  }
});
window.addEventListener('mousedown', () => invoke('activity'));
window.addEventListener('wheel', () => invoke('activity'), { passive: true });

window.addEventListener('keydown', (event) => {
  event.preventDefault();
  if (event.ctrlKey && event.metaKey && event.code === 'KeyQ') {
    invoke('lock_screen');
    return;
  }
  // Holding Control and Command on the way to the lock shortcut doesn't count as using the Mac.
  if (event.key !== 'Control' && event.key !== 'Meta') invoke('activity');
});

window.addEventListener('contextmenu', (event) => event.preventDefault());

snooze.addEventListener('click', () => invoke('snooze'));
