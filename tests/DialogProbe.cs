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
        CallStatic("LockScreenTimer", "UseStateDir", data);

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

        var settingsForm = (Form)New("SettingsForm", loaded);
        Shoot(settingsForm, "dialog-settings.png");
        settingsForm.Dispose();

        File.WriteAllText(Path.Combine(data, "lockstate.txt"), "");
        var installedForm = (Form)New("SettingsForm", loaded);
        Shoot(installedForm, "dialog-settings-installed.png");
        Check("no lock-screen row once it is on", !AllControls(installedForm).Any(c => c.Visible && c.Text.Contains("ock-screen timer")));
        Check("no unlock checkbox in Settings", !AllControls(installedForm).Any(c => c.Text.Contains("unlock before")));
        installedForm.Dispose();

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
        for (int i = 0; i < 60; i++)
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
        Check("all 12 pictures are in the pool (saw " + seen.Count + ")", seen.Count == 12);
        Check("the same picture never shows twice in a row", !repeated);

        // Lock-screen panel, drawn on the normal desktop because the real one needs SYSTEM.
        string state = Path.Combine(data, "lockstate.txt");
        File.WriteAllText(state, "");
        CallStatic("LockScreenTimer", "Publish", DateTime.UtcNow.AddSeconds(95), 120);
        Process panel = Process.Start(exe, "--test --data-dir \"" + data + "\" --lock-overlay");
        Thread.Sleep(1800);
        Rectangle all = SystemInformation.VirtualScreen;
        using (var bmp = new Bitmap(all.Width, all.Height))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(all.Location, Point.Empty, all.Size);
            bmp.Save(Path.Combine(outDir, "lock-panel.png"), ImageFormat.Png);
        }
        Check("lock-screen panel stays up while a break is published", !panel.HasExited);
        CallStatic("LockScreenTimer", "Clear");
        bool gone = panel.WaitForExit(5000);
        Check("lock-screen panel exits when the break is cleared", gone);
        if (!gone) panel.Kill();

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
