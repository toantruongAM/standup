using System;

namespace StandUp
{
    public enum Phase { Working, Break, Paused }

    public sealed class EngineConfig
    {
        public int WorkSeconds = 3000;
        public int BreakSeconds = 120;
        public int SnoozeSeconds = 120;
        public int MaxSnoozes = 2;
    }

    /// <summary>
    /// The work/break rules, with no UI and no real clock, so every rule can be tested.
    /// All times are UTC and passed in by the caller.
    /// </summary>
    public sealed class BreakEngine
    {
        // A tick arriving this late means the machine slept without telling us.
        static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(20);

        bool _locked, _suspended, _presenting, _pausedBreak;
        DateTime _lastTick;

        public BreakEngine(EngineConfig config) { Config = config; }

        public EngineConfig Config { get; private set; }
        public Phase Phase { get; private set; }
        public DateTime Deadline { get; private set; }
        public DateTime LastActivity { get; private set; }
        public int SnoozesUsed { get; private set; }
        public bool InSnooze { get; private set; }

        public bool CanSnooze { get { return Phase == Phase.Break && SnoozesUsed < Config.MaxSnoozes; } }
        public bool IsBreakPaused { get { return Phase == Phase.Paused && _pausedBreak; } }
        public bool IsPresenting { get { return _presenting; } }

        public event Action PhaseChanged;
        /// <summary>Time the break finished (UTC) and its length in seconds.</summary>
        public event Action<DateTime, int> BreakCompleted;
        public event Action<DateTime> Snoozed;

        public void Start(DateTime now)
        {
            _lastTick = now;
            BeginWork(now);
        }

        public void Tick(DateTime now)
        {
            if (Phase != Phase.Paused && now - _lastTick > SleepGap)
            {
                DateTime before = _lastTick;
                _lastTick = now;
                Suspend(before);
                Resume(now);
                return;
            }
            _lastTick = now;

            if (now < Deadline) return;
            if (Phase == Phase.Working) BeginBreak(now);
            else if (Phase == Phase.Break) { CompleteBreak(now); BeginWork(now); }
        }

        /// <summary>Any keyboard or mouse use. During a break it restarts the break timer.</summary>
        public void Activity(DateTime now)
        {
            if (Phase != Phase.Break) return;
            Deadline = now.AddSeconds(Config.BreakSeconds);
            LastActivity = now;
        }

        public bool Snooze(DateTime now)
        {
            if (!CanSnooze) return false;
            SnoozesUsed++;
            InSnooze = true;
            Deadline = now.AddSeconds(Config.SnoozeSeconds);
            Phase = Phase.Working;
            Action<DateTime> snoozed = Snoozed;
            if (snoozed != null) snoozed(now);
            RaisePhase();
            return true;
        }

        public void TakeBreakNow(DateTime now)
        {
            if (Phase == Phase.Working) BeginBreak(now);
        }

        public void Lock(DateTime now) { _locked = true; EnterPause(); }
        public void Suspend(DateTime now) { _suspended = true; EnterPause(); }
        public void Unlock(DateTime now) { _locked = false; LeavePause(now); }
        public void Resume(DateTime now) { _suspended = false; LeavePause(now); }

        /// <summary>A slide show or screen share cancels any break. Counting starts afresh when it ends.</summary>
        public void SetPresenting(bool presenting, DateTime now)
        {
            if (presenting == _presenting) return;
            _presenting = presenting;
            if (presenting)
            {
                _pausedBreak = false;
                Phase = Phase.Paused;
                RaisePhase();
            }
            else
            {
                LeavePause(now);
            }
        }

        /// <summary>New durations apply at once to a normal work period, otherwise from the next one.</summary>
        public void ApplyConfig(EngineConfig config, DateTime now)
        {
            Config = config;
            if (Phase == Phase.Working && !InSnooze) Deadline = now.AddSeconds(config.WorkSeconds);
        }

        void EnterPause()
        {
            if (Phase == Phase.Paused) return;
            _pausedBreak = Phase == Phase.Break;
            Phase = Phase.Paused;
            RaisePhase();
        }

        void LeavePause(DateTime now)
        {
            if (_locked || _suspended || _presenting || Phase != Phase.Paused) return;

            bool wasBreak = _pausedBreak;
            _pausedBreak = false;
            _lastTick = now;

            if (wasBreak && now >= Deadline)
            {
                CompleteBreak(Deadline);
                BeginWork(now);
            }
            else if (wasBreak)
            {
                // Locking can't be used to skip a break: it carries on for the time left.
                Phase = Phase.Break;
                RaisePhase();
            }
            else
            {
                BeginWork(now);
            }
        }

        void BeginWork(DateTime now)
        {
            SnoozesUsed = 0;
            InSnooze = false;
            Deadline = now.AddSeconds(Config.WorkSeconds);
            Phase = Phase.Working;
            RaisePhase();
        }

        void BeginBreak(DateTime now)
        {
            InSnooze = false;
            Deadline = now.AddSeconds(Config.BreakSeconds);
            LastActivity = DateTime.MinValue;
            Phase = Phase.Break;
            RaisePhase();
        }

        void CompleteBreak(DateTime at)
        {
            Action<DateTime, int> handler = BreakCompleted;
            if (handler != null) handler(at, Config.BreakSeconds);
        }

        void RaisePhase()
        {
            Action handler = PhaseChanged;
            if (handler != null) handler();
        }
    }
}
