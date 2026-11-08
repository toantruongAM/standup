using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StandUp
{
    sealed class TestOptions
    {
        public int? WorkSeconds, BreakSeconds, SnoozeSeconds, ExitAfterSeconds;
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly AppSettings _settings;
        readonly TestOptions _test;
        readonly BreakEngine _engine;
        readonly History _history = new History();
        readonly PhotoPool _photos = new PhotoPool();
        readonly Random _random = new Random();
        readonly List<OverlayForm> _overlays = new List<OverlayForm>();
        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _status, _today, _breakNow;
        readonly Timer _timer;

        const int PresentingCheckTicks = 8;   // every 2 seconds
        static readonly TimeSpan PresentingEndDelay = TimeSpan.FromSeconds(10);   // ride out a slide show that is restarted

        InputGuard _guard;
        Bitmap _photo;
        int _topmostCountdown, _presentingCountdown;
        DateTime _lastPresentingSeen;
        bool _dialogOpen;

        public TrayApp(AppSettings settings, TestOptions test)
        {
            _settings = settings;
            _test = test;
            _engine = new BreakEngine(BuildConfig());
            _engine.PhaseChanged += OnPhaseChanged;
            _engine.BreakCompleted += (at, seconds) =>
            {
                _history.RecordBreak(at, seconds);
                Log.Write("break completed");
            };
            _engine.Snoozed += at => _history.RecordSnooze(at, _engine.Config.SnoozeSeconds);

            var menu = new ContextMenuStrip();
            _status = new ToolStripMenuItem { Enabled = false };
            _today = new ToolStripMenuItem { Enabled = false };
            _breakNow = new ToolStripMenuItem("Take a break now", null, delegate { _engine.TakeBreakNow(DateTime.UtcNow); });
            menu.Items.Add(_status);
            menu.Items.Add(_today);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_breakNow);
            menu.Items.Add("Settings...", null, delegate { OpenSettings(); });
            menu.Items.Add("History...", null, delegate { OpenHistory(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit...", null, delegate { TryExit(); });
            menu.Opening += delegate { RefreshTray(); };

            _tray = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                Text = "StandUp",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += delegate { OpenHistory(); };

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            LockScreenTimer.Clear();
            _engine.Start(DateTime.UtcNow);

            _timer = new Timer { Interval = 250 };
            _timer.Tick += OnTick;
            _timer.Start();

            if (test.ExitAfterSeconds.HasValue)
            {
                var stop = new Timer { Interval = test.ExitAfterSeconds.Value * 1000 };
                stop.Tick += delegate { stop.Stop(); Shutdown(); };
                stop.Start();
            }
        }

        EngineConfig BuildConfig()
        {
            EngineConfig config = _settings.ToEngineConfig();
            if (_test.WorkSeconds.HasValue) config.WorkSeconds = _test.WorkSeconds.Value;
            if (_test.BreakSeconds.HasValue) config.BreakSeconds = _test.BreakSeconds.Value;
            if (_test.SnoozeSeconds.HasValue) config.SnoozeSeconds = _test.SnoozeSeconds.Value;
            return config;
        }

        void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            CheckPresenting(now);
            _engine.Tick(now);

            foreach (OverlayForm overlay in _overlays) overlay.UpdateClock(now);

            if (_overlays.Count > 0 && --_topmostCountdown <= 0)
            {
                _topmostCountdown = 2;
                foreach (OverlayForm overlay in _overlays) overlay.KeepOnTop();
            }
            RefreshTray();
        }

        void CheckPresenting(DateTime now)
        {
            // Always look just before a break would start, so one can't slip in between checks.
            bool breakIsDue = _engine.Phase == Phase.Working && now >= _engine.Deadline;
            if (--_presentingCountdown > 0 && !breakIsDue) return;
            _presentingCountdown = PresentingCheckTicks;

            string reason = PresentingDetector.Detect();
            if (reason != null)
            {
                if (!_engine.IsPresenting) Log.Write("presenting: " + reason);
                _lastPresentingSeen = now;
                _engine.SetPresenting(true, now);
            }
            else if (_engine.IsPresenting && now - _lastPresentingSeen >= PresentingEndDelay)
            {
                Log.Write("presenting ended");
                _engine.SetPresenting(false, now);
            }
        }

        void OnPhaseChanged()
        {
            Log.Write("phase " + _engine.Phase + " snoozes=" + _engine.SnoozesUsed);

            if (_engine.Phase == Phase.Break) ShowOverlays();
            else HideOverlays();

            if (_engine.IsBreakPaused) LockScreenTimer.Publish(_engine.Deadline, _engine.Config.BreakSeconds);
            else LockScreenTimer.Clear();
        }

        void ShowOverlays()
        {
            if (_overlays.Count > 0) return;

            _photo = _photos.Next();
            foreach (Screen screen in Screen.AllScreens)
                _overlays.Add(new OverlayForm(screen, _photo, _engine, Snooze, _random));
            foreach (OverlayForm overlay in _overlays) overlay.Show();

            _guard = new InputGuard(delegate
            {
                _engine.Activity(DateTime.UtcNow);
                Log.Write("input during break");
            });
        }

        void HideOverlays()
        {
            if (_guard != null)
            {
                _guard.Dispose();
                _guard = null;
            }
            foreach (OverlayForm overlay in _overlays)
            {
                overlay.Close();
                overlay.Dispose();
            }
            _overlays.Clear();
            if (_photo != null)
            {
                _photo.Dispose();
                _photo = null;
            }
        }

        void Snooze()
        {
            _engine.Snooze(DateTime.UtcNow);
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) _engine.Lock(DateTime.UtcNow);
            else if (e.Reason == SessionSwitchReason.SessionUnlock) _engine.Unlock(DateTime.UtcNow);
        }

        void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) _engine.Suspend(DateTime.UtcNow);
            else if (e.Mode == PowerModes.Resume) _engine.Resume(DateTime.UtcNow);
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (_overlays.Count == 0) return;
            HideOverlays();
            ShowOverlays();
        }

        void RefreshTray()
        {
            string state;
            switch (_engine.Phase)
            {
                case Phase.Break: state = "On a break"; break;
                case Phase.Paused: state = _engine.IsPresenting ? "Paused while presenting" : "Paused while locked"; break;
                default:
                    int minutes = (int)Math.Ceiling((_engine.Deadline - DateTime.UtcNow).TotalMinutes);
                    state = "Next break in " + Math.Max(1, minutes) + " min" + (_engine.InSnooze ? " (snoozed)" : "");
                    break;
            }

            int breaks = _history.BreaksToday;
            string today = "Breaks today: " + breaks;
            if (_status.Text != state) _status.Text = state;
            if (_today.Text != today) _today.Text = today;
            _breakNow.Enabled = _engine.Phase == Phase.Working;

            string tip = "StandUp: " + state + ", " + breaks + " today";
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (_tray.Text != tip) _tray.Text = tip;
        }

        void OpenSettings()
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                using (var gate = new PasscodeForm(_settings, "Enter your passcode to change settings."))
                {
                    if (gate.ShowDialog() != DialogResult.OK) return;
                }
                using (var form = new SettingsForm(_settings))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;
                    _engine.ApplyConfig(BuildConfig(), DateTime.UtcNow);
                    if (!Program.TestMode) AutoStart.Apply(_settings.StartWithWindows, Application.ExecutablePath);
                }
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        void OpenHistory()
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                using (var form = new HistoryForm(_history)) form.ShowDialog();
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        void TryExit()
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                using (var form = new PasscodeForm(_settings, "Enter your passcode to close StandUp."))
                {
                    if (form.ShowDialog() == DialogResult.OK) Shutdown();
                }
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        void Shutdown()
        {
            _timer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            HideOverlays();
            LockScreenTimer.Clear();
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }
    }
}
