using JdAmulet.Config;
using JdAmulet.Engine;
using JdAmulet.Game;
using JdAmulet.Input;
using JdAmulet.Native;
using static JdAmulet.Native.NativeMethods;

namespace JdAmulet.UI;

/// <summary>
/// Полоса кнопок поверх окна игры. Не активируется (WS_EX_NOACTIVATE + MA_NOACTIVATE) —
/// клик не отнимает фокус у игры. Позиция обновляется только по событиям GameWindowTracker.
/// Перерисовка — только при смене состояния.
/// </summary>
public sealed class OverlayBar : Form
{
    private sealed class Item
    {
        public required CompiledBinding Binding;
        public required string Text;
        public Rectangle Rect;
    }

    private readonly MacroEngine _engine;
    private readonly GameWindowTracker _tracker;
    private readonly OverlayConfig _cfg;
    private readonly List<Item> _items = new();

    private int _currentAmulet;
    private bool _macrosEnabled = true;
    private bool _shown;
    private int _gripWidth;
    private int _barWidth, _barHeight;

    private bool _dragging;
    private int _dragStartCursorX, _dragStartOffset;

    private static readonly Color BackDark = Color.FromArgb(22, 22, 26);
    private static readonly Color ItemNormal = Color.FromArgb(52, 54, 62);
    private static readonly Color ItemActive = Color.FromArgb(38, 150, 70);
    private static readonly Color ItemDisabled = Color.FromArgb(70, 44, 44);
    private static readonly Color Border = Color.FromArgb(90, 92, 104);

    /// <summary>Пользователь закончил перетаскивание — смещение нужно сохранить.</summary>
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

        _ = Handle; // создаём окно сразу, показываем — только через ShowWindow(SW_SHOWNOACTIVATE)

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
        foreach (CompiledBinding b in _engine.Bindings)
            _items.Add(new Item { Binding = b, Text = Label(b) });
        if (_engine.MainAmulet is { } main)
            _items.Add(new Item { Binding = main, Text = Label(main) });

        float scale = DeviceDpi / 96f;
        int pad = (int)(10 * scale);
        int gap = (int)(3 * scale);
        _barHeight = (int)(24 * scale);
        _gripWidth = _cfg.Locked ? (int)(4 * scale) : (int)(14 * scale);

        int x = _gripWidth;
        foreach (Item it in _items)
        {
            int w = TextRenderer.MeasureText(it.Text, Font).Width + pad;
            it.Rect = new Rectangle(x, 2, w, _barHeight - 4);
            x += w + gap;
        }
        _barWidth = x + 1;
        _currentAmulet = _engine.CurrentAmulet;

        Invalidate();
        UpdatePlacement();
    }

    private static string Label(CompiledBinding b)
    {
        string hk = (b.Hotkey & Keys.KeyCode) != Keys.None ? "  " + KeyCodes.Display(b.Hotkey) : "";
        return b.Name + hk;
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
    // Позиционирование (вызывается только по событиям)
    // ===================================================================================

    public void UpdatePlacement()
    {
        if (IsDisposed) return;
        nint game = _tracker.GameHwnd;
        bool show = _cfg.Visible && _items.Count > 0 && game != 0 &&
                    _tracker.IsGameForeground && !IsIconic(game);

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

        int maxOffset = Math.Max(0, rc.Width - _barWidth);
        int offset = Math.Clamp(_cfg.OffsetX, 0, maxOffset);

        SetWindowPos(Handle, HWND_TOPMOST, origin.X + offset, origin.Y, _barWidth, _barHeight,
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

        if (!_cfg.Locked)
        {
            using var dot = new SolidBrush(Color.FromArgb(150, 150, 160));
            int cx = _gripWidth / 2 - 2;
            for (int y = 5; y < _barHeight - 5; y += 4)
            {
                g.FillRectangle(dot, cx, y, 2, 2);
                g.FillRectangle(dot, cx + 4, y, 2, 2);
            }
        }

        using var border = new Pen(Border);
        foreach (Item it in _items)
        {
            bool active = _currentAmulet != 0 && it.Binding.Amulet.Scan == _currentAmulet;
            Color fill = !_macrosEnabled ? ItemDisabled : active ? ItemActive : ItemNormal;
            using (var brush = new SolidBrush(fill))
                g.FillRectangle(brush, it.Rect);
            g.DrawRectangle(border, it.Rect.X, it.Rect.Y, it.Rect.Width - 1, it.Rect.Height - 1);
            TextRenderer.DrawText(g, it.Text, Font, it.Rect, active ? Color.White : Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // ===================================================================================
    // Мышь
    // ===================================================================================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        if (!_cfg.Locked && e.X < _gripWidth)
        {
            _dragging = true;
            _dragStartCursorX = Cursor.Position.X;
            _dragStartOffset = _cfg.OffsetX;
            Capture = true;
            return;
        }

        foreach (Item it in _items)
        {
            if (it.Rect.Contains(e.Location))
            {
                _engine.Run(it.Binding);
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        nint game = _tracker.GameHwnd;
        int maxOffset = int.MaxValue;
        if (game != 0 && GetClientRect(game, out RECT rc))
            maxOffset = Math.Max(0, rc.Width - _barWidth);

        _cfg.OffsetX = Math.Clamp(_dragStartOffset + Cursor.Position.X - _dragStartCursorX, 0, maxOffset);
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
        }
        base.Dispose(disposing);
    }
}
