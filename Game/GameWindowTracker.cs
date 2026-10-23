using System.Diagnostics;
using JdAmulet.Native;
using static JdAmulet.Native.NativeMethods;

namespace JdAmulet.Game;

/// <summary>
/// Находит окно игры и следит за ним только через события (SetWinEventHook, Process.Exited) —
/// без таймеров опроса. HWND кешируется в volatile-поле: хук клавиатуры читает его без поиска процесса.
/// Должен создаваться и использоваться в UI-потоке (out-of-context WinEvents приходят через его цикл сообщений).
/// </summary>
public sealed unsafe class GameWindowTracker : IDisposable
{
    private readonly WinEventProc _winEventProc; // держим ссылку, чтобы делегат не собрал GC
    private readonly SynchronizationContext _ui;

    private nint _hookForeground, _hookMinimize, _hookLocation, _hookDestroy;
    private volatile nint _game;
    private uint _pid;
    private Process? _processWatch;
    private string _processName;

    private uint _lastCheckedPid;
    private bool _lastCheckedMatch;

    /// <summary>HWND окна игры (0 — не найдено). Безопасно читать из любого потока.</summary>
    public nint GameHwnd => _game;
    public uint GamePid => _pid;
    public bool IsGameForeground { get; private set; }
    public bool IsGameMinimized => _game != 0 && IsIconic(_game);
    public string ProcessName => _processName;

    /// <summary>Игра найдена/потеряна, смена переднего плана, сворачивание/разворачивание.</summary>
    public event Action? Changed;
    /// <summary>Окно игры сдвинуто или изменило размер.</summary>
    public event Action? Moved;

    public GameWindowTracker(string processName)
    {
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("GameWindowTracker нужно создавать в UI-потоке.");
        _processName = processName;
        _winEventProc = OnWinEvent;

        _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _hookMinimize = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, 0,
            _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        Rescan();
    }

    public void SetProcessName(string name)
    {
        _processName = name.Trim();
        _lastCheckedPid = 0;
        Detach();
        Rescan();
    }

    /// <summary>Разовый поиск окна (при старте и смене имени процесса).</summary>
    public void Rescan()
    {
        nint fg = GetForegroundWindow();
        if (fg == 0 || !TryAttachFrom(fg))
        {
            nint best = 0;
            uint bestPid = 0;
            long bestArea = 0;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!IsGameProcess(pid)) return true;
                GetWindowRect(h, out RECT r);
                long area = (long)r.Width * r.Height;
                if (area > bestArea) { bestArea = area; best = h; bestPid = pid; }
                return true;
            }, 0);
            if (best != 0 && best != _game) Attach(best, bestPid);
        }
        UpdateForeground(fg);
    }

    private void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        switch (evt)
        {
            case EVENT_SYSTEM_FOREGROUND:
                if (_game != 0 && !IsWindow(_game)) Detach();
                if (hwnd != 0) TryAttachFrom(hwnd);
                UpdateForeground(hwnd);
                break;

            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
                if (hwnd == _game && hwnd != 0) Changed?.Invoke();
                break;

            case EVENT_OBJECT_LOCATIONCHANGE:
                if (hwnd == _game && hwnd != 0 && idObject == OBJID_WINDOW && idChild == CHILDID_SELF)
                    Moved?.Invoke();
                break;

            case EVENT_OBJECT_DESTROY:
                if (hwnd == _game && hwnd != 0 && idObject == OBJID_WINDOW && idChild == CHILDID_SELF)
                {
                    Detach();
                    Rescan();
                }
                break;
        }
    }

    private void UpdateForeground(nint fg)
    {
        IsGameForeground = _game != 0 && fg == _game;
        Changed?.Invoke();
    }

    private bool TryAttachFrom(nint hwnd)
    {
        nint root = GetAncestor(hwnd, GA_ROOTOWNER);
        if (root == 0) root = hwnd;
        GetWindowThreadProcessId(root, out uint pid);
        if (pid == 0 || !IsGameProcess(pid)) return false;
        if (root != _game) Attach(root, pid);
        return true;
    }

    private void Attach(nint hwnd, uint pid)
    {
        bool sameProcess = pid == _pid && _hookLocation != 0;
        if (!sameProcess)
        {
            UnhookProcessEvents();
            // Высокочастотные события слушаем только у процесса игры
            _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0,
                _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
            _hookDestroy = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, 0,
                _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
            WatchProcessExit(pid);
        }
        _pid = pid;
        _game = hwnd;
        Changed?.Invoke();
    }

    private void Detach()
    {
        if (_game == 0 && _pid == 0) return;
        UnhookProcessEvents();
        _game = 0;
        _pid = 0;
        _lastCheckedPid = 0;
        IsGameForeground = false;
        Changed?.Invoke();
    }

    private void WatchProcessExit(uint pid)
    {
        _processWatch?.Dispose();
        _processWatch = null;
        try
        {
            var p = Process.GetProcessById((int)pid);
            p.EnableRaisingEvents = true; // ожидание ядра на handle процесса, не опрос
            p.Exited += (_, _) => _ui.Post(_ =>
            {
                if (_pid == pid) Detach();
            }, null);
            _processWatch = p;
        }
        catch
        {
            // Нет доступа — останется отслеживание через EVENT_OBJECT_DESTROY
        }
    }

    private void UnhookProcessEvents()
    {
        if (_hookLocation != 0) { UnhookWinEvent(_hookLocation); _hookLocation = 0; }
        if (_hookDestroy != 0) { UnhookWinEvent(_hookDestroy); _hookDestroy = 0; }
        _processWatch?.Dispose();
        _processWatch = null;
    }

    private bool IsGameProcess(uint pid)
    {
        if (pid == 0) return false;
        if (pid == _lastCheckedPid) return _lastCheckedMatch;

        bool match = false;
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != 0)
        {
            char* buf = stackalloc char[1024];
            uint size = 1024;
            if (QueryFullProcessImageNameW(h, 0, buf, ref size))
            {
                var path = new ReadOnlySpan<char>(buf, (int)size);
                match = Path.GetFileName(path).Equals(_processName, StringComparison.OrdinalIgnoreCase);
            }
            CloseHandle(h);
        }
        _lastCheckedPid = pid;
        _lastCheckedMatch = match;
        return match;
    }

    public void Dispose()
    {
        UnhookProcessEvents();
        if (_hookForeground != 0) { UnhookWinEvent(_hookForeground); _hookForeground = 0; }
        if (_hookMinimize != 0) { UnhookWinEvent(_hookMinimize); _hookMinimize = 0; }
        _game = 0;
    }
}
