using System;
using System.Threading;
using System.Windows.Forms;

namespace StandUp
{
    static class Program
    {
        const string InstanceName = @"Local\StandUpWellnessApp";

        /// <summary>Set by --test: private data folder, no first-run prompt, no autostart changes.</summary>
        public static bool TestMode { get; private set; }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var test = new TestOptions();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--uninstall-lock-timer": return LegacyLockTimer.Uninstall();
                    case "--test": TestMode = true; Log.Enabled = true; break;
                    case "--data-dir": Paths.DataDir = args[++i]; break;
                    case "--work-seconds": test.WorkSeconds = int.Parse(args[++i]); break;
                    case "--break-seconds": test.BreakSeconds = int.Parse(args[++i]); break;
                    case "--snooze-seconds": test.SnoozeSeconds = int.Parse(args[++i]); break;
                    case "--exit-after": test.ExitAfterSeconds = int.Parse(args[++i]); break;
                    case "--idle-seconds": test.IdleSeconds = int.Parse(args[++i]); break;
                    case "--long-every-seconds": test.LongEverySeconds = int.Parse(args[++i]); break;
                    case "--language": test.Language = args[++i]; break;
                }
            }

            bool first;
            using (var instance = new Mutex(true, TestMode ? InstanceName + ".test" : InstanceName, out first))
            {
                if (!first) return 0;

                AppSettings settings = AppSettings.Load();
                if (test.Language != null) settings.Language = test.Language;
                L10n.Language = settings.Language;
                if (!TestMode && !settings.HasPasscode)
                {
                    string intro = L10n.T("passcode.welcomeTitle") + "\n\n" + L10n.T("passcode.welcome");
                    using (var dialog = new SetPasscodeForm(settings, intro))
                    {
                        if (dialog.ShowDialog() != DialogResult.OK) return 0;
                    }
                }
                if (!TestMode) AutoStart.Apply(settings.StartWithWindows, Application.ExecutablePath);

                Application.Run(new TrayApp(settings, test));
            }
            return 0;
        }
    }
}
