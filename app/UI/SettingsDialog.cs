using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OverlayDisk.UI;

internal sealed class SettingsDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 32 };
    private readonly ComboBox _drive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly Label _error = UiStyle.Label(string.Empty);
    private readonly Button _save = UiStyle.Button("保存设置", true);

    internal string DiskName { get; private set; } = string.Empty;
    internal char DriveLetter { get; private set; }

    internal SettingsDialog(DiskEntry disk, IEnumerable<char> otherDiskDriveLetters)
    {
        UiStyle.Apply(this);
        Text = "磁盘设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24),
            ColumnCount = 2, RowCount = 7
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 7; row++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = UiStyle.Label("修改未挂载磁盘的名称和盘符。", true);
        intro.Margin = new Padding(0, 0, 0, 20);
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);
        _name.Text = disk.Name;
        _name.AccessibleName = "磁盘名称";
        _drive.AccessibleName = "磁盘盘符";
        AddRow(layout, 1, "名称", _name);
        AddRow(layout, 2, "盘符", _drive);
        double gib = disk.CapacityBytes / (1024.0 * 1024 * 1024);
        string capacity = gib >= 1 ? $"{gib:0.##} GiB" : $"{disk.CapacityBytes / (1024.0 * 1024):0.##} MiB";
        AddRow(layout, 3, "容量", UiStyle.Label(capacity + "（固定）", true));
        AddRow(layout, 4, "加密", UiStyle.Label((disk.Encrypted ? "已启用" : "未启用") + "（固定）", true));
        var note = UiStyle.Label("容量和加密方式在创建后固定。\n名称修改不会移动磁盘的数据文件夹。", true);
        note.Margin = new Padding(0, 4, 0, 0);
        layout.Controls.Add(note, 0, 5);
        layout.SetColumnSpan(note, 2);
        _error.ForeColor = Color.Firebrick;
        _error.Margin = new Padding(0, 12, 0, 8);
        layout.Controls.Add(_error, 0, 6);
        layout.SetColumnSpan(_error, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        var cancel = UiStyle.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        _save.Click += (_, _) => Save();
        actions.Controls.Add(_save);
        actions.Controls.Add(cancel);
        UiStyle.ArrangeDialog(this, layout, actions, 490, intro, note, _error);
        AcceptButton = _save;
        CancelButton = cancel;

        PopulateLetters(disk.DriveLetter, otherDiskDriveLetters);
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private static void AddRow(TableLayoutPanel layout, int row, string text, Control input)
    {
        var label = UiStyle.Label(text);
        label.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        label.Margin = new Padding(0, 4, 10, 15);
        input.Margin = new Padding(0, 0, 0, 15);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private void PopulateLetters(char currentLetter, IEnumerable<char> otherDiskDriveLetters)
    {
        currentLetter = char.ToUpperInvariant(currentLetter);
        var occupied = otherDiskDriveLetters.Select(char.ToUpperInvariant).ToHashSet();
        foreach (var path in Environment.GetLogicalDrives())
            if (path.Length > 0) occupied.Add(char.ToUpperInvariant(path[0]));
        for (char letter = 'D'; letter <= 'Z'; letter++)
            if (!occupied.Contains(letter)) _drive.Items.Add($"{letter}:");
        if (_drive.Items.Count == 0)
        {
            _error.Text = "没有可用盘符，请先释放一个盘符。";
            _save.Enabled = false;
            return;
        }
        int currentIndex = _drive.Items.IndexOf($"{currentLetter}:");
        _drive.SelectedIndex = currentIndex >= 0 ? currentIndex : _drive.Items.Count - 1;
        if (currentIndex < 0) _error.Text = $"原盘符 {currentLetter}: 已被占用，请确认新的盘符。";
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || name.Any(char.IsControl) || name.IndexOfAny("\\/:*?\"<>|".ToCharArray()) >= 0)
        {
            _error.Text = "名称应为 1–32 个字符，且不能包含文件名特殊字符。";
            _name.Focus();
            return;
        }
        if (_drive.SelectedItem is not string letter)
        {
            _error.Text = "请选择一个可用盘符。";
            _drive.Focus();
            return;
        }
        DiskName = name;
        DriveLetter = letter[0];
        DialogResult = DialogResult.OK;
    }
}
