using System;
using TMProOld;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Ui.Resources;

/// <summary>
/// Manages font resources used throughout the game UI and rendering systems.
/// Handles loading and caching of Unity fonts, TextMeshPro font assets, system fonts,
/// and emoji fonts from the operating system.
/// </summary>
internal static class FontManager {
    /// <summary>
    /// The primary font used for general UI elements throughout the application.
    /// Typically set to Perpetua or similar serif font found in the game's resources.
    /// </summary>
    public static Font UIFontRegular = null!;

    /// <summary>
    /// The emoji font loaded from the operating system.
    /// On Windows: Segoe UI Emoji (Windows 10/11) or Segoe UI Symbol (Windows 8/8.1)
    /// On macOS: Apple Color Emoji
    /// On Linux: Noto Color Emoji
    /// Used for rendering Unicode emoji characters in chat messages and other text.
    /// May be null if no system emoji font is available.
    /// </summary>
    public static Font? EmojiFont;

    /// <summary>
    /// The TextMeshPro font asset used for rendering player usernames above player objects in the game world.
    /// Typically set to "TrajanPro-Bold SDF" or similar font found in the game's TMP assets.
    /// </summary>
    public static TMP_FontAsset InGameNameFont = null!;

    /// <summary>
    /// A reliable system font (Arial/Segoe UI/Verdana/Helvetica) to use for chat input and other system UI.
    /// </summary>
    public static Font? SystemFont { get; private set; }

    /// <summary>
    /// An enhanced TextMeshPro font for the chat log display (e.g., Liberation Sans, Arial SDF).
    /// Provides better readability and rendering quality for chat messages.
    /// May be null if no suitable TMP font is found in the game resources.
    /// </summary>
    private static TMP_FontAsset? _chatLogFont;

    /// <summary>
    /// Priority-ordered list of system font names to try when loading the system font.
    /// Ordered from most common/reliable to least common.
    /// </summary>
    private static readonly string[] SystemFontNames = ["Arial", "Segoe UI", "Verdana", "Helvetica"];

    /// <summary>
    /// Priority-ordered list of emoji font names to try when loading the emoji font.
    /// Covers Windows, macOS, and Linux platforms.
    /// </summary>
    private static readonly string[] EmojiFontNames = [
        "Segoe UI Emoji", // Windows 10/11
        "Segoe UI Symbol", // Windows 8/8.1
        "Apple Color Emoji", // macOS
        "Noto Color Emoji" // Linux
    ];

    /// <summary>
    /// Loads all required fonts by searching Unity's resource system and the operating system.
    /// This method should be called during application initialization.
    /// </summary>
    public static void LoadFonts() {
        Logger.Info("Loading fonts...");

        // 1. Get OS installed fonts ONCE to avoid exceptions.
        string[] osFonts = Font.GetOSInstalledFontNames();

        // 2. Scan heap ONCE for each type. 
        var activeFonts = UnityEngine.Resources.FindObjectsOfTypeAll<Font>();
        var activeTmpFonts = UnityEngine.Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

        try {
            LoadSystemFont(osFonts, activeFonts);
            LoadUnityFonts(activeFonts);
            LoadTMPFonts(activeTmpFonts);
            LoadEmojiFont(osFonts);

            SSMP.Util.EmojiSpriteLoader.Load();
            ValidateFonts();
        } finally {
            Array.Clear(activeFonts, 0, activeFonts.Length);
            Array.Clear(activeTmpFonts, 0, activeTmpFonts.Length);
        }
    }

    /// <summary>
    /// Loads Unity Font objects from the game's resources.
    /// Currently searches for Perpetua as the UI font.
    /// Uses a ReadOnlySpan to avoid enumerator allocations and caches the font name to reduce native crossings.
    /// </summary>
    private static void LoadUnityFonts(ReadOnlySpan<Font> fonts) {
        foreach (var font in fonts) {
            var fontName = font.name;

            if (fontName.Equals("Perpetua", StringComparison.OrdinalIgnoreCase)) {
                UIFontRegular = font;
                Logger.Info($"Found UI font: {fontName}");
                break;
            }
        }
    }

