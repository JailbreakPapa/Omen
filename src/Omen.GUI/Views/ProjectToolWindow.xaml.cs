// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Omen.GUI.Models;
using Omen.GUI.Services;
using Omen.GUI.ViewModels;

namespace Omen.GUI.Views;

public partial class ProjectToolWindow : UserControl
{
    public ProjectToolWindow() => InitializeComponent();

    /// <summary>Double-clicking a rule file opens it in the rule editor. Other file types (and
    /// directories, which the double-click already toggles via TreeViewItem's own expander) are
    /// left alone - this only ever adds an open, never intercepts the existing expand/collapse.</summary>
    private void OnTreeMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<TreeViewItem>(source)?.DataContext is not ProjectTreeNode { IsDirectory: false } node)
            return;

        if (!RuleFileKinds.IsRuleFile(node.FullPath))
            return;

        viewModel.OpenRuleFile(node.FullPath);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
