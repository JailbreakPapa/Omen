// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Executors.Server;

namespace Omen.Tray;

/// <summary>What the tray icon opens: each server, its running builds with progress, and stop/cancel buttons.</summary>
internal sealed class StatusWindow : Form
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Button _cancelBuild = new() { Text = "Cancel build", AutoSize = true, Enabled = false };
    private readonly Button _stopServer = new() { Text = "Stop server", AutoSize = true, Enabled = false };
    private readonly Label _empty = new() { Text = "No Omen build server is running.", Dock = DockStyle.Top, Padding = new Padding(8), AutoSize = true, Visible = false };
    private IReadOnlyList<TrayRow> _rows = [];

    public StatusWindow()
    {
        Text = "Omen build servers";
        Width = 760;
        Height = 320;
        StartPosition = FormStartPosition.Manual;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        ShowInTaskbar = false;
        using (var stream = typeof(StatusWindow).Assembly.GetManifestResourceStream("omen.ico")!)
            Icon = new Icon(stream);

        _list.Columns.Add("Server / build", 580);
        _list.Columns.Add("Progress", 140);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        buttons.Controls.Add(_stopServer);
        buttons.Controls.Add(_cancelBuild);
        _cancelBuild.Click += async (_, _) =>
        {
            if (Selected is { BuildId: { } id } row)
                await ServerControl.CancelAsync(row.Pipe, id, default);
        };
        _stopServer.Click += async (_, _) =>
        {
            if (Selected is { } row)
                await ServerControl.StopAsync(row.Pipe, default);
        };

        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(buttons);

        // Closing only hides: the tray keeps polling and reopens the same window.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private TrayRow? Selected => _list.SelectedIndices.Count == 1 && _list.SelectedIndices[0] < _rows.Count ? _rows[_list.SelectedIndices[0]] : null;

    public void Show(TrayModel model)
    {
        if (!Visible)
            return;
        var selected = Selected;
        _rows = model.Rows;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in model.Rows)
        {
            var item = new ListViewItem(row.Text);
            item.SubItems.Add(row.BuildId == null ? "" : $"{new string('#', row.Progress / 10),-10} {row.Progress}%");
            if (row.BuildId == null)
                item.Font = new Font(_list.Font, FontStyle.Bold);
            _list.Items.Add(item);
            if (selected != null && row.Pipe == selected.Pipe && row.BuildId == selected.BuildId)
                item.Selected = true;
        }
        _list.EndUpdate();
        _empty.Visible = model.Rows.Count == 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _cancelBuild.Enabled = Selected?.BuildId != null;
        _stopServer.Enabled = Selected != null;
    }
}
