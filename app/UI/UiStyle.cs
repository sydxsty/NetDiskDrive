using System;
using System.Drawing;
using System.Windows.Forms;

namespace OverlayDisk.UI;

internal static class UiStyle
{
    internal static readonly Color Background = Color.FromArgb(245, 247, 250);
    internal static readonly Color Text = Color.FromArgb(31, 41, 55);
    internal static readonly Color MutedText = Color.FromArgb(90, 103, 120);
    internal static readonly Color Accent = Color.FromArgb(35, 88, 173);

    internal static void Apply(Form form)
    {
        form.Font = new Font("Microsoft YaHei UI", 9F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Icon = SystemIcons.Application;
        form.StartPosition = FormStartPosition.CenterParent;
    }

    internal static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 34),
            Padding = new Padding(12, 3, 12, 3),
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Text,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(209, 216, 224);
        button.EnabledChanged += (_, _) =>
        {
            button.BackColor = button.Enabled ? (primary ? Accent : Color.White) : Color.FromArgb(232, 236, 241);
            button.ForeColor = button.Enabled ? (primary ? Color.White : Text) : Color.FromArgb(145, 155, 169);
            button.FlatAppearance.BorderColor = button.Enabled
                ? (primary ? Accent : Color.FromArgb(209, 216, 224))
                : Color.FromArgb(220, 226, 234);
        };
        return button;
    }

    internal static Label Label(string text, bool muted = false) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = muted ? MutedText : Text,
        Margin = new Padding(0, 0, 0, 6)
    };

    internal static void ArrangeDialog(Form form, TableLayoutPanel content, FlowLayoutPanel actions,
        int logicalWidth, params Label[] wrappedLabels)
    {
        // Measure actual control sizes after the window has its monitor's DPI.
        // A separate footer keeps actions reachable even on a short display.
        form.ClientSize = new Size(logicalWidth, 240);
        content.Dock = DockStyle.Top;
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Padding = Padding.Empty;
        content.Margin = Padding.Empty;
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        viewport.Controls.Add(content);
        actions.Dock = DockStyle.Fill;
        actions.AutoSize = true;
        actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        actions.Margin = new Padding(0, 16, 0, 0);
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24),
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(viewport, 0, 0);
        page.Controls.Add(actions, 0, 1);
        form.Controls.Add(page);

        bool arranging = false;
        void Arrange()
        {
            if (arranging || !form.IsHandleCreated || form.IsDisposed) return;
            arranging = true;
            try
            {
                var screen = Screen.FromControl(form).WorkingArea;
                int nonClientWidth = form.Width - form.ClientSize.Width;
                int nonClientHeight = form.Height - form.ClientSize.Height;
                int width = Math.Min((int)Math.Ceiling(logicalWidth * form.DeviceDpi / 96.0), screen.Width - nonClientWidth - 32);
                form.ClientSize = new Size(width, form.ClientSize.Height);
                page.PerformLayout();
                int contentWidth = Math.Max(100, viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
                foreach (var label in wrappedLabels)
                    label.MaximumSize = new Size(Math.Max(1, contentWidth - label.Margin.Horizontal), 0);
                content.PerformLayout();
                int desiredHeight = content.GetPreferredSize(new Size(contentWidth, 0)).Height
                    + actions.GetPreferredSize(new Size(contentWidth, 0)).Height
                    + actions.Margin.Vertical + page.Padding.Vertical + 4;
                int maximumHeight = Math.Max(180, screen.Height - nonClientHeight - 32);
                form.ClientSize = new Size(width, Math.Min(desiredHeight, maximumHeight));
                page.PerformLayout();
            }
            finally { arranging = false; }
        }
        form.Load += (_, _) => Arrange();
        form.DpiChanged += (_, _) => form.BeginInvoke((Action)Arrange);
        foreach (var label in wrappedLabels)
            label.TextChanged += (_, _) => Arrange();
    }
}
