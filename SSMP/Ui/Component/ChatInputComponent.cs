using System;
using System.Collections.Generic;
using SSMP.Networking.Packet.Data;
using SSMP.Ui.Resources;
using SSMP.Util;
using UnityEngine;
using UnityEngine.UI;

namespace SSMP.Ui.Component;

/// <summary>
/// An input component specifically for the chat.
/// </summary>
internal class ChatInputComponent : InputComponent {
    /// <summary>
    /// List of characters that are disallowed to be input.
    /// </summary>
    private static readonly List<char> DisallowedChars = ['\n'];
    
    /// <summary>
    /// Reference to the placeholder Text component.
    /// </summary>
    private readonly Text _placeholderText;
    
    /// <summary>
    /// Action that is executed when the user submits the input field.
    /// </summary>
    public event Action<string>? OnSubmit;

    public ChatInputComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        int fontSize
    ) : base(
        componentGroup,
        position,
        size,
        "",
        "",
        TextureManager.InputFieldBg,
        // Use System Font (Arial/etc) for input to avoid missing characters, fallback to Perpetua
        Resources.FontManager.SystemFont ?? Resources.FontManager.UIFontRegular,
        fontSize
    ) {
        Text.alignment = TextAnchor.MiddleLeft;
        
        // Store reference to placeholder text component
        _placeholderText = InputField.placeholder as Text ?? throw new InvalidOperationException("Placeholder is not a Text component");

        InputField.characterLimit = ChatMessage.MaxMessageLength;

        InputField.onValidateInput += (_, _, addedChar) => {
            if (DisallowedChars.Contains(addedChar)) {
                return '\0';
            }

            return addedChar;
        };

       
        InputField.onSubmit.AddListener(text => {
            OnSubmit?.Invoke(text);
            InputField.text = "";
            // Keep focus after submitting
            InputField.ActivateInputField(); 
        });
    }

    /// <summary>
    /// Focus the input field.
    /// </summary>
    public void Focus() {
        InputField.ActivateInputField();
    }

    /// <summary>
    /// Gets the current text in the input field.
    /// </summary>
    /// <returns>The current input text.</returns>
    public string GetText() {
        return InputField.text;
    }

    /// <summary>
    /// Sets the text in the input field.
    /// </summary>
    /// <param name="text">The text to set.</param>
    public void SetText(string text) {
        InputField.text = text;
    }

    /// <summary>
    /// Sets the placeholder text for the input field.
    /// </summary>
    /// <param name="placeholder">The placeholder text to display.</param>
    public void SetPlaceholder(string placeholder) {
        _placeholderText.text = placeholder;
    }

    /// <summary>
    /// Gets the current caret position.
    /// </summary>
    public int GetCaretPosition() {
        return InputField.caretPosition;
    }

    /// <summary>
    /// Checks if the input field is empty.
    /// </summary>
    public bool IsEmpty() {
        return string.IsNullOrEmpty(InputField.text);
    }

    /// <summary>
    /// Moves the caret to the end of the current text.
    /// </summary>
    public void MoveTextEnd() {
        InputField.MoveTextEnd(false);
    }
}
