// Checks the passcode gate, storage, dialogs and the lock-screen timer panel, in a private data folder.
// Usage: DialogProbe <StandUp.exe> <output folder>
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class DialogProbe
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static Assembly app;
    static string outDir;
    static int failures;
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "pass  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    static Type T(string name) { return app.GetType("StandUp." + name, true); }
    static object New(string name, params object[] args) { return Activator.CreateInstance(T(name), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null); }
    static object Call(object target, string method, params object[] args) { return target.GetType().GetMethod(method, Any).Invoke(target, args); }
    static object CallStatic(string type, string method, params object[] args) { return T(type).GetMethod(method, Any).Invoke(null, args); }

    static void Shoot(Form form, string file)
    {
        form.Show();
        Application.DoEvents();
        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(Path.Combine(outDir, file), ImageFormat.Png);
        }
    }

    static TextBox[] Boxes(Control root)
    {
        return root.Controls.Cast<Control>().SelectMany(c => (c is TextBox ? new[] { (TextBox)c } : new TextBox[0]).Concat(Boxes(c))).ToArray();
    }

    static System.Collections.Generic.IEnumerable<Control> AllControls(Control root)
    {
        return root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(AllControls(c)));
    }

    [STAThread]
    static int Main(string[] args)
    {
        SetProcessDPIAware();
        string exe = Path.GetFullPath(args[0]);
        outDir = Path.GetFullPath(args[1]);
        string data = Path.Combine(outDir, "dialogdata");
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Directory.CreateDirectory(data);
        Application.EnableVisualStyles();

        app = Assembly.LoadFrom(exe);
        T("Paths").GetProperty("DataDir", Any).SetValue(null, data);

        File.WriteAllLines(Path.Combine(data, "history.csv"), new[]
        {
            DateTime.Today.AddDays(-1).AddHours(10).ToString("yyyy-MM-dd'T'HH:mm:ss") + ",break,120",
            DateTime.Today.AddHours(9).ToString("yyyy-MM-dd'T'HH:mm:ss") + ",break,120",
            DateTime.Today.AddHours(9).AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss") + ",snooze,120",
            "garbage line",
            DateTime.Today.AddHours(10).ToString("yyyy-MM-dd'T'HH:mm:ss") + ",break,120"
        });
        object history = New("History");
        Check("history counts today's breaks", (int)T("History").GetProperty("BreaksToday").GetValue(history) == 2);

        object settings = New("AppSettings");
        Check("no passcode until one is set", !(bool)T("AppSettings").GetProperty("HasPasscode").GetValue(settings));
        Call(settings, "SetPasscode", "4321");
        Call(settings, "Save");
        object loaded = CallStatic("AppSettings", "Load");
        Check("right passcode accepted after reload", (bool)Call(loaded, "CheckPasscode", "4321"));
        Check("wrong passcode refused", !(bool)Call(loaded, "CheckPasscode", "1234"));
        Check("passcode is not stored in clear", !File.ReadAllText(Path.Combine(data, "settings.ini")).Contains("4321"));

        var exit = (Form)New("PasscodeForm", loaded, "Enter your passcode to close StandUp.");
        Shoot(exit, "dialog-exit.png");
        Boxes(exit)[0].Text = "wrong";
        ((Button)exit.AcceptButton).PerformClick();
        Check("exit dialog refuses wrong passcode", exit.DialogResult != DialogResult.OK);
        Boxes(exit)[0].Text = "4321";
        ((Button)exit.AcceptButton).PerformClick();
        Check("exit dialog accepts right passcode", exit.DialogResult == DialogResult.OK);
        exit.Dispose();

        var change = (Form)New("SetPasscodeForm", loaded, "Choose the passcode needed to close StandUp.");
        Shoot(change, "dialog-passcode.png");
        TextBox[] boxes = Boxes(change);
        boxes[0].Text = "nope"; boxes[1].Text = "5555"; boxes[2].Text = "5555";
        ((Button)change.AcceptButton).PerformClick();
        Check("changing passcode needs the current one", change.DialogResult != DialogResult.OK);
        change.Dispose();

        object fresh = New("AppSettings");
        Check("new installs: 1-minute short breaks", (decimal)T("AppSettings").GetField("BreakMinutes").GetValue(fresh) == 1m);
        Check("new installs: 2 snoozes allowed", (int)T("AppSettings").GetField("MaxSnoozes").GetValue(fresh) == 2);
        T("AppSettings").GetField("MaxSnoozes").SetValue(loaded, 4);
        Call(loaded, "Save");
        Check("snooze count is saved", (int)T("AppSettings").GetField("MaxSnoozes").GetValue(CallStatic("AppSettings", "Load")) == 4);
        Check("snooze count reaches the break rules", (int)T("EngineConfig").GetField("MaxSnoozes").GetValue(Call(loaded, "ToEngineConfig")) == 4);
        T("AppSettings").GetField("MaxSnoozes").SetValue(loaded, 2);

        var settingsForm = (Form)New("SettingsForm", loaded, false);
        Shoot(settingsForm, "dialog-settings.png");
        Check("no lock-screen timer in Settings", !AllControls(settingsForm).Any(c => c.Text.Contains("ock-screen")));
        Check("Settings has three bold group headings", AllControls(settingsForm).OfType<Label>().Count(l => l.Font.Bold) == 3);
        Check("pictures stay out of the main Settings", !AllControls(settingsForm).Any(c => c.Text.Contains("Add pictures")));
        Check("Settings has no Save or Cancel button", !AllControls(settingsForm).OfType<Button>().Any(b => b.Text == "Save" || b.Text == "Cancel"));

        // Changing a value saves it at once and lights only that row's check mark, which then fades.
        NumericUpDown[] numbers = AllControls(settingsForm).OfType<NumericUpDown>().ToArray();
        Control[] marks = AllControls(settingsForm).Where(c => c.GetType().Name == "SavedMark").ToArray();
        Func<Control, bool> lit = m => (bool)m.GetType().GetProperty("IsLit").GetValue(m);
        Check("every setting has a check mark beside it (" + marks.Length + ")", marks.Length == 6 && numbers.Length == 5);
        Check("no check marks before anything changes", !marks.Any(m => lit(m)));
        numbers[0].Value = 45;
        Shoot(settingsForm, "autosave-settings.png");
        Check("short break 'every' saved without pressing anything", (decimal)T("AppSettings").GetField("WorkMinutes").GetValue(CallStatic("AppSettings", "Load")) == 45m);
        Check("its check mark shows, and only that one", lit(numbers[0].Parent.Controls[1]) && marks.Count(m => lit(m)) == 1);
        numbers[4].Value = 3;
        Check("snooze length saved too", (decimal)T("AppSettings").GetField("SnoozeMinutes").GetValue(CallStatic("AppSettings", "Load")) == 3m);
        var fade = Stopwatch.StartNew();
        while (fade.ElapsedMilliseconds < 1200) { Application.DoEvents(); Thread.Sleep(20); }
        Check("check mark still there after 1.2s", lit(numbers[4].Parent.Controls[1]));
        while (fade.ElapsedMilliseconds < 2800) { Application.DoEvents(); Thread.Sleep(20); }
        Check("check marks have faded away after 2.8s", !marks.Any(m => lit(m)) && marks.All(m => m.ForeColor.ToArgb() == m.BackColor.ToArgb()));
        numbers[2].Text = "150";
        var typing = Stopwatch.StartNew();
        while (typing.ElapsedMilliseconds < 1000) { Application.DoEvents(); Thread.Sleep(20); }
        Check("a typed number saves after a short pause", (decimal)T("AppSettings").GetField("LongEveryMinutes").GetValue(CallStatic("AppSettings", "Load")) == 150m && lit(numbers[2].Parent.Controls[1]));
        settingsForm.Dispose();

        var advancedForm = (Form)New("AdvancedForm", loaded, false);
        Shoot(advancedForm, "dialog-advanced.png");
        Check("Advanced settings has no Save or Cancel button", !AllControls(advancedForm).OfType<Button>().Any(b => b.Text == "Save" || b.Text == "Cancel"));
        NumericUpDown snoozeCount = AllControls(advancedForm).OfType<NumericUpDown>().ElementAt(1);
        snoozeCount.Value = 3;
        Check("snooze count saved at once with a check mark", (int)T("AppSettings").GetField("MaxSnoozes").GetValue(CallStatic("AppSettings", "Load")) == 3 && lit(snoozeCount.Parent.Controls[1]));
        AllControls(advancedForm).OfType<ComboBox>().First().SelectedIndex = 1;
        Application.DoEvents();
        Check("picking a language saves it and closes to reopen in it", (string)T("AppSettings").GetField("Language").GetValue(CallStatic("AppSettings", "Load")) == "es"
            && (bool)advancedForm.GetType().GetProperty("LanguageChanged").GetValue(advancedForm));
        advancedForm.Dispose();
        var reopened = (Form)New("AdvancedForm", loaded, true);
        Shoot(reopened, "autosave-language-es.png");
        Check("reopened window shows the check by Language", lit(AllControls(reopened).OfType<ComboBox>().First().Parent.Controls[1]));
        reopened.Dispose();
        T("AppSettings").GetField("Language").SetValue(loaded, "en");
        T("AppSettings").GetField("MaxSnoozes").SetValue(loaded, 2);
        Call(loaded, "Save");
        T("L10n").GetProperty("Language", Any).SetValue(null, "en");

        foreach (string lang in new[] { "en", "es", "vi" })
        {
            T("L10n").GetProperty("Language", Any).SetValue(null, lang);
            var s = (Form)New("SettingsForm", loaded, false);
            Shoot(s, "i18n-settings-" + lang + ".png");
            s.Dispose();
            var a = (Form)New("AdvancedForm", loaded, false);
            Shoot(a, "i18n-advanced-" + lang + ".png");
            if (lang == "en") Check("pictures, language and snooze count are in Advanced settings", AllControls(a).Any(c => c.Text == "Add pictures...") && AllControls(a).OfType<ComboBox>().Any() && AllControls(a).Any(c => c.Text == "Snoozes allowed per break"));
            a.Dispose();
            var h = (Form)New("HistoryForm", New("History"));
            Shoot(h, "i18n-history-" + lang + ".png");
            h.Dispose();
        }
        T("L10n").GetProperty("Language", Any).SetValue(null, "en");

        // A spread of days for the chart, seeded after the count check above.
        var seeded = new System.Collections.Generic.List<string>();
        var rng = new Random(4);
        for (int d = 1; d < 40; d++)
        {
            if (d % 7 == 6 || d % 7 == 0) continue;
            string stamp = DateTime.Today.AddDays(-d).AddHours(10).ToString("yyyy-MM-dd'T'HH:mm:ss");
            for (int b = rng.Next(2, 7); b > 0; b--) seeded.Add(stamp + ",break,120");
            for (int z = rng.Next(0, 3); z > 0; z--) seeded.Add(stamp + ",snooze,120");
        }
        File.AppendAllLines(Path.Combine(data, "history.csv"), seeded);
        object busyHistory = New("History");
        var historyForm = (Form)New("HistoryForm", busyHistory);
        Shoot(historyForm, "dialog-history.png");
        Control chart = AllControls(historyForm).First(c => c.GetType().Name == "HistoryChart");
        typeof(Control).GetMethod("OnMouseMove", Any).Invoke(chart, new object[] { new MouseEventArgs(MouseButtons.None, 0, chart.Width * 2 / 3, 120, 0) });
        Shoot(historyForm, "dialog-history-hover.png");
        AllControls(historyForm).OfType<RadioButton>().First(r => r.Text == "Snoozes").Checked = true;
        Shoot(historyForm, "dialog-history-snoozes.png");
        AllControls(historyForm).OfType<RadioButton>().First(r => r.Text == "Raw data").Checked = true;
        Shoot(historyForm, "dialog-history-raw.png");
        Check("raw data view shows the table, not the chart", !chart.Visible && AllControls(historyForm).OfType<ListView>().First().Visible);
        historyForm.Dispose();

        string picDir = Path.Combine(data, "incoming");
        Directory.CreateDirectory(picDir);
        string goodPng = Path.Combine(picDir, "good.png");
        using (var bmp = new Bitmap(40, 30)) bmp.Save(goodPng, ImageFormat.Png);
        string goodJfif = Path.Combine(picDir, "saved from browser.jfif");
        using (var bmp = new Bitmap(40, 30)) bmp.Save(goodJfif, ImageFormat.Jpeg);
        string junk = Path.Combine(picDir, "broken.jpg");
        File.WriteAllText(junk, "this is not a picture");
        var added = (string[])CallStatic("PhotoPool", "AddFiles", new object[] { new[] { goodPng, goodJfif, junk } });
        Check("unreadable picture is rejected by name", added.Length == 1 && added[0] == "broken.jpg");
        Check("readable pictures join the pool", (int)T("PhotoPool").GetProperty("UserCount", Any).GetValue(null) == 2);
        CallStatic("PhotoPool", "AddFiles", new object[] { new[] { goodPng } });
        Check("adding the same name again keeps both", File.Exists(Path.Combine(data, "photos", "good (2).png")));
        foreach (string f in Directory.GetFiles(Path.Combine(data, "photos"))) File.Delete(f);

        object pool = New("PhotoPool");
        var seen = new System.Collections.Generic.HashSet<string>();
        string previous = null;
        bool repeated = false;
        for (int i = 0; i < 200; i++)
        {
            using (var bmp = (Bitmap)Call(pool, "Next"))
            {
                var field = pool.GetType().GetField("_last", Any);
                string name = (string)field.GetValue(pool);
                repeated |= name == previous;
                previous = name;
                seen.Add(name);
            }
        }
        Check("all 24 pictures are in the pool (saw " + seen.Count + ")", seen.Count == 24);
        Check("the same picture never shows twice in a row", !repeated);

        // The right-click menu: actions only, no "Next break in" or "Breaks today" lines.
        object tray = New("TrayApp", loaded, New("TestOptions"));
        var icon = (NotifyIcon)tray.GetType().GetField("_tray", Any).GetValue(tray);
        string[] items = icon.ContextMenuStrip.Items.Cast<ToolStripItem>().Select(i => i is ToolStripSeparator ? "---" : i.Text).ToArray();
        Console.WriteLine("      tray menu: " + string.Join(" | ", items));
        Check("tray menu shows no status lines", items.SequenceEqual(new[] { "Take a break now", "Settings...", "History...", "---", "Exit..." }));
        Check("status still shows as the hover tip", icon.Text.StartsWith("StandUp: "));
        Call(tray, "Shutdown");

        Check("lock-screen timer code is gone", app.GetType("StandUp.LockScreenTimer") == null && app.GetType("StandUp.LockTimerForm") == null);

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
