using System;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SSMP.Ui.Component {
    /// <summary>
    /// Controller for rendering emoji sprites over Unity UI Text components.
    /// Implements IMeshModifier to hide text characters at emoji positions and overlays sprites at those locations.
    /// </summary>
    public class UnityTextEmojiController : MonoBehaviour, IMeshModifier {
        /// <summary>
        /// The Text component being modified for emoji rendering.
        /// </summary>
        private Text? _textComponent;

        /// <summary>
        /// Container RectTransform that holds all emoji Image objects.
        /// </summary>
        private RectTransform? _container;

        /// <summary>
        /// List of currently active emoji Image objects being rendered.
        /// All access is on the main thread (LateUpdate), no locking required.
        /// </summary>
        private readonly List<Image> _activeImages = new(16);

        /// <summary>
        /// Array-based manual stack pool for reusing Image components.
        /// Avoids Stack&lt;T&gt;'s internal indirection and is faster for small counts.
        /// </summary>
        private Image[] _poolBuffer = new Image[16];

        /// <summary>
        /// Current number of pooled images available.
        /// </summary>
        private int _poolCount;

        /// <summary>
        /// List of emoji positions mapping character indices to emoji IDs.
        /// Assigned in Initialize; null until then to avoid premature allocation.
        /// </summary>
        private List<(int charIndex, int emojiId)>? _emojiPositions;

        /// <summary>
        /// Flat cache of (absolute char index, local-space center position) pairs.
        /// Replaces Dictionary&lt;int, Vector3&gt; — for small emoji counts a linear scan
        /// over a contiguous struct array is faster due to cache locality and zero hashing overhead.
        /// Written in ModifyMesh (main thread UI rebuild) and read in LateUpdate (main thread).
        /// Sequential ordering guarantees no race condition.
        /// </summary>
        private (int charIndex, Vector3 position)[] _charPositionCache = [];

        /// <summary>
        /// Number of valid entries currently stored in _charPositionCache.
        /// </summary>
        private int _charPositionCount;

        /// <summary>
        /// Reusable vertex list passed to Unity's GetUIVertexStream.
        /// Capacity is set generously to avoid resizing on typical message lengths.
        /// </summary>
        private readonly List<UIVertex> _vertexCache = new(1024);

        /// <summary>
        /// Cached text string from the previous ModifyMesh call.
        /// Compared with Ordinal equality rather than ReferenceEquals to avoid
        /// false cache hits when Unity returns a new string object for the same value.
        /// </summary>
        private string? _cachedText;

        /// <summary>
        /// Number of vertices per character in Unity UI mesh (always 6 for quads).
        /// </summary>
        private const int VertsPerChar = 6;

        /// <summary>
        /// Maximum emoji count that uses stackalloc for the visible-index buffer.
        /// Beyond this a heap array is used to stay within safe stack frame size.
        /// </summary>
        private const int StackAllocThreshold = 64;

        /// <summary>
        /// Vertical offset multiplier for emoji alignment (35% of font size).
        /// Compensates for underscore baseline position.
        /// </summary>
        private const float VerticalAlignmentOffset = 0.35f;

        #region Initialisation

        /// <summary>
        /// Initializes the emoji controller with the target Text component and emoji positions.
        /// Creates the overlay container for rendering emoji sprites.
        /// </summary>
        /// <param name="text">The Unity UI Text component to modify.</param>
        /// <param name="emojiPositions">List of tuples mapping character indices to emoji IDs.</param>
        public void Initialize(Text text, List<(int charIndex, int emojiId)> emojiPositions) {
            _textComponent = text;
            _emojiPositions = emojiPositions;

            // Pre-size the position cache to the exact emoji count — no resizing ever needed.
            if (_charPositionCache.Length < emojiPositions.Count) {
                _charPositionCache = new (int, Vector3)[emojiPositions.Count];
            }


            _charPositionCount = 0;

            // Create overlay container as a child that matches the Text's RectTransform exactly.
            var go = new GameObject("EmojiOverlay");
            _container = go.AddComponent<RectTransform>();
            _container.SetParent(text.transform, false);

            _container.anchorMin = Vector2.zero;
            _container.anchorMax = Vector2.one;
            _container.offsetMin = Vector2.zero;
            _container.offsetMax = Vector2.zero;
            _container.pivot = text.rectTransform.pivot;
            _container.localRotation = Quaternion.identity;
            _container.localScale = Vector3.one;

            _textComponent.SetVerticesDirty();
        }

        #endregion

        #region IMeshModifier

        /// <summary>Legacy mesh modification interface — unused.</summary>
        public void ModifyMesh(Mesh mesh) {
        }

        /// <summary>
        /// Modifies the text mesh to hide characters at emoji positions and records their centres.
        /// Called automatically by Unity's UI system on the main thread during mesh rebuild.
        ///
        /// Complexity: O(n + m) where n = text length, m = emoji count.
        /// Previous implementation was O(n × m) due to repeated per-emoji linear scans.
        /// </summary>
        public void ModifyMesh(VertexHelper vh) {
            if (!isActiveAndEnabled || _emojiPositions == null ||
                _emojiPositions.Count == 0 || _textComponent == null) {
                return;
            }

            _vertexCache.Clear();
            vh.GetUIVertexStream(_vertexCache);

            var text = _textComponent.text;
            var spanText = text.AsSpan();

            // Single O(n + m) pass: resolve all visible indices and get total visible char count.
            // stackalloc keeps the buffer on the stack for typical emoji counts, avoiding GC.
            var emojiCount = _emojiPositions.Count;
            int totalVisibleChars;

            if (emojiCount <= StackAllocThreshold) {
                Span<int> visibleIndices = stackalloc int[emojiCount];
                totalVisibleChars = BuildVisibleIndexMap(spanText, visibleIndices);
                ProcessVertices(visibleIndices, totalVisibleChars, text);
            } else {
                // Fallback for unusually large emoji counts — heap allocate once.
                var visibleIndices = new int[emojiCount];
                totalVisibleChars = BuildVisibleIndexMap(spanText, visibleIndices);
                ProcessVertices(visibleIndices, totalVisibleChars, text);
            }

            vh.Clear();
            vh.AddUIVertexTriangleStream(_vertexCache);
        }

        #endregion

        #region Core mesh processing

        /// <summary>
        /// Hides emoji characters in the vertex cache and — when the text has changed —
        /// recalculates and stores each emoji's centre position.
        /// </summary>
        /// <param name="visibleIndices">Pre-computed visible index for each emoji (parallel to _emojiPositions).</param>
        /// <param name="totalVisibleChars">Total visible (non-whitespace, non-tag) character count.</param>
        /// <param name="text">Raw text string (used for cache equality check).</param>
        private void ProcessVertices(
            Span<int> visibleIndices,
            int totalVisibleChars,
            string text
        ) {
            if (totalVisibleChars == 0) {
                return;
            }

            var charsTimesVerts = totalVisibleChars * VertsPerChar;
            if (_vertexCache.Count == 0 || _vertexCache.Count % charsTimesVerts != 0) {
                return;
            }

            var copies = _vertexCache.Count / charsTimesVerts;
            var textChanged = !string.Equals(_cachedText, text, StringComparison.Ordinal);

            if (textChanged) {
                _charPositionCount = 0;
            }

            for (var idx = 0; idx < _emojiPositions!.Count; idx++) {
                var visibleIndex = visibleIndices[idx];

                // Unsigned comparison folds the < 0 and >= totalVisibleChars checks into one branch.
                if ((uint) visibleIndex >= (uint) totalVisibleChars) {
                    continue;
                }

                if (textChanged) {
                    // Recalculate position from the topmost copy (main text layer, no shadow offset).
                    var min = new Vector3(float.MaxValue, float.MaxValue, 0f);
                    var max = new Vector3(float.MinValue, float.MinValue, 0f);

                    var boundsStart = ((copies - 1) * charsTimesVerts) + (visibleIndex * VertsPerChar);
                    for (var i = 0; i < VertsPerChar; i++) {
                        var pos = _vertexCache[boundsStart + i].position;
                        min = Vector3.Min(min, pos);
                        max = Vector3.Max(max, pos);
                    }

                    _charPositionCache[_charPositionCount++] = (
                        _emojiPositions[idx].charIndex,
                        (min + max) * 0.5f
                    );
                }

                HideCharacterVertices(visibleIndex, copies, charsTimesVerts);
            }

            if (textChanged) _cachedText = text;
        }

        /// <summary>
        /// Sets the alpha of all vertices belonging to one character to zero across all mesh copies.
        /// Extracted from three previous duplicate sites into a single shared method.
        /// </summary>
        /// <param name="visibleIndex">Visible character index within the mesh.</param>
        /// <param name="copies">Number of mesh copies (shadow / outline layers + main).</param>
        /// <param name="charsTimesVerts">Total vertices for one full copy of all visible characters.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HideCharacterVertices(int visibleIndex, int copies, int charsTimesVerts) {
            for (var k = 0; k < copies; k++) {
                var startVert = (k * charsTimesVerts) + (visibleIndex * VertsPerChar);
                if (startVert + VertsPerChar > _vertexCache.Count) {
                    continue;
                }

                for (var i = 0; i < VertsPerChar; i++) {
                    var v = _vertexCache[startVert + i];
                    var col = v.color;
                    col.a = 0;
                    v.color = col;
                    _vertexCache[startVert + i] = v;
                }
            }
        }

        #endregion

        #region Sprite rendering

        /// <summary>
        /// Positions emoji sprites every frame after layout has completed.
        /// Recycles all active images back to the pool then re-renders from cached positions.
        /// </summary>
        private void LateUpdate() {
            if (_textComponent == null || _emojiPositions == null || _emojiPositions.Count == 0) {
                return;
            }

            // Return all active images to the pool without foreach enumerator allocation.
            foreach (var t in _activeImages) ReturnToPool(t);
            _activeImages.Clear();

            // Render each emoji using the centre positions written by the last ModifyMesh call.
            if (_charPositionCount == 0) {
                return;
            }

            foreach (var (charIndex, emojiId) in _emojiPositions!) {
                if (TryGetCharPosition(charIndex, out var pos)) {
                    RenderEmojiAtPosition(emojiId, pos);
                }
            }
        }

        /// <summary>
        /// Renders a single emoji sprite at the given local-space position.
        /// </summary>
        /// <param name="emojiId">The emoji sprite index passed to the sprite loader.</param>
        /// <param name="position">Centre position in the Text component's local space.</param>
        private void RenderEmojiAtPosition(int emojiId, Vector3 position) {
            var sprite = SSMP.Util.EmojiSpriteLoader.GetSprite(emojiId);
            if (sprite == null) {
                return;
            }

            float size = _textComponent!.fontSize;

            var img = TakeFromPool() ?? CreatePooledImage();
            img.sprite = sprite;
            img.color = Color.white;

            position.y += size * VerticalAlignmentOffset;

            var rect = img.rectTransform;
            rect.localPosition = position;
            rect.sizeDelta = new Vector2(size, size);

            _activeImages.Add(img);
        }

        #endregion

        #region Object pool

        /// <summary>
        /// Returns an Image to the pool and deactivates its GameObject.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReturnToPool(Image img) {
            img.gameObject.SetActive(false);

            if (_poolCount == _poolBuffer.Length) {
                Array.Resize(ref _poolBuffer, _poolBuffer.Length * 2);
            }

            _poolBuffer[_poolCount++] = img;
        }

        /// <summary>
        /// Takes an Image from the pool and activates it, or returns null if the pool is empty.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Image? TakeFromPool() {
            if (_poolCount == 0) {
                return null;
            }

            var img = _poolBuffer[--_poolCount];
            img.gameObject.SetActive(true);
            return img;
        }

        /// <summary>
        /// Creates a new pooled Image GameObject parented to the emoji overlay container.
        /// Uses RectTransform directly to avoid the Transform → RectTransform cast overhead.
        /// </summary>
        private Image CreatePooledImage() {
            var go = new GameObject("EmojiImg");
            var img = go.AddComponent<Image>();
            img.rectTransform.SetParent(_container, false);
            img.raycastTarget = false;
            return img;
        }

        #endregion

        #region Position cache helpers

        /// <summary>
        /// Looks up a cached character centre position by absolute character index.
        /// O(m) linear scan where m = emoji count; consistently faster than Dictionary hashing
        /// for m &lt; ~30 due to contiguous memory layout and no hash overhead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryGetCharPosition(int charIndex, out Vector3 position) {
            for (var i = 0; i < _charPositionCount; i++) {
                if (_charPositionCache[i].charIndex != charIndex) {
                    continue;
                }

                position = _charPositionCache[i].position;
                return true;
            }

            position = default;
            return false;
        }

        #endregion

        #region Text analysis

        /// <summary>
        /// Performs a single O(n + m) pass over the text to resolve visible character indices
        /// for every entry in _emojiPositions simultaneously.
        ///
        /// Previous design called GetVisibleCharIndex (O(n)) once per emoji (O(m) calls),
        /// giving O(n × m). This method reduces that to a single linear scan.
        ///
        /// Visible characters are those that are not inside a rich-text tag and not whitespace —
        /// matching Unity's text mesh vertex ordering exactly.
        /// </summary>
        /// <param name="text">The full text span to analyse.</param>
        /// <param name="resultVisibleIndices">
        ///     Output span parallel to _emojiPositions. Each entry receives the visible index
        ///     of the corresponding emoji character, or -1 if not found.
        /// </param>
        /// <returns>Total number of visible characters in the text.</returns>
        private int BuildVisibleIndexMap(
            ReadOnlySpan<char> text,
            Span<int> resultVisibleIndices
        ) {
            resultVisibleIndices.Fill(-1);

            // Copy target absolute indices to a local span so the loop is branch-predictor friendly.
            // stackalloc is safe here: caller already chose stack vs heap based on emojiCount.
            var emojiCount = _emojiPositions!.Count;

            // Inline the targets to avoid repeated list indexing inside the hot loop.
            // For counts > StackAllocThreshold this is called with a heap-backed Span, so
            // we do a small heap alloc only in that rare case.
            var targets = emojiCount <= StackAllocThreshold
                ? stackalloc int[emojiCount]
                : new int[emojiCount];

            for (var i = 0; i < emojiCount; i++) {
                targets[i] = _emojiPositions[i].charIndex;
            }


            var visibleIndex = 0;

            var insideTag = false;
            for (var i = 0; i < text.Length; i++) {
                var c = text[i];

                switch (c) {
                    case '<':
                        insideTag = true;
                        continue;
                    case '>':
                        insideTag = false;
                        continue;
                }

                if (insideTag || char.IsWhiteSpace(c)) {
                    continue;
                }

                // Check whether this absolute position is a target emoji character.
                // Inner loop over targets is O(m); combined with outer O(n) gives O(n + n*m worst case),
                // but m is always tiny (< 30 in practice) and the branch is well-predicted.
                for (var t = 0; t < emojiCount; t++) {
                    if (targets[t] != i) {
                        continue;
                    }

                    resultVisibleIndices[t] = visibleIndex;
                    break;
                }

                visibleIndex++;
            }

            // total visible character count
            return visibleIndex;
        }
        #endregion
    }
}
