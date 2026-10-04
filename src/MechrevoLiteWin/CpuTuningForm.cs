using MechrevoLite.Hardware;
using System.Diagnostics;
using MechrevoLite.Properties;
using MechrevoLite.UI;

namespace MechrevoLite;

public sealed class CpuTuningForm : RForm
{
    readonly RComboBox _option = new() { Name = "cpuTuneOption", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly RNumericUpDown _value = new() { Name = "cpuTuneValue", Width = 120 };
    readonly Label _current = new() { AutoSize = true };
    readonly Label _status = new() { Name = "cpuTuneStatus", AutoSize = true };
    readonly RButton _probeButton, _applyButton, _restoreButton;
    readonly RButton _installButton;
    readonly List<Label> _paragraphs = new();
    bool _busy;
    bool _hasChanges;
    readonly CpuTuningProbe? _fixture;

    internal static string Localized(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;
    public CpuTuningForm() : this(null) { }

    internal CpuTuningForm(CpuTuningProbe? fixture)
    {
        _fixture = fixture;
        _hasChanges = fixture is null && !Program.UiAuditMode && RuntimeCpuTuning.Instance.HasChanges;
        Text = Localized("CpuTuneTitle");
        ClientSize = new Size(580, 430);
        MinimumSize = new Size(420, 260);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        InitTheme(true);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(root);
        Controls.Add(scroll);
        void Add(Control control)
        {
            control.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(control, 0, root.RowCount++);
        }
        Label Paragraph(string text)
        {
            var label = new Label { Text = text, AutoSize = true, ForeColor = UiVisualStyle.Muted };
            _paragraphs.Add(label);
            return label;
        }
        Add(Paragraph(fixture?.Cpu ?? PawnIO.CpuInfo.Name));
        Add(Paragraph(Localized("CpuTuneScope")));
        Add(_option);
        var valueRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        valueRow.Controls.Add(_value);
        _current.Margin = new Padding(12, 6, 0, 0);
        valueRow.Controls.Add(_current);
        Add(valueRow);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        RButton Button(string key)
        {
            var button = new RButton { Text = Localized(key), AutoSize = true, Height = 30, Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand };
            buttons.Controls.Add(button);
            return button;
        }
        _probeButton = Button("CpuTuneProbe");
        _applyButton = Button("CpuTuneApply");
        _restoreButton = Button("CpuTuneRestore");
        _installButton = Button("CpuTuneInstallDriver");
        _installButton.Click += async (_, _) => await InstallDriverAsync();
        Add(buttons);
        _paragraphs.Add(_status);
        Add(_status);
        Add(Paragraph(Localized("CpuTuneRisk")));
        var link = new LinkLabel { Text = Localized("CpuTuneDriverLink"), AutoSize = true };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo { FileName = "https://pawnio.eu/", UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = ex.Message; Logger.WriteLine("PawnIO download page: " + ex); }
        };
        Add(link);
        _option.SelectedIndexChanged += (_, _) => UpdateSelection();
        _probeButton.Click += async (_, _) => await ProbeAsync();
        _applyButton.Click += async (_, _) =>
        {
            if (_option.SelectedItem is not Option item) return;
            decimal value = _value.Value;
            await RunAsync(() => RuntimeCpuTuning.Instance.Apply(item.Setting.Id, value), false);
        };
        _restoreButton.Click += async (_, _) => await RunAsync(RuntimeCpuTuning.Instance.Restore, true);
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        root.SizeChanged += (_, _) =>
        {
            int width = Math.Max(200, root.ClientSize.Width - root.Padding.Horizontal);
            foreach (Label label in _paragraphs) label.MaximumSize = new Size(width, 0);
        };
        Shown += async (_, _) =>
        {
            if (_fixture is null && !Program.UiAuditMode) await ProbeAsync();
        };
        ApplyProbe(fixture ?? new(PawnIO.CpuInfo.Name, "CpuTuneProbePrompt", "", Array.Empty<CpuTuningOption>()));
        UiVisualStyle.ApplyWindow(this);
    }

    sealed record Option(CpuTuningOption Setting)
    {
        public override string ToString() => Localized(Setting.LabelKey) + " (" + Setting.Unit + ")";
    }

    internal void ApplyProbe(CpuTuningProbe probe)
    {
        _installButton.Visible = probe.StatusKey == "CpuTuneDriverMissing";
        _installButton.Enabled = !_busy && _fixture is null && !Program.UiAuditMode;
        _option.Items.Clear();
        foreach (CpuTuningOption setting in probe.Options) _option.Items.Add(new Option(setting));
        int writable = probe.Options.ToList().FindIndex(o => o.CanWrite);
        if (_option.Items.Count > 0) _option.SelectedIndex = Math.Max(0, writable);
        _status.Text = Localized(probe.StatusKey) + (probe.Detail.Length == 0 ? "" : Environment.NewLine + probe.Detail);
        UpdateSelection();
    }

    void UpdateSelection()
    {
        bool writable = !_busy && _fixture is null && !Program.UiAuditMode &&
            _option.SelectedItem is Option choice && choice.Setting.CanWrite;
        _value.Enabled = _applyButton.Enabled = writable;
        _probeButton.Enabled = !_busy && _fixture is null && !Program.UiAuditMode;
        _installButton.Enabled = !_busy && _fixture is null && !Program.UiAuditMode;
        _restoreButton.Enabled = !_busy && _hasChanges;
        _option.Enabled = !_busy && _option.Items.Count > 0;
        _current.Visible = _option.SelectedItem is Option;
        if (_option.SelectedItem is not Option item) { _current.Text = ""; return; }
        var setting = item.Setting;
        _value.Minimum = Math.Min(_value.Minimum, setting.Minimum);
        _value.Maximum = Math.Max(_value.Maximum, setting.Maximum);
        _value.Value = Math.Clamp(decimal.Round(setting.Current ?? 0), setting.Minimum, setting.Maximum);
        _value.Minimum = setting.Minimum;
        _value.Maximum = setting.Maximum;
        _current.Text = Localized("CpuTuneCurrent") + ": " + (setting.Current is decimal current ?
            $"{current:F3} {setting.Unit}" : Localized("CpuTuneNoReadback"));
    }

    async Task ProbeAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateSelection();
        try { ApplyProbe(await Task.Run(RuntimeCpuTuning.Instance.Probe)); }
        finally { _busy = false; UpdateSelection(); }
    }

