using UnityEngine;
using UnityEngine.UI;
using TMProOld;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SSMP.Util;

namespace SSMP.Ui.Component {
    /// <summary>
    /// Controller component that renders emoji images as overlays on TextMeshPro text.
    /// Bypasses TMP's native sprite rendering which has compatibility issues in this version.
    /// Uses Unity UI Images positioned over link-tagged text regions to display emoji sprites.
    /// Implements object pooling for efficient image reuse across frames.
    /// </summary>
    public class EmojiOverlayController : MonoBehaviour {
        #region Fields

        /// <summary>
        /// The TextMeshPro UGUI component being monitored for emoji links.
        /// </summary>
        private TextMeshProUGUI? _textComponent;

        /// <summary>
        /// Container RectTransform that holds all emoji Image objects.
        /// Configured to fill the parent text component's rect for proper positioning.
        /// </summary>
        private RectTransform? _container;

        /// <summary>
        /// List of currently active emoji Image objects being rendered this frame.
        /// Pre-sized to a realistic capacity to avoid early resizing.
        /// </summary>
        private readonly List<Image> _activeImages = new(16);

        /// <summary>
        /// Array-based manual stack pool for reusing Image components.
        /// Avoids Stack&lt;T&gt;'s internal indirection and is faster for small counts.
        /// Auto-grows via Array.Resize if the pool overflows.
        /// </summary>
        private Image[] _poolBuffer = new Image[16];

        /// <summary>
        /// Number of valid (pooled) entries currently in _poolBuffer.
        /// </summary>
        private int _poolCount;

        /// <summary>
        /// Maximum number of images retained in the pool.
        /// Excess images are discarded rather than growing the buffer unboundedly.
        /// </summary>
        private const int PoolMaxSize = 64;

        #endregion

        #region Initialisation

        /// <summary>
        /// Initializes the emoji overlay controller with the target TextMeshPro component.
        /// Creates a container RectTransform for holding emoji Image overlays.
        /// Images are configured with raycastTarget=false to avoid blocking text interactions.
        /// </summary>
        /// <param name="text">The TextMeshPro UGUI component to overlay emojis on.</param>
        public void Initialize(TextMeshProUGUI text) {
            _textComponent = text;

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
        }

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Updates emoji overlays every frame after layout is complete.
        /// Recycles inactive images back to the pool and renders active emojis based on TMP link tags.
        /// Runs in LateUpdate to ensure TextMeshPro has completed its layout pass.
        /// </summary>
        private void LateUpdate() {
            // Guard both fields — _container may be null if Initialize was never called.
            if (_textComponent == null || _container == null) {
                return;
            }

            // Return all active images to the pool. List<T>.Enumerator is a struct — no heap alloc.
            foreach (var t in _activeImages) {
                ReturnToPool(t);
            }

            _activeImages.Clear();

            var textInfo = _textComponent.textInfo;
            if (textInfo == null) {
                return;
            }

            // Cache fontSize once — avoids repeated property access inside the loop.
            var fontSize = _textComponent.fontSize;

            var linkCount = textInfo.linkCount;
            for (var i = 0; i < linkCount; i++) {
                var link = textInfo.linkInfo[i];

                // TODO: Replace GetLinkID() with a zero-alloc span parse once TMProOld
                // exposes ReadOnlySpan<char> over its internal source buffer.
                // Until then, assign once per link to avoid multiple allocating calls.
                var id = link.GetLinkID();
                if (string.IsNullOrEmpty(id)) {
                    continue;
                }

                if (int.TryParse(id, out var spriteIndex)) {
                    RenderEmoji(spriteIndex, link, textInfo, fontSize);
                }
            }
        }

        #endregion

        #region Rendering

        /// <summary>
        /// Renders an emoji sprite at the position of a link in the text.
        /// Calculates the bounding box of the link's characters and positions an Image overlay.
        /// TMP_LinkInfo is passed by value: it has non-readonly members so using `in` would
        /// produce silent defensive copies on every field/method access.
        /// </summary>
        /// <param name="spriteIndex">The emoji sprite index to render.</param>
        /// <param name="link">The TMP link info containing character position data.</param>
        /// <param name="textInfo">Pre-fetched TMP_TextInfo to avoid repeated property access.</param>
        /// <param name="fontSize"> The emoji font size in pixels.</param>
        private void RenderEmoji(int spriteIndex, TMP_LinkInfo link, TMP_TextInfo textInfo, float fontSize) {
            var sprite = EmojiSpriteLoader.GetSprite(spriteIndex);
            // Sprite not yet loaded — silently skip.
            if (sprite == null) {
                return;
            }

            if (!TryCalculateBounds(link, textInfo, out var center)) {
                return;
            }

            var img = TakeFromPool() ?? CreatePooledImage();
            // Cache: avoid repeated property lookup below.
            var rect = img.rectTransform;

            img.sprite = sprite;
            rect.localPosition = center;
            rect.sizeDelta = new Vector2(fontSize, fontSize);

            _activeImages.Add(img);
        }

