using Venquery.Models;

namespace Venquery;

/// <summary>Everything that differs between editions of Venquery, in one place. The build selects
/// which profile is <see cref="Edition.Current"/> via a compile constant (see the <c>Edition</c>
/// MSBuild property in the .csproj); all shared code just reads <c>Edition.Current.*</c>. This is what
/// lets one codebase produce every edition instead of maintaining a copied source tree per edition.</summary>
public sealed class EditionProfile
{
    public required string Id { get; init; }
    /// <summary>User-visible product name, window title, in-app logo caption, MessageBox captions.</summary>
    public required string DisplayName { get; init; }
    /// <summary>Per-edition settings folder under %LOCALAPPDATA% so editions don't fight over prefs
    /// when installed side by side.</summary>
    public required string SettingsDir { get; init; }
    /// <summary>Resource file names for the title-bar logo (PNG) and window/taskbar icon (ICO).</summary>
    public required string LogoAsset { get; init; }
    public required string IconAsset { get; init; }
    /// <summary>Whether this build includes the optional setup-script page (not part of the public builds).</summary>
    public bool HasToolkit { get; init; }
    /// <summary>Whether this build includes the Repair Kit (decided by the PROTOOLS constant; this mirrors it
    /// for code that asks at runtime).</summary>
    public bool HasProTools { get; init; }
    /// <summary>What the Repair Kit page is called.</summary>
    public string ToolsName { get; init; } = "Repair Kit";
    /// <summary>The page's one-paragraph intro under the title.</summary>
    public string ToolsIntro { get; init; } =
        "Check your PC's health in depth and fix common Windows problems. The full check needs one administrator " +
        "approval and only reads, it changes nothing. Every repair below is written as a script you run yourself, " +
        "behind a confirmation, the Windows UAC prompt, and a typed Y.";
    /// <summary>The explanation in the "full system read" box (after the bold "Read-only.").</summary>
    public string ToolsDeepReadText { get; init; } =
        " One click relaunches Venquery with administrator rights for a deeper check than Windows normally allows: " +
        "your drives' health (wear, temperature, hours used, failing sectors), temperature sensors, TPM / Secure Boot / " +
        "BitLocker, whether Windows is waiting to restart, devices with problems, stopped services and network settings. " +
        "It writes nothing. Windows will show a UAC prompt.";
    /// <summary>The small print at the bottom of the page.</summary>
    public string ToolsFooter { get; init; } =
        "Checks use Windows' own data; repairs use Windows' own built-in tools (DISM, SFC, chkdsk, netsh, powercfg). " +
        "Venquery loads no drivers and sends none of this anywhere.";
    /// <summary>Whether winget is available (drives the Backup &amp; Restore page). False on the Legacy
    /// edition, Windows 7/8.1 have no winget, so the page is hidden there.</summary>
    public bool HasWinget { get; init; } = true;
    /// <summary>The two base brush palettes (dark, light), same keys/roles across editions, only the
    /// colour values differ.</summary>
    public required (string Key, string Dark, string Light)[] Palette { get; init; }
    /// <summary>The selectable themes shown in Settings (base palettes + accent-family variants).</summary>
    public required IReadOnlyList<ThemeOption> Themes { get; init; }
}

public static class Edition
{
    // A property (not a field) so it never depends on static-field initialization order.
    /// <summary>The active edition, chosen at build time.</summary>
    public static EditionProfile Current =>
#if EDITION_LEGACY
        Legacy;
#else
        Community;
#endif

    // ---- palettes (declared before the profiles that reference them) -------

    // The "Slate/Snow" dark/light palette.
    private static readonly (string Key, string Dark, string Light)[] StandardPalette =
    {
        ("Mica",         "#1E1E20", "#F3F3F3"),
        ("Frame",        "#1C1C1E", "#FAFAFA"),
        ("Pane",         "#282828", "#F3F3F3"),
        ("Pane2",        "#2C2C2C", "#FFFFFF"),
        ("Card",         "#2F2F2F", "#FBFBFB"),
        ("CardHover",    "#353535", "#FFFFFF"),
        ("Ctl",          "#33FFFFFF", "#0F000000"),
        ("Line",         "#22FFFFFF", "#16000000"),
        ("LineStrong",   "#33FFFFFF", "#22000000"),
        ("Text",         "#F0F0F0", "#1A1A1A"),
        ("Muted",        "#9BA0A6", "#57606A"),
        ("Dim",          "#6C7178", "#8B929B"),
        ("Accent",       "#2F6FED", "#005FB8"),
        ("AccentHover",  "#4079F2", "#1A6FC4"),
        ("AccentPress",  "#2A63D4", "#00519F"),
        ("Link",         "#62B4FF", "#0067C0"),
        ("Green",        "#6CCB5F", "#2E8B3D"),
        ("GreenBg",      "#1F6CCB5F", "#182E8B3D"),
        ("GreenBd",      "#556CCB5F", "#442E8B3D"),
        ("Amber",        "#F2C94C", "#9A6A00"),
        ("AmberBg",      "#1FF2C94C", "#1A9A6A00"),
        ("AmberBd",      "#55F2C94C", "#449A6A00"),
        ("AmberDot",     "#F5B13D", "#E0A400"),
        ("Red",          "#E8756C", "#C42B1C"),
        ("Subtle",       "#0DFFFFFF", "#08000000"),
        ("SubtleHover",  "#14FFFFFF", "#0F000000"),
        ("CtlBd",        "#18FFFFFF", "#22000000"),
        ("InfoBg",       "#1F2F6FED", "#14005FB8"),
        ("InfoBd",       "#552F6FED", "#33005FB8"),
        ("SidebarBg",    "#14000000", "#05000000"),
        ("DetailBg",     "#10000000", "#03000000"),
        ("AccentSubtle", "#332F6FED", "#22005FB8"),
        ("ScrollThumb",  "#3AFFFFFF", "#33000000"),
        // 1.4.0 redesign: text/icons ON an accent fill, the content layer, and the scrims used when a custom
        // background image is showing (so text stays readable over a busy picture).
        ("OnAccent",        "#FFFFFF",   "#FFFFFF"),
        ("Layer",           "#0AFFFFFF", "#80FFFFFF"),
        ("LayerOverImage",  "#B31E1E20", "#CCF7F7F7"),
        ("ChromeOverImage", "#80141416", "#A6F3F3F3"),
    };

