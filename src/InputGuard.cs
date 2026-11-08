using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StandUp
{
    /// <summary>
    /// Active only during a break. Swallows the keyboard so nothing reaches other apps,
    /// reports keyboard and mouse use, and leaves Win+L working.
    /// </summary>
    sealed class InputGuard : IDisposable
    {
        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_MOUSEMOVE = 0x200;
        const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_L = 0x4C;
        const int MoveThreshold = 3;   // pixels; a bumped desk should not restart the timer

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KeyInfo { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct MouseInfo { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        readonly Action _activity;
        readonly HookProc _keyProc, _mouseProc;   // held in fields so the GC cannot collect them
        IntPtr _keyHook, _mouseHook;
        int _anchorX, _anchorY;
        bool _winDown;

        public InputGuard(Action activity)
        {
            _activity = activity;
            _keyProc = OnKey;
            _mouseProc = OnMouse;
            _anchorX = Cursor.Position.X;
            _anchorY = Cursor.Position.Y;

            IntPtr module = GetModuleHandle(null);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
            if (_keyHook == IntPtr.Zero) throw new Win32Exception();
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero) throw new Win32Exception();
        }

        IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0) return CallNextHookEx(_keyHook, code, wParam, lParam);

            var key = (KeyInfo)Marshal.PtrToStructure(lParam, typeof(KeyInfo));
            int message = wParam.ToInt32();
            bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;

            if (key.Vk == VK_LWIN || key.Vk == VK_RWIN)
            {
                // Held back so Start stays closed, and not counted so Win+L doesn't restart the timer.
                _winDown = down;
            }
            else if (down && key.Vk == VK_L && _winDown)
            {
                LockWorkStation();
            }
            else if (down)
            {
                _activity();
            }
            return new IntPtr(1);
        }

        IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                if (wParam.ToInt32() == WM_MOUSEMOVE)
                {
                    var mouse = (MouseInfo)Marshal.PtrToStructure(lParam, typeof(MouseInfo));
                    if (Math.Abs(mouse.X - _anchorX) + Math.Abs(mouse.Y - _anchorY) >= MoveThreshold)
                    {
                        _anchorX = mouse.X;
                        _anchorY = mouse.Y;
                        _activity();
                    }
                }
                else
                {
                    _activity();
                }
            }
            return CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyHook = _mouseHook = IntPtr.Zero;
        }
    }
}
