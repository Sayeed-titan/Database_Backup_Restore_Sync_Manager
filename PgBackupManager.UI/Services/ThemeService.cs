using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PgBackupManager.Core.Services;

namespace PgBackupManager.UI.Services;

public sealed record AccentOption(string Name, Color Color)
{
    public SolidColorBrush Brush { get; } = Freeze(new SolidColorBrush(Color));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}

// Live theme switching. Every colour in Theme/Design.xaml is referenced via
// DynamicResource, so replacing the brushes in Application.Resources here
// repaints the whole app instantly — no restart, no per-view code.
public static class ThemeService
{
    public static readonly IReadOnlyList<string> Modes = new[] { "Light", "Dark", "System" };

    public static readonly IReadOnlyList<AccentOption> Accents = new AccentOption[]
    {
        new("Teal",    C("#0A7D9A")),
        new("Blue",    C("#2563EB")),
        new("Indigo",  C("#4F46E5")),
        new("Violet",  C("#7C3AED")),
        new("Emerald", C("#059669")),
        new("Rose",    C("#E11D48")),
        new("Amber",   C("#D97706")),
        new("Slate",   C("#475569")),
    };

    public static string CurrentMode { get; private set; } = "Light";
    public static string CurrentAccent { get; private set; } = "Teal";
    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void ApplySaved()
    {
        var s = new SettingsStore().Load();
        Apply(s.ThemeMode, s.AccentName);
    }

    // Applies and persists (only the theme fields — other unsaved Settings
    // edits are not flushed, since this reloads from disk first).
    public static void ApplyAndSave(string mode, string accent)
    {
        Apply(mode, accent);
        var store = new SettingsStore();
        var s = store.Load();
        s.ThemeMode = CurrentMode;
        s.AccentName = CurrentAccent;
        store.Save(s);
    }

    public static void Apply(string? mode, string? accentName)
    {
        CurrentMode = Modes.Contains(mode ?? "") ? mode! : "Light";
        var accent = Accents.FirstOrDefault(a => a.Name == accentName) ?? Accents[0];
        CurrentAccent = accent.Name;
        IsDark = CurrentMode == "Dark" || (CurrentMode == "System" && WindowsPrefersDark());

        var res = Application.Current.Resources;
        var a = accent.Color;

        var p = IsDark ? DarkPalette() : LightPalette();
        // Accent-derived keys (same for both modes, tinted to suit the surface).
        p["Teal"] = a;
        p["TealHover"] = Mix(a, Colors.White, IsDark ? 0.18 : 0.10);
        p["Teal2"] = Mix(a, Colors.White, 0.16);
        p["TealBadgeBg"] = IsDark ? Mix(p["CardBg"], a, 0.22) : Mix(Colors.White, a, 0.12);
        p["HoverBg"] = IsDark ? Mix(p["CardBg"], a, 0.12) : Mix(Colors.White, a, 0.06);
        p["SelectionBg"] = IsDark ? Mix(p["CardBg"], a, 0.35) : Mix(Colors.White, a, 0.20);
        if (IsDark) p["Teal"] = Mix(a, Colors.White, 0.12); // a touch brighter for contrast on dark cards

        foreach (var (key, color) in p)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            res[key] = b;
        }
        res["TealColor"] = p["Teal"];
        res["TealHoverColor"] = p["TealHover"];
        res["Teal2Color"] = p["Teal2"];
        res["NavyColor"] = p["Navy"];
        res["BgColor"] = p["AppBg"];

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static Dictionary<string, Color> LightPalette() => new()
    {
        ["Navy"] = C("#0F2340"),
        ["AppBg"] = C("#F0F4F8"),
        ["CardBg"] = C("#FFFFFF"),
        ["CardSubtle"] = C("#F8FAFC"),
        ["BorderSoft"] = C("#E2E8F0"),
        ["BorderLight"] = C("#F1F5F9"),
        ["Muted"] = C("#94A3B8"),
        ["Body"] = C("#334155"),
        ["Secondary"] = C("#64748B"),
        ["CheckBorder"] = C("#CBD5E1"),
        ["TitleBar"] = C("#0F2340"),
        ["TitleBarHover"] = C("#1B3454"),
        ["TitleText"] = C("#AEBFD4"),
        ["ScrollThumb"] = C("#CBD5E1"),
        ["StatusGreen"] = C("#166534"),
        ["StatusAmber"] = C("#B45309"),
        ["StatusRed"] = C("#B91C1C"),
        ["TermBg"] = C("#0D0D16"),
        ["TermBar"] = C("#1A1A26"),
        ["TermBorder"] = C("#1C1C2A"),
        ["TermText"] = C("#9AA4B2"),
        ["TermSuccess"] = C("#4ADE80"),
        ["TermWarn"] = C("#FBBF24"),
    };

    private static Dictionary<string, Color> DarkPalette() => new()
    {
        ["Navy"] = C("#E6EDF3"),
        ["AppBg"] = C("#0D1117"),
        ["CardBg"] = C("#161B22"),
        ["CardSubtle"] = C("#1C2129"),
        ["BorderSoft"] = C("#2D333B"),
        ["BorderLight"] = C("#22272E"),
        ["Muted"] = C("#768390"),
        ["Body"] = C("#CDD9E5"),
        ["Secondary"] = C("#9DA7B3"),
        ["CheckBorder"] = C("#444C56"),
        ["TitleBar"] = C("#010409"),
        ["TitleBarHover"] = C("#1C2129"),
        ["TitleText"] = C("#9DA7B3"),
        ["ScrollThumb"] = C("#373E47"),
        ["StatusGreen"] = C("#57D38C"),
        ["StatusAmber"] = C("#F0B849"),
        ["StatusRed"] = C("#F47067"),
        ["TermBg"] = C("#010409"),
        ["TermBar"] = C("#0D1117"),
        ["TermBorder"] = C("#2D333B"),
        ["TermText"] = C("#9DA7B3"),
        ["TermSuccess"] = C("#57D38C"),
        ["TermWarn"] = C("#F0B849"),
    };

    private static bool WindowsPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
