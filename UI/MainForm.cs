using JdAmulet.Config;
using JdAmulet.Engine;
using JdAmulet.Game;
using JdAmulet.Input;

namespace JdAmulet.UI;

public sealed class MainForm : Form
{
    private const int MaxLogLines = 500;

    private readonly AppConfig _config;
    private readonly GameWindowTracker _tracker;
    private readonly MacroEngine _engine;
    private readonly OverlayBar _overlay;
    private KeyboardHook? _hook;
    private bool _exiting;
    private bool _loading;
    private bool _trayHintShown;

    // одноразовый (не периодический) таймер отложенного сохранения при перетаскивании ползунков
    private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 700 };

    // --- элементы UI ---
    private readonly ComboBox _profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly TextBox _processName = new() { Width = 160 };
    private readonly ComboBox _inputMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly NumericUpDown _holdMs = new() { Minimum = AppConfig.MinHoldMs, Maximum = AppConfig.MaxHoldMs, Width = 60 };
    private readonly KeyCaptureBox _toggleKey = new() { Width = 110 };
    private readonly CheckBox _startMinimized = new() { Text = "Запускать свёрнутым в трей", AutoSize = true };
    private readonly CheckBox _overlayVisible = new() { Text = "Оверлей-бар", AutoSize = true };
    private readonly TrackBar _overlayOpacity = new() { Minimum = 20, Maximum = 100, TickFrequency = 10, Width = 140, AutoSize = false, Height = 30 };
    private readonly CheckBox _overlayLocked = new() { Text = "Закрепить положение", AutoSize = true };

    private readonly DataGridView _grid = new();
    private readonly TrackBar _delaySlider = new() { Minimum = 0, Maximum = AppConfig.MaxDelayMs, TickFrequency = 10, SmallChange = 1, LargeChange = 10, Dock = DockStyle.Fill, AutoSize = false, Height = 30 };
    private readonly Label _delayLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "Пауза выбранного амулета: —" };
    private readonly Button _testButton = new() { Text = "Тест (3 с)", AutoSize = true };

    private readonly ListBox _log = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, Font = new Font("Consolas", 9f) };
    private readonly CheckBox _logEnabled = new() { Text = "Вести лог", AutoSize = true };

    private readonly ToolStripStatusLabel _statusMacros = new();
    private readonly ToolStripStatusLabel _statusGame = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusTimer = new();

    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayToggle = new("Макросы включены");

    public MainForm(AppConfig config, string? loadError)
    {
        _config = config;
        _ = Handle; // нужен для BeginInvoke ещё до первого показа (старт в трее)

        Text = "JD Amulet Switcher";
        Icon = TrayIcons.On;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 680);
        MinimumSize = new Size(760, 560);

        BuildUi();

        _tracker = new GameWindowTracker(_config.ProcessName);
        _engine = new MacroEngine(_tracker, PostToUi);
        _overlay = new OverlayBar(_engine, _tracker, _config.Overlay);

        _engine.EnabledChanged += OnMacrosEnabledChanged;
        _engine.AmuletChanged += scan => _overlay.SetCurrentAmulet(scan);
        _engine.Logged += AppendLog;
        _tracker.Changed += UpdateGameStatus;
        _overlay.OffsetCommitted += SaveConfig;
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveConfig(); };

        BuildTray();
        LoadConfigIntoUi();
        ApplyConfig(save: false);

        try
        {
            _hook = new KeyboardHook(_engine);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось установить хук клавиатуры: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Emergency.ReleaseKeys = () => InputSender<MacroJob>.ReleaseKeys(_engine.UsedScans);

        if (loadError != null) _engine.Log(loadError);
        _engine.Log($"Запуск. Конфиг: {ConfigStore.FilePath}");
        _engine.Log($"Таймер пауз: {_engine.TimerMode}. Вкл/выкл макросов: {KeyCodes.Display(_config.ToggleKey)}.");
        UpdateGameStatus();
        UpdateMacrosStatus(_engine.Enabled);
    }

    private void PostToUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { /* окно закрывается */ }
    }

    // ===================================================================================
    // Построение интерфейса
    // ===================================================================================

    private static Label L(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
        f.Controls.AddRange(controls);
        return f;
    }

    private static Button B(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += click;
        return b;
    }

    private void BuildUi()
    {
        // --- Настройки ---
        _inputMode.Items.AddRange(new object[] { "SendInput (скан-коды) — основной", "PostMessage в окно игры — запасной" });

        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        settings.Controls.Add(Flow(
            L("Профиль:"), _profiles,
            B("Новый", (_, _) => NewProfile(false)),
            B("Копия", (_, _) => NewProfile(true)),
            B("Переименовать", (_, _) => RenameProfile()),
            B("Удалить", (_, _) => DeleteProfile())));
        settings.Controls.Add(Flow(
            L("Процесс игры:"), _processName, B("Применить", (_, _) => ApplyProcessName()),
            L("   Режим ввода:"), _inputMode));
        settings.Controls.Add(Flow(
            L("Клавиша вкл/выкл макросов:"), _toggleKey,
            L("   Длительность нажатия:"), _holdMs, L("мс (если игра не видит нажатия — увеличьте до 40–60)")));
        settings.Controls.Add(Flow(
            B("Сбросить состояние амулета (считать, что ничего не надето)", (_, _) => _engine.ResetAmuletState()), _startMinimized));
        settings.Controls.Add(Flow(
            _overlayVisible, L("  Непрозрачность:"), _overlayOpacity, _overlayLocked));
        var settingsBox = new GroupBox { Text = "Настройки", Dock = DockStyle.Fill, AutoSize = true };
        settingsBox.Controls.Add(settings);

        // --- Связки ---
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Вкл", FillWeight = 30 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Амулет", FillWeight = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Скиллы под этот амулет", FillWeight = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Пауза, мс", FillWeight = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Название", FillWeight = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Комбинация «надеть»", FillWeight = 85 });
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditGroup(); };
        _grid.SelectionChanged += (_, _) => SyncDelaySlider();

        _testButton.Click += async (_, _) => await TestBindingAsync();
        _delaySlider.ValueChanged += (_, _) => OnDelaySliderChanged();

        var gridButtons = Flow(
            B("Добавить амулет", (_, _) => AddGroup()),
            B("Изменить", (_, _) => EditGroup()),
            B("Удалить", (_, _) => DeleteGroup()),
            B("▲", (_, _) => MoveGroup(-1)),
            B("▼", (_, _) => MoveGroup(1)),
            _testButton);

        var sliderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        sliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        sliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sliderRow.Controls.Add(_delayLabel, 0, 0);
        sliderRow.Controls.Add(_delaySlider, 1, 0);

        var bindingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        bindingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bindingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bindingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bindingsLayout.Controls.Add(_grid, 0, 0);
        bindingsLayout.Controls.Add(sliderRow, 0, 1);
        bindingsLayout.Controls.Add(gridButtons, 0, 2);
        var bindingsBox = new GroupBox { Text = "Амулеты и скиллы: нажали скилл → надевается его амулет → срабатывает скилл", Dock = DockStyle.Fill };
        bindingsBox.Controls.Add(bindingsLayout);

        // --- Лог ---
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logLayout.Controls.Add(_log, 0, 0);
        logLayout.Controls.Add(Flow(_logEnabled, B("Очистить", (_, _) => _log.Items.Clear())), 0, 1);
        var logBox = new GroupBox { Text = "Лог (время от перехвата нажатия до отправки скилла)", Dock = DockStyle.Fill };
        logBox.Controls.Add(logLayout);

        // --- Статус ---
        var status = new StatusStrip();
        status.Items.AddRange(new ToolStripItem[] { _statusMacros, _statusGame, _statusTimer });

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.Controls.Add(settingsBox, 0, 0);
        root.Controls.Add(bindingsBox, 0, 1);
        root.Controls.Add(logBox, 0, 2);

        Controls.Add(root);
        Controls.Add(status);

        // --- события настроек ---
        _profiles.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _profiles.SelectedItem is not string name) return;
            _config.ActiveProfile = name;
            LoadProfileIntoUi();
            ApplyConfig();
        };
        _inputMode.SelectedIndexChanged += (_, _) => Change(() => _config.InputMode = (InputMode)_inputMode.SelectedIndex);
        _holdMs.ValueChanged += (_, _) => Change(() => _config.KeyHoldMs = (int)_holdMs.Value);
        _toggleKey.ValueChanged += (_, _) => Change(() => _config.ToggleKey = _toggleKey.Value == Keys.None ? Keys.Pause : _toggleKey.Value);
        _startMinimized.CheckedChanged += (_, _) => Change(() => _config.StartMinimized = _startMinimized.Checked);
        _logEnabled.CheckedChanged += (_, _) => Change(() => _config.LogEnabled = _logEnabled.Checked);
        _overlayVisible.CheckedChanged += (_, _) => Change(() => _config.Overlay.Visible = _overlayVisible.Checked);
        _overlayLocked.CheckedChanged += (_, _) => Change(() => _config.Overlay.Locked = _overlayLocked.Checked);
        _overlayOpacity.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _config.Overlay.Opacity = _overlayOpacity.Value / 100.0;
            _overlay.ApplyAppearance();
            ScheduleSave();
        };
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать / скрыть", null, (_, _) => ToggleWindow());
        _trayToggle.Click += (_, _) => _engine.Enabled = !_engine.Enabled;
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray.Text = "JD Amulet Switcher";
        _tray.Icon = TrayIcons.On;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleWindow();
        _tray.Visible = true;
    }

    // ===================================================================================
    // Конфиг ↔ UI
    // ===================================================================================

    private Profile Profile => _config.GetActiveProfile();

    private void LoadConfigIntoUi()
    {
        _loading = true;
        try
        {
            _processName.Text = _config.ProcessName;
            _inputMode.SelectedIndex = (int)_config.InputMode;
            _holdMs.Value = Math.Clamp(_config.KeyHoldMs, AppConfig.MinHoldMs, AppConfig.MaxHoldMs);
            _toggleKey.Value = _config.ToggleKey;
            _startMinimized.Checked = _config.StartMinimized;
            _logEnabled.Checked = _config.LogEnabled;
            _overlayVisible.Checked = _config.Overlay.Visible;
            _overlayLocked.Checked = _config.Overlay.Locked;
            _overlayOpacity.Value = Math.Clamp((int)Math.Round(_config.Overlay.Opacity * 100), 20, 100);
            RefreshProfileList();
        }
        finally { _loading = false; }
        LoadProfileIntoUi();
    }

    private void RefreshProfileList()
    {
        bool was = _loading;
        _loading = true;
        _profiles.Items.Clear();
        foreach (Profile p in _config.Profiles) _profiles.Items.Add(p.Name);
        _profiles.SelectedItem = Profile.Name;
        _loading = was;
    }

    private void LoadProfileIntoUi()
    {
        bool was = _loading;
        _loading = true;
        try
        {
            RefreshGrid();
        }
        finally { _loading = was; }
    }

    private void RefreshGrid(int select = -1)
    {
        if (select < 0) select = _grid.CurrentRow?.Index ?? 0;
        _grid.Rows.Clear();
        foreach (AmuletGroup g in Profile.Groups)
        {
            string skills = g.SkillKeys.Count == 0 ? "— (только кнопка/комбинация)" : string.Join(", ", g.SkillKeys.Select(KeyCodes.Display));
            _grid.Rows.Add(g.Enabled, KeyCodes.Display(g.AmuletKey), skills, g.DelayMs, g.Name, KeyCodes.Display(g.Hotkey));
        }
        if (_grid.Rows.Count > 0)
        {
            select = Math.Clamp(select, 0, _grid.Rows.Count - 1);
            _grid.CurrentCell = _grid.Rows[select].Cells[1];
        }
        SyncDelaySlider();
    }

    private void Change(Action apply)
    {
        if (_loading) return;
        apply();
        ApplyConfig();
    }

    private void ApplyConfig(bool save = true)
    {
        List<string> warnings = _engine.Apply(_config);
        foreach (string w in warnings) _engine.Log("⚠ " + w);
        _overlay.ApplyAppearance();
        if (save) SaveConfig();
    }

    private void ScheduleSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveConfig()
    {
        try { ConfigStore.Save(_config); }
        catch (Exception ex) { _engine.Log("Не удалось сохранить конфиг: " + ex.Message); }
    }

    // ===================================================================================
    // Связки
    // ===================================================================================

    private int SelectedIndex => _grid.CurrentRow?.Index ?? -1;

    private void AddGroup()
    {
        var g = new AmuletGroup { DelayMs = 20 };
        int n = Profile.Groups.Count;
        if (n < 9) g.Hotkey = Keys.Control | (Keys.D1 + n);
        using var dlg = new GroupEditForm(g);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Profile.Groups.Add(dlg.Result);
        RefreshGrid(Profile.Groups.Count - 1);
        ApplyConfig();
    }

    private void EditGroup()
    {
        int i = SelectedIndex;
        if (i < 0 || i >= Profile.Groups.Count) return;
        using var dlg = new GroupEditForm(Profile.Groups[i]);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Profile.Groups[i] = dlg.Result;
        RefreshGrid(i);
        ApplyConfig();
    }

    private void DeleteGroup()
    {
        int i = SelectedIndex;
        if (i < 0 || i >= Profile.Groups.Count) return;
        if (MessageBox.Show(this, $"Удалить «{Profile.Groups[i].DisplayName}»?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Profile.Groups.RemoveAt(i);
        RefreshGrid(i);
        ApplyConfig();
    }

    private void MoveGroup(int dir)
    {
        int i = SelectedIndex, j = i + dir;
        List<AmuletGroup> list = Profile.Groups;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        RefreshGrid(j);
        ApplyConfig();
    }

    private void SyncDelaySlider()
    {
        int i = SelectedIndex;
        bool has = i >= 0 && i < Profile.Groups.Count;
        bool was = _loading;
        _loading = true;
        _delaySlider.Enabled = has;
        _testButton.Enabled = has;
        if (has) _delaySlider.Value = Math.Clamp(Profile.Groups[i].DelayMs, 0, AppConfig.MaxDelayMs);
        _delayLabel.Text = has ? $"Пауза «{Profile.Groups[i].DisplayName}»: {_delaySlider.Value} мс" : "Пауза выбранного амулета: —";
        _loading = was;
    }

    private void OnDelaySliderChanged()
    {
        if (_loading) return;
        int i = SelectedIndex;
        if (i < 0 || i >= Profile.Groups.Count) return;
        AmuletGroup g = Profile.Groups[i];
        g.DelayMs = _delaySlider.Value;
        _delayLabel.Text = $"Пауза «{g.DisplayName}»: {g.DelayMs} мс";
        _grid.Rows[i].Cells[3].Value = g.DelayMs;
        _engine.Apply(_config); // мгновенно, без перестройки UI
        ScheduleSave();
    }

    private async Task TestBindingAsync()
    {
        int i = SelectedIndex;
        if (i < 0 || i >= Profile.Groups.Count) return;
        AmuletGroup cfg = Profile.Groups[i];
        CompiledGroup? g = _engine.Groups.FirstOrDefault(x => x.AmuletKey == (cfg.AmuletKey & Keys.KeyCode));
        if (g == null)
        {
            _engine.Log($"Тест: «{cfg.DisplayName}» выключен или настроен некорректно.");
            return;
        }

        string original = _testButton.Text;
        _testButton.Enabled = false;
        try
        {
            for (int s = 3; s > 0; s--)
            {
                _testButton.Text = $"Переключитесь в игру… {s}";
                await Task.Delay(1000); // UI-отсчёт; к вводу отношения не имеет
            }
            _engine.Test(g);
        }
        finally
        {
            _testButton.Text = original;
            _testButton.Enabled = true;
        }
    }

    // ===================================================================================
    // Профили
    // ===================================================================================

    private void NewProfile(bool copy)
    {
        string? name = Prompt.Show(this, copy ? "Копия профиля" : "Новый профиль", "Название профиля:",
            copy ? Profile.Name + " (копия)" : "Профиль " + (_config.Profiles.Count + 1));
        if (name == null) return;
        if (_config.Profiles.Any(p => p.Name == name))
        {
            MessageBox.Show(this, "Профиль с таким именем уже есть.", Text);
            return;
        }
        _config.Profiles.Add(copy ? Profile.Clone(name) : AppConfig.CreateDefaultProfile(name));
        _config.ActiveProfile = name;
        RefreshProfileList();
        LoadProfileIntoUi();
        ApplyConfig();
    }

    private void RenameProfile()
    {
        string? name = Prompt.Show(this, "Переименовать профиль", "Новое название:", Profile.Name);
        if (name == null || name == Profile.Name) return;
        if (_config.Profiles.Any(p => p.Name == name))
        {
            MessageBox.Show(this, "Профиль с таким именем уже есть.", Text);
            return;
        }
        Profile.Name = name;
        _config.ActiveProfile = name;
        RefreshProfileList();
        SaveConfig();
    }

    private void DeleteProfile()
    {
        if (_config.Profiles.Count <= 1)
        {
            MessageBox.Show(this, "Нельзя удалить единственный профиль.", Text);
            return;
        }
        if (MessageBox.Show(this, $"Удалить профиль «{Profile.Name}»?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _config.Profiles.Remove(Profile);
        _config.ActiveProfile = _config.Profiles[0].Name;
        RefreshProfileList();
        LoadProfileIntoUi();
        ApplyConfig();
    }

    private void ApplyProcessName()
    {
        string name = _processName.Text.Trim();
        if (name.Length == 0) return;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        _processName.Text = name;
        _config.ProcessName = name;
        _tracker.SetProcessName(name);
        SaveConfig();
    }

    // ===================================================================================
    // Статус, лог, трей
    // ===================================================================================

    private void OnMacrosEnabledChanged(bool enabled)
    {
        UpdateMacrosStatus(enabled);
        _overlay.SetMacrosEnabled(enabled);
    }

    private void UpdateMacrosStatus(bool enabled)
    {
        _statusMacros.Text = enabled ? "● Макросы ВКЛ" : "○ Макросы ВЫКЛ";
        _statusMacros.ForeColor = enabled ? Color.ForestGreen : Color.Firebrick;
        _trayToggle.Checked = enabled;
        _tray.Icon = enabled ? TrayIcons.On : TrayIcons.Off;
        _tray.Text = enabled ? "JD Amulet Switcher — ВКЛ" : "JD Amulet Switcher — ВЫКЛ";
        _statusTimer.Text = "Таймер: " + _engine.TimerMode;
    }

    private void UpdateGameStatus()
    {
        nint h = _tracker.GameHwnd;
        _statusGame.Text = h == 0
            ? $"Игра: окно {_tracker.ProcessName} не найдено (будет подхвачено при активации)"
            : $"Игра: {_tracker.ProcessName} (PID {_tracker.GamePid}, HWND 0x{h:X})" +
              (_tracker.IsGameForeground ? " — активна" : "");
    }

    private void AppendLog(LogEntry e)
    {
        _log.BeginUpdate();
        _log.Items.Add($"{e.Time:HH:mm:ss.fff}  {e.Text}");
        while (_log.Items.Count > MaxLogLines) _log.Items.RemoveAt(0);
        _log.TopIndex = _log.Items.Count - 1;
        _log.EndUpdate();
    }

    private void ToggleWindow()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            Hide();
        }
        else
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide(); // сворачиваем в трей
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2500, "JD Amulet Switcher", "Программа работает в трее. Выход — через меню значка.", ToolTipIcon.Info);
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Shutdown();
        base.OnFormClosed(e);
    }

    private void Shutdown()
    {
        _saveDebounce.Stop();
        _hook?.Dispose();          // 1) больше никаких перехватов
        _hook = null;
        _engine.Dispose();         // 2) останавливаем поток ввода
        Emergency.ReleaseKeys = null;
        _overlay.Close();
        _tracker.Dispose();        // 3) снимаем WinEvent-хуки
        _tray.Visible = false;
        _tray.Dispose();
        SaveConfig();
    }
}

/// <summary>Простой диалог ввода строки.</summary>
internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string label, string value)
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 9f),
            Padding = new Padding(10),
        };
        var box = new TextBox { Text = value, Width = 280 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        layout.Controls.AddRange(new Control[] { new Label { Text = label, AutoSize = true }, box, buttons });
        f.Controls.Add(layout);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        string s = box.Text.Trim();
        return s.Length == 0 ? null : s;
    }
}

/// <summary>Иконки рисуются в рантайме — внешние ресурсы не нужны.</summary>
internal static class TrayIcons
{
    public static readonly Icon On = Make(Color.FromArgb(38, 150, 70));
    public static readonly Icon Off = Make(Color.FromArgb(120, 120, 120));

    private static Icon Make(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("A", font, Brushes.White, new RectangleF(0, 1, 32, 32), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
