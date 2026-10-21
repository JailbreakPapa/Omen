// Omen Build System
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Omen.GUI.Controls;

/// <summary>
/// An integer-only TextBox. WPF has no NumericUpDown; Omen only ever edits whole-number build
/// options, so this deliberately accepts integers rather than reproducing a full spinner.
/// Non-numeric input is rejected at the keystroke, which also removes the old Avalonia
/// behaviour where a NumericUpDown accepted fractions that were then silently truncated.
/// </summary>
public class NumericTextBox : TextBox
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(long), typeof(NumericTextBox),
            new FrameworkPropertyMetadata(0L,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public long Value
    {
        get => (long)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    static NumericTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NumericTextBox),
            new FrameworkPropertyMetadata(typeof(NumericTextBox)));
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (NumericTextBox)d;
        var text = ((long)e.NewValue).ToString(CultureInfo.InvariantCulture);
        if (box.Text != text)
            box.Text = text;
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        e.Handled = !IsNumeric(e.Text);
        base.OnPreviewTextInput(e);
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        // Only update Value on successful parse; leave it unchanged for transient states like "-" or "".
        // This prevents OnValueChanged from firing with a stale value mid-edit, which would rewrite Text.
        if (long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            Value = parsed;
        }
    }

    private bool IsNumeric(string text)
    {
        if (text.Length == 0)
            return false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (char.IsDigit(c))
                continue;

            if (c == '-')
            {
                // Minus is valid only as the first character of the incoming text,
                // when caret is at position 0, and when current Text doesn't already start with minus.
                if (i == 0 && SelectionStart == 0 && !Text.StartsWith('-'))
                    continue;
                return false;
            }

            return false;
        }

        return true;
    }
}

