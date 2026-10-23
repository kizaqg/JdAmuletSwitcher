using JdAmulet.Config;
using JdAmulet.Engine;
using JdAmulet.Game;
using JdAmulet.Input;
using JdAmulet.Native;
using static JdAmulet.Native.NativeMethods;

namespace JdAmulet.UI;

/// <summary>
/// Панель поверх окна игры: [ВКЛ/ВЫКЛ] [⠿ ручка] [амулеты…].
/// Не активируется (WS_EX_NOACTIVATE + MA_NOACTIVATE) — клик не отнимает фокус у игры.
/// Позиция обновляется только по событиям GameWindowTracker; перерисовка — только при смене состояния.
/// </summary>
public sealed class OverlayBar : Form
{
    private sealed class Item
    {
        public required CompiledGroup Group;
        public required string Text;
        public Rectangle Rect;
    }

    private readonly MacroEngine _engine;
    private readonly GameWindowTracker _tracker;
    private readonly OverlayConfig _cfg;
    private readonly List<Item> _items = new();
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 150 }; // одноразовая подсветка нажатия

    private Rectangle _statusRect, _gripRect;
    private int _currentAmulet;
    private bool _macrosEnabled = true;
    private bool _shown;
    private int _barWidth, _barHeight;
    private Item? _flash;

    private bool _dragging;
    private Point _dragStartCursor;
    private int _dragStartX, _dragStartY;

    private static readonly Color BackDark = Color.FromArgb(22, 22, 26);
    private static readonly Color ItemNormal = Color.FromArgb(52, 54, 62);
    private static readonly Color ItemFlash = Color.FromArgb(110, 115, 135);
    private static readonly Color Equipped = Color.FromArgb(60, 200, 90);
    private static readonly Color OnColor = Color.FromArgb(38, 150, 70);
    private static readonly Color OffColor = Color.FromArgb(180, 45, 45);
    private static readonly Color Border = Color.FromArgb(90, 92, 104);

    /// <summary>Пользователь закончил перетаскивание — позицию нужно сохранить.</summary>
    public event Action? OffsetCommitted;

    public OverlayBar(MacroEngine engine, GameWindowTracker tracker, OverlayConfig cfg)
    {
        _engine = engine;
        _tracker = tracker;
        _cfg = cfg;

        Text = "JD Amulet Bar";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = BackDark;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Opacity = cfg.Opacity;
        Location = new Point(-32000, -32000);
        Size = new Size(10, 10);

        _ = Handle; // окно создаётся сразу, показывается только через ShowWindow(SW_SHOWNOACTIVATE)

        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flash = null;
            Invalidate();
        };
        _tracker.Changed += UpdatePlacement;
        _tracker.Moved += UpdatePlacement;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    // ===================================================================================
    // Состояние
    // ===================================================================================

    public void Rebuild()
    {
        _items.Clear();
        foreach (CompiledGroup g in _engine.Groups)
            _items.Add(new Item { Group = g, Text = Label(g) });

        float scale = DeviceDpi / 96f;
        int pad = (int)(12 * scale);
        int gap = (int)(3 * scale);
        _barHeight = (int)(26 * scale);
        int h = _barHeight - 4;

        int statusW = TextRenderer.MeasureText("ВЫКЛ", Font).Width + pad;
        _statusRect = new Rectangle(2, 2, statusW, h);
        int x = _statusRect.Right + gap;

        int gripW = _cfg.Locked ? 0 : (int)(16 * scale);
        _gripRect = new Rectangle(x, 2, gripW, h);
        x += gripW + (gripW > 0 ? gap : 0);

        foreach (Item it in _items)
        {
            int w = TextRenderer.MeasureText(it.Text, Font).Width + pad;
            it.Rect = new Rectangle(x, 2, w, h);
            x += w + gap;
        }
        _barWidth = x + 1;
        _currentAmulet = _engine.CurrentAmulet;
        _macrosEnabled = _engine.Enabled;

        Invalidate();
        UpdatePlacement();
    }

    private static string Label(CompiledGroup g)
    {
        string skills = g.SkillKeys.Length == 0 ? "" : " → " + string.Join(",", g.SkillKeys.Select(KeyCodes.Display));
        string hk = (g.Hotkey & Keys.KeyCode) != Keys.None ? "  [" + KeyCodes.Display(g.Hotkey) + "]" : "";
        return g.ShortLabel + skills + hk;
    }

    public void SetCurrentAmulet(int scan)
    {
        if (scan == _currentAmulet) return;
        _currentAmulet = scan;
        if (_shown) Invalidate();
    }

    public void SetMacrosEnabled(bool enabled)
    {
        if (enabled == _macrosEnabled) return;
        _macrosEnabled = enabled;
        if (_shown) Invalidate();
    }

    public void ApplyAppearance()
    {
        Opacity = Math.Clamp(_cfg.Opacity, 0.2, 1.0);
        Rebuild();
    }

    // ===================================================================================
    // Позиционирование (только по событиям)
    // ===================================================================================

    public void UpdatePlacement()
    {
        if (IsDisposed) return;
        nint game = _tracker.GameHwnd;
        bool show = _cfg.Visible && game != 0 && _tracker.IsGameForeground && !IsIconic(game);

        if (!show)
        {
            if (_shown)
            {
                ShowWindow(Handle, SW_HIDE);
                _shown = false;
            }
            return;
        }

        GetClientRect(game, out RECT rc);
        var origin = new POINT();
        ClientToScreen(game, ref origin);

        int x = Math.Clamp(_cfg.OffsetX, 0, Math.Max(0, rc.Width - _barWidth));
        int y = Math.Clamp(_cfg.OffsetY, 0, Math.Max(0, rc.Height - _barHeight));

        SetWindowPos(Handle, HWND_TOPMOST, origin.X + x, origin.Y + y, _barWidth, _barHeight,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER);

        if (!_shown)
        {
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            _shown = true;
        }
    }

    // ===================================================================================
    // Отрисовка
    // ===================================================================================

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackDark);
        var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        // Индикатор: включены ли макросы
        using (var b = new SolidBrush(_macrosEnabled ? OnColor : OffColor))
            g.FillRectangle(b, _statusRect);
        TextRenderer.DrawText(g, _macrosEnabled ? "ВКЛ" : "ВЫКЛ", Font, _statusRect, Color.White, center);

        // Ручка перетаскивания
        if (_gripRect.Width > 0)
        {
            using var dot = new SolidBrush(Color.FromArgb(160, 160, 170));
            int cx = _gripRect.X + _gripRect.Width / 2 - 3;
            for (int y = _gripRect.Y + 4; y < _gripRect.Bottom - 4; y += 4)
            {
                g.FillRectangle(dot, cx, y, 2, 2);
                g.FillRectangle(dot, cx + 4, y, 2, 2);
            }
        }

        using var border = new Pen(Border);
        using var equippedBrush = new SolidBrush(Equipped);
        int stripe = Math.Max(3, _barHeight / 8);
        foreach (Item it in _items)
        {
            bool equipped = _currentAmulet != 0 && it.Group.Amulet.Scan == _currentAmulet;
            using (var brush = new SolidBrush(it == _flash ? ItemFlash : ItemNormal))
                g.FillRectangle(brush, it.Rect);
            // Надетый амулет — тонкая зелёная полоска снизу, кнопка при этом не «залипает»
            if (equipped)
                g.FillRectangle(equippedBrush, it.Rect.X, it.Rect.Bottom - stripe, it.Rect.Width, stripe);
            g.DrawRectangle(border, it.Rect.X, it.Rect.Y, it.Rect.Width - 1, it.Rect.Height - 1);
            TextRenderer.DrawText(g, it.Text, Font, it.Rect, equipped ? Color.White : Color.Gainsboro, center);
        }
    }

    // ===================================================================================
    // Мышь
    // ===================================================================================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (_statusRect.Contains(e.Location))
        {
            _engine.Enabled = !_engine.Enabled;
            return;
        }

        if (_gripRect.Width > 0 && _gripRect.Contains(e.Location))
        {
            _dragging = true;
            _dragStartCursor = Cursor.Position;
            _dragStartX = _cfg.OffsetX;
            _dragStartY = _cfg.OffsetY;
            Capture = true;
            return;
        }

        foreach (Item it in _items)
        {
            if (!it.Rect.Contains(e.Location)) continue;
            _engine.Equip(it.Group);
            _flash = it;
            Invalidate();
            _flashTimer.Stop();
            _flashTimer.Start();
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            Cursor = _gripRect.Width > 0 && _gripRect.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
            return;
        }

        int maxX = int.MaxValue, maxY = int.MaxValue;
        nint game = _tracker.GameHwnd;
        if (game != 0 && GetClientRect(game, out RECT rc))
        {
            maxX = Math.Max(0, rc.Width - _barWidth);
            maxY = Math.Max(0, rc.Height - _barHeight);
        }

        Point p = Cursor.Position;
        _cfg.OffsetX = Math.Clamp(_dragStartX + p.X - _dragStartCursor.X, 0, maxX);
        _cfg.OffsetY = Math.Clamp(_dragStartY + p.Y - _dragStartCursor.Y, 0, maxY);
        UpdatePlacement();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        OffsetCommitted?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tracker.Changed -= UpdatePlacement;
            _tracker.Moved -= UpdatePlacement;
            _flashTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
