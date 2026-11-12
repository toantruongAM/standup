using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
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
            return new TableLayoutPanel
            {
                ColumnCount = columns,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(14),
                Dock = DockStyle.Fill
            };
        }

        public static Label Label(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 7) };
        }

        public static TextBox Secret()
        {
            return new TextBox { UseSystemPasswordChar = true, Width = 190, Margin = new Padding(3, 4, 3, 4) };
        }

        public static void Span(TableLayoutPanel grid, Control control)
        {
            grid.Controls.Add(control);
            grid.SetColumnSpan(control, grid.ColumnCount);
        }

        public static NumericUpDown Minutes(decimal value, decimal min, decimal max, int decimals)
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
                var cancel = new Button { Text = L10n.T("common.cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
                form.CancelButton = cancel;
                panel.Controls.Add(cancel);
            }
            panel.Controls.Add(ok);
            return panel;
        }
    }

    /// <summary>Asks for the current passcode. Used before Settings and before closing the app.</summary>
    sealed class PasscodeForm : Form
    {
        public PasscodeForm(AppSettings settings, string prompt)
        {
            Ui.Dialog(this, "StandUp");
            TableLayoutPanel grid = Ui.Grid(1);
            TextBox box = Ui.Secret();
            box.Width = 260;
            var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Text = " " };
            FlowLayoutPanel buttons = Ui.Buttons(this, L10n.T("common.ok"), true);

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
                error.Text = L10n.T("passcode.wrong");
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
            Ui.Dialog(this, L10n.T("passcode.title"));
            TableLayoutPanel grid = Ui.Grid(2);
            TextBox current = Ui.Secret(), first = Ui.Secret(), second = Ui.Secret();
            var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Text = " " };

            var introLabel = new Label { Text = intro, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(3, 3, 3, 10) };
            Ui.Span(grid, introLabel);

            if (settings.HasPasscode)
            {
                grid.Controls.Add(Ui.Label(L10n.T("passcode.current")));
                grid.Controls.Add(current);
            }
            grid.Controls.Add(Ui.Label(L10n.T("passcode.new")));
            grid.Controls.Add(first);
            grid.Controls.Add(Ui.Label(L10n.T("passcode.again")));
            grid.Controls.Add(second);
            Ui.Span(grid, error);
            Ui.Span(grid, Ui.Buttons(this, L10n.T("common.save"), true));
            Controls.Add(grid);

            ((Button)AcceptButton).Click += delegate
            {
                if (settings.HasPasscode && !settings.CheckPasscode(current.Text))
                    error.Text = L10n.T("passcode.currentWrong");
                else if (first.Text.Length < MinimumLength)
                    error.Text = L10n.T("passcode.tooShort", MinimumLength);
                else if (first.Text != second.Text)
                    error.Text = L10n.T("passcode.mismatch");
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
        public SettingsForm(AppSettings settings)
        {
            _settings = settings;
            Ui.Dialog(this, L10n.T("settings.title"));
            TableLayoutPanel grid = Ui.Grid(2);

            NumericUpDown shortEvery = Ui.Minutes(settings.WorkMinutes, 1, 480, 0);
            NumericUpDown shortLength = Ui.Minutes(settings.BreakMinutes, 0.5m, 60, 1);
            NumericUpDown longEvery = Ui.Minutes(settings.LongEveryMinutes, 1, 480, 0);
            NumericUpDown longLength = Ui.Minutes(settings.LongBreakMinutes, 0.5m, 60, 1);
            NumericUpDown snooze = Ui.Minutes(settings.SnoozeMinutes, 0.5m, 60, 1);

            // Three groups, each with a bold heading and a rule above it, so they're easy to tell apart.
            Group(grid, L10n.T("settings.shortGroup"), false);
            Row(grid, L10n.T("settings.every"), shortEvery);
            Row(grid, L10n.T("settings.length"), shortLength);
            Group(grid, L10n.T("settings.longGroup"), true);
            Row(grid, L10n.T("settings.every"), longEvery);
            Row(grid, L10n.T("settings.length"), longLength);
            Group(grid, L10n.T("settings.snoozeGroup"), true);
            Row(grid, L10n.T("settings.length"), snooze);
            Group(grid, null, true);

            var autostart = new CheckBox { Text = L10n.T("settings.autostartWindows"), AutoSize = true, Checked = settings.StartWithWindows, Margin = new Padding(3, 10, 3, 3) };
            Ui.Span(grid, autostart);

            var buttonsRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            var passcode = new Button { Text = L10n.T("settings.changePasscode"), AutoSize = true };
            passcode.Click += delegate
            {
                using (var dialog = new SetPasscodeForm(_settings, L10n.T("passcode.changeIntro")))
                    dialog.ShowDialog(this);
            };
            var advanced = new Button { Text = L10n.T("settings.advanced"), AutoSize = true };
            advanced.Click += delegate { OpenAdvanced(); };
            buttonsRow.Controls.Add(passcode);
            buttonsRow.Controls.Add(advanced);
            Ui.Span(grid, buttonsRow);

            Ui.Span(grid, Ui.Buttons(this, L10n.T("common.save"), true));
            Controls.Add(grid);

            ((Button)AcceptButton).Click += delegate
            {
                settings.WorkMinutes = shortEvery.Value;
                settings.BreakMinutes = shortLength.Value;
                settings.LongEveryMinutes = longEvery.Value;
                settings.LongBreakMinutes = longLength.Value;
                settings.SnoozeMinutes = snooze.Value;
                settings.StartWithWindows = autostart.Checked;
                settings.Save();
                DialogResult = DialogResult.OK;
            };
        }

        /// <summary>Set when Advanced settings switched language; Settings then closes to reopen in it.</summary>
        public bool LanguageChanged { get; private set; }

        void OpenAdvanced()
        {
            using (var advanced = new AdvancedForm(_settings))
            {
                if (advanced.ShowDialog(this) != DialogResult.OK || !advanced.LanguageChanged) return;
            }
            LanguageChanged = true;
            DialogResult = DialogResult.Retry;
        }

        /// <summary>A thin rule (unless first) and a bold heading across both columns.</summary>
        static void Group(TableLayoutPanel grid, string heading, bool rule)
        {
            if (rule)
            {
                var line = new Label
                {
                    AutoSize = false,
                    Height = 1,
                    BackColor = SystemColors.ControlDark,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right,
                    Margin = new Padding(3, 10, 3, heading == null ? 0 : 6)
                };
                Ui.Span(grid, line);
            }
            if (heading == null) return;
            var label = new Label
            {
                Text = heading,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Margin = new Padding(3, rule ? 2 : 0, 3, 2)
            };
            Ui.Span(grid, label);
        }

        static void Row(TableLayoutPanel grid, string text, Control input)
        {
            Label label = Ui.Label(text);
            label.Margin = new Padding(16, 7, 12, 7);
            grid.Controls.Add(label);
            grid.Controls.Add(input);
        }
    }

    sealed class AdvancedForm : Form
    {
        readonly Label _photoStatus;

        public AdvancedForm(AppSettings settings)
        {
            Ui.Dialog(this, L10n.T("advanced.title"));
            TableLayoutPanel grid = Ui.Grid(2);

            NumericUpDown idle = Ui.Minutes(settings.IdleMinutes, 1, 60, 0);
            grid.Controls.Add(Ui.Label(L10n.T("advanced.idle")));
            grid.Controls.Add(idle);

            NumericUpDown snoozes = Ui.Minutes(settings.MaxSnoozes, 0, 10, 0);
            grid.Controls.Add(Ui.Label(L10n.T("advanced.snoozes")));
            grid.Controls.Add(snoozes);

            var language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            foreach (string code in L10n.Languages) language.Items.Add(L10n.NameOf(code));
            language.SelectedIndex = Math.Max(0, Array.IndexOf(L10n.Languages, settings.Language));
            grid.Controls.Add(Ui.Label(L10n.T("advanced.language")));
            grid.Controls.Add(language);

            _photoStatus = new Label { AutoSize = true, Margin = new Padding(3, 16, 3, 3) };
            var addPhotos = new Button { Text = L10n.T("advanced.addPictures"), AutoSize = true };
            addPhotos.Click += delegate { AddPictures(); };
            var openPhotos = new LinkLabel { Text = L10n.T("advanced.openFolder"), AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            openPhotos.LinkClicked += delegate
            {
                // This dialog stays on top otherwise and would hide the folder window.
                TopMost = false;
                System.IO.Directory.CreateDirectory(Paths.Photos);
                Process.Start("explorer.exe", "\"" + Paths.Photos + "\"");
            };
            Ui.Span(grid, _photoStatus);
            Ui.Span(grid, addPhotos);
            Ui.Span(grid, openPhotos);
            RefreshPhotoRow();

            Ui.Span(grid, Ui.Buttons(this, L10n.T("common.save"), true));
            Controls.Add(grid);

            ((Button)AcceptButton).Click += delegate
            {
                string chosen = L10n.Languages[language.SelectedIndex];
                LanguageChanged = chosen != settings.Language;
                settings.IdleMinutes = idle.Value;
                settings.MaxSnoozes = (int)snoozes.Value;
                settings.Language = chosen;
                settings.Save();
                L10n.Language = chosen;
                DialogResult = DialogResult.OK;
            };
        }

        public bool LanguageChanged { get; private set; }

        void RefreshPhotoRow()
        {
            _photoStatus.Text = L10n.T("advanced.pool", PhotoPool.BuiltInCount, PhotoPool.UserCount);
        }

        void AddPictures()
        {
            using (var picker = new OpenFileDialog
            {
                Title = L10n.T("advanced.pickerTitle"),
                Multiselect = true,
                Filter = L10n.T("advanced.pickerFilter") + "|" + PhotoPool.FileDialogPattern
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;

                string[] rejected = PhotoPool.AddFiles(picker.FileNames);
                RefreshPhotoRow();
                if (rejected.Length > 0)
                    MessageBox.Show(this, L10n.T("advanced.rejected") + "\n\n" + string.Join("\n", rejected), "StandUp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    sealed class HistoryForm : Form
    {
        public HistoryForm(History history)
        {
            Ui.Dialog(this, L10n.T("history.title"));
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
                ranges.Controls.Add(Choice(L10n.T("history.days", days), days == 14, delegate { chart.ShowDays(span); }));
            }

            Action<bool, bool> showCharts = (visible, snoozes) =>
            {
                chart.Visible = visible;
                list.Visible = !visible;
                ranges.Visible = visible;
                if (visible) chart.ShowSnoozes(snoozes);
            };
            var views = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            views.Controls.Add(Choice(L10n.T("history.breaks"), true, delegate { showCharts(true, false); }));
            views.Controls.Add(Choice(L10n.T("history.snoozes"), false, delegate { showCharts(true, true); }));
            views.Controls.Add(Choice(L10n.T("history.raw"), false, delegate { showCharts(false, false); }));

            var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(views, 0, 0);
            header.Controls.Add(ranges, 1, 0);

            list.Columns.Add(L10n.T("history.day"), 200);
            list.Columns.Add(L10n.T("history.colBreaks"), 90, HorizontalAlignment.Right);
            list.Columns.Add(L10n.T("history.colSnoozes"), 90, HorizontalAlignment.Right);
            list.Columns.Add(L10n.T("history.colMinutes"), 90, HorizontalAlignment.Right);

            foreach (DayStats day in history.NewestFirst.Take(365))
            {
                list.Items.Add(new ListViewItem(new[]
                {
                    day.Date.ToString("ddd, d MMM yyyy", L10n.Culture),
                    day.Breaks.ToString(L10n.Culture),
                    day.Snoozes.ToString(L10n.Culture),
                    (day.BreakSeconds / 60.0).ToString("0.#", L10n.Culture)
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
