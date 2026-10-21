// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AvalonDock.Layout;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Omen.GUI.Controls;

/// <summary>
/// One icon on a tool-window rail. Wraps an AvalonDock anchorable so the rail can toggle and
/// reflect its visibility.
/// </summary>
public sealed partial class ToolWindowRailItem : ObservableObject
{
    public required Geometry Icon { get; init; }
    public required string Label { get; init; }

    private LayoutAnchorable _anchorable = null!;

    /// <summary>
    /// The wrapped anchorable. Subscribes to <see cref="LayoutAnchorable.IsVisibleChanged"/> so
    /// <see cref="IsActive"/> tracks real visibility even when it changes outside <see cref="Toggle"/>
    /// (e.g. the pane's own tab context-menu "Hide", which AvalonDock allows since these anchorables
    /// keep the default CanHide="True").
    /// </summary>
    public required LayoutAnchorable Anchorable
    {
        get => _anchorable;
        init
        {
            _anchorable = value;
            IsActive = value.IsVisible;
            value.IsVisibleChanged += (_, _) => IsActive = _anchorable.IsVisible;
        }
    }

    [ObservableProperty]
    private bool _isActive;

    public void Toggle() => Anchorable.IsVisible = !Anchorable.IsVisible;
}

/// <summary>
/// Rider-style vertical icon strip that shows and hides docked tool windows. AvalonDock's own
/// auto-hide strips do not match this design, so the rail is implemented here.
/// </summary>
public partial class ToolWindowRail : UserControl
{
    public static readonly DependencyProperty RailBorderThicknessProperty =
        DependencyProperty.Register(nameof(RailBorderThickness), typeof(Thickness), typeof(ToolWindowRail),
            new PropertyMetadata(new Thickness(0, 0, 1, 0)));

    public Thickness RailBorderThickness
    {
        get => (Thickness)GetValue(RailBorderThicknessProperty);
        set => SetValue(RailBorderThicknessProperty, value);
    }

    public ObservableCollection<ToolWindowRailItem> Items { get; } = [];

    public ToolWindowRail() => InitializeComponent();

    private void OnRailItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToolWindowRailItem item })
            item.Toggle();
    }
}

