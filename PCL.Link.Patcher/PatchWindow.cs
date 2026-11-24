using System.Drawing.Drawing2D;

namespace PclLink.Patching;

public sealed class PatchWindow : Form
{
    private readonly TextBox _input = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(244, 247, 252), BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 10), WordWrap = true };
    private readonly Label _status = new() { Text = "选择官方 PCL EXE，先检查再应用。", AutoSize = false, Height = 42, Dock = DockStyle.Fill };
    private readonly Button _apply = new() { Text = "生成补丁副本", AutoSize = true, Enabled = false };
    private readonly Button _restore = new() { Text = "还原所选启动器", AutoSize = true, Enabled = false };
    private readonly CheckBox _inPlace = new() { Text = "直接修改所选 EXE（自动备份；不修改游戏与存档）", AutoSize = true };
    private bool _busy;
    private readonly CheckBox _download = new() { Text = "恢复下载与整合包", Checked = true, AutoSize = true };
    private readonly CheckBox _link = new() { Text = "启动页联机", Checked = true, AutoSize = true };
    private readonly CheckBox _protect = new() { Text = "防止自动更新覆盖补丁", Checked = true, AutoSize = true };
    private CombinedOptions SelectedOptions() => new() { Download = _download.Checked, Link = _link.Checked, ProtectUpdates = _protect.Checked };

    public PatchWindow()
    {
        Text = "PCL 功能补丁 · 第三方工具";
        ClientSize = new Size(820, 580); MinimumSize = new Size(760, 550);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), ColumnCount = 1, RowCount = 9 };
        foreach (var height in new[] { 75, 45, 45, 44, 30, 30, 50 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "把下载与联机带回正式版", Font = new Font("Microsoft YaHei UI", 20, FontStyle.Bold),
            ForeColor = Color.FromArgb(19, 112, 243), Dock = DockStyle.Top, Height = 42 });
        header.Controls.Add(new Label { Text = "分别选择功能，统一检查、备份、应用和还原。", Dock = DockStyle.Bottom, Height = 27 });
        layout.Controls.Add(header, 0, 0);
        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        inputRow.Controls.Add(new Label { Text = "官方 EXE", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        inputRow.Controls.Add(_input, 1, 0);
        var browse = new Button { Text = "选择文件…", Dock = DockStyle.Fill };
        browse.Click += async (_, _) => await ChooseAsync(); inputRow.Controls.Add(browse, 2, 0);
        layout.Controls.Add(inputRow, 0, 1);
        var outputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRow.Controls.Add(new Label { Text = "副本位置", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _output.Text = Path.Combine(AppContext.BaseDirectory, "Patched", "Plain Craft Launcher 2.exe");
        outputRow.Controls.Add(_output, 1, 0); layout.Controls.Add(outputRow, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill };
        options.Controls.Add(_download); options.Controls.Add(_link); options.Controls.Add(_protect);
        layout.Controls.Add(options, 0, 4); layout.Controls.Add(_inPlace, 0, 5);
        foreach (var check in new[] { _download, _link, _protect }) check.CheckedChanged += async (_, _) => { if (!_busy && _input.Text.Length > 0) await InspectAsync(_input.Text); };
        _inPlace.CheckedChanged += (_, _) => { _output.Enabled = !_inPlace.Checked; _apply.Text = _inPlace.Checked ? "备份并应用补丁" : "生成补丁副本"; };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 4) };
        _apply.BackColor = Color.FromArgb(19, 112, 243); _apply.ForeColor = Color.White;
        _apply.FlatStyle = FlatStyle.Flat; _apply.FlatAppearance.BorderSize = 0;
        _apply.Padding = new Padding(12, 4, 12, 4); _restore.Padding = new Padding(12, 4, 12, 4);
        _apply.Click += async (_, _) => await ApplyAsync(); _restore.Click += async (_, _) => await RestoreAsync();
        actions.Controls.Add(_apply); actions.Controls.Add(_restore); layout.Controls.Add(actions, 0, 6);
        layout.Controls.Add(_log, 0, 7);
        layout.Controls.Add(new Label { Text = "只适配已验证的 2.99.0.1；版本/指纹不符即停止。副本不复制配置、登录缓存或游戏文件。",
            Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 8);
        Controls.Add(layout);
        AllowDrop = true;
        DragEnter += (_, e) => { if (!_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (!_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) await InspectAsync(files[0]); };
    }

    private async Task ChooseAsync()
    {
        if (_busy) return;
        using var dialog = new OpenFileDialog { Filter = "PCL 启动器 (*.exe)|*.exe", Title = "选择官方 PCL EXE" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await InspectAsync(dialog.FileName);
    }

    private async Task InspectAsync(string path)
    {
        SetBusy(true);
        try
        {
            var options = SelectedOptions();
            if (!options.Link && !options.Download && !options.ProtectUpdates)
            {
                _apply.Enabled = false;
                _status.Text = "至少选择一项功能。";
                return;
            }
            var result = await Task.Run(() => PatchEngine.Inspect(path, options));
            _input.Text = path;
            _status.Text = $"实际版本 {result.Version} · {result.Detail}";
            Log(_status.Text); Log("SHA256 " + result.Hash);
            foreach (var item in result.Items) Log(item.Title + " · " + item.State + " · " + item.Detail);
            _apply.Enabled = result.Supported;
            _restore.Enabled = result.Patched;
        }
        catch (Exception ex) { _apply.Enabled = _restore.Enabled = false; Log(ex.Message); _status.Text = "检查失败：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private async Task ApplyAsync()
    {
        if (_busy) return;
        var input = _input.Text;
        var output = _inPlace.Checked ? input : _output.Text;
        var options = SelectedOptions();
        if (MessageBox.Show(this, _inPlace.Checked ? "将备份并修改所选 EXE。请先关闭 PCL，游戏和存档不会被修改。" :
            "将在指定位置生成程序副本和联机文件。原 EXE、配置和存档不变。", "确认应用联机补丁", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => PatchEngine.Apply(input, output, Path.Combine(AppContext.BaseDirectory, "Payload"), options));
            Log(result.Already ? "已有所选补丁，无需重复修改。" : $"补丁成功；校验保留了 {result.PreservedMethods} 个无关方法和全部原始资源。");
            foreach (var item in result.Items) Log(item.Title + " · " + item.State);
            Log("输出：" + result.Target); Log("备份：" + result.Backup);
            _status.Text = "已完成。已安装功能会保留；还原可恢复本工具首次处理前的 EXE。";
        }
        catch (Exception ex) { Log("未完成：" + ex.Message); _status.Text = "操作已停止，请查看详情。"; }
        finally { SetBusy(false); }
    }

    private async Task RestoreAsync()
    {
        if (_busy) return;
        var input = _input.Text;
        if (MessageBox.Show(this, "只恢复所选启动器 EXE，保留联机文件、账号和原始备份。请先关闭 PCL。", "确认还原",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        SetBusy(true);
        try
        {
            var backup = await Task.Run(() => PatchEngine.Restore(input));
            Log("已从校验通过的备份还原：" + backup); _restore.Enabled = false; _status.Text = "启动器已还原，其他文件未删除。";
        }
        catch (Exception ex) { Log("还原停止：" + ex.Message); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy) { _busy = busy; UseWaitCursor = busy; _inPlace.Enabled = !busy; _download.Enabled = _link.Enabled = _protect.Enabled = !busy; }
    private void Log(string text) => _log.AppendText(text + Environment.NewLine);
}
