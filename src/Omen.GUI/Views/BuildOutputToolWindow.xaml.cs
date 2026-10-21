// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

/// <summary>
/// AlternationIndex is 0-based; build/log line numbers read naturally starting at 1.
/// </summary>
public sealed class AlternationIndexToLineNumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index ? (index + 1).ToString(culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class BuildOutputToolWindow : UserControl
{
    private INotifyCollectionChanged? _subscribed;
    private bool _scrollPending;

    public BuildOutputToolWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnOutputTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ActiveOutputTab = "output";
    }

    private void OnProblemsTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ActiveOutputTab = "problems";
    }

    /// <summary>
    /// Re-subscribes on DataContext change and unsubscribes from the previous collection. The
    /// Avalonia version subscribed on every DataContext change without ever detaching, leaking
    /// the old ViewModel and double-scrolling.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        Attach();
    }

    /// <summary>
    /// AvalonDock re-shows a hidden pane by reloading the same UserControl instance while the
    /// DataContext (the same MainWindowViewModel) never changes, so DataContextChanged does not
    /// fire again. Without this, Detach() on the prior Unloaded leaves the pane permanently
    /// unsubscribed after a hide/show cycle.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed is null)
            Attach();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Attach()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _subscribed = vm.OutputLines;
            _subscribed.CollectionChanged += OnOutputLinesChanged;
        }
    }

    private void Detach()
    {
        if (_subscribed is null)
            return;

        _subscribed.CollectionChanged -= OnOutputLinesChanged;
        _subscribed = null;
    }

    /// <summary>
    /// Calling ScrollIntoView synchronously from inside CollectionChanged re-enters the
    /// ListBox's container generation while it is still processing the Add (worse during a
    /// burst of rapid-fire Add events from build-log streaming) and throws "ItemsControl is
    /// inconsistent with its items source". Instead, queue at most one Background-priority
    /// dispatch, which runs only after WPF has finished handling the collection change and
    /// generating containers, so ScrollIntoView can no longer land mid-generation.
    /// </summary>
    private void OnOutputLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollPending)
            return;

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ScrollToLatest);
    }

    private void ScrollToLatest()
    {
        _scrollPending = false;

        // Control may have been unloaded, DataContext swapped, or OutputLines.Clear()'d
        // (CloseProject) between the event firing and this callback running.
        if (_subscribed is null || OutputList.ItemsSource is null || OutputList.Items.Count == 0)
            return;

        OutputList.ScrollIntoView(OutputList.Items[^1]);
    }
}

