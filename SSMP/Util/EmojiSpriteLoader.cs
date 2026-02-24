using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMProOld;
using UnityEngine;
using Logger = SSMP.Logging.Logger;

namespace SSMP.Util;

/// <summary>
/// Loads emoji sprites from embedded PNG spritesheet and JSON metadata at runtime.
/// Creates a TMP_SpriteAsset that can be used with TextMeshPro's sprite tags.
/// This loader handles the conversion from the IamCal emoji coordinate system to Unity's coordinate system,
/// manages sprite caching, and provides efficient lookup mechanisms for emoji shortcodes.
/// </summary>
public static class EmojiSpriteLoader {

    #region Constants

    /// <summary>
    /// The sprite size in pixels for each emoji in the grid (64x64).
    /// This is the standard size used in the emoji spritesheet layout.
    /// </summary>
    private const int SpriteSize = 64;

    /// <summary>
    /// The stride between sprite centres in the spritesheet (64 pixels + 2 padding = 66).
    /// Used to calculate sprite positions from grid coordinates.
    /// </summary>
    private const int SpriteStride = 66;

    /// <summary>
    /// The padding around each sprite in the spritesheet (1 pixel on each side).
    /// </summary>
    private const int SpritePadding = 1;

    /// <summary>
    /// The total height of the emoji spritesheet in pixels (4096).
    /// Used for coordinate conversion from top-left to bottom-left origin.
    /// </summary>
    private const int SheetHeight = 4096;

    /// <summary>
    /// Vertical offset multiplier for emoji positioning (90% of sprite size).
    /// Used to align emojis properly with text baselines.
    /// </summary>
    private const float YOffsetMultiplier = 0.9f;

    /// <summary>
    /// Embedded resource name for the PNG spritesheet.
    /// </summary>
    private const string PngResourceName = "SSMP.Ui.Resources.Images.emojis.png";

    /// <summary>
    /// Embedded resource name for the JSON metadata containing emoji coordinates and shortcodes.
    /// </summary>
    private const string JsonResourceName = "SSMP.Resource.emojis.json";

    #endregion

    #region Fields

    /// <summary>
    /// The loaded TextMeshPro sprite asset containing all emoji sprites.
    /// Null if sprites have not been loaded yet or loading failed.
    /// </summary>
    private static TMP_SpriteAsset? SpriteAsset { get; set; }

    /// <summary>
    /// Maps emoji shortcode names (e.g., ":smile:") to sprite indices for O(1) lookup.
    /// Uses case-insensitive comparison to handle variations in shortcode casing.
    /// </summary>
    private static readonly Dictionary<string, int> ShortcodeToIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cache of created Unity Sprite objects indexed by sprite ID.
    /// Prevents redundant sprite creation and improves performance for repeated lookups.
    /// </summary>
    private static readonly Dictionary<int, Sprite> SpriteCache = new();

    /// <summary>
    /// Indicates whether the loader has attempted initialization.
    /// Prevents redundant loading attempts.
    /// </summary>
    private static bool _initialized;

    #endregion

    #region Public API

    /// <summary>
    /// Gets a value indicating whether the emoji sprites have been successfully loaded.
    /// </summary>
    public static bool IsLoaded => SpriteAsset != null;

    /// <summary>
    /// Returns a collection of all registered emoji shortcodes.
    /// This can be used for autocomplete functionality or validation.
    /// </summary>
    /// <returns>A collection of shortcode strings (e.g., ":smile:", ":heart:").</returns>
    public static ICollection<string> GetShortcodes() => ShortcodeToIndex.Keys;

    /// <summary>
    /// Loads the emoji spritesheet and metadata from embedded resources.
    /// Idempotent — calling multiple times will only load once.
    /// The loading process:
    /// 1. Reads the PNG spritesheet from embedded resources.
    /// 2. Reads the JSON metadata with sprite coordinates.
    /// 3. Converts coordinates from IamCal format to Unity format.
    /// 4. Creates a TMP_SpriteAsset with all sprites.
    /// 5. Sets up materials and lookup tables.
    /// </summary>
    public static void Load() {
        if (_initialized) { return; }
        _initialized = true;

        try {
            var assembly = Assembly.GetExecutingAssembly();

            using var pngStream = assembly.GetManifestResourceStream(PngResourceName);
            if (pngStream == null) {
                Logger.Warn($"[EmojiSpriteLoader] PNG resource not found: {PngResourceName}");
                return;
            }

            using var jsonStream = assembly.GetManifestResourceStream(JsonResourceName);
            if (jsonStream == null) {
                Logger.Warn($"[EmojiSpriteLoader] JSON resource not found: {JsonResourceName}");
                return;
            }

            var texture = LoadTextureFromStream(pngStream);
            if (texture == null) {
                Logger.Error("[EmojiSpriteLoader] Failed to load PNG texture.");
                return;
            }

            Logger.Info($"[EmojiSpriteLoader] Loaded texture: {texture.width}x{texture.height}");
            Logger.Info($"[EmojiSpriteLoader] Grid size: {texture.width / SpriteSize}x{texture.height / SpriteSize}");

            var emojiData = LoadEmojiMetadata(jsonStream);
            if (emojiData == null) {
                Logger.Error("[EmojiSpriteLoader] Failed to parse JSON metadata.");
                return;
            }

            SpriteAsset = CreateSpriteAsset(texture, emojiData);
            if (SpriteAsset == null) {
                Logger.Error("[EmojiSpriteLoader] Failed to create sprite asset.");
                return;
            }

            if (!SetupMaterial(texture)) {
                Logger.Error("[EmojiSpriteLoader] Failed to setup material.");
                return;
            }

            UpdateSpriteAssetTables();

            Logger.Info($"[EmojiSpriteLoader] Loaded {ShortcodeToIndex.Count} emoji sprites.");
        } catch (Exception ex) {
            Logger.Error($"[EmojiSpriteLoader] Failed to load emojis: {ex}");
        }
    }

