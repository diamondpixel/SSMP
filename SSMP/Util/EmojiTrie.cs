using System;
using System.Collections.Generic;

namespace SSMP.Util;

/// <summary>
/// Trie for O(k) emoji shortcode prefix search, where k is the prefix length.
/// Not thread-safe for concurrent writes. Safe for concurrent reads once fully populated.
/// </summary>
internal class EmojiTrie {
    
    private readonly EmojiTrieNode _root = new();
    
    /// <summary>
    /// Inserts a shortcode-emoji pair. O(n) where n = shortcode length.
    /// All insertions must complete before concurrent reads begin.
    /// </summary>
    /// <param name="shortcode">The shortcode string to insert (e.g., ":smile:").</param>
    /// <param name="emoji">The corresponding emoji character (e.g., "😄").</param>
    /// <param name="shortcodeString">The full shortcode string allocated once for storage.</param>
    public void Insert(ReadOnlySpan<char> shortcode, string emoji, string shortcodeString) {
        var node = _root;

        foreach (var c in shortcode) {
            if (!node.Children.TryGetValue(c, out var child)) {
                child = new EmojiTrieNode();
                node.Children[c] = child;
            }

            node = child;
        }

        node.Emoji = emoji;
        node.Shortcode = shortcodeString;
    }

    /// <summary>
    /// Fills <paramref name="results"/> with up to <paramref name="maxResults"/> completions
    /// for the given prefix. Caller supplies the list to avoid per-call allocation.
    /// O(k + m) where k = prefix length, m = results collected.
    /// </summary>
    /// <param name="prefix">The shortcode prefix to search for.</param>
    /// <param name="results">The list to populate with matching shortcode-emoji pairs.</param>
    /// <param name="maxResults">The maximum number of results to return. Default is 10.</param>
    public void GetCompletions(
        ReadOnlySpan<char> prefix,
        List<(string shortcode, string emoji)> results,
        int maxResults = 10
    ) {
        var node = FindNode(prefix);
        if (node == null) {
            return;
        }

        if (node.IsEnd) {
            results.Add((node.Shortcode!, node.Emoji!));
            if (results.Count >= maxResults) {
                return;
            }
        }

        CollectCompletions(node, results, maxResults);
    }

    /// <summary>
    /// Allocating overload for call-site compatibility. Prefer the list-parameter overload
    /// in hot paths (e.g., per-keystroke autocomplete) to avoid per-call GC pressure.
    /// </summary>
    /// <param name="prefix">The shortcode prefix to search for.</param>
    /// <param name="maxResults">The maximum number of results to return. Default is 10.</param>
    /// <returns>A new list containing up to <paramref name="maxResults"/> matching shortcode-emoji pairs.</returns>
    public List<(string shortcode, string emoji)> GetCompletions(string prefix, int maxResults = 10) {
        var results = new List<(string, string)>(maxResults);
        GetCompletions(prefix.AsSpan(), results, maxResults);
        return results;
    }
    
    /// <summary>
    /// Navigates to the node at the end of <paramref name="prefix"/>.
    /// Returns null if no path exists. Shared by GetCompletions and GetExact
    /// to eliminate duplicated traversal logic.
    /// </summary>
    /// <param name="prefix">The prefix path to follow through the trie.</param>
    /// <returns>The node at the end of the prefix path, or null if not found.</returns>
    private EmojiTrieNode? FindNode(ReadOnlySpan<char> prefix) {
        var node = _root;

        foreach (var c in prefix) {
            if (!node.Children.TryGetValue(c, out var child)) {
                return null;
            }

            node = child;
        }

        return node;
    }

    /// <summary>
    /// DFS completion collector. Depth is bounded by max shortcode length (~20),
    /// so recursion is safe and preferable to an explicit stack at this scale.
    /// </summary>
    /// <param name="node">The starting node for the DFS traversal.</param>
    /// <param name="results">The list to populate with matching shortcode-emoji pairs.</param>
    /// <param name="maxResults">The maximum number of results to collect.</param>
    private static void CollectCompletions(
        EmojiTrieNode node,
        List<(string, string)> results,
        int maxResults
    ) {
        foreach (var child in node.Children.Values) {
            if (child.IsEnd) {
                results.Add((child.Shortcode!, child.Emoji!));
            }

            if (results.Count < maxResults) {
                CollectCompletions(child, results, maxResults);
            }

            if (results.Count >= maxResults) {
                return;
            }
        }
    }
}

/// <summary>
/// A Trie node. Fields are used instead of auto-properties to eliminate
/// getter/setter dispatch overhead on the hot traversal path.
/// </summary>
internal class EmojiTrieNode {
    /// <summary>
    /// Child nodes indexed by character.
    /// Initial capacity of 2 reflects that most non-root nodes have very few children,
    /// reducing per-node allocation from ~200 bytes (default Dictionary) to ~80 bytes.
    /// </summary>
    public readonly Dictionary<char, EmojiTrieNode> Children = new(2);

    /// <summary>
    /// The emoji value at this node, or null if this is not a terminal node.
    /// </summary>
    public string? Emoji;

    /// <summary>
    /// The full shortcode string at this node.
    /// Stored explicitly to avoid path reconstruction during GetCompletions.
    /// </summary>
    public string? Shortcode;

    /// <summary>
    /// True when this node marks the end of a valid shortcode.
    /// </summary>
    public bool IsEnd => Emoji != null;
}
