using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using JdAmulet.Config;
using JdAmulet.Game;
using JdAmulet.Input;
using JdAmulet.Native;

namespace JdAmulet.Engine;

/// <summary>Связка, подготовленная к работе (скан-коды и задержка в тиках Stopwatch).</summary>
public sealed class CompiledBinding
{
    public required string Name { get; init; }
    public required KeyStroke Skill { get; init; }   // пусто у «основного амулета»
    public required KeyStroke Amulet { get; init; }
    public required Keys SkillKey { get; init; }
    public required Keys AmuletKey { get; init; }
    public required Keys Hotkey { get; init; }
    public required int DelayMs { get; init; }
    public required long DelayTicks { get; init; }
    public bool IsMain { get; init; }
}

public enum MacroJobKind : byte
{
    Binding,
    SkillOnly,
    ManualEquip,
    EnabledChanged,
    ResetState,
}

public readonly struct MacroJob
{
    public readonly MacroJobKind Kind;
    public readonly CompiledBinding? Binding;
    public readonly ushort Scan;
    public readonly byte ReleaseMods;
    public readonly bool IsTest;
    public readonly long Timestamp;

    public MacroJob(MacroJobKind kind, CompiledBinding? binding, ushort scan, int releaseMods, long timestamp, bool isTest = false)
    {
        Kind = kind;
        Binding = binding;
        Scan = scan;
        ReleaseMods = (byte)releaseMods;
        Timestamp = timestamp;
        IsTest = isTest;
    }
}

public readonly record struct LogEntry(DateTime Time, string Text);

/// <summary>
/// Ядро: логика хука (поток хука) + выполнение связок (поток ввода).
/// Конфигурация — неизменяемый снимок, подменяемый атомарно.
/// </summary>
public sealed class MacroEngine : ILowLevelKeyHandler, IInputJobHandler<MacroJob>, IDisposable
{
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new();

