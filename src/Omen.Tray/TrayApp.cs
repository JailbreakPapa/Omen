// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Drawing.Drawing2D;
using Omen.Executors.Server;

namespace Omen.Tray;

/// <summary>
/// The notification-area icon for every running Omen build server. Polls the servers' pipes every two seconds,
/// renders <see cref="TrayModel"/>, and exits 30 seconds after the last server is gone.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private static readonly TimeSpan ExitAfterNoServers = TimeSpan.FromSeconds(30);

    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly Dictionary<TrayState, Icon> _icons;
    private StatusWindow? _window;
    private IReadOnlyList<(string Pipe, ServerStatusDto Status)> _servers = [];
    private DateTime _seenUtc = DateTime.UtcNow;
    private DateTime _lastServerSeen = DateTime.UtcNow;
    private bool _polling;

    public TrayApp()
    {
        using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("omen.ico")!;
        using var baseIcon = new Icon(stream, 16, 16);
        _icons = new()
        {
            [TrayState.Idle] = WithDot(baseIcon, Color.Gray),
            [TrayState.Building] = WithDot(baseIcon, Color.DodgerBlue),
            [TrayState.Failed] = WithDot(baseIcon, Color.Red),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Stop all servers", null, async (_, _) =>
        {
            foreach (var (pipe, _) in _servers)
                await ServerControl.StopAsync(pipe, default);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray", null, (_, _) => Application.Exit());

        _icon = new NotifyIcon { Icon = _icons[TrayState.Idle], Text = "Omen", Visible = true, ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };

        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        if (_polling)
            return;
        _polling = true;
        try
        {
            var pipes = ServerDirectory.ListPipes();
            var statuses = await Task.WhenAll(pipes.Select(async pipe => (Pipe: pipe, Status: await ServerControl.QueryStatusAsync(pipe, default))));
            // A server that vanished between listing and asking simply drops out.
            _servers = statuses.Where(s => s.Status != null).Select(s => (s.Pipe, s.Status!)).ToList();

            if (_servers.Count > 0)
                _lastServerSeen = DateTime.UtcNow;
            else if (DateTime.UtcNow - _lastServerSeen > ExitAfterNoServers)
                Application.Exit();

            var list = _servers.Select(s => s.Status).ToList();
            var model = TrayModel.From(list, TrayModel.FailedSince(list, _seenUtc), _servers.Select(s => s.Pipe).ToList());
            _icon.Icon = _icons[model.State];
            // NotifyIcon.Text is limited to 127 characters.
            _icon.Text = model.Tooltip.Length > 127 ? model.Tooltip[..127] : model.Tooltip;
            _window?.Show(model);
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowWindow()
    {
        _seenUtc = DateTime.UtcNow;
        if (_window is not { IsDisposed: false })
            _window = new StatusWindow();
        _window.Show();
        _window.Activate();
        _ = PollAsync();
    }

    /// <summary>The Omen icon with a status dot in its lower-right corner.</summary>
    private static Icon WithDot(Icon baseIcon, Color color)
    {
        using var bitmap = baseIcon.ToBitmap();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            using var outline = new Pen(Color.White, 1);
            graphics.FillEllipse(brush, 8, 8, 7, 7);
            graphics.DrawEllipse(outline, 8, 8, 7, 7);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _window?.Dispose();
        foreach (var icon in _icons.Values)
            icon.Dispose();
    }
}
