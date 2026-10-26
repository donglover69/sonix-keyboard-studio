using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ajazz.Keyboard;
using Ajazz.Keyboard.Layout;
using AK980Studio.Controls;

namespace AK980Studio.Pages;

public partial class KeysPage : UserControl
{
    private static readonly (string Label, KeyActionKind Kind)[] Kinds =
    [
        ("Its normal function", KeyActionKind.Default),
        ("Nothing (disabled)", KeyActionKind.Disabled),
        ("Another key or shortcut", KeyActionKind.Key),
        ("Mouse button or wheel", KeyActionKind.Mouse),
        ("Media / launcher key", KeyActionKind.Media),
        ("Play a macro", KeyActionKind.Macro),
        ("Several keys at once", KeyActionKind.MultiKey),
    ];

    /// <summary>The stock app's "Windows Shortcut" presets (DeviceDriver.exe 0x418030 type 7).</summary>
    private static readonly (string Label, KeyModifiers Mods, byte Code)[] Shortcuts =
    [
        ("Show desktop", KeyModifiers.Win, 0x07), ("File Explorer", KeyModifiers.Win, 0x08), ("Lock PC", KeyModifiers.Win, 0x0F),
        ("Close tab", KeyModifiers.Ctrl, 0x1A), ("Switch windows", KeyModifiers.Alt, 0x2B), ("Copy", KeyModifiers.Ctrl, 0x06),
        ("Paste", KeyModifiers.Ctrl, 0x19), ("Cut", KeyModifiers.Ctrl, 0x1B),
    ];

    /// <summary>Stock play-mode names (strings 242..247) in combo order.</summary>
    private static readonly string[] PlayModes = ["Play once", "Play several times", "Stop when the key is pressed again", "Play while held (release to stop)", "Click to play"];

    private KeyMapLayer _layer = KeyMapLayer.Top;
    private LayoutKey? _key;
    private bool _loading;
    private bool _capturing;
    private byte _pendingModifier;
    private Button? _captureButton;

    public KeysPage()
    {
        InitializeComponent();
        foreach (var (label, layer) in new[] { ("Normal", KeyMapLayer.Top), ("While Fn is held", KeyMapLayer.Fn) })
        {
            var rb = new RadioButton { Content = label, GroupName = "layer", Style = (Style)FindResource("Segment"), IsChecked = layer == _layer, Tag = layer };
            rb.Checked += (_, _) => { _layer = (KeyMapLayer)rb.Tag; StopCapture(); Refresh(); if (_key is not null) Select(_key); };
            LayerPanel.Children.Add(rb);
        }
        foreach (var (label, _) in Kinds) KindBox.Items.Add(label);
        Board.KeyClicked += Select;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Refresh();
    }

    private Dictionary<int, KeyAction> Map => _layer == KeyMapLayer.Fn ? App.Settings.KeyMapFn : App.Settings.KeyMapTop;

    private IReadOnlyList<string> MacroNames => App.Settings.Macros.Select((m, i) => $"{i}: {m.Name}").ToList();

    private string Describe(KeyAction a) => a.Kind == KeyActionKind.Macro && a.MacroIndex < App.Settings.Macros.Count
        ? a.Describe(App.Settings.Macros.Select(m => m.Name).ToList())
        : a.Describe();

