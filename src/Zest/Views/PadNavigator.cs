using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Zest.Services;

namespace Zest.Views;

/// <summary>
/// Controller driving for a form-like page (Forge): the D-pad moves focus to the nearest
/// control in that direction, Confirm presses/toggles/opens it, Left/Right nudge numbers,
/// and an opened drop-down is scrolled with Up/Down, picked with Confirm, dropped with Back.
/// </summary>
public sealed class PadNavigator(Control root)
{
    private ComboBox? _openCombo;
    private int _comboStart;

    /// <summary>Handles the press; false when the page has nothing to do with it (the caller then may).</summary>
    public bool Handle(PadButton button, IInputElement? focused)
    {
        if (_openCombo is { } combo)
            return HandleDropDown(combo, button);

        var current = Owner(focused);
        switch (button)
        {
            case PadButton.Up: return Move(current, 0, -1);
            case PadButton.Down: return Move(current, 0, 1);
            case PadButton.Left when current is NumericUpDown number: return Nudge(number, -1);
            case PadButton.Right when current is NumericUpDown number: return Nudge(number, 1);
            case PadButton.Left: return Move(current, -1, 0);
            case PadButton.Right: return Move(current, 1, 0);
            case PadButton.Confirm: return Activate(current);
            default: return false;
        }
    }

    private bool HandleDropDown(ComboBox combo, PadButton button)
    {
        var count = combo.ItemCount;
        switch (button)
        {
            case PadButton.Up or PadButton.Down or PadButton.PrevTab or PadButton.NextTab:
                // Shoulders page through long lists (moves, items).
                var step = button switch { PadButton.Up => -1, PadButton.Down => 1, PadButton.PrevTab => -10, _ => 10 };
                combo.SelectedIndex = Math.Clamp(combo.SelectedIndex + step, 0, Math.Max(0, count - 1));
                if (combo.ContainerFromIndex(combo.SelectedIndex) is { } item)
                    item.BringIntoView();
                else
                    combo.ScrollIntoView(combo.SelectedIndex);
                return true;
            case PadButton.Confirm:
                CloseDropDown(combo);
                return true;
            case PadButton.Back:
                combo.SelectedIndex = _comboStart;
                CloseDropDown(combo);
                return true;
            default:
                return true; // the drop-down owns the controller while open
        }
    }

    private void CloseDropDown(ComboBox combo)
    {
        combo.IsDropDownOpen = false;
        _openCombo = null;
        combo.Focus(NavigationMethod.Directional);
    }

    private static bool Nudge(NumericUpDown number, int delta)
    {
        var value = (number.Value ?? 0) + delta;
        number.Value = Math.Clamp(value, number.Minimum, number.Maximum);
        return true;
    }

    private bool Activate(Control? control)
    {
        switch (control)
        {
            case ToggleButton toggle:
                toggle.IsChecked = !(toggle.IsChecked ?? false);
                return true;
            case Button button:
                if (button.Command is { } command)
                {
                    if (command.CanExecute(button.CommandParameter))
                        command.Execute(button.CommandParameter);
                }
                else
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                return true;
            case ComboBox combo:
                _comboStart = combo.SelectedIndex;
                _openCombo = combo;
                combo.IsDropDownOpen = true;
                return true;
            case ListBoxItem item:
                item.IsSelected = true;
                return true;
            default:
                return control is not null; // text boxes need a keyboard; swallow the press
        }
    }

    /// <summary>Moves focus to the nearest control whose centre lies in the (dx, dy) direction.</summary>
    private bool Move(Control? current, int dx, int dy)
    {
        var targets = Targets().ToList();
        if (targets.Count == 0)
            return false;
        if (current is null || Center(current) is not { } from)
            return Focus(targets.OrderBy(c => Center(c)?.Y ?? 0).ThenBy(c => Center(c)?.X ?? 0).First());

        Control? best = null;
        var bestScore = double.MaxValue;
        foreach (var target in targets)
        {
            if (target == current || Center(target) is not { } to)
                continue;
            var along = (to.X - from.X) * dx + (to.Y - from.Y) * dy;   // distance in the pressed direction
            var across = Math.Abs(dx != 0 ? to.Y - from.Y : to.X - from.X);
            if (along <= 1)
                continue;
            var score = along + across * 2.5;                          // prefer staying in the same row/column
            if (score < bestScore)
            {
                bestScore = score;
                best = target;
            }
        }
        return best is not null && Focus(best);
    }

    private static bool Focus(Control control)
    {
        control.Focus(NavigationMethod.Directional);
        control.BringIntoView();
        return true;
    }

    private Point? Center(Control control)
    {
        var b = control.Bounds;
        return control.TranslatePoint(new Point(b.Width / 2, b.Height / 2), root);
    }

    /// <summary>Every control the pad can land on: buttons, check boxes, pickers, number and text fields, list rows.</summary>
    private IEnumerable<Control> Targets() => root.GetVisualDescendants().OfType<Control>().Where(c =>
        c is Button or ComboBox or NumericUpDown or TextBox or ListBoxItem
        && c.TemplatedParent is not (NumericUpDown or ComboBox)
        && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.Bounds.Width > 0
        && (c is not TextBox || c.FindAncestorOfType<NumericUpDown>() is null)
        && IsOnScreen(c));

    // Skip controls scrolled out of their viewer, so the pad never jumps into hidden rows.
    private bool IsOnScreen(Control control)
    {
        if (control.FindAncestorOfType<ScrollViewer>() is not { } viewer)
            return true;
        if (control.TranslatePoint(new Point(0, 0), viewer) is not { } p)
            return false;
        return p.Y + control.Bounds.Height > -control.Bounds.Height && p.Y < viewer.Bounds.Height + control.Bounds.Height;
    }

    /// <summary>Focus often sits inside a control's template (a NumericUpDown's text box); walk up to the control.</summary>
    private static Control? Owner(IInputElement? focused)
    {
        if (focused is not Control control)
            return null;
        return (Control?)control.FindAncestorOfType<NumericUpDown>(includeSelf: true)
            ?? control.FindAncestorOfType<ComboBox>(includeSelf: true)
            ?? control;
    }
}