    async Task InstallDriverAsync()
    {
        if (_busy || _fixture is not null || Program.UiAuditMode) return;
        _busy = true;
        _installButton.Enabled = false;
        UpdateSelection();
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Install-PawnIO.ps1") })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Driver installer could not start.");
            await process.WaitForExitAsync();
            if (process.ExitCode == 3010) _status.Text = Localized("CpuTuneDriverRestart");
            else if (process.ExitCode != 0) _status.Text = Localized("CpuTuneFailed") + $" ({process.ExitCode})";
            else ApplyProbe(await Task.Run(RuntimeCpuTuning.Instance.Probe));
        }
        catch (Exception ex) { _status.Text = ex.Message; Logger.WriteLine("PawnIO installation failed: " + ex); }
        finally { _busy = false; _installButton.Enabled = true; UpdateSelection(); }
    }

    async Task RunAsync(Func<CpuTuningResult> action, bool restore)
    {
        if (_busy || _fixture is not null || Program.UiAuditMode) return;
        _busy = true;
        decimal requested = _value.Value;
        UpdateSelection();
        try
        {
            CpuTuningResult result = await Task.Run(action);
            _hasChanges = RuntimeCpuTuning.Instance.HasChanges;
            ApplyProbe(await Task.Run(RuntimeCpuTuning.Instance.Probe));
            _status.Text = Localized(result.Success ? (restore ? "CpuTuneRestored" : result.Verified ? "CpuTuneVerified" : "CpuTuneAccepted") : "CpuTuneFailed")
                + (result.Detail.Length == 0 ? "" : Environment.NewLine + result.Detail);
        }
        finally { _busy = false; UpdateSelection(); if (!restore) _value.Value = Math.Clamp(requested, _value.Minimum, _value.Maximum); }
    }
}
