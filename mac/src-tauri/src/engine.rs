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
    pub snooze_seconds: i64,
    pub max_snoozes: u32,
}

impl Default for Config {
    fn default() -> Self {
        Config { work_seconds: 3000, break_seconds: 120, snooze_seconds: 120, max_snoozes: 2 }
    }
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Event {
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
    locked: bool,
    suspended: bool,
    presenting: bool,
    paused_break: bool,
    last_tick: i64,
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
            locked: false,
            suspended: false,
            presenting: false,
            paused_break: false,
            last_tick: 0,
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

    pub fn start(&mut self, now: i64) {
        self.last_tick = now;
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
        self.last_tick = now;

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
        self.deadline = now + self.config.break_seconds * 1000;
        self.last_activity = now;
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

    pub fn lock(&mut self) {
        self.locked = true;
        self.enter_pause();
    }

    pub fn unlock(&mut self, now: i64) {
        self.locked = false;
        self.leave_pause(now);
    }

    pub fn suspend(&mut self, _now: i64) {
        self.suspended = true;
        self.enter_pause();
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
            self.paused_break = false;
            self.phase = Phase::Paused;
        } else {
            self.leave_pause(now);
        }
    }

    /// New durations apply at once to a normal work period, otherwise from the next one.
    pub fn apply_config(&mut self, config: Config, now: i64) {
        self.config = config;
        if self.phase == Phase::Working && !self.in_snooze {
            self.deadline = now + self.config.work_seconds * 1000;
        }
    }

    fn enter_pause(&mut self) {
        if self.phase == Phase::Paused {
            return;
        }
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

        if was_break && now >= self.deadline {
            let at = self.deadline;
            self.complete_break(at);
            self.begin_work(now);
        } else if was_break {
            // Locking can't be used to skip a break: it carries on for the time left.
            self.phase = Phase::Break;
        } else {
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
        self.deadline = now + self.config.break_seconds * 1000;
        self.last_activity = 0;
        self.phase = Phase::Break;
    }

    fn complete_break(&mut self, at: i64) {
        self.events.push(Event::BreakCompleted { at, seconds: self.config.break_seconds });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const T0: i64 = 1_800_000_000_000;

    fn new_engine() -> Engine {
        let mut e = Engine::new(Config::default());
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
    fn break_starts_after_work_and_completes() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 2990);
        assert_eq!(e.phase, Phase::Working);
        let t = run(&mut e, t, 10);
        assert_eq!(e.phase, Phase::Break);
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
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 120_000);
        let t = run(&mut e, t, 120);
        assert_eq!(e.phase, Phase::Break);
        assert!(e.snooze(t));
        let t = run(&mut e, t, 120);
        assert_eq!(e.phase, Phase::Break);
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
        e.lock();
        assert_eq!(e.phase, Phase::Paused);
        let t = t + 600_000;
        e.tick(t);
        assert_eq!(e.phase, Phase::Paused);
        e.unlock(t);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 3_000_000);
    }

    #[test]
    fn counting_starts_at_unlock_not_wake() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 1000);
        e.lock();
        e.suspend(t);
        let t = t + 3_600_000;
        e.resume(t);
        assert_eq!(e.phase, Phase::Paused);
        let t = t + 30_000;
        e.unlock(t);
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
        e.lock();
        assert_eq!(e.deadline, t + 120_000);
        let t = t + 30_000;
        e.unlock(t);
        assert_eq!(e.phase, Phase::Break);
        assert_eq!(e.deadline, t + 90_000);
        assert_eq!(completed(&mut e), 0);
    }

    #[test]
    fn break_finished_while_locked_counts() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 3000);
        let end = t + 120_000;
        e.lock();
        let t = t + 300_000;
        e.unlock(t);
        assert_eq!(e.take_events(), vec![Event::BreakCompleted { at: end, seconds: 120 }]);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 3_000_000);
    }

    #[test]
    fn presenting_pauses_then_starts_a_full_period() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 2900);
        e.set_presenting(true, t);
        assert_eq!(e.phase, Phase::Paused);
        let t = t + 3_600_000;
        e.tick(t);
        assert_eq!(e.phase, Phase::Paused);
        e.set_presenting(false, t);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 3_000_000);
    }

    #[test]
    fn presenting_cancels_a_running_break() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 3000);
        e.snooze(t);
        let t = run(&mut e, t, 120);
        e.set_presenting(true, t);
        assert_eq!(e.phase, Phase::Paused);
        let t = t + 600_000;
        e.set_presenting(false, t);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(completed(&mut e), 0);
        assert_eq!(e.snoozes_used, 0);
    }

    #[test]
    fn lock_and_presenting_both_have_to_end() {
        let mut e = new_engine();
        let t = run(&mut e, T0, 1000);
        e.set_presenting(true, t);
        e.lock();
        e.set_presenting(false, t);
        assert_eq!(e.phase, Phase::Paused);
        let t = t + 30_000;
        e.unlock(t);
        assert_eq!(e.phase, Phase::Working);
        assert_eq!(e.deadline, t + 3_000_000);
    }
}
