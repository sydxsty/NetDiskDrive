using System;
using System.Drawing;
using System.Windows.Forms;

namespace OverlayDisk.UI;

internal sealed class PasswordDialog : Form
{
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly Label _error = UiStyle.Label(string.Empty);

    internal string Password { get; private set; } = string.Empty;

    internal PasswordDialog(string diskName, string actionText = "解锁并挂载")
    {
        UiStyle.Apply(this);
        Text = "解锁磁盘";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = UiStyle.Label($"输入“{diskName}”的密码");
        title.Margin = new Padding(0, 0, 0, 16);
        layout.Controls.Add(title, 0, 0);
        _password.Margin = new Padding(0, 0, 0, 8);
        _password.AccessibleName = "磁盘密码";
        layout.Controls.Add(_password, 0, 1);
        _error.ForeColor = Color.Firebrick;
        layout.Controls.Add(_error, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false
        };
        var confirm = UiStyle.Button(actionText, true);
        var cancel = UiStyle.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        confirm.Click += (_, _) =>
        {
            if (_password.Text.Length == 0)
            {
                _error.Text = "请输入密码。";
                _password.Focus();
                return;
            }
            Password = _password.Text;
            DialogResult = DialogResult.OK;
        };
        buttons.Controls.Add(confirm);
        buttons.Controls.Add(cancel);
        UiStyle.ArrangeDialog(this, layout, buttons, 440, title, _error);
        AcceptButton = confirm;
        CancelButton = cancel;
        Shown += (_, _) => _password.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _password.Clear();
            Password = string.Empty;
        }
        base.Dispose(disposing);
    }
}
