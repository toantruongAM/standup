using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace StandUp
{
    static class Ui
    {
        public static void Dialog(Form form, string title)
        {
            form.Text = title;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.TopMost = true;
            form.Font = SystemFonts.MessageBoxFont;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.AutoSize = true;
            form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        public static TableLayoutPanel Grid(int columns)
        {
            var grid = new TableLayoutPanel
            {
                ColumnCount = columns,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14),
                Dock = DockStyle.Fill
            };
            return grid;
        }

        public static Label Label(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 7) };
        }

        public static TextBox Secret()
        {
            return new TextBox { UseSystemPasswordChar = true, Width = 190, Margin = new Padding(3, 4, 3, 4) };
        }

        public static FlowLayoutPanel Buttons(Form form, string okText, bool canCancel)
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 12, 0, 0)
            };
            var ok = new Button { Text = okText, AutoSize = true, DialogResult = DialogResult.None };
            form.AcceptButton = ok;
            if (canCancel)
            {
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                form.CancelButton = cancel;
                panel.Controls.Add(cancel);
            }
            panel.Controls.Add(ok);
            return panel;
        }
    }

    /// <summary>Asks for the current passcode. Used before closing the app.</summary>
    sealed class PasscodeForm : Form
    {
        public PasscodeForm(AppSettings settings, string prompt)
        {
            Ui.Dialog(this, "StandUp");
            TableLayoutPanel grid = Ui.Grid(1);
            TextBox box = Ui.Secret();
            box.Width = 260;
            var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Text = " " };
            FlowLayoutPanel buttons = Ui.Buttons(this, "OK", true);

            grid.Controls.Add(Ui.Label(prompt));
            grid.Controls.Add(box);
            grid.Controls.Add(error);
            grid.Controls.Add(buttons);
            Controls.Add(grid);

            ((Button)AcceptButton).Click += delegate
            {
                if (settings.CheckPasscode(box.Text))
                {
                    DialogResult = DialogResult.OK;
                    return;
                }
                error.Text = "That passcode is not right.";
                box.SelectAll();
                box.Focus();
            };
        }
    }

    /// <summary>Sets a new passcode. Asks for the old one first when there is one.</summary>
    sealed class SetPasscodeForm : Form
    {
        const int MinimumLength = 4;

        public SetPasscodeForm(AppSettings settings, string intro)
        {
            Ui.Dialog(this, "StandUp passcode");
            TableLayoutPanel grid = Ui.Grid(2);
            TextBox current = Ui.Secret(), first = Ui.Secret(), second = Ui.Secret();
            var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Text = " " };

            var introLabel = new Label { Text = intro, AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(3, 3, 3, 10) };
            grid.Controls.Add(introLabel);
            grid.SetColumnSpan(introLabel, 2);

            if (settings.HasPasscode)
            {
                grid.Controls.Add(Ui.Label("Current passcode"));
                grid.Controls.Add(current);
            }
            grid.Controls.Add(Ui.Label("New passcode"));
            grid.Controls.Add(first);
            grid.Controls.Add(Ui.Label("Type it again"));
            grid.Controls.Add(second);
            grid.Controls.Add(error);
            grid.SetColumnSpan(error, 2);

            FlowLayoutPanel buttons = Ui.Buttons(this, "Save", true);
            grid.Controls.Add(buttons);
            grid.SetColumnSpan(buttons, 2);
            Controls.Add(grid);

            ((Button)AcceptButton).Click += delegate
            {
                if (settings.HasPasscode && !settings.CheckPasscode(current.Text))
                    error.Text = "The current passcode is not right.";
                else if (first.Text.Length < MinimumLength)
                    error.Text = "Use at least " + MinimumLength + " characters.";
                else if (first.Text != second.Text)
                    error.Text = "The two new passcodes don't match.";
                else
                {
                    settings.SetPasscode(first.Text);
                    settings.Save();
                    DialogResult = DialogResult.OK;
                }
            };
        }
    }

    sealed class SettingsForm : Form
    {
        readonly AppSettings _settings;
        readonly Button _lockButton;
        readonly Label _lockStatus;
        readonly Label _photoStatus;

        public SettingsForm(AppSettings settings)
        {
            _settings = settings;
            Ui.Dialog(this, "StandUp settings");
            TableLayoutPanel grid = Ui.Grid(2);

            NumericUpDown work = Minutes(settings.WorkMinutes, 1, 480, 0);
            NumericUpDown pause = Minutes(settings.BreakMinutes, 0.5m, 60, 1);
            NumericUpDown snooze = Minutes(settings.SnoozeMinutes, 0.5m, 60, 1);

            grid.Controls.Add(Ui.Label("Work time (minutes)"));
            grid.Controls.Add(work);
            grid.Controls.Add(Ui.Label("Break time (minutes)"));
            grid.Controls.Add(pause);
            grid.Controls.Add(Ui.Label("Snooze time (minutes)"));
            grid.Controls.Add(snooze);

            var autostart = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = settings.StartWithWindows, Margin = new Padding(3, 10, 3, 3) };
            Span(grid, autostart);

            var passcode = new Button { Text = "Change passcode...", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
            passcode.Click += delegate
            {
                using (var dialog = new SetPasscodeForm(_settings, "Choose the passcode needed to close StandUp."))
                    dialog.ShowDialog(this);
            };
            Span(grid, passcode);

            _photoStatus = new Label { AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
            var addPhotos = new Button { Text = "Add pictures...", AutoSize = true };
            addPhotos.Click += delegate { AddPictures(); };
            var openPhotos = new LinkLabel { Text = "Open my pictures folder", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            openPhotos.LinkClicked += delegate
            {
                // This dialog stays on top otherwise and would hide the folder window.
                TopMost = false;
                System.IO.Directory.CreateDirectory(Paths.Photos);
                Process.Start("explorer.exe", "\"" + Paths.Photos + "\"");
            };
            Span(grid, _photoStatus);
            Span(grid, addPhotos);
            Span(grid, openPhotos);
            RefreshPhotoRow();

            _lockStatus = new Label { AutoSize = true, Margin = new Padding(3, 14, 3, 3), MaximumSize = new Size(360, 0) };
            _lockButton = new Button { AutoSize = true };
            _lockButton.Click += delegate { InstallLockScreenTimer(); };
            Span(grid, _lockStatus);
            Span(grid, _lockButton);
            RefreshLockScreenRow();

            FlowLayoutPanel buttons = Ui.Buttons(this, "Save", true);
            Span(grid, buttons);
            Controls.Add(grid);

            ((Button)AcceptButton).Click += delegate
            {
                settings.WorkMinutes = work.Value;
                settings.BreakMinutes = pause.Value;
                settings.SnoozeMinutes = snooze.Value;
                settings.StartWithWindows = autostart.Checked;
                settings.Save();
                DialogResult = DialogResult.OK;
            };
        }

        static void Span(TableLayoutPanel grid, Control control)
        {
            grid.Controls.Add(control);
            grid.SetColumnSpan(control, 2);
        }

        static NumericUpDown Minutes(decimal value, decimal min, decimal max, int decimals)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = decimals == 0 ? 1 : 0.5m,
                Value = Math.Min(max, Math.Max(min, value)),
                Width = 80
            };
        }

        void RefreshPhotoRow()
        {
            _photoStatus.Text = "Picture pool: " + PhotoPool.BuiltInCount + " built-in, " + PhotoPool.UserCount + " of yours";
        }

        void AddPictures()
        {
            using (var picker = new OpenFileDialog
            {
                Title = "Add pictures to the pool",
                Multiselect = true,
                Filter = "Pictures|" + PhotoPool.FileDialogPattern
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;

                string[] rejected = PhotoPool.AddFiles(picker.FileNames);
                RefreshPhotoRow();
                if (rejected.Length > 0)
                    MessageBox.Show(this, "Windows couldn't read these, so they weren't added:\n\n" + string.Join("\n", rejected), "StandUp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void RefreshLockScreenRow()
        {
            // Only offered while it's missing. Once on, it stays on and takes no space here.
            bool installed = LockScreenTimer.IsInstalled;
            _lockStatus.Visible = _lockButton.Visible = !installed;
            _lockStatus.Text = "Lock-screen timer is off. Turning it on needs administrator approval once.";
            _lockButton.Text = "Turn on lock-screen timer";
        }

        void InstallLockScreenTimer()
        {
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--install-lock-timer") { Verb = "runas", UseShellExecute = true }))
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        MessageBox.Show(this, "That didn't work (error " + process.ExitCode + ").", "StandUp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Win32Exception)
            {
                // The user declined the administrator prompt.
            }
            RefreshLockScreenRow();
        }
    }

    sealed class HistoryForm : Form
    {
        public HistoryForm(History history)
        {
            Ui.Dialog(this, "StandUp history");
            TableLayoutPanel grid = Ui.Grid(1);

            float scale = DeviceDpi / 96f;
            var chart = new HistoryChart(history) { Dock = DockStyle.Fill };
            var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, Visible = false, HeaderStyle = ColumnHeaderStyle.Nonclickable };

            var content = new Panel { Size = new Size((int)(680 * scale), (int)(400 * scale)) };
            content.Controls.Add(chart);
            content.Controls.Add(list);

            var ranges = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
            foreach (int days in new[] { 14, 30, 90 })
            {
                int span = days;
                ranges.Controls.Add(Choice(days + " days", days == 14, delegate { chart.ShowDays(span); }));
            }

            Action<bool, bool> showCharts = (visible, snoozes) =>
            {
                chart.Visible = visible;
                list.Visible = !visible;
                ranges.Visible = visible;
                if (visible) chart.ShowSnoozes(snoozes);
            };
            var views = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            views.Controls.Add(Choice("Breaks", true, delegate { showCharts(true, false); }));
            views.Controls.Add(Choice("Snoozes", false, delegate { showCharts(true, true); }));
            views.Controls.Add(Choice("Raw data", false, delegate { showCharts(false, false); }));

            var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(views, 0, 0);
            header.Controls.Add(ranges, 1, 0);

            list.Columns.Add("Day", 170);
            list.Columns.Add("Breaks", 65, HorizontalAlignment.Right);
            list.Columns.Add("Snoozes", 70, HorizontalAlignment.Right);
            list.Columns.Add("Minutes", 70, HorizontalAlignment.Right);

            foreach (DayStats day in history.NewestFirst.Take(365))
            {
                list.Items.Add(new ListViewItem(new[]
                {
                    day.Date.ToString("ddd, d MMM yyyy", CultureInfo.CurrentCulture),
                    day.Breaks.ToString(CultureInfo.CurrentCulture),
                    day.Snoozes.ToString(CultureInfo.CurrentCulture),
                    (day.BreakSeconds / 60.0).ToString("0.#", CultureInfo.CurrentCulture)
                }));
            }

            grid.Controls.Add(header);
            grid.Controls.Add(content);
            Controls.Add(grid);
        }

        static RadioButton Choice(string text, bool selected, Action chosen)
        {
            var choice = new RadioButton { Text = text, Appearance = Appearance.Button, AutoSize = true, Checked = selected };
            choice.CheckedChanged += delegate { if (choice.Checked) chosen(); };
            return choice;
        }
    }
}
