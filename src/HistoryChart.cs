using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace StandUp
{
    /// <summary>Three stacked column charts sharing one time axis, one measure each. Hover shows a day's numbers.</summary>
    sealed class HistoryChart : Control
    {
        static readonly Color Surface = ColorTranslator.FromHtml("#fcfcfb");
        static readonly Color Primary = ColorTranslator.FromHtml("#0b0b0b");
        static readonly Color Secondary = ColorTranslator.FromHtml("#52514e");
        static readonly Color Grid = ColorTranslator.FromHtml("#e6e5e0");
        static readonly Color Baseline = ColorTranslator.FromHtml("#b9b8b1");
        static readonly Color HoverBand = ColorTranslator.FromHtml("#efeeea");
        static readonly Color Bar = ColorTranslator.FromHtml("#2a78d6");

        sealed class Measure
        {
            public string Title;
            public string Unit;
            public string Column;
            public Func<DayStats, double> Value;
        }

        readonly Measure[] Measures;
        readonly History _history;
        readonly Font _titleFont, _labelFont;
        Measure[] _shown;
        int _days = 14;
        int _hover = -1;

        public HistoryChart(History history)
        {
            _history = history;
            Measures = new[]
            {
                new Measure { Title = L10n.T("history.breaksPerDay"), Unit = L10n.T("history.unitBreaks"), Column = L10n.T("history.colBreaks"), Value = d => d.Breaks },
                new Measure { Title = L10n.T("history.snoozesPerDay"), Unit = L10n.T("history.unitSnoozes"), Column = L10n.T("history.colSnoozes"), Value = d => d.Snoozes },
                new Measure { Title = L10n.T("history.minutesPerDay"), Unit = L10n.T("history.unitMinutes"), Column = L10n.T("history.colMinutes"), Value = d => d.BreakSeconds / 60.0 }
            };
            _shown = new[] { Measures[0], Measures[2] };
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Surface;
            AccessibleName = L10n.T("history.chartLabel");
            _titleFont = new Font("Segoe UI Semibold", 10f);
            _labelFont = new Font("Segoe UI", 8.5f);
        }

        float Scale1 { get { return DeviceDpi / 96f; } }
        float PlotLeft { get { return 38 * Scale1; } }
        float PlotRight { get { return ClientSize.Width - 14 * Scale1; } }
        float Band { get { return (PlotRight - PlotLeft) / _days; } }

        public void ShowDays(int days)
        {
            _days = days;
            _hover = -1;
            Invalidate();
        }

        /// <summary>Switches between the snooze chart and the two break charts.</summary>
        public void ShowSnoozes(bool snoozes)
        {
            _shown = snoozes ? new[] { Measures[1] } : new[] { Measures[0], Measures[2] };
            _hover = -1;
            Invalidate();
        }

        DayStats Day(int index)
        {
            return _history.For(DateTime.Today.AddDays(index - (_days - 1)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float s = Scale1;
            float axis = 24 * s;
            float height = (ClientSize.Height - axis) / _shown.Length;
            for (int i = 0; i < _shown.Length; i++) DrawMeasure(g, _shown[i], i * height, height);
            DrawDates(g, ClientSize.Height - axis);
            if (_hover >= 0) DrawTooltip(g);
        }

        void DrawMeasure(Graphics g, Measure measure, float top, float height)
        {
            float s = Scale1;
            double[] values = Enumerable.Range(0, _days).Select(i => measure.Value(Day(i))).ToArray();
            double total = values.Sum();

            using (var primary = new SolidBrush(Primary))
            using (var secondary = new SolidBrush(Secondary))
            {
                g.DrawString(measure.Title, _titleFont, primary, 6 * s, top + 4 * s);
                SizeF used = g.MeasureString(measure.Title, _titleFont);
                g.DrawString(L10n.T("history.total", Format(total), measure.Unit, _days), _labelFont, secondary, 6 * s + used.Width + 4 * s, top + 6 * s);

                float plotTop = top + 34 * s, plotBottom = top + height - 6 * s;
                double max = NiceMax(values.Max());

                using (var band = new SolidBrush(HoverBand))
                    if (_hover >= 0) g.FillRectangle(band, PlotLeft + _hover * Band, plotTop, Band, plotBottom - plotTop);

                using (var grid = new Pen(Grid, 1))
                using (var baseline = new Pen(Baseline, 1))
                {
                    g.DrawLine(grid, PlotLeft, plotTop, PlotRight, plotTop);
                    g.DrawLine(baseline, PlotLeft, plotBottom, PlotRight, plotBottom);
                }
                DrawRight(g, Format(max), secondary, plotTop);
                DrawRight(g, "0", secondary, plotBottom);

                using (var bar = new SolidBrush(Bar))
                {
                    float width = Math.Min(24 * s, Math.Max(2f, Band - 2 * s));
                    for (int i = 0; i < _days; i++)
                    {
                        if (values[i] <= 0) continue;
                        float h = Math.Max(2f, (float)(values[i] / max * (plotBottom - plotTop)));
                        float x = PlotLeft + i * Band + (Band - width) / 2;
                        using (GraphicsPath path = TopRounded(x, plotBottom - h, width, h, Math.Min(4 * s, width / 2)))
                            g.FillPath(bar, path);
                    }
                }
            }
        }

        void DrawRight(Graphics g, string text, Brush brush, float centerY)
        {
            SizeF size = g.MeasureString(text, _labelFont);
            g.DrawString(text, _labelFont, brush, PlotLeft - 4 * Scale1 - size.Width, centerY - size.Height / 2);
        }

        void DrawDates(Graphics g, float y)
        {
            int step = _days <= 14 ? 2 : _days <= 30 ? 5 : 15;
            using (var secondary = new SolidBrush(Secondary))
            {
                for (int i = _days - 1; i >= 0; i -= step)
                {
                    string text = Day(i).Date.ToString("d MMM", L10n.Culture);
                    SizeF size = g.MeasureString(text, _labelFont);
                    float x = PlotLeft + (i + 0.5f) * Band - size.Width / 2;
                    x = Math.Min(x, ClientSize.Width - size.Width - 2);
                    g.DrawString(text, _labelFont, secondary, Math.Max(0, x), y + 3 * Scale1);
                }
            }
        }

        void DrawTooltip(Graphics g)
        {
            float s = Scale1;
            DayStats day = Day(_hover);
            string date = day.Date.ToString("ddd, d MMM yyyy", L10n.Culture);
            string[] rows = Measures.Select(m => m.Column + ": " + Format(m.Value(day))).ToArray();

            float textWidth = Math.Max(g.MeasureString(date, _titleFont).Width, rows.Max(r => g.MeasureString(r, _labelFont).Width));
            float width = Math.Max(150 * s, textWidth + 18 * s), height = 88 * s;
            float x = PlotLeft + (_hover + 1) * Band + 6 * s;
            if (x + width > ClientSize.Width) x = PlotLeft + _hover * Band - width - 6 * s;
            float y = 34 * s;

            using (var fill = new SolidBrush(Color.White))
            using (var border = new Pen(Baseline, 1))
            using (var primary = new SolidBrush(Primary))
            using (var secondary = new SolidBrush(Secondary))
            {
                g.FillRectangle(fill, x, y, width, height);
                g.DrawRectangle(border, x, y, width, height);
                g.DrawString(date, _titleFont, primary, x + 8 * s, y + 6 * s);
                for (int i = 0; i < rows.Length; i++)
                    g.DrawString(rows[i], _labelFont, secondary, x + 8 * s, y + (30 + i * 18) * s);
            }
        }

        static GraphicsPath TopRounded(float x, float y, float width, float height, float radius)
        {
            float r = Math.Min(radius, height);
            var path = new GraphicsPath();
            path.AddLine(x, y + height, x, y + r);
            path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
            path.AddArc(x + width - 2 * r, y, 2 * r, 2 * r, 270, 90);
            path.AddLine(x + width, y + r, x + width, y + height);
            path.CloseFigure();
            return path;
        }

        static double NiceMax(double value)
        {
            if (value <= 1) return 1;
            foreach (double step in new double[] { 1, 2, 5, 10, 20, 50, 100, 200, 500 })
                if (Math.Ceiling(value / step) <= 5) return Math.Ceiling(value / step) * step;
            return Math.Ceiling(value / 1000) * 1000;
        }

        static string Format(double value)
        {
            return value.ToString("0.#", L10n.Culture);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = (int)Math.Floor((e.X - PlotLeft) / Band);
            if (index < 0 || index >= _days || e.Y >= ClientSize.Height - 24 * Scale1) index = -1;
            if (index == _hover) return;
            _hover = index;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _labelFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
