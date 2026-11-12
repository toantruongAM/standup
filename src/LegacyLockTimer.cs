using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace StandUp
{
    /// <summary>
    /// StandUp 1.0 and 1.1 could install a timer on the Windows lock screen: a copy of the app in
    /// Program Files, run as SYSTEM by a scheduled task. That feature is gone; this only removes it.
    /// Run elevated with --uninstall-lock-timer. The installer offers to do it.
    /// </summary>
    static class LegacyLockTimer
    {
        const string TaskName = "StandUp Lock Screen Timer";

        static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "StandUp"); } }
        static string StateDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StandUp"); } }

        public static int Uninstall()
        {
            try
            {
                var info = new ProcessStartInfo("schtasks.exe", "/delete /tn \"" + TaskName + "\" /f") { UseShellExecute = false, CreateNoWindow = true };
                using (Process process = Process.Start(info)) process.WaitForExit();
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
    }
}
