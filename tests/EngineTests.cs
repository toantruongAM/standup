using System;
using System.Collections.Generic;
using StandUp;

static class EngineTests
{
    static int failures;
    static readonly DateTime T0 = new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "pass  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    // Short break every 50 minutes for 2, long break after 3 hours for 10.
    static BreakEngine New(out List<DateTime> done)
    {
        var e = new BreakEngine(new EngineConfig { WorkSeconds = 3000 });
        var list = new List<DateTime>();
        e.BreakCompleted += (at, s) => list.Add(at);
        done = list;
        e.Start(T0);
        return e;
    }

    // Walk the clock forward in 10s ticks so the sleep detector stays quiet.
    static DateTime Run(BreakEngine e, DateTime from, int seconds)
    {
        for (int i = 10; i <= seconds; i += 10) e.Tick(from.AddSeconds(i));
        return from.AddSeconds(seconds);
    }

    static int Main()
    {
        List<DateTime> done;

        Check("default short break is every hour", new EngineConfig().WorkSeconds == 3600);
        Check("default long break is 10 minutes after 3 hours", new EngineConfig().LongBreakSeconds == 600 && new EngineConfig().LongEverySeconds == 10800);

        var e = New(out done);
        var t = Run(e, T0, 2990);
        Check("still working just before 50 min", e.Phase == Phase.Working);
        t = Run(e, t, 10);
        Check("break starts at 50 min", e.Phase == Phase.Break && !e.IsLongBreak);
        t = Run(e, t, 120);
        Check("break completes after 2 min", e.Phase == Phase.Working && done.Count == 1);
        Check("next work period is a full 50 min", e.Deadline == t.AddSeconds(3000));

        e = New(out done);
        t = Run(e, T0, 3000);
        t = Run(e, t, 100);
        e.Activity(t);
        Check("input restarts break timer", e.Deadline == t.AddSeconds(120));
        t = Run(e, t, 110);
        Check("break not done 110s after input", e.Phase == Phase.Break && done.Count == 0);
        t = Run(e, t, 10);
        Check("break done 120s after input", done.Count == 1);

        e = New(out done);
        t = Run(e, T0, 3000);
        Check("snooze 1 allowed", e.Snooze(t) && e.Phase == Phase.Working);
        Check("snooze lasts 2 min", e.Deadline == t.AddSeconds(120));
        t = Run(e, t, 120);
        Check("break returns after snooze", e.Phase == Phase.Break);
        Check("snooze 2 allowed", e.Snooze(t));
        t = Run(e, t, 120);
        Check("snooze gone after 2 uses", e.Phase == Phase.Break && !e.CanSnooze && !e.Snooze(t));
        t = Run(e, t, 120);
        Check("completing a break refills snoozes", e.Phase == Phase.Working && e.SnoozesUsed == 0);

        e = New(out done);
        t = Run(e, T0, 1000);
        e.Lock(t);
        Check("lock pauses work", e.Phase == Phase.Paused);
        t = t.AddSeconds(600);
        e.Tick(t);
        Check("no break fires while locked", e.Phase == Phase.Paused);
        e.Unlock(t);
        Check("unlock restarts a full work period", e.Phase == Phase.Working && e.Deadline == t.AddSeconds(3000));

        e = New(out done);
        t = Run(e, T0, 1000);
        e.Lock(t);
        e.Suspend(t);
        t = t.AddHours(1);
        e.Resume(t);
        Check("wake to lock screen stays paused", e.Phase == Phase.Paused);
        t = t.AddSeconds(30);
        e.Unlock(t);
        Check("counting starts at unlock, not wake", e.Deadline == t.AddSeconds(3000));

        e = New(out done);
        t = Run(e, T0, 1000);
        t = t.AddMinutes(10);
        e.Tick(t);
        Check("silent sleep is caught by the clock gap", e.Phase == Phase.Working && e.Deadline == t.AddSeconds(3000));

        e = New(out done);
        t = Run(e, T0, 3000);
        e.Lock(t);
        t = t.AddSeconds(30);
        e.Unlock(t);
        Check("early unlock resumes the break", e.Phase == Phase.Break && e.Deadline == t.AddSeconds(90) && done.Count == 0);

        e = New(out done);
        t = Run(e, T0, 3000);
        DateTime breakEnd = t.AddSeconds(120);
        e.Lock(t);
        t = t.AddSeconds(300);
        e.Unlock(t);
        Check("break finished on lock screen counts", done.Count == 1 && done[0] == breakEnd);

        e = New(out done);
        t = Run(e, T0, 2900);
        e.SetPresenting(true, t);
        t = t.AddHours(1);
        e.Tick(t);
        Check("no break fires while presenting", e.Phase == Phase.Paused);
        e.SetPresenting(false, t);
        Check("full work period after presenting", e.Phase == Phase.Working && e.Deadline == t.AddSeconds(3000));

        // Away from the computer
        e = New(out done);
        t = Run(e, T0, 1000);
        DateTime due = e.Deadline;
        e.SetIdle(true, t);
        t = Run(e, t, 1800);
        Check("idle stops the work clock", e.Phase == Phase.Working && e.IsIdle && e.Deadline == due.AddSeconds(1800));
        e.SetIdle(false, t);
        t = Run(e, t, 1990);
        Check("clock resumes where it stopped (10s left)", e.Phase == Phase.Working);
        t = Run(e, t, 10);
        Check("break comes after the remaining time", e.Phase == Phase.Break);

        e = New(out done);
        t = Run(e, T0, 3000);
        e.SetIdle(true, t);
        t = Run(e, t, 120);
        Check("being idle doesn't stop a break", done.Count == 1);

        // Changing settings
        e = New(out done);
        t = Run(e, T0, 1200);
        e.ApplyConfig(new EngineConfig { WorkSeconds = 3600 }, t);
        Check("longer work time keeps time worked", e.Deadline == T0.AddSeconds(3600));
        e.ApplyConfig(new EngineConfig { WorkSeconds = 600 }, t);
        Check("shorter work time keeps time worked", e.Deadline == T0.AddSeconds(600) && e.Phase == Phase.Working);
        t = Run(e, t, 10);
        Check("already past the new work time: break starts", e.Phase == Phase.Break);

        // Long breaks
        e = New(out done);
        t = T0;
        int shortBreaks = 0;
        bool sawLong = false;
        for (int i = 0; i < 4 && !sawLong; i++)
        {
            t = Run(e, t, 3000);
            if (e.IsLongBreak) sawLong = true;
            else { shortBreaks++; t = Run(e, t, 120); }
        }
        Check("three short breaks, then a long one (after 3h20m of work)", shortBreaks == 3 && sawLong);
        Check("long break lasts 10 minutes", e.Deadline == t.AddSeconds(600));
        t = Run(e, t, 590);
        e.Activity(t);
        Check("input restarts a long break at 10 minutes", e.Deadline == t.AddSeconds(600));
        t = Run(e, t, 600);
        Check("after a long break, next break is short again", e.Phase == Phase.Working && !e.NextBreakIsLong(t));

        e = New(out done);
        t = Run(e, T0, 3000);
        t = Run(e, t, 120);
        t = Run(e, t, 3000);
        t = Run(e, t, 120);
        e.Lock(t);
        t = t.AddMinutes(30);
        e.Unlock(t);
        t = Run(e, t, 3000);
        Check("a long time locked counts as a long break", e.Phase == Phase.Break && !e.IsLongBreak);

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