        public FrozenDictionary<int, CompiledBinding> Triggers = FrozenDictionary<int, CompiledBinding>.Empty; // скан-код → связка
        public FrozenDictionary<int, CompiledBinding> Combos = FrozenDictionary<int, CompiledBinding>.Empty;   // (mods<<16)|скан-код → связка
        public bool[] IsAmulet = new bool[0x200];
        public CompiledBinding[] Bindings = Array.Empty<CompiledBinding>();
        public CompiledBinding? Main;
        public RepeatMode Repeat;
        public bool AlwaysEquip;
        public uint ToggleVk = (uint)Keys.Pause;
    }

    private readonly GameWindowTracker _tracker;
    private readonly InputSender<MacroJob> _sender;
    private readonly Action<Action> _postToUi;

    private volatile Snapshot _snap = Snapshot.Empty;
    private volatile bool _enabled = true;
    private volatile bool _logEnabled = true;

    // --- состояние потока хука ---
    private readonly bool[] _held = new bool[0x200];                       // нажатие подавлено → подавить и автоповтор, и key-up
    private readonly CompiledBinding?[] _heldBinding = new CompiledBinding?[0x200];
    private bool _toggleHeld;
    private int _mods;                                                     // физически зажатые модификаторы (ModBits)

    // --- состояние потока ввода ---
    private ushort _currentAmulet;
    private volatile int _currentPublished;

    // --- лог ---
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private int _logDrainScheduled;
    private volatile ushort[] _usedScans = Array.Empty<ushort>();

    public event Action<bool>? EnabledChanged;   // в UI-потоке
    public event Action<int>? AmuletChanged;     // в UI-потоке, скан-код надетого амулета (0 — неизвестно)
    public event Action<LogEntry>? Logged;       // в UI-потоке

    public MacroEngine(GameWindowTracker tracker, Action<Action> postToUi)
    {
        _tracker = tracker;
        _postToUi = postToUi;
        _sender = new InputSender<MacroJob>(this);
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _sender.Post(new MacroJob(MacroJobKind.EnabledChanged, null, 0, 0, Stopwatch.GetTimestamp()));
        }
    }

    public bool LogEnabled { get => _logEnabled; set => _logEnabled = value; }
    public int CurrentAmulet => _currentPublished;
    public string TimerMode => _sender.Delay.Mode;
    public IReadOnlyList<CompiledBinding> Bindings => _snap.Bindings;
    public CompiledBinding? MainAmulet => _snap.Main;

    /// <summary>Скан-коды всех клавиш конфигурации — для аварийного отпускания при падении.</summary>
    public ReadOnlySpan<ushort> UsedScans => _usedScans;

    // ===================================================================================
    // Конфигурация
    // ===================================================================================

    public List<string> Apply(AppConfig cfg)
    {
        var warnings = new List<string>();
        Profile p = cfg.GetActiveProfile();
        var triggers = new Dictionary<int, CompiledBinding>();
        var combos = new Dictionary<int, CompiledBinding>();
        var isAmulet = new bool[0x200];
        var list = new List<CompiledBinding>();
        var scans = new HashSet<ushort>();
        uint toggleVk = (uint)(cfg.ToggleKey & Keys.KeyCode);

        void AddCombo(Keys hotkey, CompiledBinding cb)
        {
            KeyStroke ks = KeyStroke.From(hotkey);
            if (ks.IsEmpty) return;
            int mods = KeyCodes.ModsOf(hotkey);
            if ((uint)(hotkey & Keys.KeyCode) == toggleVk)
            {
                warnings.Add($"«{cb.Name}»: комбинация совпадает с клавишей вкл/выкл — пропущена.");
                return;
            }
            if (mods == 0 && triggers.ContainsKey(ks.Scan))
            {
                warnings.Add($"«{cb.Name}»: комбинация {KeyCodes.Display(hotkey)} совпадает с клавишей скилла — пропущена.");
                return;
            }
            if (!combos.TryAdd((mods << 16) | ks.Scan, cb))
                warnings.Add($"«{cb.Name}»: комбинация {KeyCodes.Display(hotkey)} уже занята — пропущена.");
        }

        foreach (BindingConfig b in p.Bindings)
        {
            if (!b.Enabled) continue;
            KeyStroke skill = KeyStroke.From(b.SkillKey);
            KeyStroke amulet = KeyStroke.From(b.AmuletKey);
            if (skill.IsEmpty || amulet.IsEmpty)
            {
                warnings.Add($"«{b.Name}»: не задана клавиша скилла или амулета — связка пропущена.");
                continue;
            }
            if (skill.Scan == amulet.Scan)
            {
                warnings.Add($"«{b.Name}»: скилл и амулет на одной клавише — связка пропущена.");
                continue;
            }
            if ((uint)(b.SkillKey & Keys.KeyCode) == toggleVk)
            {
                warnings.Add($"«{b.Name}»: клавиша скилла совпадает с клавишей вкл/выкл — связка пропущена.");
                continue;
            }

            int delay = Math.Clamp(b.DelayMs, 0, AppConfig.MaxDelayMs);
            var cb = new CompiledBinding
            {
                Name = b.Name,
                Skill = skill,
                Amulet = amulet,
                SkillKey = b.SkillKey & Keys.KeyCode,
                AmuletKey = b.AmuletKey & Keys.KeyCode,
                Hotkey = b.Hotkey,
                DelayMs = delay,
                DelayTicks = PrecisionDelay.MsToTicks(delay),
            };
            if (!triggers.TryAdd(skill.Scan, cb))
            {
                warnings.Add($"«{b.Name}»: клавиша {KeyCodes.Display(b.SkillKey)} уже используется другой связкой — пропущена.");
                continue;
            }
            list.Add(cb);
            isAmulet[amulet.Scan] = true;
            scans.Add(skill.Scan);
            scans.Add(amulet.Scan);
        }

        foreach (CompiledBinding cb in list)
        {
            if (triggers.ContainsKey(cb.Amulet.Scan))
                warnings.Add($"«{cb.Name}»: клавиша амулета {KeyCodes.Display(cb.AmuletKey)} совпадает со скиллом другой связки.");
            AddCombo(cb.Hotkey, cb);
        }

        CompiledBinding? main = null;
        if (p.MainAmulet.Enabled)
        {
            KeyStroke am = KeyStroke.From(p.MainAmulet.AmuletKey);
            if (am.IsEmpty)
            {
                warnings.Add("Основной амулет: не задана клавиша.");
            }
            else
            {
                main = new CompiledBinding
                {
                    Name = string.IsNullOrWhiteSpace(p.MainAmulet.Name) ? "Основной" : p.MainAmulet.Name,
                    Skill = default,
                    Amulet = am,
                    SkillKey = Keys.None,
                    AmuletKey = p.MainAmulet.AmuletKey & Keys.KeyCode,
                    Hotkey = p.MainAmulet.Hotkey,
                    DelayMs = 0,
                    DelayTicks = 0,
                    IsMain = true,
                };
                isAmulet[am.Scan] = true;
                scans.Add(am.Scan);
                AddCombo(main.Hotkey, main);
            }
        }

        _sender.Mode = cfg.InputMode;
        _logEnabled = cfg.LogEnabled;
        _usedScans = scans.ToArray();
        _snap = new Snapshot
        {
            Triggers = triggers.ToFrozenDictionary(),
            Combos = combos.ToFrozenDictionary(),
            IsAmulet = isAmulet,
            Bindings = list.ToArray(),
            Main = main,
            Repeat = cfg.RepeatMode,
            AlwaysEquip = cfg.AlwaysEquip,
            ToggleVk = toggleVk,
        };
        return warnings;
    }

    // ===================================================================================
    // Поток хука: только словарь, подавление и постановка в очередь
    // ===================================================================================

    public bool OnKey(in KBDLLHOOKSTRUCT k, long ts)
    {
        if (k.dwExtraInfo == InputSender<MacroJob>.Marker) return false; // наше синтетическое событие

        bool up = (k.flags & NativeMethods.LLKHF_UP) != 0;
        uint vk = k.vkCode;

        int modBit = ModBits.FromVk(vk);
        if (modBit != 0)
        {
            _mods = up ? _mods & ~modBit : _mods | modBit;
            return false;
        }

        int sc = (int)(k.scanCode & 0xFF) | ((k.flags & NativeMethods.LLKHF_EXTENDED) != 0 ? 0x100 : 0);
        Snapshot s = _snap;

        if (up)
        {
            if (vk == s.ToggleVk) _toggleHeld = false;
            if (_held[sc])
            {
                _held[sc] = false;
                _heldBinding[sc] = null;
                return true; // key-up исходной клавиши подавляем вместе с её key-down
            }
            return false;
        }

        // Глобальная клавиша вкл/выкл (работает при любом активном окне)
        if (vk == s.ToggleVk)
        {
            if (!_toggleHeld)
            {
                _toggleHeld = true;
                _enabled = !_enabled;
                _sender.Post(new MacroJob(MacroJobKind.EnabledChanged, null, 0, 0, ts));
            }
            return false;
        }

        // Автоповтор клавиши, чьё нажатие мы уже подавили. Проверяется ДО проверки вкл/фокуса,
        // иначе повторы ушли бы в игру без парного key-up.
        if (_held[sc])
        {
            CompiledBinding? hb = _heldBinding[sc];
            if (hb != null && s.Repeat == RepeatMode.SkillOnly && _enabled)
                _sender.Post(new MacroJob(MacroJobKind.SkillOnly, hb, 0, 0, ts));
            return true;
        }

        nint game = _tracker.GameHwnd;
        if (game == 0 || NativeMethods.GetForegroundWindow() != game) return false;

        int mods = ModBits.Collapse(_mods);
        if ((mods & ModBits.Win) != 0) return false;

        if (!_enabled)
        {
            // Макросы выключены, но ручную смену амулета всё равно запоминаем
            if (mods == 0 && s.IsAmulet[sc])
                _sender.Post(new MacroJob(MacroJobKind.ManualEquip, null, (ushort)sc, 0, ts));
            return false;
        }

        if (mods == 0)
        {
            if (s.Triggers.TryGetValue(sc, out CompiledBinding? b))
            {
                _held[sc] = true;
                _heldBinding[sc] = b;
                _sender.Post(new MacroJob(MacroJobKind.Binding, b, 0, 0, ts));
                return true;
            }
        }

        if (s.Combos.TryGetValue((mods << 16) | sc, out CompiledBinding? c))
        {
            _held[sc] = true;
            _heldBinding[sc] = null; // повторы комбинации просто подавляются
            _sender.Post(new MacroJob(MacroJobKind.Binding, c, 0, _mods, ts));
            return true;
        }

        // Игрок сам нажал клавишу амулета — обновим состояние, нажатие пропускаем в игру
        if (mods == 0 && s.IsAmulet[sc])
            _sender.Post(new MacroJob(MacroJobKind.ManualEquip, null, (ushort)sc, 0, ts));

        return false;
    }

    // ===================================================================================
    // Команды из UI (кнопки бара, тест)
    // ===================================================================================

    public void Run(CompiledBinding b, bool isTest = false) =>
        _sender.Post(new MacroJob(MacroJobKind.Binding, b, 0, Volatile.Read(ref _mods), Stopwatch.GetTimestamp(), isTest));

    public void ResetAmuletState() =>
        _sender.Post(new MacroJob(MacroJobKind.ResetState, null, 0, 0, Stopwatch.GetTimestamp()));

    // ===================================================================================
    // Поток ввода
    // ===================================================================================

    public void Execute(in MacroJob job)
    {
        switch (job.Kind)
        {
            case MacroJobKind.Binding:
                RunBinding(in job);
                break;

            case MacroJobKind.SkillOnly:
                if (!_sender.Send(job.Binding!.Skill, default, 0, 0, _tracker.GameHwnd))
                    Log($"Ошибка отправки ввода (код {_sender.LastError}).");
                break;

            case MacroJobKind.ManualEquip:
                if (_currentAmulet != job.Scan)
                {
                    SetCurrentAmulet(job.Scan);
                    Log($"Амулет надет вручную: {ScanName(job.Scan)}");
                }
                break;

            case MacroJobKind.EnabledChanged:
                bool en = _enabled;
                _postToUi(() => EnabledChanged?.Invoke(en));
                Log(en ? "Макросы ВКЛЮЧЕНЫ" : "Макросы ВЫКЛЮЧЕНЫ");
                break;

            case MacroJobKind.ResetState:
                SetCurrentAmulet(0);
                Log("Состояние амулета сброшено.");
                break;
        }
    }

    private void RunBinding(in MacroJob job)
    {
        CompiledBinding b = job.Binding!;
        Snapshot s = _snap;
        nint game = _tracker.GameHwnd;

        if (job.IsTest && _sender.Mode == InputMode.SendInput &&
            (game == 0 || NativeMethods.GetForegroundWindow() != game))
        {
            Log($"Тест «{b.Name}»: окно игры не активно — нажатия не отправлены.");
            return;
        }

        long start = Stopwatch.GetTimestamp();
        int release = job.ReleaseMods;
        bool needAmulet = b.IsMain || s.AlwaysEquip || _currentAmulet != b.Amulet.Scan;
        bool ok;
        double pauseMs = 0;

        if (!needAmulet)
        {
            ok = _sender.Send(b.Skill, default, release, RestoreMask(release), game);
        }
        else if (b.Skill.IsEmpty || b.DelayTicks == 0)
        {
            // Задержка 0: все четыре события одним вызовом SendInput
            ok = _sender.Send(b.Amulet, b.Skill, release, RestoreMask(release), game);
        }
        else
        {
            ok = _sender.Send(b.Amulet, default, release, 0, game);
            if (ok)
            {
                long amuletSent = Stopwatch.GetTimestamp();
                _sender.Delay.WaitUntil(amuletSent + b.DelayTicks);
                long skillStart = Stopwatch.GetTimestamp();
                pauseMs = PrecisionDelay.TicksToMs(skillStart - amuletSent);
                ok = _sender.Send(b.Skill, default, 0, RestoreMask(release), game);
            }
        }

        long end = Stopwatch.GetTimestamp();
        if (ok && needAmulet) SetCurrentAmulet(b.Amulet.Scan);

        if (!_logEnabled && ok) return;

        double total = PrecisionDelay.TicksToMs(end - job.Timestamp);
        double queue = PrecisionDelay.TicksToMs(start - job.Timestamp);
        string prefix = job.IsTest ? "Тест " : "";
        if (!ok)
            Log($"{prefix}«{b.Name}»: ОШИБКА отправки ввода (код {_sender.LastError}). Попробуйте другой режим ввода.");
        else if (b.IsMain)
            Log($"{prefix}«{b.Name}»: амулет {KeyCodes.Display(b.AmuletKey)} | {total:F2} мс");
        else if (!needAmulet)
            Log($"{prefix}«{b.Name}»: амулет уже надет → скилл {KeyCodes.Display(b.SkillKey)} | до скилла {total:F2} мс (очередь {queue:F3})");
        else if (b.DelayTicks == 0)
            Log($"{prefix}«{b.Name}»: амулет {KeyCodes.Display(b.AmuletKey)} + скилл {KeyCodes.Display(b.SkillKey)} одним пакетом | до скилла {total:F2} мс (очередь {queue:F3})");
        else
            Log($"{prefix}«{b.Name}»: амулет {KeyCodes.Display(b.AmuletKey)} → пауза {pauseMs:F2} мс (задано {b.DelayMs}) → скилл {KeyCodes.Display(b.SkillKey)} | до скилла {total:F2} мс (очередь {queue:F3})");
    }

    /// <summary>Какие из отпущенных модификаторов вернуть: только те, что игрок всё ещё держит.</summary>
    private int RestoreMask(int released) => released & Volatile.Read(ref _mods);

    private void SetCurrentAmulet(ushort scan)
    {
        if (_currentAmulet == scan) return;
        _currentAmulet = scan;
        _currentPublished = scan;
        _postToUi(() => AmuletChanged?.Invoke(scan));
    }

    private string ScanName(ushort scan)
    {
        Snapshot s = _snap;
        foreach (CompiledBinding b in s.Bindings)
            if (b.Amulet.Scan == scan) return KeyCodes.Display(b.AmuletKey);
        if (s.Main != null && s.Main.Amulet.Scan == scan) return KeyCodes.Display(s.Main.AmuletKey);
        return $"sc 0x{scan:X}";
    }

    public void OnError(Exception ex) => Log("Ошибка потока ввода: " + ex.Message);

    // ===================================================================================
    // Лог: очередь + одна отложенная выгрузка в UI
    // ===================================================================================

    public void Log(string text)
    {
        _logQueue.Enqueue(new LogEntry(DateTime.Now, text));
        if (Interlocked.Exchange(ref _logDrainScheduled, 1) == 0)
            _postToUi(DrainLog);
    }

    private void DrainLog()
    {
        Volatile.Write(ref _logDrainScheduled, 0);
        while (_logQueue.TryDequeue(out LogEntry e))
            Logged?.Invoke(e);
    }

    public void Dispose() => _sender.Dispose();
}
