const primary = !window.STANDUP || window.STANDUP.primary;
const MOVE_THRESHOLD = 3; // pixels; a bumped desk should not restart the timer

const clock = document.getElementById('clock');
const statusLine = document.getElementById('status');
const headline = document.getElementById('headline');
const snooze = document.getElementById('snooze');

if (primary) {
  invoke('next_photo').then((url) => {
    if (url) document.getElementById('photo').src = url;
  });
  snooze.style.left = `${6 + Math.random() * 86}vw`;
} else {
  document.body.classList.add('secondary');
}

async function refresh() {
  const state = await invoke('break_state');
  const seconds = Math.ceil(state.left_ms / 1000);
  clock.textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  headline.textContent = primary ? t(state.long ? 'overlay.headlineLong' : 'overlay.headline') : t('overlay.secondary');
  statusLine.classList.toggle('warn', state.restarted);
  // Long breaks don't restart on input, so they have no status line.
  statusLine.textContent = state.restarted ? t('overlay.restarted') : state.long ? '' : t('overlay.status');
  snooze.hidden = !(primary && state.can_snooze);
}

loadStrings().then(() => {
  refresh();
  setInterval(refresh, 250);
});

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
