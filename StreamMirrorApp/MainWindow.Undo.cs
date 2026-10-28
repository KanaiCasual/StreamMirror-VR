using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace StreamMirror;

/// <summary>
/// Undo and redo (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z) of the settings, page by page: Ctrl+Z takes back the last change made
/// on the page you look at, so nothing changes out of sight. One step is one gesture - a drag of the crop box or a
/// slider, a click, a key press, a run of mouse-wheel ticks - however many values it moved. Typing in a text box keeps
/// its own undo. Choosing another crop profile starts afresh.
/// </summary>
public partial class MainWindow
{
    private sealed class UndoStep(TabItem? page)
    {
        public TabItem? Page { get; } = page;
        public Dictionary<string, (string Before, string After)> Changes { get; } = new(StringComparer.Ordinal);
    }

    private readonly List<UndoStep> _undo = [], _redo = [];
    private UndoStep? _undoOpen; // The gesture in progress: its changes join one step.
    private bool _undoWheel;     // The last gesture was the mouse wheel: the next tick joins it.
    private bool _undoing;

    private void BuildUndo()
    {
        // A new gesture closes the step in progress.
        PreviewMouseDown += (_, _) => { _undoOpen = null; _undoWheel = false; };
        PreviewMouseWheel += (_, _) => { if (!_undoWheel) _undoOpen = null; _undoWheel = true; };
        PreviewKeyDown += (_, e) =>
        {
            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var undo = control && e.Key == Key.Z && !shift;
            var redo = control && (e.Key == Key.Y || (e.Key == Key.Z && shift));
            if (!undo && !redo)
            {
                _undoOpen = null;
                _undoWheel = false;
                return;
            }
            // A text box (a profile's name, the game box) undoes its own typing; the tour holds the keys while it runs.
            if (Keyboard.FocusedElement is TextBoxBase || _tourIndex >= 0) return;
            e.Handled = undo ? UndoLast() : RedoLast();
        };
    }

    /// <summary>A setting is about to change from `before` to `after`: it joins the gesture's step.</summary>
    private void RecordUndo(string key, string? before, string after)
    {
        if (_undoing || _loading) return;
        before ??= DefaultOf(key);
        if (before == after) return;
        if (_undoOpen == null)
        {
            _undoOpen = new UndoStep(Tabs.SelectedItem as TabItem);
            _undo.Add(_undoOpen);
            if (_undo.Count > 200) _undo.RemoveAt(0);
        }
        _redo.Clear();
        _undoOpen.Changes[key] = _undoOpen.Changes.TryGetValue(key, out var earlier) ? (earlier.Before, after) : (before, after);
    }

    private static string DefaultOf(string key) => Settings.All.FirstOrDefault(d => d.Key == key)?.Default ?? "";

    /// <summary>Profile switched or new: the earlier steps belong to another framing.</summary>
    private void ForgetUndo()
    {
        _undo.Clear();
        _redo.Clear();
        _undoOpen = null;
    }

    private bool UndoLast() => Step(_undo, _redo, back: true);
    private bool RedoLast() => Step(_redo, _undo, back: false);

    /// <summary>Takes the last step of this page from one list, applies its values (before or after), and keeps it in the other.</summary>
    private bool Step(List<UndoStep> from, List<UndoStep> to, bool back)
    {
        var page = Tabs.SelectedItem as TabItem;
        var index = from.FindLastIndex(s => ReferenceEquals(s.Page, page));
        if (index < 0) return false;
        var step = from[index];
        from.RemoveAt(index);
        to.Add(step);
        _undoOpen = null;
        ApplyAsSetting(step.Changes.ToDictionary(c => c.Key, c => back ? c.Value.Before : c.Value.After));
        return true;
    }

    /// <summary>
    /// Values put in place as if the user had set them: the controls show them, and whatever follows a change follows
    /// (the profile, the crop box, the save). Not recorded as a step of its own.
    /// </summary>
    private void ApplyAsSetting(IReadOnlyDictionary<string, string> values)
    {
        _undoing = true;
        try
        {
            ApplyValues(values);
            foreach (var (key, value) in values) OnValueChanged(key, value);
        }
        finally
        {
            _undoing = false;
        }
        ApplyCropMargin(moveBox: false);
        DrawCrop();
        UpdateProfileState();
    }

    /// <summary>Values put in place as one undoable step (Revert): Ctrl+Z brings back what was there.</summary>
    private void ApplyAsUndoableStep(IReadOnlyDictionary<string, string> values)
    {
        var step = new UndoStep(Tabs.SelectedItem as TabItem);
        foreach (var (key, value) in values)
        {
            var before = _values.GetValueOrDefault(key) ?? DefaultOf(key);
            if (before != value) step.Changes[key] = (before, value);
        }
        if (step.Changes.Count == 0) return;
        _undo.Add(step);
        _redo.Clear();
        _undoOpen = null;
        ApplyAsSetting(values);
    }
}
