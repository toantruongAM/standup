using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace StandUp
{
    /// <summary>Works out whether a PowerPoint slide show or Windows presentation mode is running.</summary>
    static class PresentingDetector
    {
        const int QUNS_PRESENTATION_MODE = 4;

        static readonly HashSet<string> SlideShowClasses = new HashSet<string> { "screenClass", "PodiumParent" };

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);

        /// <summary>Returns what is being presented, or null if nothing is.</summary>
        public static string Detect()
        {
            int state;
            if (SHQueryUserNotificationState(out state) == 0 && state == QUNS_PRESENTATION_MODE) return "Windows presentation mode";
            return SlideShowWindowIsOpen() ? "PowerPoint slide show" : null;
        }

        static bool SlideShowWindowIsOpen()
        {
            bool found = false;
            var name = new StringBuilder(256);

            EnumWindows((hwnd, lParam) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                name.Clear();
                GetClassName(hwnd, name, name.Capacity);
                found = SlideShowClasses.Contains(name.ToString());
                return !found;
            }, IntPtr.Zero);

            return found;
        }
    }
}