    private void Refresh()
    {
        Board.ClearDetails();
        foreach (var (index, action) in Map)
            if (!action.IsDefault) Board.SetDetail(index, Describe(action), $"{Board.Layout.ByIndex(index)?.Name} → {Describe(action)}");

        RemapList.Children.Clear();
        var all = App.Settings.KeyMapTop.Select(kv => (Layer: KeyMapLayer.Top, kv.Key, kv.Value))
            .Concat(App.Settings.KeyMapFn.Select(kv => (Layer: KeyMapLayer.Fn, kv.Key, kv.Value)))
            .Where(r => !r.Value.IsDefault)
            .OrderBy(r => r.Layer).ThenBy(r => r.Key);
        foreach (var (layer, index, action) in all)
        {
            var name = Board.Layout.ByIndex(index)?.Name ?? $"key {index}";
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Content = "Reset", Padding = new Thickness(10, 3, 10, 3), Style = (Style)FindResource("Ghost") };
            remove.Click += (_, _) => { (layer == KeyMapLayer.Fn ? App.Settings.KeyMapFn : App.Settings.KeyMapTop).Remove(index); Save(); Refresh(); if (_key?.Index == index) Select(_key); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new TextBlock { Text = $"{(layer == KeyMapLayer.Fn ? "Fn + " : "")}{name}  →  {Describe(action)}", VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(text);
            RemapList.Children.Add(row);
        }
        NoRemaps.Visibility = RemapList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int count = Map.Count(kv => !kv.Value.IsDefault);
        bool unsent = App.Settings.KeysHash() != App.Settings.KeysAppliedHash;
        Summary.Text = $"{count} remapped on this layer{(unsent ? " · not applied yet" : "")}";
    }

    private void Select(LayoutKey key)
    {
        StopCapture();
        _key = key;
        Board.Selected = key.Index;
        EditorHost.IsEnabled = true;
        KeyTitle.Text = _layer == KeyMapLayer.Fn ? $"Fn + {key.Name}" : key.Name;
        KeyHint.Text = _layer == KeyMapLayer.Fn
            ? "Normal function: whatever the firmware does with Fn + this key."
            : $"Normal function: {key.Name}.";
        var action = Map.GetValueOrDefault(key.Index) ?? KeyAction.Default;
        _loading = true;
        KindBox.SelectedIndex = Array.FindIndex(Kinds, k => k.Kind == action.Kind);
        _loading = false;
        BuildEditor(action);
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _key is null || KindBox.SelectedIndex < 0) return;
        var kind = Kinds[KindBox.SelectedIndex].Kind;
        var action = kind switch
        {
            KeyActionKind.Key => new KeyAction { Kind = kind, Code = (byte)Math.Clamp(_key.Code, 0, 0xFF) },
            KeyActionKind.Media => new KeyAction { Kind = kind, Usage = MediaKeys.All[0].Usage },
            KeyActionKind.MultiKey => new KeyAction { Kind = kind, Keys = [0xE0, 0x06] },
            _ => new KeyAction { Kind = kind },
        };
        Set(action);
        BuildEditor(action);
    }

    private void Set(KeyAction action)
    {
        if (_key is null) return;
        if (action.IsDefault) Map.Remove(_key.Index);
        else Map[_key.Index] = action;
        EntryHex.Text = $"Sent as {Convert.ToHexString(action.ToEntry())} at key {_key.Index}";
        Save();
        Refresh();
    }

    private static void Save() => App.Settings.Save();

    // ------------------------------------------------------------ editors

    private ComboBox KeyCombo(byte selected, bool allowNone = false)
    {
        var box = new ComboBox { Margin = new Thickness(0, 0, 0, 6), MaxDropDownHeight = 320 };
        if (allowNone) box.Items.Add(new ComboBoxItem { Content = "(none)", Tag = (byte)0 });
        foreach (var (name, code) in HidKeys.All)
            box.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (byte)i.Tag == selected);
        return box;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, Margin = new Thickness(0, 6, 0, 4), Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Fg1") };

