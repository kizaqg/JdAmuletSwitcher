using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using JdAmulet.Config;
using JdAmulet.Game;
using JdAmulet.Input;
using JdAmulet.Native;

namespace JdAmulet.Engine;

/// <summary>Амулет и его скиллы, подготовленные к работе.</summary>
public sealed class CompiledGroup
{
    public required string Name { get; init; }
    /// <summary>Подпись на панели: своё название или клавиша амулета.</summary>
    public required string ShortLabel { get; init; }
    public required KeyStroke Amulet { get; init; }
    public required Keys AmuletKey { get; init; }
    public required Keys[] SkillKeys { get; init; }
    public required Keys Hotkey { get; init; }
    public required int DelayMs { get; init; }
    public required long DelayTicks { get; init; }
}

/// <summary>Клавиша скилла, привязанная к амулету.</summary>
public sealed class CompiledSkill
{
    public required CompiledGroup Group { get; init; }
    public required KeyStroke Skill { get; init; }
    public required Keys SkillKey { get; init; }
}

public enum MacroJobKind : byte
{
    SkillDown,      // надеть амулет (если нужно) → нажать скилл (без отпускания)
    SkillUp,        // отпустить скилл (не раньше минимального удержания)
    Equip,          // только надеть амулет (кнопка бара / комбинация)
    Test,           // SkillDown + SkillUp
    ManualEquip,
    EnabledChanged,
    ResetState,
}

public readonly struct MacroJob
{
    public readonly MacroJobKind Kind;
    public readonly CompiledSkill? Skill;
    public readonly CompiledGroup? Group;
    public readonly ushort Scan;
    public readonly byte ReleaseMods;
    public readonly long Timestamp;

    public MacroJob(MacroJobKind kind, long timestamp, CompiledSkill? skill = null, CompiledGroup? group = null,
        ushort scan = 0, int releaseMods = 0)
    {
        Kind = kind;
        Timestamp = timestamp;
        Skill = skill;
        Group = group;
        Scan = scan;
        ReleaseMods = (byte)releaseMods;
    }
}

public readonly record struct LogEntry(DateTime Time, string Text);

/// <summary>
/// Ядро: логика хука (поток хука) + выполнение (поток ввода).
/// Конфигурация — неизменяемый снимок, подменяемый атомарно.
/// </summary>
public sealed class MacroEngine : ILowLevelKeyHandler, IInputJobHandler<MacroJob>, IDisposable
{
    private sealed class Snapshot
    {
        public FrozenDictionary<int, CompiledSkill> Triggers = FrozenDictionary<int, CompiledSkill>.Empty; // скан-код → скилл
        public FrozenDictionary<int, CompiledGroup> Combos = FrozenDictionary<int, CompiledGroup>.Empty;   // (mods<<16)|скан-код → амулет
        public bool[] IsAmulet = new bool[0x200];
        public CompiledGroup[] Groups = Array.Empty<CompiledGroup>();
        public bool AlwaysEquip;
        public long HoldTicks = PrecisionDelay.MsToTicks(30);
        public uint ToggleVk = (uint)Keys.Pause;
    }

    private readonly GameWindowTracker _tracker;
    private readonly InputSender<MacroJob> _sender;
    private readonly Action<Action> _postToUi;

    private volatile Snapshot _snap = new();
    private volatile bool _enabled = true;
    private volatile bool _logEnabled = true;

    // --- состояние потока хука ---
    private readonly bool[] _held = new bool[0x200];                    // нажатие подавлено → подавить и автоповтор, и key-up
    private readonly CompiledSkill?[] _heldSkill = new CompiledSkill?[0x200];
    private bool _toggleHeld;
    private int _mods;                                                  // физически зажатые модификаторы (ModBits)