    /// <summary>
    /// Gets a standard Unity Sprite for the given sprite index.
    /// Creates and caches the sprite on first access for performance.
    /// Handles coordinate clamping to prevent texture boundary issues.
    /// </summary>
    /// <param name="index">The sprite index to retrieve.</param>
    /// <returns>The Unity Sprite object, or null if the index is invalid or sprite creation failed.</returns>
    public static Sprite? GetSprite(int index) {
        if (SpriteCache.TryGetValue(index, out var cached)) { return cached; }

        if (SpriteAsset == null || index < 0 || index >= SpriteAsset.spriteInfoList.Count) {
            return null;
        }

        var info = SpriteAsset.spriteInfoList[index];
        var tex  = SpriteAsset.spriteSheet as Texture2D;

        if (tex == null) { return null; }

        // Clamp to texture bounds to handle edge sprites.
        var x = info.x;
        var y = info.y;
        var w = info.width;
        var h = info.height;

        if (x + w > tex.width)  { w = tex.width  - x; }
        if (y + h > tex.height) { h = tex.height - y; }

        if (w <= 0 || h <= 0) { return null; }

        var sprite = Sprite.Create(tex, new Rect(x, y, w, h), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = info.name;

        SpriteCache[index] = sprite;
        return sprite;
    }

    /// <summary>
    /// Gets the sprite index for a given emoji shortcode.
    /// Uses case-insensitive matching for flexibility.
    /// </summary>
    /// <param name="shortcode">The emoji shortcode to look up (e.g., ":smile:").</param>
    /// <returns>The sprite index if found, or -1 if the shortcode is not recognised.</returns>
    public static int GetSpriteIndex(string shortcode) {
        return ShortcodeToIndex.GetValueOrDefault(shortcode, -1);
    }

    /// <summary>
    /// Checks if a sprite exists for the given emoji shortcode.
    /// Prefer this over GetSpriteIndex when only existence is needed — avoids the out-value overhead.
    /// </summary>
    /// <param name="shortcode">The emoji shortcode to check (e.g., ":smile:").</param>
    /// <returns>True if the shortcode has a corresponding sprite, false otherwise.</returns>
    public static bool HasSprite(string shortcode) {
        return ShortcodeToIndex.ContainsKey(shortcode);
    }

    #endregion

    #region Loading

    /// <summary>
    /// Loads a Texture2D from a stream containing PNG data.
    /// Reads the entire stream into a byte array before handing off to Unity's LoadImage,
    /// which requires a materialized buffer rather than a streaming API.
    /// </summary>
    /// <param name="pngStream">Stream containing PNG image data.</param>
    /// <returns>The loaded texture, or null if loading failed.</returns>
    private static Texture2D? LoadTextureFromStream(Stream pngStream) {
        using var memStream = new MemoryStream();
        pngStream.CopyTo(memStream);
        var pngBytes = memStream.ToArray();

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) {
            filterMode = FilterMode.Bilinear
        };

        return !texture.LoadImage(pngBytes) ? null : texture;
    }

    /// <summary>
    /// Loads and parses the emoji metadata JSON from a stream.
    /// </summary>
    /// <param name="jsonStream">Stream containing JSON metadata.</param>
    /// <returns>Dictionary mapping shortcodes to coordinate objects, or null if parsing failed.</returns>
    private static Dictionary<string, JObject>? LoadEmojiMetadata(Stream jsonStream) {
        using var jsonReader = new StreamReader(jsonStream);
        var jsonText = jsonReader.ReadToEnd();
        return JsonConvert.DeserializeObject<Dictionary<string, JObject>>(jsonText);
    }

    #endregion

    #region Sprite Asset

