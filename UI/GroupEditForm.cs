using JdAmulet.Config;
using JdAmulet.Input;

namespace JdAmulet.UI;

/// <summary>Редактирование строки «амулет → скиллы».</summary>
public sealed class GroupEditForm : Form
{
    private readonly KeyCaptureBox _amulet = new() { Dock = DockStyle.Fill };
    private readonly MultiKeyCaptureBox _skills = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, PlaceholderText = "необязательно" };
    private readonly KeyCaptureBox _hotkey = new() { Dock = DockStyle.Fill, AllowModifiers = true };
    private readonly TrackBar _delay = new() { Minimum = 0, Maximum = AppConfig.MaxDelayMs, TickFrequency = 10, SmallChange = 1, LargeChange = 10, Dock = DockStyle.Fill };
    private readonly NumericUpDown _delayNum = new() { Minimum = 0, Maximum = AppConfig.MaxDelayMs, Width = 64 };
    private readonly CheckBox _enabled = new() { Text = "Включено", AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(460, 0) };

    public AmuletGroup Result { get; }

    public GroupEditForm(AmuletGroup source)
    {
        Result = source.Clone();

        Text = "Амулет и его скиллы";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));

        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) });
            grid.Controls.Add(c);
        }

        var delayPanel = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, AutoSize = true };
        delayPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        delayPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        delayPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        delayPanel.Controls.Add(_delay, 0, 0);
        delayPanel.Controls.Add(_delayNum, 1, 0);
        delayPanel.Controls.Add(new Label { Text = "мс", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);

        Row("Клавиша амулета:", _amulet);
        Row("Скиллы под амулет:", _skills);
        Row("Пауза амулет → скилл:", delayPanel);
        Row("Название:", _name);
        Row("Комбинация «надеть»:", _hotkey);
        grid.Controls.Add(new Label { AutoSize = true });
        grid.Controls.Add(_enabled);
        grid.SetColumnSpan(_hint, 2);
        grid.Controls.Add(_hint);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.SetColumnSpan(buttons, 2);
        grid.Controls.Add(buttons);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;

        _amulet.Value = Result.AmuletKey;
        _skills.SetKeys(Result.SkillKeys);
        _name.Text = Result.Name;
        _hotkey.Value = Result.Hotkey;
        _delay.Value = Math.Clamp(Result.DelayMs, 0, AppConfig.MaxDelayMs);
        _delayNum.Value = _delay.Value;
        _enabled.Checked = Result.Enabled;

        _delay.ValueChanged += (_, _) => _delayNum.Value = _delay.Value;
        _delayNum.ValueChanged += (_, _) => _delay.Value = (int)_delayNum.Value;
        _amulet.ValueChanged += (_, _) => UpdateHint();
        _skills.ValueChanged += (_, _) => UpdateHint();
        UpdateHint();

        ok.Click += (_, _) =>
        {
            if (_amulet.Value == Keys.None)
            {
                MessageBox.Show(this, "Укажите клавишу амулета.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            if (_skills.SelectedKeys.Contains(_amulet.Value))
            {
                MessageBox.Show(this, "Клавиша амулета не может быть одновременно скиллом.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            Result.AmuletKey = _amulet.Value;
            Result.SkillKeys = _skills.SelectedKeys.ToList();
            Result.Name = _name.Text.Trim();
            Result.Hotkey = _hotkey.Value;
            Result.DelayMs = _delay.Value;
            Result.Enabled = _enabled.Checked;
        };
    }

    private void UpdateHint()
    {
        string skills = _skills.SelectedKeys.Count == 0 ? "—" : string.Join(", ", _skills.SelectedKeys.Select(KeyCodes.Display));
        string amulet = KeyCodes.Display(_amulet.Value);
        bool odd = (_amulet.Value != Keys.None && !KeyCodes.IsHotbarKey(_amulet.Value)) ||
                   _skills.SelectedKeys.Any(k => !KeyCodes.IsHotbarKey(k));
        _hint.ForeColor = odd ? Color.DarkOrange : Color.DimGray;
        _hint.Text = (_skills.SelectedKeys.Count == 0
                         ? $"Без скиллов: амулет {amulet} надевается только кнопкой на панели или комбинацией."
                         : $"Нажатие {skills} в игре: сначала надевается амулет {amulet}, затем срабатывает скилл.")
                     + (odd ? "\nВнимание: панель игры использует только клавиши 1–0 и F1–F8." : "");
    }
}
