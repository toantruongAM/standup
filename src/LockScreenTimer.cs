using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace StandUp
{
    /// <summary>
    /// Normal apps cannot draw on the Windows lock screen. Only a SYSTEM process can, on the
    /// "Winlogon" desktop. An optional scheduled task starts this exe as SYSTEM on every lock.
    /// That copy lives in Program Files, reads two numbers from a state file, and shows a
    /// timer that takes no input. It exits straight away unless a break is running.
    /// </summary>
    static class LockScreenTimer
    {
        const string TaskName = "StandUp Lock Screen Timer";

        static string _stateDirOverride;

        static string StateDir
        {
            get
            {
                return _stateDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StandUp");
            }
        }
        static string StateFile { get { return Path.Combine(StateDir, "lockstate.txt"); } }
        static string LogFile { get { return Path.Combine(StateDir, "lock.log"); } }
        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StandUp"); } }
        static string InstalledExe { get { return Path.Combine(InstallDir, "StandUp.exe"); } }

        public static bool IsInstalled { get { return File.Exists(InstalledExe) && File.Exists(StateFile); } }

        /// <summary>Lets a test run use a private state file.</summary>
        public static void UseStateDir(string dir) { _stateDirOverride = dir; }

        // ---- the normal app's side ----

        public static void Publish(DateTime deadlineUtc, int totalSeconds)
        {
            WriteState(deadlineUtc.Ticks.ToString(CultureInfo.InvariantCulture) + " " + totalSeconds.ToString(CultureInfo.InvariantCulture));
        }

        public static void Clear() { WriteState(""); }

        static void WriteState(string text)
        {
            if (!File.Exists(StateFile)) return;   // lock-screen timer not installed
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(text);
                using (var stream = new FileStream(StateFile, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.SetLength(bytes.Length);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool TryRead(out DateTime deadlineUtc, out int totalSeconds)
        {
            deadlineUtc = DateTime.MinValue;
            totalSeconds = 0;
            try
            {
                var buffer = new byte[64];
                int count;
                using (var stream = new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    count = stream.Read(buffer, 0, buffer.Length);

                string[] parts = Encoding.ASCII.GetString(buffer, 0, count).Split(' ');
                long ticks;
                if (parts.Length != 2) return false;
                if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)) return false;
                if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out totalSeconds)) return false;
                if (ticks > DateTime.MaxValue.Ticks || totalSeconds < 1 || totalSeconds > 86400) return false;
                deadlineUtc = new DateTime(ticks, DateTimeKind.Utc);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // ---- install and uninstall, run elevated ----

        public static int Install()
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(Application.ExecutablePath, InstalledExe, true);

                // Only admins and SYSTEM can change the folder. Users may only write this one file's contents.
                var folder = new DirectorySecurity();
                folder.SetAccessRuleProtection(true, false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                folder.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                folder.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                folder.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.BuiltinUsersSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.CreateDirectory(StateDir);
                Directory.SetAccessControl(StateDir, folder);

                if (!File.Exists(StateFile)) File.WriteAllText(StateFile, "");
                FileSecurity file = File.GetAccessControl(StateFile);
                file.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.AuthenticatedUserSid), FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, AccessControlType.Allow));
                File.SetAccessControl(StateFile, file);

                string xml = Path.Combine(Path.GetTempPath(), "StandUpTask.xml");
                File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
                int result = RunSchtasks("/create /tn \"" + TaskName + "\" /xml \"" + xml + "\" /f");
                File.Delete(xml);
                return result;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "StandUp");
                return 1;
            }
        }

        public static int Uninstall()
        {
            try
            {
                RunSchtasks("/delete /tn \"" + TaskName + "\" /f");
                if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
                if (Directory.Exists(StateDir)) Directory.Delete(StateDir, true);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "StandUp");
                return 1;
            }
        }

        static SecurityIdentifier Sid(WellKnownSidType type) { return new SecurityIdentifier(type, null); }

        static int RunSchtasks(string arguments)
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true };
            using (Process process = Process.Start(info))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        static string TaskXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<Triggers><SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>SessionLock</StateChange></SessionStateChangeTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<ExecutionTimeLimit>PT5M</ExecutionTimeLimit><Priority>4</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + InstalledExe + "</Command><Arguments>--lock-launcher</Arguments></Exec></Actions>" +
                "</Task>";
        }

        // ---- running as SYSTEM in session 0: start the timer on the lock screen ----

        const int TokenSessionId = 12, SecurityImpersonation = 2, TokenPrimary = 1;
        const uint TOKEN_ALL_ACCESS = 0xF01FF, SE_PRIVILEGE_ENABLED = 2, CREATE_NO_WINDOW = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string reserved, desktop, title;
            public int x, y, xSize, ySize, xChars, yChars, fill, flags;
            public short showWindow, reserved2;
            public IntPtr reserved3, stdIn, stdOut, stdErr;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation { public IntPtr process, thread; public int processId, threadId; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct TokenPrivileges { public int count; public long luid; public uint attributes; }

        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint value, int size);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation info);

        public static int RunLauncher()
        {
            try
            {
                // The app publishes the break as soon as it sees the lock, which may be just after we start.
                DateTime deadline = DateTime.MinValue;
                int total = 0;
                bool found = false;
                for (int i = 0; i < 40 && !found; i++)
                {
                    found = TryRead(out deadline, out total);
                    if (!found) Thread.Sleep(100);
                }
                if (!found || deadline < DateTime.UtcNow.AddSeconds(-5)) return 0;

                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out token)) throw new Win32Exception();
                EnablePrivilege(token, "SeAssignPrimaryTokenPrivilege");
                EnablePrivilege(token, "SeIncreaseQuotaPrivilege");

                IntPtr duplicate;
                if (!DuplicateTokenEx(token, TOKEN_ALL_ACCESS, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out duplicate)) throw new Win32Exception();
                uint session = WTSGetActiveConsoleSessionId();
                if (!SetTokenInformation(duplicate, TokenSessionId, ref session, sizeof(uint))) throw new Win32Exception();

                var startup = new StartupInfo { desktop = @"winsta0\winlogon" };
                startup.cb = Marshal.SizeOf(typeof(StartupInfo));
                ProcessInformation info;
                var command = new StringBuilder("\"" + Application.ExecutablePath + "\" --lock-overlay");
                if (!CreateProcessAsUser(duplicate, Application.ExecutablePath, command, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW, IntPtr.Zero, null, ref startup, out info))
                    throw new Win32Exception();

                CloseHandle(info.process);
                CloseHandle(info.thread);
                CloseHandle(duplicate);
                CloseHandle(token);
                return 0;
            }
            catch (Exception ex)
            {
                WriteLog("launcher: " + ex.Message);
                return 1;
            }
        }

        static void EnablePrivilege(IntPtr token, string name)
        {
            var privileges = new TokenPrivileges { count = 1, attributes = SE_PRIVILEGE_ENABLED };
            if (LookupPrivilegeValue(null, name, out privileges.luid))
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }

        internal static void WriteLog(string message)
        {
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- running as SYSTEM on the lock screen ----

        public static int RunOverlay()
        {
            try
            {
                var forms = Screen.AllScreens.Select(s => new LockTimerForm(s)).ToList();
                forms[0].FormClosed += delegate { Application.ExitThread(); };
                foreach (LockTimerForm form in forms) form.Show();
                Application.Run();
                return 0;
            }
            catch (Exception ex)
            {
                WriteLog("overlay: " + ex.Message);
                return 1;
            }
        }
    }

    /// <summary>A small timer panel that never takes focus or input, so the password box keeps working.</summary>
    sealed class LockTimerForm : Form
    {
        const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int UOI_NAME = 2;
        const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
        const int EmptyReadsBeforeExit = 8;
        const int WrongDesktopTicksBeforeExit = 8;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);

        static readonly StringFormat Centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        readonly System.Windows.Forms.Timer _timer;
        readonly Font _labelFont, _timerFont;
        string _label = "Break time left";
        string _clock = "";
        double _progress;
        int _emptyReads, _wrongDesktopTicks;

        public LockTimerForm(Screen screen)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(16, 20, 24);

            Rectangle s = screen.Bounds;
            int height = Math.Max(120, s.Height / 7), width = height * 3;
            Bounds = new Rectangle(s.X + (s.Width - width) / 2, s.Y + (int)(s.Height * 0.8) - height / 2, width, height);

            _labelFont = new Font("Segoe UI", height * 0.15f, GraphicsUnit.Pixel);
            _timerFont = new Font("Segoe UI Semibold", height * 0.46f, GraphicsUnit.Pixel);

            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += OnTick;
            _timer.Start();
            OnTick(null, EventArgs.Empty);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return p;
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (ShouldExit()) { Close(); return; }

            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            DateTime deadline;
            int total;
            if (!LockScreenTimer.TryRead(out deadline, out total))
            {
                _emptyReads++;
                return;
            }
            _emptyReads = 0;

            int left = Math.Max(0, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
            string clock = left == 0 ? "Done" : (left / 60) + ":" + (left % 60).ToString("00");
            _label = left == 0 ? "Nice walk. Welcome back." : "Keep walking. Break time left";
            _progress = 1.0 - Math.Min(1.0, left / (double)total);
            if (clock != _clock)
            {
                _clock = clock;
                Invalidate();
            }
        }

        bool ShouldExit()
        {
            if (_emptyReads >= EmptyReadsBeforeExit) return true;
            if (Program.TestMode) return false;
            _wrongDesktopTicks = InputDesktopIsWinlogon() ? 0 : _wrongDesktopTicks + 1;
            return _wrongDesktopTicks >= WrongDesktopTicksBeforeExit;
        }

        static bool InputDesktopIsWinlogon()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 0);
            if (desktop == IntPtr.Zero) return true;   // can't tell; stay up rather than flicker away
            try
            {
                var name = new StringBuilder(64);
                int needed;
                return !GetUserObjectInformation(desktop, UOI_NAME, name, name.Capacity * 2, out needed)
                    || string.Equals(name.ToString(), "Winlogon", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CloseDesktop(desktop);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;

            using (var muted = new SolidBrush(Color.FromArgb(190, 198, 208)))
            using (var accent = new SolidBrush(Color.FromArgb(255, 200, 87)))
            {
                g.DrawString(_label, _labelFont, muted, new RectangleF(0, h * 0.06f, w, h * 0.26f), Centered);
                g.DrawString(_clock, _timerFont, accent, new RectangleF(0, h * 0.3f, w, h * 0.58f), Centered);
                g.FillRectangle(accent, 0, h - h * 0.05f, (float)(w * _progress), h * 0.05f);
            }
        }
    }
}
