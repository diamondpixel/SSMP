using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using SSMP.Logging;

namespace SSMP.Util;

/// <summary>
/// Utility class for parsing text and replacing emoji shortcodes with Unicode emojis.
/// Uses a Trie data structure for O(k) prefix search autocomplete where k is the prefix length.
/// Supports encoding emojis as rich text tags for layout-aware rendering.
/// </summary>
internal static class TextParser {

    #region Constants

    /// <summary>
    /// Fullwidth low line character used as a placeholder for emojis in encoded text.
    /// This character provides consistent width for layout calculations.
    /// </summary>
    private const string EmojiPlaceholder = "\uFF3F";

    /// <summary>
    /// Precomputed byte length of the static parts of the encoded emoji format:
    /// "&lt;color=#" (8) + "XXXXXX" (6) + "&gt;" (1) + placeholder + "&lt;/color&gt; " (9) = 24 + placeholder length.
    /// Used by string.Create to allocate the exact correct size with no resizing.
    /// </summary>
    private static readonly int EncodedEmojiStaticLength = 24 + EmojiPlaceholder.Length;

    #endregion

    #region Fields

    /// <summary>
    /// Lazily initializes the trie exactly once in a thread-safe manner.
    /// LazyThreadSafetyMode.ExecutionAndPublication ensures:
    ///   1. Only one thread runs the factory even under concurrent access.
    ///   2. The flag is only set after the factory completes successfully.
    ///   3. If the factory throws, the next access retries — no permanently broken state.
    /// Replaces the previous manual _isLoaded / _emojiTrie pattern which had three
    /// distinct race conditions and a misleading "thread-safe" comment.
    /// </summary>
    private static readonly Lazy<EmojiTrie?> LazyTrie = new(
        BuildTrie,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    /// <summary>
    /// Compiled regex pattern to find emoji shortcodes in text (e.g., :smile:, :thumbs_up:).
    /// Pattern matches colons surrounding alphanumeric characters and common punctuation.
    /// </summary>
    private static readonly Regex ShortcodeRegex = new(
        @":[a-zA-Z0-9_\-\.,'()]+:",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Compiled regex pattern to find encoded emojis (emoji ID stored in color hex value).
    /// Format: &lt;color=#XXXXXX&gt;PLACEHOLDER&lt;/color&gt; where XXXXXX is a 6-digit hex ID.
    /// This encoding allows the emoji to be processed through Unity's text layout system
    /// while preserving positional information.
    /// </summary>
    private static readonly Regex EncodedEmojiRegex = new(
        $@"<color=#([0-9A-Fa-f]{{6}})>({EmojiPlaceholder})</color>",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Cached static MatchEvaluator delegate for ParseAndEncode.
    /// Storing as a static field prevents a new delegate instance from being allocated
    /// on every ParseAndEncode call. The method captures no instance state so this is safe.
    /// </summary>
    private static readonly MatchEvaluator ShortcodeEvaluator = EvaluateShortcode;

    /// <summary>
    /// Per-thread StringBuilder for DecodeAndGetPositions.
    /// [ThreadStatic] gives each thread its own instance — no locking, no contention,
    /// and no silent data corruption from concurrent calls on the previous shared instance.
    /// Marked nullable because [ThreadStatic] fields are always null on non-initializing threads.
    /// </summary>
    [ThreadStatic]
    private static System.Text.StringBuilder? _threadLocalStringBuilder;

    #endregion

    #region Initialisation

    /// <summary>
    /// Builds and populates the emoji trie from the sprite loader.
    /// Called exactly once by <see cref="LazyTrie"/>; subsequent accesses return the cached result.
    /// Returns null if the sprite loader fails, allowing callers to handle the missing-data case
    /// gracefully rather than working with an empty trie silently.
    /// </summary>
    private static EmojiTrie? BuildTrie() {
        EmojiSpriteLoader.Load();

        if (!EmojiSpriteLoader.IsLoaded) {
            Logger.Debug("[TextParser] Sprite loader failed — trie not populated.");
            return null;
        }

        var trie = new EmojiTrie();
        var keys = EmojiSpriteLoader.GetShortcodes();

        foreach (var key in keys) {
            // Insert shortcode as both key and value — unicode is not available at this stage
            // and only the shortcode string is needed for completion suggestions.
            trie.Insert(key, key, key);
        }

        Logger.Debug($"[TextParser] Populated Trie with {keys.Count} shortcodes from loader.");
        return trie;
    }

    #endregion

    #region Encoding

    /// <summary>
    /// Parses text and replaces emoji shortcodes with encoded placeholders for wrapping-aware processing.
    /// The encoding format is: &lt;color=#XXXXXX&gt;PLACEHOLDER&lt;/color&gt; followed by a space.
    /// The emoji ID is encoded in the hex color value (e.g., #00007B for ID 123).
    /// This allows Unity's StripRichTextTags to remove the tag but keep the placeholder width,
    /// ensuring GetPreferredWidth sees exactly 1 character width (plus space) for proper text wrapping.
    /// </summary>
    /// <param name="text">The input text containing emoji shortcodes (e.g., :smile:).</param>
    /// <returns>Text with shortcodes replaced by encoded emoji placeholders.</returns>
    /// <example>
    /// Input:  "Hello :smile: world"
    /// Output: "Hello &lt;color=#000005&gt;＿&lt;/color&gt; world"  (where 5 is the sprite index)
    /// </example>
    public static string ParseAndEncode(string text) {
        return string.IsNullOrEmpty(text) ? text : ShortcodeRegex.Replace(text, ShortcodeEvaluator);
    }

    /// <summary>
    /// MatchEvaluator for <see cref="ParseAndEncode"/>.
    /// Stored as a static delegate in <see cref="ShortcodeEvaluator"/> to prevent a new
    /// delegate allocation on every ParseAndEncode call.
    ///
    /// Uses string.Create with a TryFormat span-write to produce the final encoded string
    /// in a single allocation of exactly the right size, replacing the previous two-allocation
    /// pattern of ToString("X6") + string interpolation.
    /// </summary>
    private static string EvaluateShortcode(Match match) {
        if (!EmojiSpriteLoader.HasSprite(match.Value)) { return match.Value; }

        var index = EmojiSpriteLoader.GetSpriteIndex(match.Value);
        if (index < 0) { return match.Value; }

        // string.Create allocates exactly one string of the correct length and writes
        // all parts directly into its buffer — no intermediate hex string, no interpolation.
        return string.Create(EncodedEmojiStaticLength, index, static (span, idx) => {
            var pos = 0;

            "<color=#".AsSpan().CopyTo(span);
            pos += 8;

            idx.TryFormat(span[pos..], out var written, "X6");
            pos += written;

            ">".AsSpan().CopyTo(span[pos..]);
            pos += 1;

            EmojiPlaceholder.AsSpan().CopyTo(span[pos..]);
            pos += EmojiPlaceholder.Length;

            "</color> ".AsSpan().CopyTo(span[pos..]);
        });
    }

    #endregion

    #region Decoding

    /// <summary>
    /// Decodes an encoded string, extracting emoji positions and IDs while returning clean text.
    /// Allocating overload — allocates both the result string and the emoji list internally.
    /// For hot paths where the list can be reused, prefer the overload that accepts a caller-supplied list.
    /// </summary>
    /// <param name="encodedText">Text containing encoded emoji placeholders with hex color IDs.</param>
    /// <returns>
    /// A tuple containing:
    /// - text: The decoded text with placeholder characters but without color tags.
    /// - emojis: List of tuples mapping character indices to emoji IDs.
    /// </returns>
    /// <example>
    /// Input:  "Hello &lt;color=#000005&gt;＿&lt;/color&gt; world"
    /// Output: ("Hello ＿ world", [(6, 5)])
    /// </example>
    public static (string text, List<(int charIndex, int emojiId)> emojis) DecodeAndGetPositions(string encodedText) {
        // Guard before list allocation — avoids constructing an empty list on trivial inputs.
        if (string.IsNullOrEmpty(encodedText)) { return (encodedText, []); }

        var emojis = new List<(int charIndex, int emojiId)>();
        var text   = DecodeAndGetPositions(encodedText, emojis);
        return (text, emojis);
    }

    /// <summary>
    /// Decodes an encoded string into a caller-supplied emoji list, avoiding the per-call list allocation.
    /// Prefer this overload in hot paths where the list can be cleared and reused across calls.
    /// </summary>
    /// <param name="encodedText">Text containing encoded emoji placeholders with hex color IDs.</param>
    /// <param name="emojis">Caller-supplied list that will be populated with (charIndex, emojiId) pairs.
    /// The list is not cleared before use — caller is responsible for clearing if reusing.</param>
    /// <returns>The decoded text with placeholder characters but without color tags.</returns>
    private static string DecodeAndGetPositions(string encodedText, List<(int charIndex, int emojiId)> emojis) {
        if (string.IsNullOrEmpty(encodedText)) { return encodedText; }

        // Retrieve or create the per-thread StringBuilder.
        // [ThreadStatic] fields are null on every thread except the one that ran the field initializer,
        // so we must null-check and lazily construct here.
        _threadLocalStringBuilder ??= new System.Text.StringBuilder(2048);
        var sb = _threadLocalStringBuilder;
        sb.Clear();
        sb.EnsureCapacity(encodedText.Length);

        var lastIndex = 0;

        foreach (Match match in EncodedEmojiRegex.Matches(encodedText)) {
            // Append the literal text between the previous match end and this match start.
            if (match.Index > lastIndex) {
                sb.Append(encodedText, lastIndex, match.Index - lastIndex);
            }

            if (int.TryParse(
                    match.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out var id
            )) {
                emojis.Add((sb.Length, id));
                sb.Append(match.Groups[2].Value);
            } else {
                // Malformed encoding — preserve original text.
                sb.Append(match.Value);
            }

            lastIndex = match.Index + match.Length;
        }

        // Append any remaining text after the last match.
        if (lastIndex < encodedText.Length) {
            sb.Append(encodedText, lastIndex, encodedText.Length - lastIndex);
        }

        return sb.ToString();
    }

    #endregion

    #region Autocomplete

    /// <summary>
    /// Returns emoji shortcode suggestions for the given prefix using trie-based autocomplete.
    /// Allocating overload — allocates a new list per call.
    /// For per-keystroke autocomplete, prefer the overload that accepts a caller-supplied list
    /// to avoid GC pressure on every keystroke.
    /// Provides O(k + m) lookup where k is prefix length and m is number of results.
    /// </summary>
    /// <param name="prefix">The prefix to search for (e.g., ":sm" to find ":smile:").</param>
    /// <param name="max">Maximum number of suggestions to return. Default is 10.</param>
    /// <returns>
    /// List of (shortcode, emoji) tuples matching the prefix.
    /// Empty list if trie is not loaded or no matches are found.
    /// </returns>
    public static List<(string shortcode, string emoji)> GetCompletions(ReadOnlySpan<char> prefix, int max = 10) {
        var results = new List<(string, string)>(max);
        GetCompletions(prefix, results, max);
        return results;
    }

    /// <summary>
    /// Fills a caller-supplied list with emoji shortcode suggestions for the given prefix.
    /// Prefer this overload in hot paths (e.g., per-keystroke UI autocomplete) to avoid
    /// allocating a new list on every call.
    /// </summary>
    /// <param name="prefix">The prefix to search for (e.g., ":sm" to find ":smile:").</param>
    /// <param name="results">Caller-supplied list to populate. Not cleared before use.</param>
    /// <param name="max">Maximum number of suggestions to return. Default is 10.</param>
    public static void GetCompletions(ReadOnlySpan<char> prefix, List<(string shortcode, string emoji)> results, int max = 10) {
        var trie = LazyTrie.Value;
        if (trie == null) { return; }

        trie.GetCompletions(prefix, results, max);
    }

    #endregion
}
