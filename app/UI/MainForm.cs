using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OverlayDisk.UI;

public sealed class MainForm : Form
{
    private readonly DiskController _controller;
    private readonly DataGridView _list = new();
    private readonly Label _empty = UiStyle.Label("还没有磁盘\n\n创建一个新磁盘，或导入已有的 .odv2 文件。", true);
    private readonly Label _environment = UiStyle.Label(string.Empty, true);
    private readonly Label _detailTitle = UiStyle.Label("选择一个磁盘");
    private readonly Label _detailSummary = UiStyle.Label("可查看容量、加密和挂载状态。", true);
    private readonly TextBox _detailDirectory = new();
    private readonly Label _detailNote = UiStyle.Label(string.Empty, true);
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "就绪" };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 100 };
    private readonly Button _create = UiStyle.Button("创建磁盘", true);
    private readonly Button _import = UiStyle.Button("导入");
    private readonly Button _mount = UiStyle.Button("挂载");
    private readonly Button _unmount = UiStyle.Button("安全卸载");
    private readonly Button _open = UiStyle.Button("打开");
    private readonly Button _compact = UiStyle.Button("整理空间");
    private readonly Button _settings = UiStyle.Button("设置");
    private readonly Button _installDriver = UiStyle.Button("安装驱动");
    private readonly Button _elevate = UiStyle.Button("以管理员重新打开");
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ToolStripMenuItem _trayUnmount;
    private readonly ToolStripMenuItem _trayExit;
    private bool _busy;
    private bool _exiting;
    private bool _allowClose;
    private bool _trayHintShown;
    private bool _refreshing;

    public MainForm(DiskController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        UiStyle.Apply(this);
        Text = "OverlayDisk";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 660);
        MinimumSize = new Size(1000, 600);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 12),
            ColumnCount = 1, RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 18) };
        var title = UiStyle.Label("OverlayDisk  ·  V2 本地版");
        title.Font = new Font(Font.FontFamily, 19F, FontStyle.Bold);
        header.Controls.Add(title);
        header.Controls.Add(_environment);
        layout.Controls.Add(header, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 16) };
        foreach (var button in new[] { _create, _import, _mount, _unmount, _open, _compact, _settings, _installDriver, _elevate })
        {
            button.Margin = new Padding(0, 0, 8, 8);
            actions.Controls.Add(button);
        }
        layout.Controls.Add(actions, 0, 1);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Size = new Size(1052, 430), SplitterDistance = 715,
            Panel1MinSize = 570, Panel2MinSize = 270, SplitterWidth = 14,
            FixedPanel = FixedPanel.Panel2, Margin = Padding.Empty
        };
        ConfigureList();
        split.Panel1.Controls.Add(_list);
        _empty.AutoSize = false;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.Dock = DockStyle.Fill;
        _empty.BackColor = Color.White;
        split.Panel1.Controls.Add(_empty);
        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(20),
            ColumnCount = 1, RowCount = 6, AutoScroll = true
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _detailTitle.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        _detailTitle.Dock = DockStyle.Fill;
        _detailTitle.Margin = new Padding(0, 0, 0, 16);
        detail.Controls.Add(_detailTitle, 0, 0);
        _detailSummary.Dock = DockStyle.Fill;
        _detailSummary.Margin = new Padding(0, 0, 0, 22);
        detail.Controls.Add(_detailSummary, 0, 1);
        detail.Controls.Add(UiStyle.Label("容器文件", true), 0, 2);
        _detailDirectory.ReadOnly = true;
        _detailDirectory.Multiline = true;
        _detailDirectory.Dock = DockStyle.Fill;
        _detailDirectory.BackColor = Color.White;
        _detailDirectory.BorderStyle = BorderStyle.None;
        _detailDirectory.ScrollBars = ScrollBars.Vertical;
        _detailDirectory.AccessibleName = "磁盘容器文件";
        detail.Controls.Add(_detailDirectory, 0, 3);
        _detailNote.Dock = DockStyle.Fill;
        detail.Controls.Add(_detailNote, 0, 4);
        split.Panel2.Controls.Add(detail);
        layout.Controls.Add(split, 0, 2);

        var footer = UiStyle.Label("关闭窗口会收起到系统托盘；从托盘退出时会先安全卸载磁盘。", true);
        footer.Margin = new Padding(0, 14, 0, 0);
        layout.Controls.Add(footer, 0, 3);
        var statusBar = new StatusStrip { SizingGrip = true, BackColor = UiStyle.Background };
        statusBar.Items.Add(_status);
        statusBar.Items.Add(_progress);
        Controls.Add(layout);
        Controls.Add(statusBar);

        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("打开 OverlayDisk", null, (_, _) => ShowMainWindow());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayUnmount = new ToolStripMenuItem("安全卸载全部磁盘");
        _trayUnmount.Click += async (_, _) => await RunOperationAsync("正在安全卸载全部磁盘…", "所有磁盘已安全卸载。", () => _controller.UnmountAllAsync());
        _trayMenu.Items.Add(_trayUnmount);
        _trayExit = new ToolStripMenuItem("退出");
        _trayExit.Click += async (_, _) => await RequestExitAsync();
        _trayMenu.Items.Add(_trayExit);
        _trayMenu.Opening += (_, _) => UpdateActions();
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application, Text = "OverlayDisk · 本地虚拟硬盘",
            ContextMenuStrip = _trayMenu, Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        _create.Click += async (_, _) => await CreateDiskAsync();
        _import.Click += async (_, _) => await ImportDiskAsync();
        _mount.Click += async (_, _) => await MountSelectedAsync();
        _unmount.Click += async (_, _) =>
        {
            var disk = SelectedDisk;
            if (disk is not null)
                await RunOperationAsync($"正在安全卸载“{disk.Name}”…", "磁盘已安全卸载。", () => _controller.UnmountAsync(disk));
        };
        _open.Click += (_, _) => OpenSelected();
        _compact.Click += async (_, _) => await CompactSelectedAsync();
        _settings.Click += async (_, _) => await EditSettingsAsync();
        _installDriver.Click += async (_, _) => await RunOperationAsync("正在准备磁盘驱动…", "驱动准备操作已完成。", () => _controller.InstallDriverAsync());
        _elevate.Click += async (_, _) => await RestartElevatedAsync();
        _list.SelectionChanged += (_, _) => { if (!_refreshing) UpdateSelection(); };
        _list.CellDoubleClick += async (_, args) =>
        {
            if (args.RowIndex < 0 || _busy) return;
            if (SelectedDisk?.Mounted == true) OpenSelected();
            else if (_mount.Enabled) await MountSelectedAsync();
        };
        _controller.Changed += ControllerChanged;
        Shown += (_, _) => RefreshView();
        FormClosing += OnFormClosing;
        RefreshView();
    }

    private DiskEntry? SelectedDisk
    {
        get
        {
            if (_list.SelectedRows.Count == 0) return null;
            var id = _list.SelectedRows[0].Tag as string;
            return _controller.Disks.FirstOrDefault(disk => disk.Id == id);
        }
    }

    private void ConfigureList()
    {
        _list.Dock = DockStyle.Fill;
        _list.BackgroundColor = Color.White;
        _list.BorderStyle = BorderStyle.None;
        _list.ReadOnly = true;
        _list.AllowUserToAddRows = false;
        _list.AllowUserToDeleteRows = false;
        _list.AllowUserToResizeRows = false;
        _list.MultiSelect = false;
        _list.RowHeadersVisible = false;
        _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _list.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _list.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _list.GridColor = UiStyle.Background;
        _list.EnableHeadersVisualStyles = false;
        _list.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(234, 239, 246);
        _list.ColumnHeadersDefaultCellStyle.ForeColor = UiStyle.Text;
        _list.ColumnHeadersDefaultCellStyle.Padding = new Padding(7);
        _list.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _list.DefaultCellStyle.Padding = new Padding(7);
        _list.DefaultCellStyle.SelectionBackColor = Color.FromArgb(223, 234, 251);
        _list.DefaultCellStyle.SelectionForeColor = UiStyle.Text;
        _list.RowTemplate.Height = 43;
        foreach (var (name, label, weight) in new[]
        {
            ("Name", "名称", 140F), ("Drive", "盘符", 55F), ("Capacity", "容量", 80F),
            ("Encrypted", "加密", 65F), ("Status", "状态", 140F)
        })
        {
            _list.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = label, FillWeight = weight,
                SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = 56
            });
        }
        _list.AccessibleName = "虚拟磁盘列表";
    }

    private void ControllerChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke((Action)RefreshView); }
            catch (InvalidOperationException) { }
            return;
        }
        RefreshView();
    }

    private void RefreshView()
    {
        if (IsDisposed || Disposing) return;
        var selectedId = SelectedDisk?.Id;
        _refreshing = true;
        try
        {
            _list.Rows.Clear();
            foreach (var disk in _controller.Disks)
            {
                var index = _list.Rows.Add(disk.Name, $"{disk.DriveLetter}:", FormatCapacity(disk.CapacityBytes), disk.Encrypted ? "已加密" : "未加密", disk.Status);
                _list.Rows[index].Tag = disk.Id;
            }
            _list.ClearSelection();
            if (_list.Rows.Count != 0)
            {
                var row = _list.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => (string?)row.Tag == selectedId) ?? _list.Rows[0];
                _list.CurrentCell = row.Cells[0];
                row.Selected = true;
            }
            _empty.Visible = _list.Rows.Count == 0;
        }
        finally { _refreshing = false; }
        _environment.Text = _controller.IsAdministrator
            ? (_controller.DriverAvailable ? "驱动已就绪，可以创建和挂载磁盘。数据保存在本机。" : "尚未安装磁盘驱动，请先安装驱动。")
            : "当前可导入和查看磁盘。创建或挂载前，请以管理员权限重新打开。";
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var disk = SelectedDisk;
        _detailTitle.Text = disk?.Name ?? "选择一个磁盘";
        _detailSummary.Text = disk is null
            ? "可查看容量、加密和挂载状态。"
            : $"盘符  {disk.DriveLetter}:\n\n容量  {FormatCapacity(disk.CapacityBytes)}\n\n加密  {(disk.Encrypted ? "已启用" : "未启用")}\n\n状态  {disk.Status}";
        _detailDirectory.Text = disk?.ContainerPath ?? string.Empty;
        _detailNote.Text = disk is null ? string.Empty : "单文件容器 · 内部 4 MiB 对象\nNTFS · 4 KiB 分配单元\n\n启用写缓存，请安全卸载后再复制容器。";
        UpdateActions();
    }

    private void UpdateActions()
    {
        var disk = SelectedDisk;
        var available = !_busy && !_exiting;
        _create.Enabled = available && _controller.IsAdministrator && _controller.DriverAvailable;
        _import.Enabled = available;
        _mount.Enabled = available && disk is { Mounted: false } && _controller.IsAdministrator && _controller.DriverAvailable;
        _unmount.Enabled = available && disk is { Mounted: true };
        _open.Enabled = available && disk is { Mounted: true };
        _compact.Enabled = available && disk is { Mounted: false };
        _settings.Enabled = available && disk is { Mounted: false };
        _installDriver.Visible = !_controller.DriverAvailable;
        _installDriver.Enabled = available;
        _elevate.Visible = !_controller.IsAdministrator;
        _elevate.Enabled = available;
        if (_trayUnmount is not null) _trayUnmount.Enabled = available && _controller.Disks.Any(d => d.Mounted);
        if (_trayExit is not null) _trayExit.Enabled = available;
    }

    private async Task CreateDiskAsync()
    {
        if (_busy) return;
        CreateDiskRequest request;
        string? password;
        using (var dialog = new CreateDiskDialog(_controller.Disks.Select(disk => disk.DriveLetter)))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Request is null) return;
            request = dialog.Request;
            password = dialog.Password;
        }
        await RunOperationAsync($"正在创建“{request.Name}”，请稍候…", "磁盘已创建。", () => _controller.CreateAsync(request, password));
    }

    private async Task ImportDiskAsync()
    {
        if (_busy) return;
        using var picker = new OpenFileDialog
        {
            Title = "选择 OverlayDisk V2 容器", Filter = "OverlayDisk V2 (*.odv2)|*.odv2",
            CheckFileExists = true, Multiselect = false
        };
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "Disks");
        if (Directory.Exists(defaultPath)) picker.InitialDirectory = defaultPath;
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync("正在导入磁盘…", "磁盘已导入。", () => _controller.ImportAsync(picker.FileName));
    }

    private async Task MountSelectedAsync()
    {
        var disk = SelectedDisk;
        if (_busy || disk is null || disk.Mounted) return;
        if (!TryGetPassword(disk, "解锁并挂载", out var password)) return;
        await RunOperationAsync($"正在挂载“{disk.Name}”…", $"磁盘已挂载到 {disk.DriveLetter}:。", () => _controller.MountAsync(disk, password));
    }

    private async Task CompactSelectedAsync()
    {
        var disk = SelectedDisk;
        if (_busy || disk is null || disk.Mounted) return;
        if (!TryGetPassword(disk, "解锁并整理", out var password)) return;
        await RunOperationAsync($"正在整理“{disk.Name}”占用的空间，请保持程序运行…", "空间整理完成。", () => _controller.CompactAsync(disk, password));
    }

    private async Task EditSettingsAsync()
    {
        var disk = SelectedDisk;
        if (_busy || disk is null || disk.Mounted) return;
        using var dialog = new SettingsDialog(disk, _controller.Disks.Where(other => other.Id != disk.Id).Select(other => other.DriveLetter));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunOperationAsync("正在保存磁盘设置…", "磁盘设置已保存。", () => _controller.UpdateSettingsAsync(disk, dialog.DiskName, dialog.DriveLetter));
    }

    private bool TryGetPassword(DiskEntry disk, string actionText, out string? password)
    {
        password = null;
        if (!disk.Encrypted) return true;
        using var dialog = new PasswordDialog(disk.Name, actionText);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        password = dialog.Password;
        return true;
    }

    private void OpenSelected()
    {
        var disk = SelectedDisk;
        if (_busy || disk is null || !disk.Mounted) return;
        try { _controller.OpenFolder(disk); }
        catch (Exception ex) { ShowError("无法打开磁盘", ex); }
    }

    private async Task RunOperationAsync(string progress, string completed, Func<Task> operation)
    {
        if (_busy || _exiting) return;
        SetBusy(true, progress);
        try
        {
            await operation();
            _status.Text = completed;
        }
        catch (Exception ex)
        {
            _status.Text = "操作未完成。";
            ShowError("操作未完成", ex);
        }
        finally
        {
            SetBusy(false);
            RefreshView();
        }
    }

    private async Task RestartElevatedAsync()
    {
        if (_busy || _exiting) return;
        SetBusy(true, "正在以管理员权限重新打开…");
        try
        {
            await _controller.RestartElevatedAsync();
            _allowClose = true;
            _tray.Visible = false;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "尚未切换到管理员权限。";
            ShowError("未能重新打开", ex);
            SetBusy(false);
        }
    }

    private async Task RequestExitAsync()
    {
        if (_exiting) return;
        if (_busy)
        {
            ShowMainWindow();
            MessageBox.Show(this, "请等待当前操作完成，再退出程序。", "正在处理磁盘", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _exiting = true;
        SetBusy(true, "正在安全卸载磁盘并退出…");
        try
        {
            await _controller.UnmountAllAsync();
            if (_controller.Disks.Any(disk => disk.Mounted))
                throw new InvalidOperationException("仍有磁盘处于挂载状态。请关闭正在使用这些磁盘的文件和程序，再重试。");
            _allowClose = true;
            _tray.Visible = false;
            Close();
        }
        catch (Exception ex)
        {
            _exiting = false;
            _status.Text = "退出已取消：磁盘尚未全部安全卸载。";
            SetBusy(false);
            ShowMainWindow();
            ShowError("无法安全卸载，程序继续运行", ex);
        }
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        _progress.Visible = busy;
        _list.Enabled = !busy;
        if (text is not null) _status.Text = text;
        UpdateActions();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (e.CloseReason == CloseReason.UserClosing)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2500, "OverlayDisk 仍在运行", "可从系统托盘重新打开窗口，或安全卸载磁盘后退出。", ToolTipIcon.Info);
            }
        }
        else
        {
            BeginInvoke((Action)(async () => await RequestExitAsync()));
        }
    }

    private void ShowMainWindow()
    {
        if (IsDisposed) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ShowError(string title, Exception error)
    {
        ShowMainWindow();
        MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string FormatCapacity(ulong bytes)
    {
        const double gib = 1024.0 * 1024 * 1024;
        return bytes >= (ulong)gib ? $"{bytes / gib:0.##} GiB" : $"{bytes / (1024.0 * 1024):0.##} MiB";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.Changed -= ControllerChanged;
            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
