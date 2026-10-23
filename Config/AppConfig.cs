using System.Text.Json;
using System.Text.Json.Serialization;
using JdAmulet.Input;

namespace JdAmulet.Config;

/// <summary>
/// Амулет и набор скиллов, которым он нужен. Нажатие любого из скиллов:
/// сначала надевается амулет (если ещё не надет), затем нажимается скилл.
/// Группа без скиллов — просто кнопка/комбинация «надеть амулет» (например, основной амулет).
/// </summary>
public sealed class AmuletGroup
{
    public string Name { get; set; } = "";
    public Keys AmuletKey { get; set; }
    public List<Keys> SkillKeys { get; set; } = new();
    public int DelayMs { get; set; } = 20;
    public Keys Hotkey { get; set; }
    public bool Enabled { get; set; } = true;

    public AmuletGroup Clone()
    {
        var c = (AmuletGroup)MemberwiseClone();
        c.SkillKeys = new List<Keys>(SkillKeys);
        return c;
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Амулет " + KeyCodes.Display(AmuletKey) : Name;
}

public sealed class Profile
{
    public string Name { get; set; } = "Основной";
    public List<AmuletGroup> Groups { get; set; } = new();

    // --- формат версии 1.0, читается только для миграции ---
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacyBinding>? Bindings { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyMainAmulet? MainAmulet { get; set; }

    public Profile Clone(string name) => new()
    {
        Name = name,
        Groups = Groups.Select(g => g.Clone()).ToList(),
    };

    public void MigrateLegacy()
    {
        if (Groups.Count == 0 && Bindings is { Count: > 0 })
        {
            foreach (IGrouping<Keys, LegacyBinding> byAmulet in Bindings.GroupBy(b => b.AmuletKey & Keys.KeyCode))
            {
                Groups.Add(new AmuletGroup
                {
                    AmuletKey = byAmulet.Key,
                    SkillKeys = byAmulet.Select(b => b.SkillKey & Keys.KeyCode).Where(k => k != Keys.None).Distinct().ToList(),
                    DelayMs = byAmulet.Max(b => b.DelayMs),
                    Hotkey = byAmulet.Select(b => b.Hotkey).FirstOrDefault(h => h != Keys.None),
                    Enabled = byAmulet.Any(b => b.Enabled),
                });
            }
        }
        if (MainAmulet is { Enabled: true } m && m.AmuletKey != Keys.None &&
            Groups.All(g => (g.AmuletKey & Keys.KeyCode) != (m.AmuletKey & Keys.KeyCode)))
        {
            Groups.Add(new AmuletGroup { Name = m.Name ?? "", AmuletKey = m.AmuletKey, Hotkey = m.Hotkey });
        }
        Bindings = null;
        MainAmulet = null;
    }
}

public sealed class LegacyBinding
{
    public string Name { get; set; } = "";
    public Keys SkillKey { get; set; }
    public Keys AmuletKey { get; set; }
    public int DelayMs { get; set; }
    public Keys Hotkey { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class LegacyMainAmulet
{
    public bool Enabled { get; set; }
    public string? Name { get; set; }
    public Keys AmuletKey { get; set; }
    public Keys Hotkey { get; set; }
}

public sealed class OverlayConfig
{
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = 0.85;
    public int OffsetX { get; set; } = 160;
    public int OffsetY { get; set; }
    public bool Locked { get; set; }
}

public sealed class AppConfig
{
    public const int MaxDelayMs = 200;
    public const int MinHoldMs = 5;
    public const int MaxHoldMs = 150;

    public string ProcessName { get; set; } = "ElementClient.exe";
    public InputMode InputMode { get; set; } = InputMode.SendInput;
    public Keys ToggleKey { get; set; } = Keys.Pause;
    /// <summary>Сколько держать нажатой клавишу амулета (и минимум для скилла). Игра опрашивает клавиши раз в кадр.</summary>
    public int KeyHoldMs { get; set; } = 30;
    public bool AlwaysEquip { get; set; }
    public bool StartMinimized { get; set; }
    public bool LogEnabled { get; set; } = true;
    public string ActiveProfile { get; set; } = "Основной";
    public List<Profile> Profiles { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();

    public Profile GetActiveProfile()
    {
        Profile? p = Profiles.FirstOrDefault(x => x.Name == ActiveProfile) ?? Profiles.FirstOrDefault();
        if (p == null)
        {
            p = CreateDefaultProfile("Основной");
            Profiles.Add(p);
        }
        ActiveProfile = p.Name;
        return p;
    }

    public static Profile CreateDefaultProfile(string name) => new()
    {
        Name = name,
        Groups =
        {
            // Пример: скиллы на 1 и 2 требуют амулет на F1
            new AmuletGroup { AmuletKey = Keys.F1, SkillKeys = { Keys.D1, Keys.D2 }, DelayMs = 20, Hotkey = Keys.Control | Keys.D1 },
        },
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ProcessName)) ProcessName = "ElementClient.exe";
        Profiles ??= new();
        Overlay ??= new();
        Overlay.Opacity = Math.Clamp(Overlay.Opacity, 0.2, 1.0);
        Overlay.OffsetX = Math.Max(0, Overlay.OffsetX);
        Overlay.OffsetY = Math.Max(0, Overlay.OffsetY);
        KeyHoldMs = Math.Clamp(KeyHoldMs, MinHoldMs, MaxHoldMs);
        foreach (Profile p in Profiles)
        {
            p.Groups ??= new();
            p.MigrateLegacy();
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Профиль" : p.Name.Trim();
            foreach (AmuletGroup g in p.Groups)
            {
                g.DelayMs = Math.Clamp(g.DelayMs, 0, MaxDelayMs);
                g.Name ??= "";
                g.SkillKeys ??= new();
            }
        }
        GetActiveProfile();
    }
}

public static class ConfigStore
{
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "JdAmuletSwitcher.json");

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load(out string? error)
    {
        error = null;
        AppConfig? cfg = null;
        try
        {
            if (File.Exists(FilePath))
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), s_options);
        }
        catch (Exception ex)
        {
            error = $"Не удалось прочитать конфиг ({ex.Message}); загружены настройки по умолчанию.";
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { /* резервная копия не критична */ }
        }

        cfg ??= new AppConfig();
        cfg.Normalize();
        if (cfg.Profiles.Count == 0)
            cfg.Profiles.Add(AppConfig.CreateDefaultProfile("Основной"));
        return cfg;
    }

    public static void Save(AppConfig cfg)
    {
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, s_options));
        File.Move(tmp, FilePath, true);
    }
}
