using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SSMP.Ui.Component;

/// <inheritdoc cref="ITextComponent" />
internal class TextComponent : Component, ITextComponent {
    /// <summary>
    /// The Unity Text component.
    /// </summary>
    private readonly Text _textObject;

    public TextComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        string text,
        int fontSize,
        FontStyle fontStyle = FontStyle.Normal,
        TextAnchor alignment = TextAnchor.MiddleCenter,
        bool useEmojiFont = false,
        bool useSystemFont = false,
        List<(int charIndex, int emojiId)>? emojiPositions = null
    ) : this(componentGroup, position, size, new Vector2(0.5f, 0.5f), text, fontSize, fontStyle, alignment, useEmojiFont, useSystemFont, emojiPositions) {
    }

    public TextComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        Vector2 pivot,
        string text,
        int fontSize,
        FontStyle fontStyle = FontStyle.Normal,
        TextAnchor alignment = TextAnchor.MiddleCenter,
        bool useEmojiFont = false,
        bool useSystemFont = false,
        List<(int charIndex, int emojiId)>? emojiPositions = null
    ) : base(componentGroup, position, size) {
        _textObject = CreateTextObject(text, fontSize, fontStyle, alignment, pivot, useEmojiFont, useSystemFont);
        AddSizeFitter();
        AddOutline();
        
        // Attach emoji overlay controller if positions are provided
        if (emojiPositions != null && emojiPositions.Count > 0) {
            var controller = GameObject.AddComponent<UnityTextEmojiController>();
            controller.Initialize(_textObject, emojiPositions);
        }
    }

    /// <inheritdoc />
    public void SetText(string text) {
        _textObject.text = text;
    }

    /// <inheritdoc />
    public void SetColor(Color color) {
        _textObject.color = color;
    }

    /// <summary>
    /// Get the current color of the text.
    /// </summary>
    /// <returns>The color of the text.</returns>
    public Color GetColor() {
        return _textObject.color;
    }
    
    /// <summary>
    /// Gets the preferred width required to render the component's text without wrapping.
    /// </summary>
    /// <returns>The preferred width in pixels.</returns>
    /// <remarks>
    /// <para>
    /// <strong>NOTE:</strong> This method is currently unused by the ChatBox implementation.
    /// ChatBox performs dynamic text measurement on arbitrary strings during its wrapping algorithm,
    /// which requires direct use of <see cref="TextGenerator"/> with custom settings rather than
    /// querying an existing component's preferred width.
    /// </para>
    /// <para>
    /// This method is retained for potential future use cases where measuring the preferred width
    /// of an already-instantiated text component may be needed, such as:
    /// <list type="bullet">
    /// <item><description>Dynamic UI layout systems</description></item>
    /// <item><description>Tooltip sizing</description></item>
    /// <item><description>Button auto-sizing based on text content</description></item>
    /// </list>
    /// </para>
    /// </remarks>

    /// <summary>
    /// Create the Unity Text object with all the parameters.
    /// </summary>
    /// <returns>The Unity <see cref="Text"/> object.</returns>
    private Text CreateTextObject(string text, int fontSize, FontStyle fontStyle, TextAnchor alignment, Vector2 pivot, bool useEmojiFont, bool useSystemFont) {
        var textObj = GameObject.AddComponent<Text>();
        
        textObj.supportRichText = true;
        textObj.text = text;
        
        // Use emoji font if requested and available, otherwise fallback to UI font
        if (useSystemFont) {
            // Force system font (lazy loaded). If unavailable, null defaults to built-in Arial.
            textObj.font = Resources.FontManager.SystemFont;
        } else if (useEmojiFont && Resources.FontManager.EmojiFont != null) {
            textObj.font = Resources.FontManager.EmojiFont;
        } else {
            // Default to Game Font (Perpetua)
            textObj.font = Resources.FontManager.UIFontRegular;
        }
        
        textObj.fontSize = fontSize;
        textObj.fontStyle = fontStyle;
        textObj.alignment = alignment;
        textObj.horizontalOverflow = HorizontalWrapMode.Overflow;
        textObj.verticalOverflow = VerticalWrapMode.Overflow;
        textObj.rectTransform.pivot = pivot;
        textObj.raycastTarget = false;
        
        return textObj;
    }

    /// <summary>
    /// Add a size fitter to the game object for this text.
    /// </summary>
    private void AddSizeFitter() {
        var sizeFitter = GameObject.AddComponent<ContentSizeFitter>();
        sizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    /// <summary>
    /// Add an outline to the text.
    /// </summary>
    private void AddOutline() {
        var outline = GameObject.AddComponent<Outline>();
        outline.effectColor = Color.black;
    }
}