    /// <summary>
    /// Creates a TMP_SpriteAsset and populates it with sprites from the metadata.
    /// Assigns the spriteInfoList via reflection since TMP_SpriteAsset does not expose
    /// a public setter in this version of the library.
    /// </summary>
    /// <param name="texture">The sprite sheet texture.</param>
    /// <param name="emojiData">Dictionary containing emoji coordinates and metadata.</param>
    /// <returns>The created sprite asset with all sprites configured.</returns>
    private static TMP_SpriteAsset CreateSpriteAsset(Texture2D texture, Dictionary<string, JObject> emojiData) {
        var spriteAsset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
        spriteAsset.name        = "EmojiSpriteAsset";
        spriteAsset.spriteSheet = texture;

        var spriteInfoList = new List<TMP_Sprite>(emojiData.Count);
        var spriteIndex    = 0;

        foreach (var (shortcode, coords) in emojiData) {
            var sheetX = coords["x"]?.Value<int>() ?? 0;
            var sheetY = coords["y"]?.Value<int>() ?? 0;

            var (rectX, rectY) = ConvertCoordinates(sheetX, sheetY);

            spriteInfoList.Add(new TMP_Sprite {
                id       = spriteIndex,
                name     = shortcode.Trim(':'),
                x        = rectX,
                y        = rectY,
                width    = SpriteSize,
                height   = SpriteSize,
                xOffset  = 0,
                yOffset  = SpriteSize * YOffsetMultiplier,
                xAdvance = SpriteSize,
                scale    = 1.0f
            });

            ShortcodeToIndex[shortcode] = spriteIndex;
            spriteIndex++;
        }

        var field = typeof(TMP_SpriteAsset).GetField(
            "spriteInfoList",
            BindingFlags.Public | BindingFlags.Instance
        );
        field?.SetValue(spriteAsset, spriteInfoList);

        return spriteAsset;
    }

    /// <summary>
    /// Converts IamCal sprite coordinates (top-left origin) to Unity coordinates (bottom-left origin).
    /// IamCal format: x = (sheet_x * 66) + 1, y = (sheet_y * 66) + 1.
    /// Unity format requires a Y-axis flip: rectY = SheetHeight - topY - SpriteSize.
    /// Simplified: rectY = SheetHeight - SpriteStride - (sheetY * SpriteStride) - SpritePadding.
    /// </summary>
    /// <param name="sheetX">The X grid coordinate in the IamCal system.</param>
    /// <param name="sheetY">The Y grid coordinate in the IamCal system.</param>
    /// <returns>Tuple of (rectX, rectY) in Unity's coordinate system.</returns>
    private static (float rectX, float rectY) ConvertCoordinates(int sheetX, int sheetY) {
        float rectX = (sheetX * SpriteStride) + SpritePadding;
        float rectY = SheetHeight - SpriteStride - (sheetY * SpriteStride) - SpritePadding;
        return (rectX, rectY);
    }

    /// <summary>
    /// Updates the sprite asset's internal lookup tables for efficient sprite access.
    /// Calls the private UpdateLookupTables method via reflection if available.
    /// </summary>
    private static void UpdateSpriteAssetTables() {
        var updateMethod = typeof(TMP_SpriteAsset).GetMethod(
            "UpdateLookupTables",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance
        );

        if (updateMethod != null) {
            updateMethod.Invoke(SpriteAsset, null);
            Logger.Debug("[EmojiSpriteLoader] Called UpdateLookupTables.");
        }
    }

    #endregion

    #region Material

    /// <summary>
    /// Sets up the material for rendering emoji sprites.
    /// Attempts to clone from an existing font material for compatibility, with a fallback path.
    /// </summary>
    /// <param name="texture">The emoji sprite sheet texture.</param>
    /// <returns>True if material setup succeeded, false otherwise.</returns>
    private static bool SetupMaterial(Texture2D texture) {
        Material? material;

        if (Ui.Resources.FontManager.InGameNameFont.material != null) {
            material = new Material(Ui.Resources.FontManager.InGameNameFont.material) {
                name        = "EmojiSpriteMaterial",
                mainTexture = texture
            };

            var spriteShader = FindSpriteShader();
            if (spriteShader != null) { material.shader = spriteShader; }

            Logger.Info($"[EmojiSpriteLoader] Cloned material from InGameNameFont, shader: {material.shader.name}");
        } else {
            material = CreateFallbackMaterial(texture);
        }

        if (material == null) { return false; }

        SpriteAsset!.material         = material;
        SpriteAsset.materialHashCode  = TMP_TextUtilities.GetSimpleHashCode(material.name);

        Logger.Info($"[EmojiSpriteLoader] Assigned material hash: {SpriteAsset.materialHashCode}");
        return true;
    }

    /// <summary>
    /// Finds an appropriate sprite shader with fallback options.
    /// </summary>
    /// <returns>The first available shader from the preferred list, or null if none are found.</returns>
    private static Shader? FindSpriteShader() {
        return Shader.Find("Mobile/Particles/Alpha Blended")
            ?? Shader.Find("UI/Default");
    }

    /// <summary>
    /// Creates a fallback material when font material cloning is not available.
    /// </summary>
    /// <param name="texture">The emoji sprite sheet texture.</param>
    /// <returns>The created material, or null if no suitable shader was found.</returns>
    private static Material? CreateFallbackMaterial(Texture2D texture) {
        var shader = FindSpriteShader();
        if (shader == null) { return null; }

        Logger.Warn("[EmojiSpriteLoader] Created fallback material (font material not found).");

        return new Material(shader) {
            mainTexture = texture,
            name        = "EmojiSpriteMaterial_Fallback"
        };
    }

    #endregion
}
