using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ajazz.Keyboard;
using AK980Studio.Controls;

namespace AK980Studio.Pages;

public partial class MacrosPage : UserControl
{
    private bool _loading;
    private bool _recording;
    private readonly Stopwatch _sinceLast = new();
    private int _insertAt;
    private readonly HashSet<byte> _down = new();

    public MacrosPage()
    {
        InitializeComponent();
        DefaultDelay.Value = 30;
        DelaySlider.ValueChanged += (_, _) => SetSelectedDelays((int)DelaySlider.Value);
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopRecording(); };
        RefreshList(0);
    }

    private static List<Macro> Macros => App.Settings.Macros;

    private Macro? Current => MacroList.SelectedIndex >= 0 && MacroList.SelectedIndex < Macros.Count ? Macros[MacroList.SelectedIndex] : null;

    private static void Save() => App.Settings.Save();

    // ------------------------------------------------------------ macro list

    private void RefreshList(int select)
    {
        _loading = true;
        MacroList.Items.Clear();
        for (int i = 0; i < Macros.Count; i++)
            MacroList.Items.Add($"{i,2}  {Macros[i].Name}   ({Macros[i].Steps.Count})");
        MacroList.SelectedIndex = Macros.Count == 0 ? -1 : Math.Clamp(select, 0, Macros.Count - 1);
        _loading = false;
        ShowMacro();
        UpdateMemory();
    }

    private void Macro_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StopRecording();
        ShowMacro();
    }

    private void ShowMacro(int selectStep = -1)
    {
        var m = Current;
        EditorPanel.IsEnabled = m is not null;
        MacroTitle.Text = m?.Name ?? "Pick or create a macro";
        StepList.Items.Clear();
        if (m is null)
        {
            StepCount.Text = "";
            return;
        }
        foreach (var s in m.Steps)
            StepList.Items.Add($"{s.Describe(),-22} {s.DelayMs,5} ms");
        if (selectStep >= 0 && selectStep < StepList.Items.Count)
        {
            StepList.SelectedIndex = selectStep;
            StepList.ScrollIntoView(StepList.Items[selectStep]);
        }
        StepCount.Text = $"{m.Steps.Count} step(s)";
        if (MacroList.SelectedIndex >= 0 && MacroList.SelectedIndex < MacroList.Items.Count)
        {
            _loading = true;
            MacroList.Items[MacroList.SelectedIndex] = $"{MacroList.SelectedIndex,2}  {m.Name}   ({m.Steps.Count})";
            _loading = false;
        }
    }

    private void UpdateMemory()
    {
        int used = MacroCodec.Used(Macros);
        MemoryBar.Value = Math.Min(used, MacroCodec.BudgetBytes);
        MemoryBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, used > MacroCodec.BudgetBytes ? "Bad" : used > MacroCodec.BudgetBytes * 0.85 ? "Warn" : "Accent");
        bool unsent = App.Settings.MacrosHash() != App.Settings.MacrosAppliedHash;
        MemoryText.Text = $"{used} of {MacroCodec.BudgetBytes} bytes used · {Macros.Count} of {MacroCodec.MaxMacros} macros"
                          + (used > MacroCodec.BudgetBytes ? " · too big, remove steps" : unsent ? " · not uploaded yet" : "");
    }

    private void Changed(int selectStep = -1)
    {
        Save();
        ShowMacro(selectStep);
        UpdateMemory();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (Macros.Count >= MacroCodec.MaxMacros) return;
        var name = Prompt.Ask(Window.GetWindow(this), "Name for the new macro", $"Macro {Macros.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        Macros.Add(new Macro { Name = name.Trim() });
        Save();
        RefreshList(Macros.Count - 1);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } m) return;
        var name = Prompt.Ask(Window.GetWindow(this), "Rename macro", m.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        m.Name = name.Trim();
        Changed();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } m || Macros.Count >= MacroCodec.MaxMacros) return;
        Macros.Add(new Macro { Name = m.Name + " copy", Steps = m.Steps.ToList() });
        Save();
        RefreshList(Macros.Count - 1);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        int i = MacroList.SelectedIndex;
        if (Current is not { } m) return;
        int users = KeysUsing(i);
        var msg = users > 0
            ? $"Delete \"{m.Name}\"? {users} key(s) play it; they go back to their normal function."
            : $"Delete \"{m.Name}\"?";
        if (MessageBox.Show(Window.GetWindow(this), msg, "AK980 Studio", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Macros.RemoveAt(i);
        RemapSlots(old => old == i ? null : old > i ? old - 1 : old);
        Save();
        RefreshList(i);
    }

    private void MacroUp_Click(object sender, RoutedEventArgs e) => MoveMacro(-1);

    private void MacroDown_Click(object sender, RoutedEventArgs e) => MoveMacro(1);

    private void MoveMacro(int dir)
    {
        int i = MacroList.SelectedIndex, j = i + dir;
        if (i < 0 || j < 0 || j >= Macros.Count) return;
        (Macros[i], Macros[j]) = (Macros[j], Macros[i]);
        RemapSlots(old => old == i ? j : old == j ? i : old);
        Save();
        RefreshList(j);
    }

    private static int KeysUsing(int slot) =>
        App.Settings.KeyMapTop.Values.Concat(App.Settings.KeyMapFn.Values).Count(a => a.Kind == KeyActionKind.Macro && a.MacroIndex == slot);

    /// <summary>Keeps key assignments pointing at the same macro after the list changes (null = macro deleted).</summary>
    private static void RemapSlots(Func<int, int?> map)
    {
        foreach (var layer in new[] { App.Settings.KeyMapTop, App.Settings.KeyMapFn })
        {
            foreach (var (index, action) in layer.ToList())
            {
                if (action.Kind != KeyActionKind.Macro) continue;
                if (map(action.MacroIndex) is int n) layer[index] = action with { MacroIndex = n };
                else layer.Remove(index);
            }
        }
    }

    // ------------------------------------------------------------ steps

    private void Step_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (Current is not { } m || StepList.SelectedIndex < 0 || StepList.SelectedIndex >= m.Steps.Count) return;
        _loading = true;
        DelaySlider.Value = Math.Min(m.Steps[StepList.SelectedIndex].DelayMs, (int)DelaySlider.Maximum);
        _loading = false;
    }

    private void SetSelectedDelays(int ms)
    {
        if (_loading || Current is not { } m) return;
        var rows = StepList.SelectedItems.Cast<object>().Select(o => StepList.Items.IndexOf(o)).Where(i => i >= 0 && i < m.Steps.Count).ToList();
        if (rows.Count == 0) return;
        foreach (var i in rows) m.Steps[i] = m.Steps[i] with { DelayMs = ms };
        int keep = StepList.SelectedIndex;
        Changed(keep);
    }

    private void DelayAll_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } m) return;
        for (int i = 0; i < m.Steps.Count; i++) m.Steps[i] = m.Steps[i] with { DelayMs = (int)DelaySlider.Value };
        Changed();
    }

    /// <summary>Where new steps go: after the selected row, or at the end.</summary>
    private int InsertIndex(Macro m) => StepList.SelectedIndex >= 0 ? Math.Min(StepList.SelectedIndex + 1, m.Steps.Count) : m.Steps.Count;

    private void Insert(IEnumerable<MacroStep> steps)
    {
        if (Current is not { } m) return;
        int at = InsertIndex(m);
        var list = steps.ToList();
        m.Steps.InsertRange(at, list);
        Changed(at + list.Count - 1);
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var text = Prompt.Ask(Window.GetWindow(this), "Text to type (US layout; characters without a key are skipped)");
        if (string.IsNullOrEmpty(text)) return;
        Insert(Macro.TextSteps(text, (int)DefaultDelay.Value));
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt.Ask(Window.GetWindow(this), "Key or shortcut, e.g. Enter, F5, Ctrl+Shift+Esc");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var a = KeyAction.Parse("key:" + name);
            Insert(Macro.ComboSteps(a.Modifiers, a.Code, (int)DefaultDelay.Value));
        }
        catch (FormatException ex)
        {
            Status.Text = ex.Message;
        }
    }

    private void AddClick_Click(object sender, RoutedEventArgs e)
    {
        byte mask = byte.Parse((string)((Button)sender).Tag);
        int d = (int)DefaultDelay.Value;
        Insert([new MacroStep(MacroAction.MouseDown, mask, d), new MacroStep(MacroAction.MouseUp, mask, d)]);
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } m) return;
        var rows = StepList.SelectedItems.Cast<object>().Select(o => StepList.Items.IndexOf(o)).Where(i => i >= 0).OrderByDescending(i => i).ToList();
        foreach (var i in rows) m.Steps.RemoveAt(i);
        Changed(rows.Count > 0 ? Math.Min(rows.Min(), m.Steps.Count - 1) : -1);
    }

    private void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);

    private void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);

    private void MoveStep(int dir)
    {
        if (Current is not { } m) return;
        int i = StepList.SelectedIndex, j = i + dir;
        if (i < 0 || j < 0 || j >= m.Steps.Count) return;
        (m.Steps[i], m.Steps[j]) = (m.Steps[j], m.Steps[i]);
        Changed(j);
    }

    private void ClearSteps_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } m || m.Steps.Count == 0) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Remove all {m.Steps.Count} steps of \"{m.Name}\"?", "AK980 Studio", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        m.Steps.Clear();
        Changed();
    }

    // ------------------------------------------------------------ recording

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        if (Current is not { } m) return;
        _recording = true;
        _insertAt = InsertIndex(m);
        _down.Clear();
        _sinceLast.Reset();
        RecordButton.Content = "■ Stop";
        RecordText.Text = "Recording… type now. Windows still reacts to the Win key and Alt+Tab, so avoid those here.";
        RecordBox.SetResourceReference(Border.BorderBrushProperty, "Bad");
        Focus();
        Keyboard.Focus(this);
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        RecordButton.Content = "● Record";
        RecordText.Text = "Record: press Record, then type on any keyboard. Steps are added at the selected row.";
        RecordBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        // Release anything still held so the macro never leaves a key stuck down.
        if (Current is { } m && _down.Count > 0)
        {
            foreach (var code in _down) m.Steps.Insert(_insertAt++, new MacroStep(MacroAction.KeyUp, code, (int)DefaultDelay.Value));
            _down.Clear();
            Changed(_insertAt - 1);
        }
    }

    private static int Vk(KeyEventArgs e) => KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);

    private void Record(MacroAction action, byte code)
    {
        if (Current is not { } m) return;
        // The pause belongs to the previous step (stock records store "delay after").
        int gap = RecordDelays.IsChecked == true && _sinceLast.IsRunning
            ? (int)Math.Clamp(_sinceLast.ElapsedMilliseconds, MacroCodec.MinDelayMs, MacroCodec.MaxDelayMs)
            : (int)DefaultDelay.Value;
        if (_insertAt > 0 && _insertAt <= m.Steps.Count && _sinceLast.IsRunning)
            m.Steps[_insertAt - 1] = m.Steps[_insertAt - 1] with { DelayMs = gap };
        m.Steps.Insert(_insertAt++, new MacroStep(action, code, (int)DefaultDelay.Value));
        _sinceLast.Restart();
        Changed(_insertAt - 1);
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        byte code = HidKeys.FromVirtualKey(Vk(e));
        if (code == 0 || !_down.Add(code)) return;
        Record(MacroAction.KeyDown, code);
    }

    private void Page_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        byte code = HidKeys.FromVirtualKey(Vk(e));
        if (code == 0 || !_down.Remove(code)) return;
        Record(MacroAction.KeyUp, code);
    }

    // ------------------------------------------------------------ upload

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        if (MacroCodec.Used(Macros) > MacroCodec.BudgetBytes)
        {
            Status.Text = "Too many steps for the keyboard's memory. Remove some first.";
            return;
        }
        Status.Text = "Uploading…";
        bool ok = await App.Keyboard.SetMacrosAsync(Macros.ToList());
        if (ok)
        {
            App.Settings.MacrosAppliedHash = App.Settings.MacrosHash();
            Save();
        }
        Status.Text = ok ? $"{Macros.Count} macro(s) are on the keyboard." : App.Keyboard.Status;
        UpdateMemory();
    }
}
