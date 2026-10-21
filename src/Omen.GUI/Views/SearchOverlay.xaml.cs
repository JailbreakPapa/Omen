// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class SearchOverlay : UserControl
{
    public SearchOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>
    /// Focus has to wait for the control to finish becoming visible - calling Focus() straight
    /// from the visibility change lands while the element is still collapsed and silently fails.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
            return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
        });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => ViewModel?.CloseSearch();

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => ViewModel?.CloseSearch();

    /// <summary>Keeps a click inside the panel from reaching the scrim and dismissing it.</summary>
    private void OnPanelClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnResultClick(object sender, MouseButtonEventArgs e) =>
        ViewModel?.ActivateSelectedSearchResult();

    /// <summary>
    /// The query box keeps focus the whole time so typing never breaks; arrow keys drive the
    /// list's selection through the view model instead of moving focus into it.
    /// </summary>
    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSearchSelection(1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSearchSelection(-1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Enter:
                vm.ActivateSelectedSearchResult();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.CloseSearch();
                e.Handled = true;
                break;
        }
    }

    private void ScrollSelectionIntoView()
    {
        if (ResultList.SelectedItem is { } selected)
            ResultList.ScrollIntoView(selected);
    }
}
