using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StandUp
{
    /// <summary>One full-screen break window per monitor. The primary one has the picture and snooze.</summary>
    sealed class OverlayForm : Form
    {
        static readonly Color Background = Color.FromArgb(16, 20, 24);
        static readonly Color Foreground = Color.FromArgb(242, 244, 247);
        static readonly Color Muted = Color.FromArgb(142, 152, 164);
        static readonly Color Accent = Color.FromArgb(255, 200, 87);
        static readonly Color Warning = Color.FromArgb(255, 122, 89);
        // Deliberately dim, but readable once you look for it.
        static readonly Color SnoozeIdle = Color.FromArgb(82, 92, 104);
        static readonly Color SnoozeHover = Color.FromArgb(190, 198, 208);

        const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        static readonly StringFormat Centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        readonly BreakEngine _engine;
        readonly Rectangle _screenBounds;
        readonly bool _primary;
        readonly Bitmap _photo;
        readonly Action _snooze;
        readonly Font _headFont, _timerFont, _hintFont, _snoozeFont;
        readonly double _snoozePlace;

        Bitmap _scaled;
        Rectangle _snoozeRect;
        bool _snoozeHot, _showRestarted;
        string _timerText = "";
        bool _canSnooze;

        public OverlayForm(Screen screen, Bitmap photo, BreakEngine engine, Action snooze, Random random)
        {
            _engine = engine;
            _screenBounds = screen.Bounds;
            _primary = screen.Primary;
            _photo = photo;
            _snooze = snooze;
            _snoozePlace = random.NextDouble();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Background;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = _screenBounds;
            KeyPreview = true;

            float h = _screenBounds.Height;
            float timerSize = _primary ? h * 0.115f : h * 0.24f;
            _headFont = new Font("Segoe UI Semibold", h * 0.05f, GraphicsUnit.Pixel);
            _timerFont = new Font("Segoe UI Semibold", timerSize, GraphicsUnit.Pixel);
            _hintFont = new Font("Segoe UI", Math.Max(13f, h * 0.0175f), GraphicsUnit.Pixel);
            _snoozeFont = new Font("Segoe UI", Math.Max(12f, h * 0.013f), GraphicsUnit.Pixel);
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
                return p;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = _screenBounds;   // again, in case the window changed DPI on its way to this monitor
            UpdateClock(DateTime.UtcNow);
            KeepOnTop();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && _engine.Phase == Phase.Break) e.Cancel = true;
            base.OnFormClosing(e);
        }

        public void KeepOnTop()
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (_primary) SetForegroundWindow(Handle);
        }

        public void UpdateClock(DateTime now)
        {
            int left = Math.Max(0, (int)Math.Ceiling((_engine.Deadline - now).TotalSeconds));
            string text = (left / 60) + ":" + (left % 60).ToString("00");
            bool restarted = now - _engine.LastActivity < TimeSpan.FromSeconds(2);
            bool canSnooze = _primary && _engine.CanSnooze;

            if (text == _timerText && restarted == _showRestarted && canSnooze == _canSnooze) return;
            _timerText = text;
            _showRestarted = restarted;
            _canSnooze = canSnooze;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float w = ClientSize.Width, h = ClientSize.Height;

            if (!_primary)
            {
                DrawText(g, _timerText, _timerFont, Accent, 0, h * 0.28f, w, h * 0.34f);
                DrawText(g, L10n.T("overlay.secondary"), _headFont, Foreground, 0, h * 0.62f, w, h * 0.1f);
                DrawStatus(g, w, h * 0.74f, h * 0.05f);
                return;
            }

            DrawText(g, L10n.T(_engine.IsLongBreak ? "overlay.headlineLong" : "overlay.headline"), _headFont, Foreground, 0, h * 0.025f, w, h * 0.09f);
            DrawPhoto(g, w, h);
            DrawText(g, _timerText, _timerFont, Accent, 0, h * 0.745f, w, h * 0.135f);
            DrawStatus(g, w, h * 0.88f, h * 0.04f);
            DrawText(g, L10n.T("overlay.hintWindows"), _hintFont, Muted, 0, h * 0.92f, w, h * 0.035f);
            if (_canSnooze) DrawSnooze(g, w, h);
        }

        void DrawStatus(Graphics g, float w, float top, float height)
        {
            if (_showRestarted)
                DrawText(g, L10n.T("overlay.restarted"), _hintFont, Warning, 0, top, w, height);
            else if (!_engine.IsLongBreak)   // long breaks don't restart on input, so they have no status line
                DrawText(g, L10n.T("overlay.status"), _hintFont, Muted, 0, top, w, height);
        }

        void DrawPhoto(Graphics g, float w, float h)
        {
            if (_photo == null) return;

            int boxTop = (int)(h * 0.12f), boxHeight = (int)(h * 0.615f), boxWidth = (int)(w * 0.8f);
            double scale = Math.Min((double)boxWidth / _photo.Width, (double)boxHeight / _photo.Height);
            int photoW = (int)(_photo.Width * scale), photoH = (int)(_photo.Height * scale);

            if (_scaled == null || _scaled.Width != photoW || _scaled.Height != photoH)
            {
                if (_scaled != null) _scaled.Dispose();
                _scaled = new Bitmap(photoW, photoH);
                using (Graphics s = Graphics.FromImage(_scaled))
                {
                    s.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    s.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    s.DrawImage(_photo, 0, 0, photoW, photoH);
                }
            }
            g.DrawImageUnscaled(_scaled, (int)(w - photoW) / 2, boxTop + (boxHeight - photoH) / 2);
        }

        void DrawSnooze(Graphics g, float w, float h)
        {
            int rectW = Math.Max(52, (int)(h * 0.055f)), rectH = Math.Max(18, (int)(h * 0.02f));
            float margin = w * 0.06f;
            int x = (int)(margin + _snoozePlace * (w - 2 * margin - rectW));
            _snoozeRect = new Rectangle(x, (int)(h - rectH * 1.6f), rectW, rectH);
            DrawText(g, L10n.T("overlay.snooze"), _snoozeFont, _snoozeHot ? SnoozeHover : SnoozeIdle, _snoozeRect.X, _snoozeRect.Y, _snoozeRect.Width, _snoozeRect.Height);
        }

        static void DrawText(Graphics g, string text, Font font, Color color, float x, float y, float w, float h)
        {
            using (var brush = new SolidBrush(color))
                g.DrawString(text, font, brush, new RectangleF(x, y, w, h), Centered);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hot = _canSnooze && _snoozeRect.Contains(e.Location);
            if (hot == _snoozeHot) return;
            _snoozeHot = hot;
            Cursor = hot ? Cursors.Hand : Cursors.Default;
            Invalidate(Rectangle.Inflate(_snoozeRect, 2, 2));
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && _canSnooze && _snoozeRect.Contains(e.Location)) _snooze();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_scaled != null) _scaled.Dispose();
                _headFont.Dispose();
                _timerFont.Dispose();
                _hintFont.Dispose();
                _snoozeFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