        /// <summary>
        /// Calculates the bounding box center of all characters within a link.
        /// Uses TMP character geometry directly; characters may be transparent but still have geometry.
        /// TMP_LinkInfo is passed by value: its members are not marked readonly so `in` would
        /// silently copy the struct on access. TMP_CharacterInfo array elements are accessed
        /// via ref readonly below because only plain fields (bottomLeft, topRight) are read.
        /// </summary>
        /// <param name="link">The TMP link info defining the character range.</param>
        /// <param name="textInfo">Pre-fetched TMP_TextInfo to avoid repeated property access.</param>
        /// <param name="center">The calculated centre position in local space, if found.</param>
        /// <returns>True if at least one valid character with geometry was found.</returns>
        private static bool TryCalculateBounds(
            TMP_LinkInfo link,
            TMP_TextInfo textInfo,
            out Vector3 center
        ) {
            // Cache array reference — avoids repeated field access.
            var charInfos = textInfo.characterInfo;
            var start = link.linkTextfirstCharacterIndex;
            // Exclusive upper bound.
            var end = start + link.linkTextLength;
            var limit = charInfos.Length;

            // Use scalar floats instead of Vector3.Min/Max to avoid constructing
            // temporary Vector3 values and performing the unused z-comparison.
            var minX = float.MaxValue; var minY = float.MaxValue;
            var maxX = float.MinValue; var maxY = float.MinValue;
            var hasVisibleChar = false;

            for (var c = start; c < end; c++) {
                if (c >= limit) {
                    break;
                }

                ref readonly var charInfo = ref charInfos[c];

                // Skip degenerate characters (zero-width geometry).
                if (charInfo.bottomLeft.x == 0f && charInfo.topRight.x == 0f) {
                    continue;
                }

                if (charInfo.bottomLeft.x < minX) minX = charInfo.bottomLeft.x;
                if (charInfo.bottomLeft.y < minY) minY = charInfo.bottomLeft.y;
                if (charInfo.topRight.x  > maxX) maxX = charInfo.topRight.x;
                if (charInfo.topRight.y  > maxY) maxY = charInfo.topRight.y;
                hasVisibleChar = true;
            }

            if (!hasVisibleChar) {
                center = Vector3.zero;
                return false;
            }

            center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f);
            return true;
        }

        #endregion

        #region Object Pool

        /// <summary>
        /// Returns an Image to the pool and deactivates its GameObject.
        /// Auto-grows the pool buffer via Array.Resize if capacity is exceeded.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReturnToPool(Image img) {
            img.gameObject.SetActive(false);

            // Discard images beyond the cap rather than growing the buffer unboundedly.
            if (_poolCount >= PoolMaxSize) {
                return;
            }

            if (_poolCount == _poolBuffer.Length) {
                Array.Resize(ref _poolBuffer, System.Math.Min(_poolBuffer.Length * 2, PoolMaxSize));
            }

            _poolBuffer[_poolCount++] = img;
        }

        /// <summary>
        /// Pops and activates an Image from the pool, or returns null when the pool is empty.
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
        /// Creates a new Image GameObject parented to the emoji overlay container.
        /// Uses AddComponent's returned Image to access RectTransform directly,
        /// avoiding the Transform → RectTransform cast that go.transform would incur.
        /// </summary>
        private Image CreatePooledImage() {
            var go = new GameObject("EmojiImg");
            var img = go.AddComponent<Image>();
            img.rectTransform.SetParent(_container, false);
            img.raycastTarget = false;
            // Set once at creation; never changes, so setting it every frame in
            // RenderEmoji would unnecessarily dirty the Canvas graphic.
            img.color = Color.white;
            return img;
        }

        /// <summary>
        /// Clears all pool references so Unity can GC the Image components
        /// after this GameObject is destroyed.
        /// </summary>
        private void OnDestroy() {
            _activeImages.Clear();
            Array.Clear(_poolBuffer, 0, _poolCount);
            _poolCount = 0;
        }

        #endregion
    }
}
