using JdAmulet.Config;
using JdAmulet.Input;

namespace JdAmulet.UI;

public sealed class BindingEditForm : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly KeyCaptureBox _skill = new() { Dock = DockStyle.Fill };
    private readonly KeyCaptureBox _amulet = new() { Dock = DockStyle.Fill };
    private readonly KeyCaptureBox _hotkey = new() { Dock = DockStyle.Fill, AllowModifiers = true };
    private readonly TrackBar _delay = new() { Minimum = 0, Maximum = AppConfig.MaxDelayMs, TickFrequency = 10, SmallChange = 1, LargeChange = 10, Dock = DockStyle.Fill };
    private readonly NumericUpDown _delayNum = new() { Minimum = 0, Maximum = AppConfig.MaxDelayMs, Width = 64 };
    private readonly CheckBox _enabled = new() { Text = "Связка включена", AutoSize = true };
    private readonly Label _hint = new() { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(420, 0) };

    public BindingConfig Result { get; }

    public BindingEditForm(BindingConfig source)
    {
        Result = source.Clone();

        Text = "Связка «скилл → амулет»";
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
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

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

        Row("Название:", _name);
        Row("Клавиша скилла:", _skill);
        Row("Клавиша амулета:", _amulet);
        Row("Задержка:", delayPanel);
        Row("Комбинация бара:", _hotkey);
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

        _name.Text = Result.Name;
        _skill.Value = Result.SkillKey;
        _amulet.Value = Result.AmuletKey;
        _hotkey.Value = Result.Hotkey;
        _delay.Value = Math.Clamp(Result.DelayMs, 0, AppConfig.MaxDelayMs);
        _delayNum.Value = _delay.Value;
        _enabled.Checked = Result.Enabled;

        _delay.ValueChanged += (_, _) => _delayNum.Value = _delay.Value;
        _delayNum.ValueChanged += (_, _) => _delay.Value = (int)_delayNum.Value;
        _skill.ValueChanged += (_, _) => UpdateHint();
        _amulet.ValueChanged += (_, _) => UpdateHint();
        UpdateHint();

        ok.Click += (_, e) =>
        {
            if (_skill.Value == Keys.None || _amulet.Value == Keys.None)
            {
                MessageBox.Show(this, "Укажите клавишу скилла и клавишу амулета.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            if (_skill.Value == _amulet.Value)
            {
                MessageBox.Show(this, "Скилл и амулет не могут быть на одной клавише.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            Result.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Связка" : _name.Text.Trim();
            Result.SkillKey = _skill.Value;
            Result.AmuletKey = _amulet.Value;
            Result.Hotkey = _hotkey.Value;
            Result.DelayMs = _delay.Value;
            Result.Enabled = _enabled.Checked;
        };
    }

    private void UpdateHint()
    {
        bool odd = (_skill.Value != Keys.None && !KeyCodes.IsHotbarKey(_skill.Value)) ||
                   (_amulet.Value != Keys.None && !KeyCodes.IsHotbarKey(_amulet.Value));
        _hint.Text = odd ? "Внимание: панель быстрого доступа игры использует только клавиши 1–0 и F1–F8." : "";
    }
}
