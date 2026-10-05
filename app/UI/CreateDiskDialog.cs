using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace OverlayDisk.UI;

internal sealed class CreateDiskDialog : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 32 };
    private readonly TextBox _directory = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _capacity = new()
    {
        Minimum = 1, Maximum = 1024, Value = 64, Width = 125, ThousandsSeparator = true
    };
    private readonly ComboBox _drive = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly CheckBox _encrypted = new()
    {
        Text = "加密磁盘数据", Checked = true, AutoSize = true, Margin = new Padding(0, 2, 0, 8)
    };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly TextBox _confirmation = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly Label _error = UiStyle.Label(string.Empty);
    private readonly Button _create = UiStyle.Button("创建磁盘", true);
    private bool _directoryCustomized;
    private bool _updatingDirectory;

    internal CreateDiskRequest? Request { get; private set; }
    internal string? Password { get; private set; }

    internal CreateDiskDialog(IEnumerable<char> reservedDriveLetters)
    {
        UiStyle.Apply(this);
        Text = "创建磁盘";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 2,
            RowCount = 10
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 10; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = UiStyle.Label("在本机创建一个可挂载为盘符的磁盘。", true);
        intro.Margin = new Padding(0, 0, 0, 22);
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);
        AddRow(layout, 1, "名称", _name);

        var location = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
        location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _directory.Margin = new Padding(0, 4, 8, 0);
        var browse = UiStyle.Button("浏览…");
        browse.MinimumSize = new Size(66, 30);
        browse.Margin = Padding.Empty;
        browse.Click += (_, _) => BrowseDirectory();
        location.Controls.Add(_directory, 0, 0);
        location.Controls.Add(browse, 1, 0);
        AddRow(layout, 2, "容器文件", location);

        var size = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        _capacity.Margin = new Padding(0, 0, 8, 0);
        size.Controls.Add(_capacity);
        var unit = UiStyle.Label("GiB（1–1024）", true);
        unit.Margin = new Padding(0, 4, 0, 0);
        size.Controls.Add(unit);
        AddRow(layout, 3, "容量", size);
        AddRow(layout, 4, "盘符", _drive);
        layout.Controls.Add(_encrypted, 1, 5);
        AddRow(layout, 6, "密码", _password);
        AddRow(layout, 7, "确认密码", _confirmation);

        var note = UiStyle.Label("加密方式在创建后固定，请妥善保管密码。\n整个磁盘保存在一个 .odv2 文件中。\nNTFS 分配单元为 4 KiB，内部逻辑对象为 4 MiB。\n请安全卸载后再复制容器文件。", true);
        note.Margin = new Padding(0, 10, 0, 0);
        layout.Controls.Add(note, 0, 8);
        layout.SetColumnSpan(note, 2);
        _error.ForeColor = Color.Firebrick;
        _error.Margin = new Padding(0, 12, 0, 8);
        layout.Controls.Add(_error, 0, 9);
        layout.SetColumnSpan(_error, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false
        };
        _create.Click += (_, _) => Confirm();
        var cancel = UiStyle.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(_create);
        buttons.Controls.Add(cancel);
        UiStyle.ArrangeDialog(this, layout, buttons, 640, intro, note, _error);
        AcceptButton = _create;
        CancelButton = cancel;

        _name.TextChanged += (_, _) => UpdateDefaultDirectory();
        _directory.TextChanged += (_, _) =>
        {
            if (!_updatingDirectory) _directoryCustomized = true;
        };
        _encrypted.CheckedChanged += (_, _) =>
        {
            _password.Enabled = _confirmation.Enabled = _encrypted.Checked;
            if (!_encrypted.Checked)
            {
                _password.Clear();
                _confirmation.Clear();
            }
        };
        _name.Text = "新磁盘";
        PopulateDriveLetters(reservedDriveLetters);
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private static void AddRow(TableLayoutPanel layout, int row, string caption, Control input)
    {
        var label = UiStyle.Label(caption);
        label.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        label.Margin = new Padding(0, 5, 10, 18);
        input.Margin = new Padding(input.Margin.Left, input.Margin.Top, input.Margin.Right, 14);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private void PopulateDriveLetters(IEnumerable<char> reservedDriveLetters)
    {
        var used = reservedDriveLetters.Select(char.ToUpperInvariant).ToHashSet();
        foreach (var path in Environment.GetLogicalDrives())
            if (path.Length != 0) used.Add(char.ToUpperInvariant(path[0]));
        for (var letter = 'D'; letter <= 'Z'; letter++)
            if (!used.Contains(letter)) _drive.Items.Add($"{letter}:");
        if (_drive.Items.Count == 0)
        {
            _error.Text = "没有可用盘符，请先释放一个盘符。";
            _create.Enabled = false;
            return;
        }
        _drive.SelectedIndex = _drive.Items.Count - 1;
    }

    private void UpdateDefaultDirectory()
    {
        if (_directoryCustomized) return;
        var safeName = string.Concat(_name.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (safeName.Length == 0) safeName = "新磁盘";
        _updatingDirectory = true;
        try
        {
            _directory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "Disks", safeName + ".odv2");
        }
        finally { _updatingDirectory = false; }
    }

    private void BrowseDirectory()
    {
        using var picker = new SaveFileDialog
        {
            Title = "选择磁盘容器的保存位置", Filter = "OverlayDisk V2 (*.odv2)|*.odv2",
            DefaultExt = "odv2", AddExtension = true, OverwritePrompt = false,
            FileName = Path.GetFileName(_directory.Text)
        };
        var parent = Path.GetDirectoryName(_directory.Text);
        if (Directory.Exists(parent)) picker.InitialDirectory = parent;
        if (picker.ShowDialog(this) == DialogResult.OK) _directory.Text = picker.FileName;
    }

    private void Confirm()
    {
        _error.Text = string.Empty;
        var name = _name.Text.Trim();
        if (name.Length == 0) { Fail("请填写磁盘名称。", _name); return; }
        if (name.IndexOfAny("\\/:*?\"<>|".ToCharArray()) >= 0) { Fail("名称不能包含文件名特殊字符。", _name); return; }
        if (string.IsNullOrWhiteSpace(_directory.Text)) { Fail("请选择数据位置。", _directory); return; }
        if (_drive.SelectedItem is not string drive) { Fail("请选择可用盘符。", _drive); return; }
        if (_encrypted.Checked && _password.Text.Length == 0) { Fail("请设置加密密码。", _password); return; }
        if (_encrypted.Checked && _password.Text != _confirmation.Text) { Fail("两次输入的密码不一致。", _confirmation); return; }

        string directory;
        try
        {
            if (!Path.IsPathFullyQualified(_directory.Text.Trim()))
            {
                Fail("请填写完整的容器文件路径。", _directory);
                return;
            }
            directory = Path.GetFullPath(_directory.Text.Trim());
            if (directory.StartsWith(@"\\", StringComparison.Ordinal)) { Fail("本地版请使用本机磁盘中的文件夹。", _directory); return; }
            if (!string.Equals(Path.GetExtension(directory), ".odv2", StringComparison.OrdinalIgnoreCase))
            {
                Fail("容器文件扩展名必须为 .odv2。", _directory);
                return;
            }
            if (File.Exists(directory) || Directory.Exists(directory))
            {
                Fail("这个位置已存在文件或文件夹，请另选名称；已有磁盘请使用导入。", _directory);
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail($"无法使用此数据位置：{ex.Message}", _directory);
            return;
        }

        Request = new CreateDiskRequest(name, directory, (ulong)_capacity.Value * 1024UL * 1024UL * 1024UL, drive[0], _encrypted.Checked);
        Password = _encrypted.Checked ? _password.Text : null;
        DialogResult = DialogResult.OK;
    }

    private void Fail(string message, Control control)
    {
        _error.Text = message;
        control.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _password.Clear();
            _confirmation.Clear();
            Password = null;
        }
        base.Dispose(disposing);
    }
}
