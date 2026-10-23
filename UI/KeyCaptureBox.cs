using JdAmulet.Input;

namespace JdAmulet.UI;

/// <summary>Поле захвата клавиши: щёлкнуть → «нажмите клавишу…» → нажать. Backspace/Delete — очистить, Esc — отмена.</summary>
public sealed class KeyCaptureBox : TextBox
{
    private Keys _value;

    public bool AllowModifiers { get; set; }

    public event EventHandler? ValueChanged;

    public Keys Value
    {
        get => _value;
        set
        {
            _value = AllowModifiers ? value : value & Keys.KeyCode;
            Text = KeyCodes.Display(_value);
        }
    }

    public KeyCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        BackColor = SystemColors.Window;
        TextAlign = HorizontalAlignment.Center;
        Text = KeyCodes.Display(Keys.None);
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        Text = "нажмите клавишу…";
        BackColor = Color.LightYellow;
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Text = KeyCodes.Display(_value);
        BackColor = SystemColors.Window;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

        Keys code = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;

        switch (code)
        {
            case Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin:
                if (AllowModifiers) Text = ModText(mods) + "…";
                return true;
            case Keys.Tab when mods == 0:
                return base.ProcessCmdKey(ref msg, keyData);
            case Keys.Escape when mods == 0:
                Text = KeyCodes.Display(_value);
                Parent?.SelectNextControl(this, true, true, true, true);
                return true;
            case Keys.Back or Keys.Delete when mods == 0:
                Commit(Keys.None);
                return true;
        }

        Commit(AllowModifiers ? keyData : code);
        return true;
    }

    private static string ModText(Keys m) =>
        ((m & Keys.Control) != 0 ? "Ctrl+" : "") +
        ((m & Keys.Shift) != 0 ? "Shift+" : "") +
        ((m & Keys.Alt) != 0 ? "Alt+" : "");

    private void Commit(Keys k)
    {
        Value = k;
        Text = KeyCodes.Display(_value);
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Поле набора клавиш скиллов: каждое нажатие добавляет клавишу, повторное нажатие той же — убирает.
/// Backspace — убрать последнюю, Delete — очистить всё.
/// </summary>
public sealed class MultiKeyCaptureBox : TextBox
{
    private readonly List<Keys> _keys = new();

    public event EventHandler? ValueChanged;

    public IReadOnlyList<Keys> SelectedKeys => _keys;

    public MultiKeyCaptureBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        BackColor = SystemColors.Window;
        UpdateText();
    }

    public void SetKeys(IEnumerable<Keys> keys)
    {
        _keys.Clear();
        foreach (Keys k in keys)
        {
            Keys c = k & Keys.KeyCode;
            if (c != Keys.None && !_keys.Contains(c)) _keys.Add(c);
        }
        UpdateText();
    }

    private void UpdateText()
    {
        string list = _keys.Count == 0 ? "" : string.Join(", ", _keys.Select(KeyCodes.Display));
        Text = Focused
            ? (list.Length == 0 ? "нажимайте клавиши скиллов…" : list + "   (ещё клавиша — добавить, Backspace — убрать)")
            : (list.Length == 0 ? "—" : list);
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        BackColor = Color.LightYellow;
        UpdateText();
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        BackColor = SystemColors.Window;
        UpdateText();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

        Keys code = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;
        if (mods != 0 || code is Keys.ShiftKey or Keys.ControlKey
                or Keys.Menu or Keys.LWin or Keys.RWin)
            return true;

        switch (code)
        {
            case Keys.Tab:
                return base.ProcessCmdKey(ref msg, keyData);
            case Keys.Escape:
                Parent?.SelectNextControl(this, true, true, true, true);
                return true;
            case Keys.Back:
                if (_keys.Count > 0) _keys.RemoveAt(_keys.Count - 1);
                break;
            case Keys.Delete:
                _keys.Clear();
                break;
            default:
                if (!_keys.Remove(code)) _keys.Add(code);
                break;
        }
        UpdateText();
        ValueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