    private void BuildEditor(KeyAction action)
    {
        Editor.Children.Clear();
        _captureButton = null;
        EntryHex.Text = action.IsDefault ? "" : $"Sent as {Convert.ToHexString(action.ToEntry())} at key {_key?.Index}";
        switch (action.Kind)
        {
            case KeyActionKind.Key:
            {
                _captureButton = new Button { Content = "Press a key to capture it…", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
                _captureButton.Click += (_, _) => StartCapture();
                Editor.Children.Add(_captureButton);
                Editor.Children.Add(Label("Key"));
                var keyBox = KeyCombo(action.Code, allowNone: true);
                keyBox.SelectionChanged += (_, _) => { if (keyBox.SelectedItem is ComboBoxItem i) Set(Current() with { Code = (byte)i.Tag }); };
                Editor.Children.Add(keyBox);
                Editor.Children.Add(Label("Hold together with"));
                var mods = new WrapPanel();
                foreach (var (name, flag) in new[] { ("Ctrl", KeyModifiers.Ctrl), ("Shift", KeyModifiers.Shift), ("Alt", KeyModifiers.Alt), ("Win", KeyModifiers.Win) })
                {
                    var cb = new CheckBox { Content = name, IsChecked = action.Modifiers.HasFlag(flag), Margin = new Thickness(0, 0, 14, 0) };
                    cb.Click += (_, _) =>
                    {
                        var cur = Current();
                        Set(cur with { Modifiers = cb.IsChecked == true ? cur.Modifiers | flag : cur.Modifiers & ~flag });
                    };
                    mods.Children.Add(cb);
                }
                Editor.Children.Add(mods);
                Editor.Children.Add(Label("Windows shortcuts"));
                var presets = new WrapPanel();
                foreach (var (label, m, code) in Shortcuts)
                {
                    var b = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4) };
                    b.Click += (_, _) => { var a = new KeyAction { Kind = KeyActionKind.Key, Modifiers = m, Code = code }; Set(a); BuildEditor(a); };
                    presets.Children.Add(b);
                }
                Editor.Children.Add(presets);
                break;
            }
            case KeyActionKind.Mouse:
            {
                var box = new ComboBox();
                foreach (var n in KeyAction.MouseNames) box.Items.Add(n);
                box.SelectedIndex = (int)action.Mouse;
                box.SelectionChanged += (_, _) => Set(Current() with { Mouse = (MouseFunction)box.SelectedIndex });
                Editor.Children.Add(box);
                break;
            }
            case KeyActionKind.Media:
            {
                var box = new ComboBox { MaxDropDownHeight = 320 };
                foreach (var (name, usage) in MediaKeys.All) box.Items.Add(new ComboBoxItem { Content = name, Tag = usage });
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (ushort)i.Tag == action.Usage);
                box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem i) Set(Current() with { Usage = (ushort)i.Tag }); };
                Editor.Children.Add(box);
                break;
            }
            case KeyActionKind.Macro:
            {
                if (App.Settings.Macros.Count == 0)
                {
                    Editor.Children.Add(new TextBlock { Text = "No macros yet. Make one on the Macros page first.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
                    break;
                }
                var box = new ComboBox();
                foreach (var n in MacroNames) box.Items.Add(n);
                box.SelectedIndex = Math.Clamp(action.MacroIndex, 0, App.Settings.Macros.Count - 1);
                if (box.SelectedIndex != action.MacroIndex) Set(action with { MacroIndex = box.SelectedIndex });
                box.SelectionChanged += (_, _) => Set(Current() with { MacroIndex = box.SelectedIndex });
                Editor.Children.Add(box);
                Editor.Children.Add(Label("How it plays"));
                var mode = new ComboBox();
                foreach (var n in PlayModes) mode.Items.Add(n);
                mode.SelectedIndex = (int)action.MacroMode;
                Editor.Children.Add(mode);
                var count = new LabeledSlider { Label = "Times", Minimum = 1, Maximum = 255, Integer = true, Format = "{0:0}×", Value = Math.Max(1, action.MacroCount), Margin = new Thickness(0, 8, 0, 0) };
                count.Visibility = action.MacroMode == MacroPlayMode.Repeat ? Visibility.Visible : Visibility.Collapsed;
                mode.SelectionChanged += (_, _) =>
                {
                    count.Visibility = mode.SelectedIndex == (int)MacroPlayMode.Repeat ? Visibility.Visible : Visibility.Collapsed;
                    Set(Current() with { MacroMode = (MacroPlayMode)mode.SelectedIndex });
                };
                count.ValueChanged += (_, _) => Set(Current() with { MacroCount = (int)count.Value });
                Editor.Children.Add(count);
                break;
            }
            case KeyActionKind.MultiKey:
            {
                Editor.Children.Add(new TextBlock { Text = "Up to three keys, pressed together.", Style = (Style)FindResource("Hint") });
                for (int slot = 0; slot < 3; slot++)
                {
                    int s = slot;
                    var box = KeyCombo(s < action.Keys.Length ? action.Keys[s] : (byte)0, allowNone: true);
                    box.Margin = new Thickness(0, 6, 0, 0);
                    box.SelectionChanged += (_, _) =>
                    {
                        var keys = Current().Keys.Concat(new byte[3]).Take(3).ToArray();
                        keys[s] = box.SelectedItem is ComboBoxItem i ? (byte)i.Tag : (byte)0;
                        Set(Current() with { Keys = keys.Where(k => k != 0).ToArray() });
                    };
                    Editor.Children.Add(box);
                }
                break;
            }
        }
    }

    private KeyAction Current() => _key is not null && Map.TryGetValue(_key.Index, out var a) ? a : KeyAction.Default;

    // ------------------------------------------------------------ key capture

    private void StartCapture()
    {
        _capturing = true;
        _pendingModifier = 0;
        if (_captureButton is not null) _captureButton.Content = "Listening… press the key or shortcut";
        Focus();
        Keyboard.Focus(this);
    }

    private void StopCapture()
    {
        _capturing = false;
        if (_captureButton is not null) _captureButton.Content = "Press a key to capture it…";
    }

    private static int Vk(KeyEventArgs e) => KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        byte code = HidKeys.FromVirtualKey(Vk(e));
        if (code is >= 0xE0 and <= 0xE7)
        {
            _pendingModifier = code;
            return;
        }
        if (code == 0) return;
        var m = Keyboard.Modifiers;
        var mods = (m.HasFlag(ModifierKeys.Control) ? KeyModifiers.Ctrl : 0) | (m.HasFlag(ModifierKeys.Shift) ? KeyModifiers.Shift : 0)
                   | (m.HasFlag(ModifierKeys.Alt) ? KeyModifiers.Alt : 0) | (m.HasFlag(ModifierKeys.Windows) ? KeyModifiers.Win : 0);
        Finish(new KeyAction { Kind = KeyActionKind.Key, Modifiers = mods, Code = code });
    }

    private void Page_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        // A modifier pressed and released on its own becomes that modifier key.
        if (_pendingModifier != 0 && HidKeys.FromVirtualKey(Vk(e)) == _pendingModifier)
            Finish(new KeyAction { Kind = KeyActionKind.Key, Code = _pendingModifier });
    }

    private void Finish(KeyAction a)
    {
        StopCapture();
        Set(a);
        BuildEditor(a);
    }

    // ------------------------------------------------------------ buttons

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        StopCapture();
        Status.Text = "Writing both layers…";
        bool ok = await App.Keyboard.SetKeyMapsAsync(App.Settings.KeyMapTop, App.Settings.KeyMapFn);
        if (ok)
        {
            App.Settings.KeysAppliedHash = App.Settings.KeysHash();
            Save();
        }
        Status.Text = ok ? "Remaps are on the keyboard." : App.Keyboard.Status;
        Refresh();
    }

    private void ResetLayer_Click(object sender, RoutedEventArgs e)
    {
        Map.Clear();
        Save();
        Refresh();
        if (_key is not null) Select(_key);
        Status.Text = "Layer reset here. Press Apply to send it.";
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.KeyMapTop.Clear();
        App.Settings.KeyMapFn.Clear();
        Save();
        Refresh();
        if (_key is not null) Select(_key);
        Status.Text = "All remaps cleared here. Press Apply to send them.";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var summary = App.Settings.ImportStockKeys();
        Save();
        Refresh();
        if (_key is not null) Select(_key);
        Status.Text = summary is null ? "No stock app data found." : $"Imported {summary}.";
    }
}
