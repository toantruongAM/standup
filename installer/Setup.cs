using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("StandUp Setup")]
[assembly: AssemblyProduct("StandUp")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace StandUpSetup
{
    /// <summary>Installs StandUp for the current user, with no administrator rights. The same exe uninstalls it.</summary>
    static class Installer
    {
        public const string Title = "StandUp Setup";
        const string Version = "1.4.0";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\StandUp";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string LocalData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        public static string InstallDir { get { return Path.Combine(LocalData, "Programs", "StandUp"); } }
        static string AppExe { get { return Path.Combine(InstallDir, "StandUp.exe"); } }
        static string UninstallerExe { get { return Path.Combine(InstallDir, "Uninstall StandUp.exe"); } }
        static string DataDir { get { return Path.Combine(LocalData, "StandUp"); } }
        static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "StandUp.lnk"); } }
        static string LockTimerExe { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StandUp", "StandUp.exe"); } }

        public static void Install()
        {
            Directory.CreateDirectory(InstallDir);

            string payload = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n == "payload.exe");
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(payload))
            using (var target = new FileStream(AppExe, FileMode.Create, FileAccess.Write))
                source.CopyTo(target);
            File.Copy(Application.ExecutablePath, UninstallerExe, true);

            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(Shortcut);
            link.TargetPath = AppExe;
            link.WorkingDirectory = InstallDir;
            link.Description = "Makes you stand up and walk";
            link.Save();

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "StandUp");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("DisplayIcon", AppExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", "\"" + UninstallerExe + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(AppExe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        /// <summary>Any StandUp running, wherever it was started from. Only one copy can run at a time.</summary>
        public static bool AnyStandUpRunning()
        {
            Process[] running = Process.GetProcessesByName("StandUp");
            foreach (Process p in running) p.Dispose();
            return running.Length > 0;
        }

        /// <summary>StandUp 1.0 and 1.1 could add a lock-screen timer. Version 1.2 dropped it, so offer to remove it.</summary>
        public static void OfferLegacyCleanup()
        {
            if (!File.Exists(LockTimerExe)) return;
            if (Ask("An older version of StandUp added a timer to the lock screen. That feature has been removed.\n\nRemove it from this PC now? Windows will ask for administrator approval.") == DialogResult.Yes)
                RemoveLockScreenTimer();
        }

        public static void LaunchApp()
        {
            Process.Start(new ProcessStartInfo(AppExe) { WorkingDirectory = InstallDir });
        }

        static bool AppIsRunning()
        {
            if (!File.Exists(AppExe)) return false;
            try
            {
                using (new FileStream(AppExe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        public static int Uninstall(bool silent)
        {
            if (AppIsRunning())
            {
                if (!silent) Tell("StandUp is still running. Close it from its tray icon (this needs your passcode), then uninstall again.", MessageBoxIcon.Warning);
                return 1;
            }

            bool deleteData = false;
            if (!silent)
            {
                if (Ask("Remove StandUp from this PC?") != DialogResult.Yes) return 0;
                deleteData = Ask("Also delete your settings, passcode, pictures and break history?") == DialogResult.Yes;
                RemoveLockScreenTimer();
            }

            if (File.Exists(AppExe)) File.Delete(AppExe);
            if (File.Exists(Shortcut)) File.Delete(Shortcut);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (key != null) key.DeleteValue("StandUp", false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (deleteData && Directory.Exists(DataDir)) Directory.Delete(DataDir, true);

            // This exe lives in the folder being removed, so a short-lived helper finishes the job after it exits.
            var cleanup = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            Process.Start(cleanup).Dispose();

            if (!silent) Tell("StandUp has been removed.", MessageBoxIcon.Information);
            return 0;
        }

        static void RemoveLockScreenTimer()
        {
            if (!File.Exists(LockTimerExe) || !File.Exists(AppExe)) return;
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo(AppExe, "--uninstall-lock-timer") { Verb = "runas", UseShellExecute = true }))
                    process.WaitForExit();
            }
            catch (Win32Exception)
            {
                Tell("The lock-screen timer was left in place because administrator approval was declined. It does nothing without StandUp.", MessageBoxIcon.Information);
            }
        }

        static DialogResult Ask(string question)
        {
            return MessageBox.Show(question, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        }

        public static void Tell(string message, MessageBoxIcon icon)
        {
            MessageBox.Show(message, Title, MessageBoxButtons.OK, icon);
        }
    }

    sealed class SetupForm : Form
    {
        public SetupForm()
        {
            Text = Installer.Title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16), Dock = DockStyle.Fill };
            grid.Controls.Add(new Label { Text = "Install StandUp", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 13f, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8) });
            grid.Controls.Add(new Label
            {
                Text = "StandUp locks your screen for a 1-minute walk every hour, and a 10-minute one every 3 hours. You'll choose a passcode when it first opens.\n\nIt installs for your account only and starts with Windows. English, Español and Tiếng Việt are included.",
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Margin = new Padding(3, 3, 3, 14)
            });

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var install = new Button { Text = "Install", AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(install);
            grid.Controls.Add(buttons);
            Controls.Add(grid);
            AcceptButton = install;
            CancelButton = cancel;

            install.Click += delegate { RunInstall(); };
        }

        void RunInstall()
        {
            if (Installer.AnyStandUpRunning())
            {
                Installer.Tell("StandUp is running. Close it from its tray icon (this needs your passcode), then click Install again.", MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Installer.Install();
            }
            catch (IOException)
            {
                Installer.Tell("StandUp is running. Close it from its tray icon (this needs your passcode), then click Install again.", MessageBoxIcon.Warning);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                Installer.Tell("StandUp is running. Close it from its tray icon (this needs your passcode), then click Install again.", MessageBoxIcon.Warning);
                return;
            }

            Installer.OfferLegacyCleanup();
            Installer.LaunchApp();
            DialogResult = DialogResult.OK;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = args.Contains("--silent");
            if (args.Contains("--uninstall")) return Installer.Uninstall(silent);
            if (silent)
            {
                if (Installer.AnyStandUpRunning()) return 2;
                Installer.Install();
                return 0;
            }

            using (var form = new SetupForm()) form.ShowDialog();
            return 0;
        }
    }
}
