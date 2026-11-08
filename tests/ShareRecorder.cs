// Records which Zoom and Teams windows and processes come and go, and what StandUp's detector says,
// so the screen-share detection can be matched to what the apps really do.
// Only Zoom and Teams windows are logged. Usage: ShareRecorder <StandUp.exe> <log file> <stop file> <seconds>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class ShareRecorder
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    static readonly Regex Meeting = new Regex(@"^(zoom|cpt|aomhost|airhost|ms-teams|teams)", RegexOptions.IgnoreCase);

    static Dictionary<uint, ProcessEntry> Processes()
    {
        var all = new Dictionary<uint, ProcessEntry>();
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);
        var e = new ProcessEntry { dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
        for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) all[e.th32ProcessID] = e;
        CloseHandle(snap);
        return all;
    }

    // A process counts if it is Zoom or Teams, or was started by them (Teams draws through WebView2 helpers).
    static string Owner(uint pid, Dictionary<uint, ProcessEntry> all)
    {
        uint current = pid;
        for (int depth = 0; depth < 5; depth++)
        {
            ProcessEntry entry;
            if (!all.TryGetValue(current, out entry)) return null;
            if (Meeting.IsMatch(entry.szExeFile)) return all[pid].szExeFile;
            current = entry.th32ParentProcessID;
        }
        return null;
    }

    static int Main(string[] args)
    {
        Assembly app = Assembly.LoadFrom(args[0]);
        MethodInfo detect = app.GetType("StandUp.PresentingDetector").GetMethod("Detect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        string logFile = args[1], stopFile = args[2];
        int seconds = int.Parse(args[3]);
        if (File.Exists(stopFile)) File.Delete(stopFile);

        Action<string> write = line => File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
        write("recording started");

        var knownProcesses = new HashSet<string>();
        var knownWindows = new HashSet<string>();
        string verdict = "(none yet)";
        DateTime end = DateTime.Now.AddSeconds(seconds);

        while (DateTime.Now < end && !File.Exists(stopFile))
        {
            Dictionary<uint, ProcessEntry> all = Processes();

            var processes = new HashSet<string>(all.Keys.Where(p => Owner(p, all) != null).Select(p => all[p].szExeFile + " #" + p));
            foreach (string p in processes.Except(knownProcesses).OrderBy(x => x)) write("+process " + p);
            foreach (string p in knownProcesses.Except(processes).OrderBy(x => x)) write("-process " + p);
            knownProcesses = processes;

            var windows = new Dictionary<string, string>();
            EnumWindows((hwnd, l) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                string owner = all.ContainsKey(pid) ? Owner(pid, all) : null;
                if (owner == null) return true;

                var cls = new StringBuilder(256);
                var title = new StringBuilder(256);
                GetClassName(hwnd, cls, 256);
                GetWindowText(hwnd, title, 256);
                Rect r;
                GetWindowRect(hwnd, out r);
                bool top = (GetWindowLongPtr(hwnd, -20).ToInt64() & 8) != 0;
                windows[owner + " | " + cls + " | '" + title + "'"] = (r.R - r.L) + "x" + (r.B - r.T) + (top ? " topmost" : "");
                return true;
            }, IntPtr.Zero);

            foreach (string w in windows.Keys.Except(knownWindows).OrderBy(x => x)) write("+window " + w + " | " + windows[w]);
            foreach (string w in knownWindows.Except(windows.Keys).OrderBy(x => x)) write("-window " + w);
            knownWindows = new HashSet<string>(windows.Keys);

            string now = (string)detect.Invoke(null, null) ?? "not presenting";
            if (now != verdict)
            {
                verdict = now;
                write("DETECTOR: " + now);
            }
            Thread.Sleep(1000);
        }
        write("recording stopped");
        return 0;
    }
}
