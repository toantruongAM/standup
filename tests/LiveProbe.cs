// Drives the real app with real input in a private data folder, on short timers.
// Usage: LiveProbe <StandUp.exe> <output folder>
// It covers the screen for about 30 seconds and moves the mouse.
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

static class LiveProbe
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    const byte VK_F15 = 0x7E;
    static readonly Color Background = Color.FromArgb(16, 20, 24);

    static string exe, outDir, dataDir, log;
    static int failures;

    static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "pass  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    static string[] Lines()
    {
        try
        {
            using (var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(s)) return r.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException) { return new string[0]; }
    }

    static int Count(string text) { return Lines().Count(l => l.Contains(text)); }

    static bool WaitFor(string text, int atLeast, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            if (Count(text) >= atLeast) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    static Bitmap Capture(string name)
    {
        Rectangle all = SystemInformation.VirtualScreen;
        var bmp = new Bitmap(all.Width, all.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(all.Location, Point.Empty, all.Size);
        bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
        return bmp;
    }

    // The snooze label is the only thing painted in the bottom strip of the primary screen.
    static Point? FindSnooze(Bitmap bmp)
    {
        Rectangle all = SystemInformation.VirtualScreen, primary = Screen.PrimaryScreen.Bounds;
        int offX = primary.X - all.X, offY = primary.Y - all.Y;
        int minX = int.MaxValue, maxX = -1, minY = 0, maxY = 0;
        for (int y = primary.Height - 36; y < primary.Height; y++)
            for (int x = 0; x < primary.Width; x++)
                if (bmp.GetPixel(x + offX, y + offY).ToArgb() != Background.ToArgb())
                {
                    if (minX == int.MaxValue) minY = y;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); maxY = y;
                }
        if (maxX < 0) return null;
        return new Point(primary.X + (minX + maxX) / 2, primary.Y + (minY + maxY) / 2);
    }

    static void Click(Point p)
    {
        SetCursorPos(p.X, p.Y);
        Thread.Sleep(250);
        mouse_event(2, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(4, 0, 0, 0, IntPtr.Zero);
    }

    static int Main(string[] args)
    {
        SetProcessDPIAware();
        exe = Path.GetFullPath(args[0]);
        outDir = Path.GetFullPath(args[1]);
        dataDir = Path.Combine(outDir, "data");
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
        Directory.CreateDirectory(dataDir);
        log = Path.Combine(dataDir, "app.log");
        Point home = Cursor.Position;

        Process app = Process.Start(exe, "--test --data-dir \"" + dataDir + "\" --work-seconds 2 --break-seconds 5 --snooze-seconds 2 --exit-after 45");
        try
        {
            Check("break starts after work time", WaitFor("phase Break", 1, 8000));
            Thread.Sleep(1200);
            using (Bitmap b = Capture("1-break.png")) Check("snooze is drawn on first break", FindSnooze(b).HasValue);

            int before = Count("input during break");
            keybd_event(VK_F15, 0, 0, IntPtr.Zero);
            keybd_event(VK_F15, 0, 2, IntPtr.Zero);
            Check("keyboard is detected during break", WaitFor("input during break", before + 1, 1500));
            Thread.Sleep(150);
            Capture("2-restarted.png").Dispose();

            before = Count("input during break");
            Point c = Cursor.Position;
            SetCursorPos(c.X + 25, c.Y + 25);
            mouse_event(1, 8, 8, 0, IntPtr.Zero);
            Check("mouse is detected during break", WaitFor("input during break", before + 1, 1500));

            Thread.Sleep(600);
            Point? spot;
            using (Bitmap b = Capture("3-before-snooze.png")) spot = FindSnooze(b);
            Check("snooze can be located", spot.HasValue);
            Click(spot.Value);
            Check("first snooze closes the break", WaitFor("phase Working snoozes=1", 1, 2000));

            Check("break returns after snooze", WaitFor("phase Break", 2, 5000));
            Thread.Sleep(1200);
            using (Bitmap b = Capture("4-second.png")) spot = FindSnooze(b);
            Check("snooze still there for second use", spot.HasValue);
            Click(spot.Value);
            Check("second snooze closes the break", WaitFor("phase Working snoozes=2", 1, 2000));

            Check("break returns again", WaitFor("phase Break", 3, 5000));
            Thread.Sleep(1200);
            using (Bitmap b = Capture("5-no-snooze.png")) Check("snooze is gone after two uses", !FindSnooze(b).HasValue);

            Check("break completes when left alone", WaitFor("break completed", 1, 12000));
            Thread.Sleep(300);
            Capture("6-back-to-work.png").Dispose();

            string[] history = File.ReadAllLines(Path.Combine(dataDir, "history.csv"));
            Check("history has 1 break and 2 snoozes", history.Count(l => l.Contains(",break,")) == 1 && history.Count(l => l.Contains(",snooze,")) == 2);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL  probe stopped: " + ex.Message);
            failures++;
        }
        finally
        {
            if (!app.HasExited) app.Kill();
            SetCursorPos(home.X, home.Y);
        }

        Console.WriteLine("--- app log");
        foreach (string line in Lines().Where(l => !l.Contains("input during"))) Console.WriteLine(line.TrimEnd());
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