    /// <summary>
    /// Loads TextMeshPro font assets from the game's resources.
    /// Searches for TrajanPro-Bold SDF for in-game names and Liberation Sans/Arial for chat.
    /// Uses StringComparison.OrdinalIgnoreCase for faster, culture-agnostic matching.
    /// </summary>
    private static void LoadTMPFonts(ReadOnlySpan<TMP_FontAsset> tmpFonts) {
        foreach (var tmpFont in tmpFonts) {
            var fontName = tmpFont.name;

            if (_chatLogFont == null &&
                (fontName.Contains("Liberation", StringComparison.OrdinalIgnoreCase) ||
                 fontName.Contains("Arial", StringComparison.OrdinalIgnoreCase))) {
                _chatLogFont = tmpFont;
                Logger.Info($"Selected ChatLogFont: {fontName}");
            }

            if (InGameNameFont == null && fontName.Equals("TrajanPro-Bold SDF", StringComparison.OrdinalIgnoreCase)) {
                InGameNameFont = tmpFont;
                Logger.Info($"Found InGameNameFont: {fontName}");
            }

            if (_chatLogFont != null && InGameNameFont != null) break;
        }
    }

    /// <summary>
    /// Attempts to load a reliable system font from the operating system or Unity's built-in resources.
    /// Validates existence against pre-fetched OS fonts to avoid expensive exceptions during Font creation.
    /// </summary>
    private static void LoadSystemFont(string[] osFonts, ReadOnlySpan<Font> builtInFonts) {
        foreach (var name in SystemFontNames) {
            if (Array.Exists(osFonts, f => f.Equals(name, StringComparison.OrdinalIgnoreCase))) {
                SystemFont = Font.CreateDynamicFontFromOSFont(name, 24);
                if (SystemFont != null) {
                    Logger.Info($"Loaded System Font: {name}");
                    return;
                }
            }
        }

        foreach (var font in builtInFonts) {
            if (font.name.Equals("Arial", StringComparison.OrdinalIgnoreCase)) {
                SystemFont = font;
                Logger.Info("Found built-in Arial");
                return;
            }
        }
    }

    /// <summary>
    /// Attempts to load the system emoji font from the operating system.
    /// Checks available OS fonts first to bypass costly try/catch exceptions.
    /// </summary>
    /// <remarks>
    /// Font priority by platform:
    /// - Windows 10/11: Segoe UI Emoji
    /// - Windows 8/8.1: Segoe UI Symbol
    /// - macOS: Apple Color Emoji
    /// - Linux: Noto Color Emoji
    /// If no emoji font is found, emoji characters may not render correctly.
    /// </remarks>
    private static void LoadEmojiFont(string[] osFonts) {
        foreach (var fontName in EmojiFontNames) {
            if (Array.Exists(osFonts, f => f.Equals(fontName, StringComparison.OrdinalIgnoreCase))) {
                EmojiFont = Font.CreateDynamicFontFromOSFont(fontName, 16);
                if (EmojiFont != null) {
                    Logger.Info($"Loaded emoji font: {fontName}");
                    return;
                }
            }
        }

        Logger.Warn("No emoji font found on system, emojis may not display correctly");
    }

    /// <summary>
    /// Validates that critical fonts have been loaded successfully.
    /// Logs errors if required UI or in-game name fonts are missing.
    /// </summary>
    private static void ValidateFonts() {
        if (UIFontRegular == null) Logger.Error("UI font regular is missing!");
        if (InGameNameFont == null) Logger.Error("In-game name font is missing!");
    }

    /// <summary>
    /// Call this from a MonoBehaviour OnDestroy() or AppDomain unload 
    /// to release GC handles over native Unity objects.
    /// </summary>
    public static void UnloadFonts() {
        UIFontRegular = null!;
        EmojiFont = null;
        InGameNameFont = null!;
        SystemFont = null;
        _chatLogFont = null;
    }
}
