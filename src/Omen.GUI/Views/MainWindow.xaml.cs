// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Data;
using AvalonDock;
using AvalonDock.Layout;
using Omen.GUI.Controls;
using Omen.GUI.Models;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BuildRails();

        StateChanged += OnWindowStateChanged;
        MenuBarControl.SetMaximized(WindowState == WindowState.Maximized);

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RuleEditor.OpenDocuments.CollectionChanged += (_, _) => SynchronizeDocuments();
            viewModel.RuleEditor.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RuleEditorPanelViewModel.ActiveDocument))
                    SynchronizeDocuments();
            };
        }
    }

    private readonly Dictionary<RuleEditorDocument, LayoutDocument> _documents = new();
    private DateTime _lastShiftRelease;
    private bool _shiftUsedWithAnotherKey;

    private void SynchronizeDocuments()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        foreach (var document in _documents.Keys.Except(vm.RuleEditor.OpenDocuments).ToArray())
        {
            DocumentPane.Children.Remove(_documents[document]);
            _documents.Remove(document);
        }
        foreach (var document in vm.RuleEditor.OpenDocuments)
        {
            if (!_documents.TryGetValue(document, out var layout))
            {
                layout = new LayoutDocument { Content = new RuleEditorView { DataContext = document } };
                BindingOperations.SetBinding(layout, LayoutDocument.TitleProperty,
                    new Binding(nameof(RuleEditorDocument.DisplayTitle)) { Source = document });
                _documents.Add(document, layout);
                DocumentPane.Children.Add(layout);
            }
            if (vm.RuleEditor.ActiveDocument == document && !layout.IsActive)
                layout.IsActive = true;
        }
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        if (DataContext is MainWindowViewModel vm &&
            args.Document.Content is RuleEditorView { DataContext: RuleEditorDocument document })
        {
            args.Cancel = true;
            vm.RuleEditor.RequestClose(document);
        }
    }

    private void OnActiveContentChanged(object? sender, EventArgs args)
    {
        if (DataContext is MainWindowViewModel vm &&
            Docking.ActiveContent is RuleEditorView { DataContext: RuleEditorDocument document } &&
            vm.RuleEditor.ActiveDocument != document)
            vm.RuleEditor.ActiveDocument = document;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (args.Key is not (Key.LeftShift or Key.RightShift))
            _shiftUsedWithAnotherKey = true;
        if (args.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && vm.RuleEditor.ActiveDocument is { } document)
        {
            vm.RuleEditor.Save(document);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            if (vm.IsSearchOpen) vm.CloseSearch();
            else if (vm.IsSettingsOpen) vm.CloseSettings();
            else if (vm.IsAboutOpen) vm.CloseAbout();
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.LeftShift or Key.RightShift))
            return;
        var now = DateTime.UtcNow;
        if (!_shiftUsedWithAnotherKey && now - _lastShiftRelease < TimeSpan.FromMilliseconds(450) &&
            DataContext is MainWindowViewModel vm)
        {
            vm.OpenSearch();
            _lastShiftRelease = DateTime.MinValue;
        }
        else
            _lastShiftRelease = _shiftUsedWithAnotherKey ? DateTime.MinValue : now;
        _shiftUsedWithAnotherKey = false;
    }

    private void BuildRails()
    {
        LeftRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Project"),
            Label = "Project",
            Anchorable = ProjectAnchorable
        });

        LeftRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Output"),
            Label = "Build Output",
            Anchorable = OutputAnchorable
        });

        RightRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Options"),
            Label = "Options",
            Anchorable = OptionsAnchorable
        });

        RightRail.Items.Add(new ToolWindowRailItem
        {
            Icon = (Geometry)FindResource("Icon.Matrix"),
            Label = "Build Matrix",
            Anchorable = MatrixAnchorable
        });
    }

    // --- Hazard 4: maximized clipping -----------------------------------------------------
    // A WindowChrome window that reports WindowState.Maximized is sized to the monitor's work
    // area (see WmGetMinMaxInfo below), but WPF still measures its content edge-to-edge against
    // that full work-area rect, so content can visually crowd/clip the screen edges. The
    // conventional fix is to pad the root by the resize-border thickness while maximized; derive
    // that value from SystemParameters rather than hardcoding a pixel count.
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness(0);

        MenuBarControl.SetMaximized(WindowState == WindowState.Maximized);
    }

    // --- Hazard 1: maximize covers the taskbar / multi-monitor -----------------------------
    // WindowStyle="None" + WindowChrome makes Windows treat "maximized" as "fill the monitor",
    // not "fill the monitor's work area", which overlaps the taskbar and (per-monitor) can
    // overflow onto the wrong screen's bounds entirely. Intercepting WM_GETMINMAXINFO and
    // filling in the correct monitor's rcWork (not rcMonitor) is the standard remedy; querying
    // via MonitorFromWindow/GetMonitorInfo naturally picks the monitor the window is actually on,
    // so this is correct for a two-display, mixed-resolution setup without any manual DPI math -
    // GetMonitorInfo already returns physical pixels for that monitor, which is what a
    // per-monitor-DPI-aware window (the .NET default) expects here.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
            hwndSource.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaxSizeToWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ClampMaxSizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Position/size are relative to the monitor's own origin, not the virtual desktop -
        // required for this to clamp correctly on a secondary monitor.
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}

