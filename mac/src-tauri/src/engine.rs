//! The work/break rules, with no UI and no real clock, so every rule can be tested.
//! This mirrors src/BreakEngine.cs in the Windows app. All times are Unix milliseconds.

use serde::Serialize;

/// A tick arriving this late means the machine slept.
const SLEEP_GAP_MS: i64 = 20_000;

#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize)]
pub enum Phase {
    Working,
    Break,
    Paused,
}

#[derive(Clone, Debug)]
pub struct Config {
    pub work_seconds: i64,
    pub break_seconds: i64,
    pub long_every_seconds: i64,
    pub long_break_seconds: i64,
    pub snooze_seconds: i64,
    pub max_snoozes: u32,
}

impl Default for Config {
    fn default() -> Self {
        Config { work_seconds: 3600, break_seconds: 120, long_every_seconds: 10_800, long_break_seconds: 600, snooze_seconds: 120, max_snoozes: 2 }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Event {
    BreakStarted { long: bool },
    BreakCompleted { at: i64, seconds: i64 },
    Snoozed { at: i64, seconds: i64 },
}

pub struct Engine {
    pub config: Config,
    pub phase: Phase,
    pub deadline: i64,
    pub last_activity: i64,
    pub snoozes_used: u32,
    pub in_snooze: bool,
    pub is_long_break: bool,
    locked: bool,
    suspended: bool,
    presenting: bool,
    paused_break: bool,
    idle: bool,
    last_tick: i64,
    paused_at: i64,
    idle_since: i64,
    /// Work counted since the last long break.
    worked_ms: i64,
    events: Vec<Event>,
}

impl Engine {
    pub fn new(config: Config) -> Self {
        Engine {
            config,
            phase: Phase::Working,
            deadline: 0,
            last_activity: 0,
            snoozes_used: 0,
            in_snooze: false,
            is_long_break: false,
            locked: false,
            suspended: false,
            presenting: false,
            paused_break: false,
            idle: false,
            last_tick: 0,
            paused_at: 0,
            idle_since: 0,
            worked_ms: 0,
            events: Vec::new(),
        }
    }

    pub fn take_events(&mut self) -> Vec<Event> {
        std::mem::take(&mut self.events)
    }

    pub fn can_snooze(&self) -> bool {
        self.phase == Phase::Break && self.snoozes_used < self.config.max_snoozes
    }

    pub fn is_presenting(&self) -> bool {
        self.presenting
    }

    pub fn is_idle(&self) -> bool {
        self.idle && self.phase == Phase::Working
    }

    pub fn current_break_seconds(&self) -> i64 {
        if self.is_long_break { self.config.long_break_seconds } else { self.config.break_seconds }
    }

    /// Whether the coming break will be a long one, if work carries on until it.
    pub fn next_break_is_long(&self, now: i64) -> bool {
        self.worked_ms + (self.deadline - now) >= self.config.long_every_seconds * 1000
    }

    pub fn start(&mut self, now: i64) {
        self.last_tick = now;
        self.worked_ms = 0;
        self.begin_work(now);
    }

    pub fn tick(&mut self, now: i64) {
        if self.phase != Phase::Paused && now - self.last_tick > SLEEP_GAP_MS {
            let before = self.last_tick;
            self.last_tick = now;
            self.suspend(before);
            self.resume(now);
            return;
        }

        let elapsed = now - self.last_tick;
        self.last_tick = now;
        if self.phase == Phase::Working {
            // While you're away the clock stands still, so it resumes where it stopped.
            if self.idle {
                self.deadline += elapsed;
            } else {
                self.worked_ms += elapsed;
            }
        }

        if now < self.deadline {
            return;
        }
        match self.phase {
            Phase::Working => self.begin_break(now),
            Phase::Break => {
                self.complete_break(now);
                self.begin_work(now);
            }
            Phase::Paused => {}
        }
    }

    /// Any keyboard or mouse use. During a break it restarts the break timer.
    pub fn activity(&mut self, now: i64) {
        if self.phase != Phase::Break {
            return;
        }
        self.deadline = now + self.current_break_seconds() * 1000;
        self.last_activity = now;
    }

    /// Whether nobody has used the computer for a while. Only work time is affected.
    pub fn set_idle(&mut self, idle: bool, now: i64) {
        if idle == self.idle {
            return;
        }
        self.idle = idle;
        if idle {
            self.idle_since = now;
        } else if now - self.idle_since >= self.config.long_break_seconds * 1000 {
            self.worked_ms = 0;
        }
    }

    pub fn snooze(&mut self, now: i64) -> bool {
        if !self.can_snooze() {
            return false;
        }
        self.snoozes_used += 1;
        self.in_snooze = true;
        self.deadline = now + self.config.snooze_seconds * 1000;
        self.phase = Phase::Working;
        self.events.push(Event::Snoozed { at: now, seconds: self.config.snooze_seconds });
        true
    }

    pub fn take_break_now(&mut self, now: i64) {
        if self.phase == Phase::Working {
            self.begin_break(now);
        }
    }

    pub fn lock(&mut self, now: i64) {
        self.locked = true;
        self.enter_pause(now);
    }

    pub fn unlock(&mut self, now: i64) {
        self.locked = false;
        self.leave_pause(now);
    }

    pub fn suspend(&mut self, now: i64) {
        self.suspended = true;
        self.enter_pause(now);
    }

    pub fn resume(&mut self, now: i64) {
        self.suspended = false;
        self.leave_pause(now);
    }

    /// A slide show cancels any break. Counting starts afresh when it ends.
    pub fn set_presenting(&mut self, presenting: bool, now: i64) {
        if presenting == self.presenting {
            return;
        }
        self.presenting = presenting;
        if presenting {
            if self.phase != Phase::Paused {
                self.paused_at = now;
            }
            self.paused_break = false;
            self.phase = Phase::Paused;
        } else {
            self.leave_pause(now);
        }
    }

    /// New settings keep the time already worked. If that is now past the new work time,
    /// the next tick starts the break.
    pub fn apply_config(&mut self, config: Config, _now: i64) {
        let old_work = self.config.work_seconds;
        self.config = config;
        if self.phase == Phase::Working && !self.in_snooze {
            self.deadline += (self.config.work_seconds - old_work) * 1000;
        }
    }

    fn enter_pause(&mut self, now: i64) {
        if self.phase == Phase::Paused {
            return;
        }
        self.paused_at = now;
        self.paused_break = self.phase == Phase::Break;
        self.phase = Phase::Paused;
    }

    fn leave_pause(&mut self, now: i64) {
        if self.locked || self.suspended || self.presenting || self.phase != Phase::Paused {
            return;
        }
        let was_break = self.paused_break;
        self.paused_break = false;
        self.last_tick = now;
        // Time away from the computer as long as a long break counts as one.
        let rested_long = now - self.paused_at >= self.config.long_break_seconds * 1000;

        if was_break && now >= self.deadline {
            let at = self.deadline;
            self.complete_break(at);
            if rested_long {
                self.worked_ms = 0;
            }
            self.begin_work(now);
        } else if was_break {
            // Locking can't be used to skip a break: it carries on for the time left.
            self.phase = Phase::Break;
        } else {
            if rested_long {
                self.worked_ms = 0;
            }
            self.begin_work(now);
        }
    }

    fn begin_work(&mut self, now: i64) {
        self.snoozes_used = 0;
        self.in_snooze = false;
        self.deadline = now + self.config.work_seconds * 1000;
        self.phase = Phase::Working;
    }

    fn begin_break(&mut self, now: i64) {
        self.in_snooze = false;
        self.is_long_break = self.worked_ms >= self.config.long_every_seconds * 1000;
        self.deadline = now + self.current_break_seconds() * 1000;
        self.last_activity = 0;
        self.phase = Phase::Break;
        self.events.push(Event::BreakStarted { long: self.is_long_break });
    }

    fn complete_break(&mut self, at: i64) {
        self.events.push(Event::BreakCompleted { at, seconds: self.current_break_seconds() });
        if self.is_long_break {
            self.worked_ms = 0;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const T0: i64 = 1_800_000_000_000;

    /// Short break every 50 minutes for 2, long break after 3 hours for 10.
    fn new_engine() -> Engine {
        let mut e = Engine::new(Config { work_seconds: 3000, ..Config::default() });
        e.start(T0);
        e
    }

    /// Walks the clock forward in 10s ticks so the sleep detector stays quiet.
    fn run(e: &mut Engine, from: i64, seconds: i64) -> i64 {
        let mut i = 10;
        while i <= seconds {
            e.tick(from + i * 1000);
            i += 10;
        }
        from + seconds * 1000
    }

    fn completed(e: &mut Engine) -> usize {
        e.take_events().iter().filter(|x| matches!(x, Event::BreakCompleted { .. })).count()
    }

    #[test]
    fn defaults_are_hourly_short_and_three_hour_long() {
        let c = Config::default();
        assert_eq!((c.work_seconds, c.break_seconds, c.long_every_seconds, c.long_break_seconds), (3600, 120, 10_800, 600));
    }

    #[test]
    fn break_starts_after_work_and_completes() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 2990);
        assert_eq!(e.phase, Phase::Working);
        let t = run(&mut e, t, 10);
        assert_eq!(e.phase, Phase::Break);
        assert!(!e.is_long_break);
        assert_eq!(e.take_events(), vec![Event::BreakStarted { long: false }]);
        let t = run(&mut e, t, 120);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(completed(&mut e), 1);
        assert_eq!(e.deadline, t + 3_000_000);
    }

    #[test]
    fn input_restarts_the_break_timer() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 3000);
        let t = run(&mut e, t, 100);
        e.activity(t);
        assert_eq!(e.deadline, t + 120_000);
        let t = run(&mut e, t, 110);
        assert_eq!(e.phase, Phase::Break);
        assert_eq!(completed(&mut e), 0);
        run(&mut e, t, 10);
        assert_eq!(completed(&mut e), 1);
    }

