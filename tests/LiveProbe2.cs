// Live checks for 1.1: the work clock pausing while you're away, and a long break in Vietnamese.
// Usage: LiveProbe2 <StandUp.exe> <output folder>. Needs nobody touching the mouse or keyboard for ~15 seconds.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class LiveProbe2
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    static int failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "pass  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    static string[] Lines(string log)
    {
        try
        {
            using (var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(s)) return r.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return new string[0]; }
    }

    static bool WaitFor(string log, string text, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            if (Lines(log).Any(l => l.Contains(text))) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    static Process Start(string exe, string data, string args)
    {
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Directory.CreateDirectory(data);
        return Process.Start(exe, "--test --data-dir \"" + data + "\" " + args);
    }

    static int Main(string[] args)
    {
        SetProcessDPIAware();
        string exe = Path.GetFullPath(args[0]), outDir = Path.GetFullPath(args[1]);

        string data = Path.Combine(outDir, "idle"), log = Path.Combine(data, "app.log");
        Process app = Start(exe, data, "--work-seconds 8 --break-seconds 3 --idle-seconds 2 --exit-after 40");
        try
        {
            Check("clock pauses after 2s without input", WaitFor(log, "] idle", 0) || WaitFor(log, " idle", 5000));
            Thread.Sleep(9000);
            Check("no break while away, even past the 8s work time", !Lines(log).Any(l => l.Contains("phase Break")));
            mouse_event(1, 12, 12, 0, IntPtr.Zero);
            Check("using the mouse resumes the clock", WaitFor(log, "back from idle", 2000));
            // Keep "working" (small mouse moves) until the ~8s of work left is used up.
            bool started = false;
            for (int i = 0; i < 12 && !started; i++)
            {
                mouse_event(1, i % 2 == 0 ? 6 : -6, 0, 0, IntPtr.Zero);
                started = WaitFor(log, "phase Break", 1000);
            }
            Check("break comes once the remaining time is worked", started);
        }
        finally
        {
            if (!app.HasExited) app.Kill();
        }
        Console.WriteLine("--- idle log");
        foreach (string l in Lines(log).Where(x => !x.Contains("input during"))) Console.WriteLine(l.TrimEnd());

        data = Path.Combine(outDir, "longvi");
        log = Path.Combine(data, "app.log");
        app = Start(exe, data, "--language vi --work-seconds 2 --break-seconds 3 --long-every-seconds 1 --exit-after 20");
        try
        {
            Check("a long break starts once enough work is done", WaitFor(log, "long break started", 6000));
            Thread.Sleep(1500);
            Rectangle all = SystemInformation.VirtualScreen;
            using (var bmp = new Bitmap(all.Width, all.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(all.Location, Point.Empty, all.Size);
                bmp.Save(Path.Combine(outDir, "long-break-vi.png"), ImageFormat.Png);
            }
        }
        finally
        {
            if (!app.HasExited) app.Kill();
        }

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