    // ---- shared theme machinery --------------------------------------------

    // The colored themes (accent re-tints of the base) are identical across editions; only the two
    // base themes' names/swatches differ. This builds the full list = the two bases + the shared four.
    private static IReadOnlyList<ThemeOption> ThemesWithBase(
        string darkName, string darkSwatch, string lightName, string lightSwatch) => new List<ThemeOption>
    {
        new() { Id = "dark",  Name = darkName,  Dark = true,  SwatchHex = darkSwatch },
        new() { Id = "light", Name = lightName, Dark = false, SwatchHex = lightSwatch },
        new()
        {
            Id = "emerald", Name = "Emerald (dark)", Dark = true, SwatchHex = "#10B497",
            Overrides = AccentFamily("#10B497", "#1AC7A9", "#0D9A81", "#5FE9D6", onAccent: "#04201A"),
        },
        new()
        {
            Id = "amethyst", Name = "Amethyst (dark)", Dark = true, SwatchHex = "#9B6CF6",
            Overrides = AccentFamily("#9B6CF6", "#AC82F8", "#8654E4", "#CBB6FD"),
        },
        new()
        {
            Id = "rose", Name = "Rose (dark)", Dark = true, SwatchHex = "#E05A87",
            Overrides = AccentFamily("#E05A87", "#EA6D96", "#C7476F", "#FBA6C2"),
        },
        new()
        {
            Id = "sandstone", Name = "Sandstone (light)", Dark = false, SwatchHex = "#B5651D",
            Overrides = AccentFamily("#B5651D", "#C4762E", "#9A5418", "#8A4B12")
                .Concat(new (string, string)[]
                {
                    ("Mica",      "#F4EFE8"),
                    ("Frame",     "#FBF7F1"),
                    ("Pane",      "#F4EFE8"),
                    ("Pane2",     "#FFFDF9"),
                    ("Card",      "#FCF8F2"),
                    ("CardHover", "#FFFDF9"),
                    ("LayerOverImage",  "#CCF4EFE8"),
                    ("ChromeOverImage", "#A6F4EFE8"),
                }).ToArray(),
        },
    };

    // Build the accent-family override set from one accent color. Alpha-prefixed entries are
    // translucent tints of the accent (info banner fill/border, selection highlight).
    // <paramref name="onAccent"/> is the text/icon colour drawn ON the accent (buttons, ticks): dark for light accents
    // (Windows 11 does the same), white otherwise, so filled buttons stay readable.
    private static (string Key, string Color)[] AccentFamily(string accent, string hover, string press, string link,
        string onAccent = "#FFFFFF")
    {
        var bare = accent.TrimStart('#');
        return new (string, string)[]
        {
            ("Accent",       accent),
            ("OnAccent",     onAccent),
            ("AccentHover",  hover),
            ("AccentPress",  press),
            ("Link",         link),
            ("AccentSubtle", "#33" + bare),
            ("InfoBg",       "#1F" + bare),
            ("InfoBd",       "#55" + bare),
        };
    }

    // ---- profiles (reference the palettes/helpers above) -------------------

    // Venquery: the standard .NET 8 build (internal id "community", settings folder Venquery-Community).
    public static readonly EditionProfile Community = new()
    {
        Id = "community", DisplayName = "Venquery", SettingsDir = "Venquery-Community",
        LogoAsset = "venquery.png", IconAsset = "venquery.ico", HasToolkit = false, HasProTools = true,
        Palette = StandardPalette,
        Themes = ThemesWithBase("Slate (dark)", "#2F6FED", "Snow (light)", "#005FB8"),
    };

    // Legacy: the .NET Framework 4.8 build for Windows 7/8.1 (separate legacy/ project). Standard look;
    // no winget (so no Backup page), no Repair Kit. The Win7-capable core only.
    public static readonly EditionProfile Legacy = new()
    {
        Id = "legacy", DisplayName = "Venquery Legacy", SettingsDir = "Venquery-Legacy",
        LogoAsset = "venquery.png", IconAsset = "venquery.ico",
        HasToolkit = false, HasProTools = false, HasWinget = false,
        Palette = StandardPalette,
        Themes = ThemesWithBase("Slate (dark)", "#2F6FED", "Snow (light)", "#005FB8"),
    };
}