    #[test]
    fn two_snoozes_then_none() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 3000);
        assert!(e.snooze(t));
        assert_eq!(e.deadline, t + 120_000);
        let t = run(&mut e, t, 120);
        assert_eq!(e.phase, Phase::Break);
        assert!(e.snooze(t));
        let t = run(&mut e, t, 120);
        assert!(!e.can_snooze());
        assert!(!e.snooze(t));
        run(&mut e, t, 120);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.snoozes_used, 0);
    }

    #[test]
    fn lock_pauses_and_unlock_restarts_work() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 1000);
        e.lock(t);
        let t = t + 600_000;
        e.tick(t);
        assert_eq!(e.phase, Phase::Paused);
        e.unlock(t);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 3_000_000);
    }

    #[test]
    fn silent_sleep_is_caught_by_the_clock_gap() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 1000) + 600_000;
        e.tick(t);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 3_000_000);
    }

    #[test]
    fn early_unlock_resumes_the_break() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 3000);
        e.lock(t);
        let t = t + 30_000;
        e.unlock(t);
        assert_eq!(e.phase, Phase::Break);
        assert_eq!(e.deadline, t + 90_000);
    }

    #[test]
    fn presenting_pauses_then_starts_a_full_period() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 2900);
        e.set_presenting(true, t);
        let t = t + 3_600_000;
        e.tick(t);
        assert_eq!(e.phase, Phase::Paused);
        e.set_presenting(false, t);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 3_000_000);
    }

    #[test]
    fn idle_stops_the_clock_and_resumes_where_it_stopped() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 1000);
        let due = e.deadline;
        e.set_idle(true, t);
        assert!(e.is_idle());
        let t = run(&mut e, t, 1800);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, due + 1_800_000);
        e.set_idle(false, t);
        let t = run(&mut e, t, 1990);
        assert_eq!(e.phase, Phase::Working);
        run(&mut e, t, 10);
        assert_eq!(e.phase, Phase::Break);
    }

    #[test]
    fn settings_changes_keep_time_worked() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 1200);
        e.apply_config(Config::default(), t);
        assert_eq!(e.deadline, T0 + 3_600_000);
        e.apply_config(Config { work_seconds: 600, ..Config::default() }, t);
        assert_eq!(e.deadline, T0 + 600_000);
        run(&mut e, t, 10);
        assert_eq!(e.phase, Phase::Break);
    }

    #[test]
    fn long_break_after_three_hours_of_work() {
        let mut e = new_engine();
        let mut t = T0;
        let mut short_breaks = 0;
        loop {
            t = run(&mut e, t, 3000);
            if e.is_long_break {
                break;
            }
            short_breaks += 1;
            t = run(&mut e, t, 120);
        }
        assert_eq!(short_breaks, 3);
        assert_eq!(e.deadline, t + 600_000);
        let t = run(&mut e, t, 600);
        assert_eq!(e.phase, Phase::Working);
        assert!(!e.next_break_is_long(t));
    }

    #[test]
    fn a_long_time_locked_counts_as_a_long_break() {
        let mut e = new_engine();
        let mut t = T0;
        for _ in 0..2 {
            t = run(&mut e, t, 3000);
            t = run(&mut e, t, 120);
        }
        e.lock(t);
        t += 1_800_000;
        e.unlock(t);
        run(&mut e, t, 3000);
        assert_eq!(e.phase, Phase::Break);
        assert!(!e.is_long_break);
    }
}
