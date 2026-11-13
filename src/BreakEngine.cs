using System;

namespace StandUp
{
    public enum Phase { Working, Break, Paused }

    public sealed class EngineConfig
    {
        public int WorkSeconds = 3600;
        public int BreakSeconds = 60;
        public int LongEverySeconds = 10800;
        public int LongBreakSeconds = 600;
        public int SnoozeSeconds = 120;
        public int MaxSnoozes = 2;
    }

    /// <summary>
    /// The work/break rules, with no UI and no real clock, so every rule can be tested.
    /// All times are UTC and passed in by the caller. mac/src-tauri/src/engine.rs mirrors this.
    /// </summary>
    public sealed class BreakEngine
    {
        // A tick arriving this late means the machine slept without telling us.
        static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(20);

        bool _locked, _suspended, _presenting, _pausedBreak, _idle;
        DateTime _lastTick, _pausedAt, _idleSince, _workStarted;   // _workStarted: when the current stretch of work (or snooze) began
        TimeSpan _worked;   // work counted since the last long break

        public BreakEngine(EngineConfig config) { Config = config; }

        public EngineConfig Config { get; private set; }
        public Phase Phase { get; private set; }
        public DateTime Deadline { get; private set; }
        public DateTime LastActivity { get; private set; }
        public int SnoozesUsed { get; private set; }
        public bool InSnooze { get; private set; }
        public bool IsLongBreak { get; private set; }

        public bool CanSnooze { get { return Phase == Phase.Break && SnoozesUsed < Config.MaxSnoozes; } }
        public bool IsBreakPaused { get { return Phase == Phase.Paused && _pausedBreak; } }
        public bool IsPresenting { get { return _presenting; } }
        public bool IsIdle { get { return _idle && Phase == Phase.Working; } }
        public int CurrentBreakSeconds { get { return IsLongBreak ? Config.LongBreakSeconds : Config.BreakSeconds; } }

        /// <summary>Whether the coming break will be a long one, if work carries on until it.</summary>
        public bool NextBreakIsLong(DateTime now)
        {
            return _worked + (Deadline - now) >= TimeSpan.FromSeconds(Config.LongEverySeconds);
        }

        public event Action PhaseChanged;
        /// <summary>A break has begun (not resumed). True for a long break.</summary>
        public event Action<bool> BreakStarted;
        /// <summary>Time the break finished (UTC) and its length in seconds.</summary>
        public event Action<DateTime, int> BreakCompleted;
        public event Action<DateTime> Snoozed;

        public void Start(DateTime now)
        {
            _lastTick = now;
            _worked = TimeSpan.Zero;
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

            TimeSpan elapsed = now - _lastTick;
            _lastTick = now;
            if (Phase == Phase.Working)
            {
                // While you're away the clock stands still, so it resumes where it stopped.
                if (_idle) Deadline += elapsed;
                else _worked += elapsed;
            }

            if (now < Deadline) return;
            if (Phase == Phase.Working) BeginBreak(now);
            else if (Phase == Phase.Break) { CompleteBreak(now); BeginWork(now); }
        }

        /// <summary>Any keyboard or mouse use. During a break it restarts the break timer.</summary>
        public void Activity(DateTime now)
        {
            // A long break runs its full length whatever you touch; only a short break restarts.
            if (Phase != Phase.Break || IsLongBreak) return;
            Deadline = now.AddSeconds(CurrentBreakSeconds);
            LastActivity = now;
        }

        /// <summary>Whether nobody has used the computer for a while. Only work time is affected.</summary>
        public void SetIdle(bool idle, DateTime now) { SetIdle(idle, now, now); }

        /// <summary>
        /// Whether nobody has used the computer for a while, and when they last did. Being away stops
        /// both work clocks (the one to the next break and the one to the next long break) from the
        /// moment you left, not just from when the away time was reached.
        /// </summary>
        public void SetIdle(bool idle, DateTime now, DateTime lastInput)
        {
            if (idle == _idle) return;
            _idle = idle;
            if (idle)
            {
                _idleSince = lastInput < now ? lastInput : now;
                if (Phase == Phase.Working)
                {
                    // Give back the minutes it took to notice you'd gone (only those since work began).
                    DateTime from = _idleSince > _workStarted ? _idleSince : _workStarted;
                    TimeSpan away = now - from;
                    if (away > TimeSpan.Zero)
                    {
                        Deadline += away;
                        _worked = _worked > away ? _worked - away : TimeSpan.Zero;
                    }
                }
            }
            else if (now - _idleSince >= TimeSpan.FromSeconds(Config.LongBreakSeconds))
            {
                _worked = TimeSpan.Zero;   // away as long as a long break: that counts as one
            }
        }

        public bool Snooze(DateTime now)
        {
            if (!CanSnooze) return false;
            SnoozesUsed++;
            InSnooze = true;
            _workStarted = now;
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

        public void Lock(DateTime now) { _locked = true; EnterPause(now); }
        public void Suspend(DateTime now) { _suspended = true; EnterPause(now); }
        public void Unlock(DateTime now) { _locked = false; LeavePause(now); }
        public void Resume(DateTime now) { _suspended = false; LeavePause(now); }

        /// <summary>A slide show or screen share cancels any break. Counting starts afresh when it ends.</summary>
        public void SetPresenting(bool presenting, DateTime now)
        {
            if (presenting == _presenting) return;
            _presenting = presenting;
            if (presenting)
            {
                if (Phase != Phase.Paused) _pausedAt = now;
                _pausedBreak = false;
                Phase = Phase.Paused;
                RaisePhase();
            }
            else
            {
                LeavePause(now);
            }
        }

        /// <summary>
        /// New settings keep the time already worked. If that is now past the new work time,
        /// the next tick starts the break.
        /// </summary>
        public void ApplyConfig(EngineConfig config, DateTime now)
        {
            EngineConfig old = Config;
            Config = config;
            if (Phase == Phase.Working && !InSnooze) Deadline = Deadline.AddSeconds(config.WorkSeconds - old.WorkSeconds);
        }

        void EnterPause(DateTime now)
        {
            if (Phase == Phase.Paused) return;
            _pausedAt = now;
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
            // Time away from the computer as long as a long break counts as one.
            bool restedLong = now - _pausedAt >= TimeSpan.FromSeconds(Config.LongBreakSeconds);

            if (wasBreak && now >= Deadline)
            {
                CompleteBreak(Deadline);
                if (restedLong) _worked = TimeSpan.Zero;
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
                if (restedLong) _worked = TimeSpan.Zero;
                BeginWork(now);
            }
        }

        void BeginWork(DateTime now)
        {
            SnoozesUsed = 0;
            InSnooze = false;
            Deadline = now.AddSeconds(Config.WorkSeconds);
            _workStarted = now;
            Phase = Phase.Working;
            RaisePhase();
        }

        void BeginBreak(DateTime now)
        {
            InSnooze = false;
            IsLongBreak = _worked >= TimeSpan.FromSeconds(Config.LongEverySeconds);
            Deadline = now.AddSeconds(CurrentBreakSeconds);
            LastActivity = DateTime.MinValue;
            Phase = Phase.Break;
            Action<bool> started = BreakStarted;
            if (started != null) started(IsLongBreak);
            RaisePhase();
        }

        void CompleteBreak(DateTime at)
        {
            Action<DateTime, int> handler = BreakCompleted;
            if (handler != null) handler(at, CurrentBreakSeconds);
            if (IsLongBreak) _worked = TimeSpan.Zero;
        }

        void RaisePhase()
        {
            Action handler = PhaseChanged;
            if (handler != null) handler();
        }
    }
}