    // --- состояние потока ввода ---
    private ushort _currentAmulet;
    private volatile int _currentPublished;
    private readonly long[] _skillDownAt = new long[0x200];
    private readonly bool[] _skillIsDown = new bool[0x200];

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
            _sender.Post(new MacroJob(MacroJobKind.EnabledChanged, Stopwatch.GetTimestamp()));
        }
    }

    public int CurrentAmulet => _currentPublished;
    public string TimerMode => _sender.Delay.Mode;
    public IReadOnlyList<CompiledGroup> Groups => _snap.Groups;

    /// <summary>Скан-коды всех клавиш конфигурации — для аварийного отпускания.</summary>
    public ReadOnlySpan<ushort> UsedScans => _usedScans;

    // ===================================================================================
    // Конфигурация
    // ===================================================================================

    public List<string> Apply(AppConfig cfg)
    {
        var warnings = new List<string>();
        Profile p = cfg.GetActiveProfile();
        var triggers = new Dictionary<int, CompiledSkill>();
        var combos = new Dictionary<int, CompiledGroup>();
        var isAmulet = new bool[0x200];
        var groups = new List<CompiledGroup>();
        var scans = new HashSet<ushort>();
        uint toggleVk = (uint)(cfg.ToggleKey & Keys.KeyCode);
        var amuletOwner = new Dictionary<ushort, string>();

        foreach (AmuletGroup g in p.Groups)
        {
            if (!g.Enabled) continue;
            string name = g.DisplayName;
            KeyStroke amulet = KeyStroke.From(g.AmuletKey);
            if (amulet.IsEmpty)
            {
                warnings.Add($"«{name}»: не задана клавиша амулета — пропущено.");
                continue;
            }
            if (!amuletOwner.TryAdd(amulet.Scan, name))
            {
                warnings.Add($"«{name}»: амулет {KeyCodes.Display(g.AmuletKey)} уже есть в «{amuletOwner[amulet.Scan]}» — объедините скиллы в одну строку.");
                continue;
            }

            int delay = Math.Clamp(g.DelayMs, 0, AppConfig.MaxDelayMs);
            var cg = new CompiledGroup
            {
                Name = name,
                ShortLabel = string.IsNullOrWhiteSpace(g.Name) ? KeyCodes.Display(g.AmuletKey) : g.Name.Trim(),
                Amulet = amulet,
                AmuletKey = g.AmuletKey & Keys.KeyCode,
                SkillKeys = g.SkillKeys.Select(k => k & Keys.KeyCode).Where(k => k != Keys.None).Distinct().ToArray(),
                Hotkey = g.Hotkey,
                DelayMs = delay,
                DelayTicks = PrecisionDelay.MsToTicks(delay),
            };
            groups.Add(cg);
            isAmulet[amulet.Scan] = true;
            scans.Add(amulet.Scan);

            foreach (Keys sk in cg.SkillKeys)
            {
                KeyStroke skill = KeyStroke.From(sk);
                if (skill.IsEmpty) continue;
                if (skill.Scan == amulet.Scan)
                {
                    warnings.Add($"«{name}»: скилл {KeyCodes.Display(sk)} на клавише самого амулета — пропущен.");
                    continue;
                }
                if ((uint)sk == toggleVk)
                {
                    warnings.Add($"«{name}»: скилл {KeyCodes.Display(sk)} совпадает с клавишей вкл/выкл — пропущен.");
                    continue;
                }
                if (!triggers.TryAdd(skill.Scan, new CompiledSkill { Group = cg, Skill = skill, SkillKey = sk }))
                {
                    warnings.Add($"«{name}»: скилл {KeyCodes.Display(sk)} уже привязан к «{triggers[skill.Scan].Group.Name}» — пропущен.");
                    continue;
                }
                scans.Add(skill.Scan);
            }
        }

        foreach (CompiledGroup cg in groups)
        {
            if (triggers.TryGetValue(cg.Amulet.Scan, out CompiledSkill? clash))
                warnings.Add($"«{cg.Name}»: клавиша амулета {KeyCodes.Display(cg.AmuletKey)} — это скилл в «{clash.Group.Name}».");

            KeyStroke hk = KeyStroke.From(cg.Hotkey);
            if (hk.IsEmpty) continue;
            int mods = KeyCodes.ModsOf(cg.Hotkey);
            if ((uint)(cg.Hotkey & Keys.KeyCode) == toggleVk)
                warnings.Add($"«{cg.Name}»: комбинация совпадает с клавишей вкл/выкл — пропущена.");
            else if (mods == 0 && triggers.ContainsKey(hk.Scan))
                warnings.Add($"«{cg.Name}»: комбинация {KeyCodes.Display(cg.Hotkey)} совпадает со скиллом — пропущена.");
            else if (!combos.TryAdd((mods << 16) | hk.Scan, cg))
                warnings.Add($"«{cg.Name}»: комбинация {KeyCodes.Display(cg.Hotkey)} уже занята — пропущена.");
        }

        _sender.Mode = cfg.InputMode;
        _logEnabled = cfg.LogEnabled;
        _usedScans = scans.ToArray();
        _snap = new Snapshot
        {
            Triggers = triggers.ToFrozenDictionary(),
            Combos = combos.ToFrozenDictionary(),
            IsAmulet = isAmulet,
            Groups = groups.ToArray(),
            AlwaysEquip = cfg.AlwaysEquip,
            HoldTicks = PrecisionDelay.MsToTicks(Math.Clamp(cfg.KeyHoldMs, AppConfig.MinHoldMs, AppConfig.MaxHoldMs)),
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
                CompiledSkill? hs = _heldSkill[sc];
                _heldSkill[sc] = null;
                if (hs != null) _sender.Post(new MacroJob(MacroJobKind.SkillUp, ts, skill: hs));
                return true; // отпускание скилла отправит поток ввода — строго после нажатия
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
                _sender.Post(new MacroJob(MacroJobKind.EnabledChanged, ts));
            }
            return false;
        }

        // Автоповтор удерживаемой клавиши: скилл уже нажат синтетически и держится до отпускания
        if (_held[sc]) return true;

        nint game = _tracker.GameHwnd;
        if (game == 0 || NativeMethods.GetForegroundWindow() != game) return false;

        int mods = ModBits.Collapse(_mods);
        if ((mods & ModBits.Win) != 0) return false;

        if (!_enabled)
        {
            // Макросы выключены, но ручную смену амулета всё равно запоминаем
            if (mods == 0 && s.IsAmulet[sc])
                _sender.Post(new MacroJob(MacroJobKind.ManualEquip, ts, scan: (ushort)sc));
            return false;
        }

        if (mods == 0 && s.Triggers.TryGetValue(sc, out CompiledSkill? skill))
        {
            _held[sc] = true;
            _heldSkill[sc] = skill;
            _sender.Post(new MacroJob(MacroJobKind.SkillDown, ts, skill: skill));
            return true;
        }

        if (s.Combos.TryGetValue((mods << 16) | sc, out CompiledGroup? g))
        {
            _held[sc] = true;
            _heldSkill[sc] = null; // повторы и отпускание комбинации просто подавляются
            _sender.Post(new MacroJob(MacroJobKind.Equip, ts, group: g, releaseMods: _mods));
            return true;
        }

        // Игрок сам нажал клавишу амулета — обновим состояние, нажатие пропускаем в игру
        if (mods == 0 && s.IsAmulet[sc])
            _sender.Post(new MacroJob(MacroJobKind.ManualEquip, ts, scan: (ushort)sc));

        return false;
    }

    // ===================================================================================
    // Команды из UI
    // ===================================================================================

    /// <summary>Надеть амулет (кнопка на баре).</summary>
    public void Equip(CompiledGroup g) =>
        _sender.Post(new MacroJob(MacroJobKind.Equip, Stopwatch.GetTimestamp(), group: g, releaseMods: Volatile.Read(ref _mods)));

    /// <summary>Тест: амулет → пауза → скилл (нажать и отпустить).</summary>
    public void Test(CompiledGroup g)
    {
        CompiledSkill? skill = _snap.Triggers.Values.FirstOrDefault(x => x.Group == g);
        if (skill != null) _sender.Post(new MacroJob(MacroJobKind.Test, Stopwatch.GetTimestamp(), skill: skill));
        else _sender.Post(new MacroJob(MacroJobKind.Equip, Stopwatch.GetTimestamp(), group: g));
    }

    public void ResetAmuletState() =>
        _sender.Post(new MacroJob(MacroJobKind.ResetState, Stopwatch.GetTimestamp()));

    // ===================================================================================
    // Поток ввода
    // ===================================================================================

    public void Execute(in MacroJob job)
    {
        switch (job.Kind)
        {
            case MacroJobKind.SkillDown:
                SkillDown(job.Skill!, job.Timestamp, test: false);
                break;

            case MacroJobKind.SkillUp:
                SkillUp(job.Skill!);
                break;

            case MacroJobKind.Test:
                if (SkillDown(job.Skill!, job.Timestamp, test: true))
                    SkillUp(job.Skill!);
                break;

            case MacroJobKind.Equip:
                EquipAmulet(job.Group!, job.ReleaseMods, job.Timestamp);
                break;

            case MacroJobKind.ManualEquip:
                if (_currentAmulet != job.Scan)
                {
                    SetCurrentAmulet(job.Scan);
                    Log($"Амулет надет вручную: {AmuletName(job.Scan)}");
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

    /// <summary>Амулет (нажать → удержать → отпустить) → пауза → скилл нажат и удерживается.</summary>
    private bool SkillDown(CompiledSkill sk, long jobTs, bool test)
    {
        CompiledGroup g = sk.Group;
        Snapshot s = _snap;
        nint game = _tracker.GameHwnd;

        if (test && _sender.Mode == InputMode.SendInput &&
            (game == 0 || NativeMethods.GetForegroundWindow() != game))
        {
            Log($"Тест «{g.Name}»: окно игры не активно — нажатия не отправлены.");
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        bool needAmulet = s.AlwaysEquip || _currentAmulet != g.Amulet.Scan;
        bool ok;
        double pauseMs = 0;
        Span<KeyAction> a = stackalloc KeyAction[2];

        if (!needAmulet)
        {
            a[0] = KeyAction.Down(sk.Skill);
            ok = _sender.Send(a[..1], 0, 0, game);
        }
        else
        {
            a[0] = KeyAction.Down(g.Amulet);
            ok = _sender.Send(a[..1], 0, 0, game);
            long amuletDown = Stopwatch.GetTimestamp();
            _sender.Delay.WaitUntil(amuletDown + s.HoldTicks);   // игра должна «увидеть» нажатие в своём кадре

            a[0] = KeyAction.Release(g.Amulet);
            if (g.DelayTicks == 0)
            {
                a[1] = KeyAction.Down(sk.Skill);
                ok &= _sender.Send(a, 0, 0, game);
            }
            else
            {
                ok &= _sender.Send(a[..1], 0, 0, game);
                long amuletUp = Stopwatch.GetTimestamp();
                _sender.Delay.WaitUntil(amuletUp + g.DelayTicks);
                pauseMs = PrecisionDelay.TicksToMs(Stopwatch.GetTimestamp() - amuletUp);
                a[0] = KeyAction.Down(sk.Skill);
                ok &= _sender.Send(a[..1], 0, 0, game);
            }
            if (ok) SetCurrentAmulet(g.Amulet.Scan);
        }

        long end = Stopwatch.GetTimestamp();
        _skillDownAt[sk.Skill.Scan] = end;
        _skillIsDown[sk.Skill.Scan] = true;

        if (!ok)
        {
            Log($"«{g.Name}»: ОШИБКА отправки ввода (код {_sender.LastError}). Попробуйте другой режим ввода.");
            return true;
        }
        if (!_logEnabled) return true;

        double total = PrecisionDelay.TicksToMs(end - jobTs);
        double queue = PrecisionDelay.TicksToMs(start - jobTs);
        string prefix = test ? "Тест: " : "";
        string skillName = KeyCodes.Display(sk.SkillKey);
        if (!needAmulet)
            Log($"{prefix}скилл {skillName} (амулет {KeyCodes.Display(g.AmuletKey)} уже надет) | до скилла {total:F2} мс");
        else if (g.DelayTicks == 0)
            Log($"{prefix}амулет {KeyCodes.Display(g.AmuletKey)} → скилл {skillName} без паузы | до скилла {total:F2} мс (очередь {queue:F3})");
        else
            Log($"{prefix}амулет {KeyCodes.Display(g.AmuletKey)} → пауза {pauseMs:F1} мс (задано {g.DelayMs}) → скилл {skillName} | до скилла {total:F2} мс (очередь {queue:F3})");
        return true;
    }

    private void SkillUp(CompiledSkill sk)
    {
        ushort sc = sk.Skill.Scan;
        if (!_skillIsDown[sc]) return;
        _sender.Delay.WaitUntil(_skillDownAt[sc] + _snap.HoldTicks); // короткий тап всё равно держим минимум KeyHoldMs
        Span<KeyAction> a = stackalloc KeyAction[1];
        a[0] = KeyAction.Release(sk.Skill);
        _sender.Send(a, 0, 0, _tracker.GameHwnd);
        _skillIsDown[sc] = false;
    }

    private void EquipAmulet(CompiledGroup g, int releaseMods, long jobTs)
    {
        nint game = _tracker.GameHwnd;
        Span<KeyAction> a = stackalloc KeyAction[1];
        a[0] = KeyAction.Down(g.Amulet);
        bool ok = _sender.Send(a, releaseMods, 0, game);
        _sender.Delay.WaitUntil(Stopwatch.GetTimestamp() + _snap.HoldTicks);
        a[0] = KeyAction.Release(g.Amulet);
        ok &= _sender.Send(a, 0, releaseMods & Volatile.Read(ref _mods), game);

        if (ok) SetCurrentAmulet(g.Amulet.Scan);
        if (!ok)
            Log($"«{g.Name}»: ОШИБКА отправки ввода (код {_sender.LastError}). Попробуйте другой режим ввода.");
        else if (_logEnabled)
            Log($"Амулет {KeyCodes.Display(g.AmuletKey)} надет ({g.Name}) | {PrecisionDelay.TicksToMs(Stopwatch.GetTimestamp() - jobTs):F2} мс");
    }

    private void SetCurrentAmulet(ushort scan)
    {
        if (_currentAmulet == scan) return;
        _currentAmulet = scan;
        _currentPublished = scan;
        _postToUi(() => AmuletChanged?.Invoke(scan));
    }

    private string AmuletName(ushort scan)
    {
        foreach (CompiledGroup g in _snap.Groups)
            if (g.Amulet.Scan == scan) return KeyCodes.Display(g.AmuletKey);
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

    public void Dispose()
    {
        _sender.Dispose();
        // Скилл мог остаться нажатым синтетически (игрок держал клавишу при выходе)
        InputSender<MacroJob>.ReleaseKeys(_usedScans);
    }
}
