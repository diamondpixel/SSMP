using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GlobalEnums;
using SSMP.Api.Client;
using SSMP.Game.Settings;
using SSMP.Ui.Component;
using SSMP.Util;
using UnityEngine;
using UnityEngine.UI;
using Logger = SSMP.Logging.Logger;
using Object = UnityEngine.Object;

namespace SSMP.Ui.Chat;

/// <summary>
/// The message box in the bottom left of the screen that shows information related to SSMP.
/// </summary>
internal class ChatBox : IChatBox {
    /// <summary>
    /// The maximum number of messages being tracked in the chat box.
    /// </summary>
    private const int MaxMessages = 100;

    /// <summary>
    /// The maximum number of messages shown when the chat box is closed.
    /// </summary>
    private const int MaxShownMessages = 10;

    /// <summary>
    /// The maximum number of messages shown when the chat box is opened.
    /// </summary>
    private const int MaxShownMessagesWhenOpen = 20;

    /// <summary>
    /// The width of the channel input component.
    /// </summary>
    private const float ChannelWidth = 110f;

    /// <summary>
    /// The spacing between the channel input and the chat input components.
    /// </summary>
    private const float InputSpacing = -20f;

    /// <summary>
    /// The width of the chat input and chat box component.
    /// </summary>
    private const float ChatWidth = 700f;

    /// <summary>
    /// The height of a single message component.
    /// </summary>
    private const float MessageHeight = 25f;

    /// <summary>
    /// The margin of the chat box with the bottom of the screen.
    /// </summary>
    private const float BoxInputMargin = 30f;

    /// <summary>
    /// The height of the chat input component.
    /// </summary>
    private const float InputHeight = 30f;

    /// <summary>
    /// The margin of the chat input component with the bottom of the screen.
    /// </summary>
    private const float InputMarginBottom = 20f;

    /// <summary>
    /// The margin of the chat with the left side of the screen.
    /// </summary>
    private const float MarginLeft = 25f;

    /// <summary>
    /// The margin of a chat message within the chat.
    /// </summary>
    private const float TextMargin = 10f;

    /// <summary>
    /// The maximum number of passes to wrap a single text message.
    /// </summary>
    private const int MaxWrapPasses = 200;

    /// <summary>
    /// A vector for the size of new chat messages.
    /// </summary>
    public static Vector2 MessageSize { get; private set; }

    /// <summary>
    /// Text generation settings used to figure out the width of to-be created text.
    /// </summary>
    private static TextGenerationSettings _textGenSettings;

    /// <summary>
    /// The component group of this chat box and all messages in it.
    /// </summary>
    private readonly ComponentGroup _chatBoxGroup;

    /// <summary>
    /// Text generator used to figure out the width of to-be created text.
    /// </summary>
    private readonly TextGenerator _textGenerator;

    /// <summary>
    /// Array containing all the messages.
    /// </summary>
    private readonly ChatMessage?[] _messages;

    /// <summary>
    /// The chat input component for the channel (All/Whisper).
    /// </summary>
    private readonly ChatInputComponent _channelInput;

    /// <summary>
    /// The chat input component for the message content.
    /// </summary>
    private readonly ChatInputComponent _chatInput;

    /// <inheritdoc />
    public bool IsOpen { get; private set; }

    /// <summary>
    /// The current scroll offset based on how much the user has scrolled the chat when opened.
    /// </summary>
    private int _scrollOffset;

    /// <summary>
    /// Whether private chat mode is active.
    /// </summary>
    private bool _isPrivateMode;

    /// <summary>
    /// The ID of the player we are privately messaging.
    /// </summary>
    private ushort? _privateTargetId;

    /// <summary>
    /// The ID of the last player we sent a private message to.
    /// </summary>
    private ushort? _lastPrivateTargetId;

    /// <summary>
    /// Event that is called when the user submits a message in the chat input.
    /// </summary>
    /// <remarks>Parameters: message, targetId (null for public)</remarks>
    /// <summary>
    /// Event that is called when the user submits a message in the chat input.
    /// </summary>
    /// <remarks>Parameters: message, targetId (null for public)</remarks>
    public event Action<string, ushort?>? ChatInputEvent;

    // Autocomplete State
    
    /// <summary>
    /// The current list of autocomplete emoji candidates that match the user's input.
    /// </summary>
    private List<string> _emojiCandidates = [];
    
    /// <summary>
    /// The index of the currently selected emoji candidate.
    /// </summary>
    private int _emojiCandidateIndex;
    
    /// <summary>
    /// The cached chat input text prior to the start of the emoji shortcode being typed.
    /// </summary>
    private string _textBeforeEmoji = "";
    
    /// <summary>
    /// Whether the user is currently cycling through emoji autocomplete suggestions.
    /// </summary>
    private bool _isEmojiCompleting;
    
    /// <summary>
    /// Flag to ignore the next OnValueChanged event to prevent infinite update loops during autocomplete.
    /// </summary>
    private bool _ignoreNextInputChange;
    
    /// <summary>
    /// Tracks if the chat input was empty during the previous frame to require dedicated backspace presses to shift focus out of whisper mode.
    /// </summary>
    private bool _wasEmptyLastFrame;

    /// <summary>
    /// Construct the chat box in the given group and with the given mod settings.
    /// </summary>
    /// <param name="chatBoxGroup">The component group it should be in.</param>
    /// <param name="modSettings">The current mod settings.</param>
    public ChatBox(ComponentGroup chatBoxGroup, ModSettings modSettings) {
        _chatBoxGroup = chatBoxGroup;
        _textGenerator = new TextGenerator();
        _messages = new ChatMessage[MaxMessages];

        // Create the chat input background/interaction layer FIRST so it sits behind the channel input layer
        var contentWidth = ChatWidth - ChannelWidth - InputSpacing;
        _chatInput = CreateChatInput(chatBoxGroup, contentWidth, ChannelWidth + InputSpacing + contentWidth / 2f + MarginLeft);
        _chatInput.SetOnChange(OnChatInputChange);
        _chatInput.OnSubmit += OnChatSubmit;

        _channelInput = CreateChatInput(chatBoxGroup, ChannelWidth, ChannelWidth / 2f + MarginLeft);
        _channelInput.SetOnChange(OnChannelInputChange);
        _channelInput.OnSubmit += _ => _chatInput.Focus();

        InitializeTextSettings();

        MonoBehaviourUtil.Instance.OnUpdateEvent += () => CheckKeyBinds(modSettings);
    }

    /// <summary>
    /// Callback method for when the chat input changes.
    /// </summary>
    /// <param name="val">The new input value.</param>
    private void OnChatInputChange(string val) {
        if (_ignoreNextInputChange) return;
        _isEmojiCompleting = false;
        UpdateChatModeDisplay();
    }

    /// <summary>
    /// Callback method for when the channel input changes.
    /// </summary>
    /// <param name="val">The new input value.</param>
    private void OnChannelInputChange(string val) {
        if (_ignoreNextInputChange) return;

        if (string.IsNullOrEmpty(val)) {
            if (_isPrivateMode) {
                // Keep it in private mode, but clear the target
                _privateTargetId = null;
                UpdateChatModeDisplay();
                _channelInput.Focus();
            }
        } else if (_isPrivateMode && _privateTargetId.HasValue) {
            _privateTargetId = null;
            UpdateChatModeDisplay();
        }
    }

    /// <summary>
    /// Create a chat input component for the chat box in the given component group.
    /// </summary>
    private ChatInputComponent CreateChatInput(ComponentGroup chatBoxGroup, float width, float posX) {
        var input = new ChatInputComponent(
            chatBoxGroup,
            new Vector2(posX, InputMarginBottom + InputHeight / 2f),
            new Vector2(width, InputHeight),
            UiManager.ChatFontSize
        );
        input.SetActive(false);
        return input;
    }

    /// <summary>
    /// Callback method for when the user inputs a message into the chat.
    /// </summary>
    /// <param name="chatInput">The string message that was input.</param>
    private void OnChatSubmit(string chatInput) {
        if (chatInput.Length > 0) {
            ushort? finalTargetId = null;

            if (_isPrivateMode) {
                if (_privateTargetId.HasValue) {
                    finalTargetId = _privateTargetId;
                } else {
                    // They typed a name but didn't hit Tab to lock it, try to resolve it now
                    var inputText = _channelInput.GetText();
                    if (!string.IsNullOrEmpty(inputText) && inputText != "All" && inputText != "Whisper") {
                        var clientManager = Game.GameManager.Instance.ClientManager;
                        var match = clientManager.Players.FirstOrDefault(p =>
                            p.Username.StartsWith(inputText, StringComparison.OrdinalIgnoreCase));
                        
                        if (match != null) {
                            finalTargetId = match.Id;
                            _privateTargetId = finalTargetId; // Lock it in for the history
                        }
                    }
                }
            }

            ChatInputEvent?.Invoke(chatInput, finalTargetId);

            // Remember last private target
            if (_isPrivateMode && finalTargetId.HasValue) {
                _lastPrivateTargetId = finalTargetId;
            }
        }

        HideChatInput();
    }

    /// <summary>
    /// Initialize the text settings so we can more easily create new chat messages on the fly.
    /// </summary>
    private static void InitializeTextSettings() {
        MessageSize = new Vector2(ChatWidth, MessageHeight);
        _textGenSettings = new TextGenerationSettings {
            // Use SystemFont for wrapping measurements so they match the actual display font (Arial)
            font = Resources.FontManager.SystemFont ?? Resources.FontManager.UIFontRegular,
            color = Color.white,
            fontSize = UiManager.ChatFontSize,
            lineSpacing = 1,
            richText = true,
            scaleFactor = 1,
            fontStyle = FontStyle.Normal,
            textAnchor = TextAnchor.LowerLeft,
            alignByGeometry = false,
            resizeTextForBestFit = false,
            resizeTextMinSize = 10,
            resizeTextMaxSize = 40,
            updateBounds = false,
            verticalOverflow = VerticalWrapMode.Overflow,
            horizontalOverflow = HorizontalWrapMode.Wrap,
            generationExtents = MessageSize,
            pivot = new Vector2(0.5f, 0.5f),
            generateOutOfBounds = false
        };
    }

    /// <summary>
    /// Check whether key-binds for the chat box are pressed.
    /// </summary>
    /// <param name="modSettings">The mod settings that hold the current key-binds.</param>
    private void CheckKeyBinds(ModSettings modSettings) {
        if (!_chatBoxGroup.IsActive()) return;

        if (IsOpen) {
            // Force cursor unlock every frame to prevent game from re-locking it (Hollow Knight InputHandler aggressive
            // lock)
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            HandleOpenChatInput();
        } else if (modSettings.Keybinds.OpenChat.IsPressed && CanOpenChat()) {
            ShowChatInput();
        }
    }

    /// <summary>
    /// Handles key-bind input when the chat box is open.
    /// </summary>
    private void HandleOpenChatInput() {
        if (InputHandler.Instance.inputActions.Pause.IsPressed) {
            HideChatInput();
            return;
        }

        // Track empty state before backspace processes to require an explicit second press
        var isEmptyNow = string.IsNullOrEmpty(_chatInput.GetText());

        if (Input.GetKeyDown(KeyCode.Backspace)) {
            if (_chatInput.IsFocused && isEmptyNow && _wasEmptyLastFrame) {
                if (_isPrivateMode) {
                    if (_privateTargetId.HasValue) {
                        _privateTargetId = null;
                        _channelInput.Focus();
                        UpdateChatModeDisplay();
                    } else {
                        // User pressed backspace from chat input while in whisper mode with no target
                        // We will just focus the channel input so they can type a name
                        _channelInput.Focus();
                    }
                }
            }
        }

        // Handle Tab key for private chat mode
        if (Input.GetKeyDown(KeyCode.Tab)) {
            HandleTabPress();
        }

        var scroll = Input.GetAxis("Mouse ScrollWheel");
        if (scroll != 0) {
            HandleScroll(scroll);
        }

        _wasEmptyLastFrame = isEmptyNow;
    }

    /// <summary>
    /// Handles Tab key press for Emoji Autocomplete and Valorant-style private chat switching. (<see href="https://www.youtube.com/watch?v=xVEwm4IAaw8">Demo</see>)
    /// </summary>
    private void HandleTabPress() {
        if (_isEmojiCompleting) {
            CycleEmojiCompletion();
            return;
        }

        var chatText = _chatInput.GetText();

        if (TryHandleEmojiAutocomplete(chatText)) {
            return;
        }

        HandlePlayerCycling();
        UpdateChatModeDisplay();
    }

    /// <summary>
    /// Attempts to handle emoji autocomplete if the input ends with a valid shortcode prefix.
    /// </summary>
    /// <returns>True if emoji autocomplete was started, false otherwise.</returns>
    private bool TryHandleEmojiAutocomplete(string inputText) {
        if (string.IsNullOrEmpty(inputText)) {
            return false;
        }

        var lastSpaceIndex = inputText.LastIndexOf(' ');
        var lastWordIndex = lastSpaceIndex == -1 ? 0 : lastSpaceIndex + 1;
        var lastWord = inputText.AsSpan(lastWordIndex);

        var colonIndexInWord = lastWord.LastIndexOf(':');
        // Only try if we have a colon that isn't the last character
        if (colonIndexInWord == -1 || colonIndexInWord >= lastWord.Length - 1) {
            return false;
        }

        var candidatePrefix = lastWord[colonIndexInWord..];
        var completions = TextParser.GetCompletions(candidatePrefix, 50);

        if (completions.Count == 0) {
            return false;
        }

        _emojiCandidates = completions.Select(x => x.shortcode).ToList();
        _emojiCandidateIndex = -1;

        // Store text *before* the emoji shortcode prefix.
        var textBeforePrefixIndex = lastWordIndex + colonIndexInWord;
        _textBeforeEmoji = inputText[..textBeforePrefixIndex];

        _isEmojiCompleting = true;
        CycleEmojiCompletion();
        return true;
    }

    /// <summary>
    /// Handles cycling between public and private chat modes, or autocompleting a player name.
    /// </summary>
    private void HandlePlayerCycling() {
        var clientManager = Game.GameManager.Instance.ClientManager;
        var players = clientManager.Players.ToList();

        // If hovering/focusing the channel input, ONLY cycle through players
        if (_channelInput.IsFocused) {
            var channelText = _channelInput.GetText();
            if (string.IsNullOrEmpty(channelText) || channelText == "All" || channelText == "Whisper") {
                if (!_isPrivateMode) {
                    _isPrivateMode = true;
                    _privateTargetId = _lastPrivateTargetId;
                    if (!_privateTargetId.HasValue && players.Count > 0) {
                        _privateTargetId = players[0].Id;
                    }
                } else {
                    CycleToNextPlayer(players);
                }
            } else {
                var match = players.FirstOrDefault(p =>
                    p.Username.StartsWith(channelText, StringComparison.OrdinalIgnoreCase)
                );

                if (match != null) {
                    _isPrivateMode = true;
                    _privateTargetId = match.Id;
                    _chatInput.Focus();
                } else {
                    CycleToNextPlayer(players);
                }
            }
            return;
        }

        // If in message input box and the chat is empty, swap channels between All and Whisper
        if (string.IsNullOrEmpty(_chatInput.GetText())) {
            if (!_isPrivateMode) {
                _isPrivateMode = true;
                _privateTargetId = _lastPrivateTargetId;

                // "If no previous whisperee exists just cycle through the players"
                if (!_privateTargetId.HasValue && players.Count > 0) {
                    _privateTargetId = players[0].Id;
                }
            } else {
                _isPrivateMode = false;
            }
        }
    }

    /// <summary>
    /// Cycles to the next available emoji completion candidate and updates the chat input text.
    /// Temporarily disables input change listening to avoid clearing the autocomplete state.
    /// </summary>
    private void CycleEmojiCompletion() {
        if (_emojiCandidates.Count == 0) return;

        _emojiCandidateIndex = (_emojiCandidateIndex + 1) % _emojiCandidates.Count;
        var selected = _emojiCandidates[_emojiCandidateIndex];

        _ignoreNextInputChange = true;
        _chatInput.SetText(_textBeforeEmoji + selected + " ");
        _chatInput.MoveTextEnd();
        _ignoreNextInputChange = false;
    }

    /// <summary>
    /// Cycles to the next player in the list, or back to public mode.
    /// </summary>
    private void CycleToNextPlayer(List<IClientPlayer> players) {
        if (!_privateTargetId.HasValue) {
            _isPrivateMode = false;
            return;
        }

        var currentIndex = players.FindIndex(p => p.Id == _privateTargetId.Value);
        if (currentIndex == -1 || currentIndex == players.Count - 1) {
            // Last player or not found, go back to public
            _isPrivateMode = false;
            _privateTargetId = null;
        } else {
            // Go to next player
            _privateTargetId = players[currentIndex + 1].Id;
        }
    }

    /// <summary>
    /// Updates the visual display to show current chat mode.
    /// </summary>
    private void UpdateChatModeDisplay() {
        _ignoreNextInputChange = true;

        if (_isPrivateMode) {
            _channelInput.SetInteractable(true);
            if (_privateTargetId.HasValue) {
                var clientManager = Game.GameManager.Instance.ClientManager;
                var player = clientManager.Players.FirstOrDefault(p => p.Id == _privateTargetId.Value);
                var targetName = player?.Username ?? "Unknown";
                _channelInput.SetText(targetName);
                _chatInput.SetPlaceholder($"Whisper...");
            } else {
                var inputText = _channelInput.GetText();
                if (!string.IsNullOrEmpty(inputText) && inputText != "All" && inputText != "Whisper") {
                    var clientManager = Game.GameManager.Instance.ClientManager;
                    var match = clientManager.Players.FirstOrDefault(p => 
                        p.Username.StartsWith(inputText, StringComparison.OrdinalIgnoreCase));
                    if (match != null) {
                        _chatInput.SetPlaceholder($"(Tab to select {match.Username})...");
                    } else {
                        _chatInput.SetPlaceholder("Player not found...");
                    }
                } else {
                    _channelInput.SetText("");
                    _channelInput.SetPlaceholder("Whisper");
                    _chatInput.SetPlaceholder("Type a player name in channel...");
                }
            }
        } else {
            // "All" channel mode
            _channelInput.SetInteractable(true);
            _channelInput.SetText("All");
            _chatInput.SetPlaceholder("Type a message...");
        }

        _ignoreNextInputChange = false;
    }

    /// <summary>
    /// Handles mouse scrolling when the chat box is open.
    /// </summary>
    /// <param name="scrollDelta">The difference in mouse scroll as a float.</param>
    private void HandleScroll(float scrollDelta) {
        var messageCount = CountMessages();
        var maxScroll = Mathf.Max(0, messageCount - MaxShownMessagesWhenOpen);

        if (maxScroll >= 0) {
            var oldOffset = _scrollOffset;
            _scrollOffset = Mathf.Clamp(_scrollOffset + (scrollDelta > 0 ? 1 : -1), 0, maxScroll);

            if (_scrollOffset != oldOffset) {
                UpdateMessageVisibility();
            }
        }
    }

    /// <summary>
    /// Get the number of non-null messages currently in the chat.
    /// </summary>
    /// <returns>The number of non-null messages.</returns>
    private int CountMessages() {
        var count = 0;
        for (var i = 0; i < MaxMessages; i++) {
            if (_messages[i] != null) count++;
        }

        return count;
    }

    /// <summary>
    /// Check whether the chat can be opened. Is based on various game state and UI checks.
    /// </summary>
    /// <returns>True if the chat can be opened, otherwise false.</returns>
    private bool CanOpenChat() {
        if (!IsGameStateValid()) return false;
        if (IsHeroCharging()) return false;
        if (IsInventoryOpen()) return false;
        if (IsGodHomeMenuOpen()) return false;
        if (IsAnyInputFieldFocused()) return false;
        return true;
    }

    /// <summary>
    /// Check whether the game state is valid for opening the chat.
    /// </summary>
    /// <returns>True if the game state is valid, otherwise false.</returns>
    private static bool IsGameStateValid() {
        var gameManager = GameManager.instance;
        if (gameManager == null) return false;

        var validGameStates = gameManager.GameState == GameState.PLAYING ||
                              gameManager.GameState == GameState.MAIN_MENU;
        if (!validGameStates) return false;

        var uiManager = UIManager.instance;
        if (uiManager == null) return false;

        return uiManager.uiState == UIState.PLAYING ||
               uiManager.uiState == UIState.MAIN_MENU_HOME;
    }

    /// <summary>
    /// Check whether the hero (Hornet) is charging their nail (Needle).
    /// </summary>
    /// <returns>True if the hero is charging their nail, otherwise false.</returns>
    private static bool IsHeroCharging() {
        var hero = HeroController.instance;
        return hero != null && hero.cState.nailCharging;
    }

    /// <summary>
    /// Check whether any input field is currently focused.
    /// </summary>
    /// <returns>True if any input field is focused, otherwise false.</returns>
    private static bool IsAnyInputFieldFocused() {
        foreach (var selectable in Selectable.allSelectablesArray) {
            var inputField = selectable.gameObject.GetComponent<InputField>();
            if (inputField && inputField.isFocused) return true;
        }

        return false;
    }

    /// <summary>
    /// Show the chat input.
    /// </summary>
    private void ShowChatInput() {
        IsOpen = true;
        _scrollOffset = 0;

        UpdateMessageVisibility();

        _chatInput.SetActive(true);
        _channelInput.SetActive(true);

        _ignoreNextInputChange = true;
        _chatInput.SetText("");
        _channelInput.SetText(_isPrivateMode ? "" : "All");
        _ignoreNextInputChange = false;

        UpdateChatModeDisplay();

        _chatInput.Focus();

        // Unlock cursor for UI interaction
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // Stop Game Input
        InputHandler.Instance.enabled = false;
        InputHandler.Instance.PreventPause();
        SetEnabledHeroActions(false);
    }

    /// <summary>
    /// Hide the chat input.
    /// </summary>
    private void HideChatInput() {
        IsOpen = false;
        _scrollOffset = 0;

        for (var i = 0; i < MaxMessages; i++)
            _messages[i]?.Hide();

        _chatInput.SetActive(false);
        _channelInput.SetActive(false);

        // Re-lock cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        InputHandler.Instance.enabled = true;
        InputHandler.Instance.inputActions.Pause.ClearInputState();
        InputHandler.Instance.AllowPause();
        SetEnabledHeroActions(true);
    }

    /// <summary>
    /// Updates the visibility of messages in the chat. Checks whether the message is in the scrolled view and whether
    /// the chat is open so it should be displayed.
    /// </summary>
    private void UpdateMessageVisibility() {
        var messageCount = CountMessages();
        var visibleCount = IsOpen ? MaxShownMessagesWhenOpen : MaxShownMessages;
        var maxScroll = Mathf.Max(0, messageCount - visibleCount);

        _scrollOffset = Mathf.Clamp(_scrollOffset, 0, maxScroll);

        var displayPosition = 0;
        for (var i = 0; i < MaxMessages; i++) {
            var message = _messages[i];
            if (message == null) continue;

            var isVisible = displayPosition >= _scrollOffset &&
                            displayPosition < _scrollOffset + visibleCount;

            if (isVisible) {
                var visualSlot = displayPosition - _scrollOffset;
                var yPos = InputMarginBottom + InputHeight + BoxInputMargin +
                           (visualSlot * MessageHeight);

                message.SetPosition(new Vector2(MessageSize.x / 2f + MarginLeft, yPos));
                message.OnChatToggle(IsOpen);
            } else {
                message.Hide();
            }

            displayPosition++;
        }
    }

    /// <summary>
    /// Add a message to the chat.
    /// </summary>
    /// <param name="messageText">The text that the message should have.</param>
    public void AddMessage(string messageText) {
        // Parse and Encode emojis BEFORE we do wrapping logic.
        // This ensures GetPreferredWidth sees the "rendered" length (placeholders) 
        // instead of the raw shortcode length, fixing premature wrapping.
        var encodedText = TextParser.ParseAndEncode(messageText);

        var remaining = encodedText;

        for (var pass = 0; pass < MaxWrapPasses && !string.IsNullOrEmpty(remaining); pass++) {
            var result = WrapTextLine(remaining);

            if (result.wrapped) {
                remaining = result.remainder;
            } else {
                var sanitized = RemoveEmptyColorTags(remaining);
                if (HasVisibleContent(sanitized)) {
                    AddTrimmedMessage(sanitized);
                }

                break;
            }
        }
    }

    /// <summary>
    /// Wrap the given text for adding to the chat. The non-wrapped part will be added to the chat and the result will
    /// be returned as a tuple.
    /// </summary>
    /// <param name="text">The string text for the message that needs to be wrapped.</param>
    /// <returns>A tuple containing whether the text was wrapped and if wrapped, the remaining string.</returns>
    private (bool wrapped, string remainder) WrapTextLine(string text) {
        var lastSpaceIndex = -1;

        for (var i = 0; i < text.Length; i++) {
            i = SkipHtmlTag(text, i);

            if (text[i] == ' ') {
                lastSpaceIndex = i;
            }

            var currentText = text.Substring(0, i + 1);
            var width = _textGenerator.GetPreferredWidth(
                StripRichTextTags(currentText),
                _textGenSettings
            );

            if (width > ChatWidth) {
                return SplitAndWrapLine(text, lastSpaceIndex, i);
            }
        }

        return (false, text);
    }

    /// <summary>
    /// Calculate the index to continue from after skipping HTML tags within the given text. If the text does not
    /// contain an HTML tag at the given index, the entire text is returned.
    /// </summary>
    /// <param name="text">The text to check and calculate with.</param>
    /// <param name="index">The starting index to read from.</param>
    /// <returns>The index of the closing character of the HTML tag that was skipped.</returns>
    private static int SkipHtmlTag(string text, int index) {
        if (text[index] == '<') {
            var closing = text.IndexOf('>', index + 1);
            if (closing != -1) {
                var tagContent = text.Substring(index + 1, closing - index - 1).Trim().ToLowerInvariant();
                // Only skip recognized Unity rich-text tags; otherwise treat '<' as a literal character
                if (IsTrackableTag(tagContent) || IsClosingTagTrackable(tagContent)) {
                    return closing;
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Split and wrap the given text to add to the chat. The first part will be added to the chat and the remainder
    /// will be returned as a tuple for further splitting.
    /// </summary>
    /// <param name="text">The text of the message.</param>
    /// <param name="lastSpace">The last space in the text that was encountered before deciding to split the line.
    /// </param>
    /// <param name="currentIndex">The current index of scanning through the text upon deciding to split the line.
    /// </param>
    /// <returns>A tuple consisting of whether the text was wrapped and if so, a string of the remainder of the text.
    /// </returns>
    private (bool wrapped, string remainder) SplitAndWrapLine(string text, int lastSpace, int currentIndex) {
        // Hybrid Wrapping Logic:
        // Calculate how much space we waste if we wrap at the last Space (Word Wrap).
        // If we waste > 50% of the line (e.g. only padding/prefix remains), force a Character Break
        // to fill the line with the long word instead of moving it entirely to the next line.

        bool forceCharWrap = false;
        if (lastSpace != -1) {
            var potentialLine = text.Substring(0, lastSpace);
            var potentialWidth = _textGenerator.GetPreferredWidth(StripRichTextTags(potentialLine), _textGenSettings);

            // If line usage is < 50%, force broken word.
            if (potentialWidth < ChatWidth * 0.5f) {
                forceCharWrap = true;
            }
        }

        var splitIndex = (lastSpace != -1 && !forceCharWrap) ? lastSpace : currentIndex;

        var firstPart = text.Substring(0, splitIndex);
        var openTags = GetUnclosedRichTextTags(firstPart);
        var firstComplete = firstPart + BuildClosingTags(openTags);

        var sanitized = RemoveEmptyColorTags(firstComplete);
        if (HasVisibleContent(sanitized)) {
            AddTrimmedMessage(sanitized);
        }

        var removedSpace = splitIndex == lastSpace && lastSpace != -1;
        var remainderStart = splitIndex + (removedSpace ? 1 : 0);

        var remainderTail = text.Substring(remainderStart);
        remainderTail = CleanRemainderText(remainderTail);

        var startsWithColor = StartsWithColorAfterSkippablePrefix(remainderTail);
        var reopenTags = startsWithColor ? FilterOutColorTags(openTags) : openTags;
        var remainder = BuildOpeningTags(reopenTags) + remainderTail;
        remainder = RemoveEmptyColorTags(remainder);

        // Prevent infinite loops
        if (StripRichTextTags(remainder).Length >= StripRichTextTags(text).Length) {
            if (HasVisibleContent(remainder)) {
                AddTrimmedMessage(remainder);
            }

            return (false, string.Empty);
        }

        return (true, remainder);
    }

    /// <summary>
    /// Clean the remainder text by trimming leading closing tags, leading dangling angle brackets, and normalizing
    /// leading open tags for colors.
    /// </summary>
    /// <param name="text">The text to clean.</param>
    /// <returns>A string of the cleaned text.</returns>
    private static string CleanRemainderText(string text) {
        text = TrimLeadingClosingTags(text);
        text = TrimLeadingDanglingAngles(text);
        text = NormalizeLeadingColorOpens(text);
        return text;
    }

    /// <summary>
    /// Add a trimmed message to the chat.
    /// </summary>
    /// <param name="messageText">The text of the message.</param>
    private void AddTrimmedMessage(string messageText) {
        messageText = EnsureLeadingCharForRichText(messageText);
        if (!HasVisibleContent(messageText)) return;

        _messages[MaxMessages - 1]?.Destroy();

        // Decode the encoded string (stripping tags) and get positions for overlay rendering
        var (parsedText, emojiPositions) = TextParser.DecodeAndGetPositions(messageText);
        Logger.Debug($"[ChatLine] {parsedText}");

        ShiftMessagesUp();

        var newMessage = new ChatMessage(
            _chatBoxGroup,
            new Vector2(
                MessageSize.x / 2f + MarginLeft,
                InputMarginBottom + InputHeight + BoxInputMargin
            ),
            parsedText,
            emojiPositions
        );
        newMessage.Display(IsOpen);
        _messages[0] = newMessage;

        _scrollOffset = 0;
        UpdateMessageVisibility();
    }

    /// <summary>
    /// Shift all chat messages up by one and set the message at the first index to null.
    /// </summary>
    private void ShiftMessagesUp() {
        for (var i = MaxMessages - 2; i >= 0; i--) {
            _messages[i + 1] = _messages[i];
        }

        _messages[0] = null;
    }

    #region Rich Text Tag Utilities

    /// <summary>
    /// Get a list of unclosed rich-text tags from the given text.
    /// </summary>
    /// <param name="text">The text to compose a list for.</param>
    /// <returns>A list of strings that each represent a rich-text tag.</returns>
    private static List<string> GetUnclosedRichTextTags(string text) {
        var stack = new List<string>();

        for (var i = 0; i < text.Length; i++) {
            if (text[i] != '<') continue;

            var end = text.IndexOf('>', i + 1);
            if (end == -1) break;

            var tagContent = text.Substring(i + 1, end - i - 1).ToLowerInvariant();

            if (tagContent.StartsWith("/")) {
                CloseMatchingTag(stack, tagContent.Substring(1).Trim());
            } else if (IsTrackableTag(tagContent)) {
                stack.Add(text.Substring(i, end - i + 1));
            }

            i = end;
        }

        return stack;
    }

    /// <summary>
    /// Whether the content of a tag is trackable (i.e. a color or formatting tag that this chat supports).
    /// </summary>
    /// <param name="tagContent">The tag content as a string.</param>
    /// <returns>True if the tag is trackable, otherwise false.</returns>
    private static bool IsTrackableTag(string tagContent) {
        return tagContent.StartsWith("color=") || tagContent.StartsWith("color=#") || tagContent.StartsWith("link=") || tagContent == "b" ||
               tagContent == "i";
    }

    /// <summary>
    /// Whether the given tag is closing and trackable. <seealso cref="IsTrackableTag"/>
    /// </summary>
    /// <param name="tagContent">The tag content as a string.</param>
    /// <returns>True if the tag is closing and trackable.</returns>
    private static bool IsClosingTagTrackable(string tagContent) {
        if (!tagContent.StartsWith("/")) return false;
        var closeName = tagContent[1..].Trim();
        return closeName.StartsWith("color") || closeName == "link" || closeName == "b" || closeName == "i";
    }

    /// <summary>
    /// Close a matching tag in the given stack. This will remove the last occurrence of a tag in the stack if it
    /// matches the given closing tag name.
    /// </summary>
    /// <param name="stack">The stack containing open tags as strings.</param>
    /// <param name="closeName">The name of the closing tag.</param>
    private static void CloseMatchingTag(List<string> stack, string closeName) {
        for (var s = stack.Count - 1; s >= 0; s--) {
            if (IsMatching(stack[s], closeName)) {
                stack.RemoveAt(s);
                break;
            }
        }
    }

    /// <summary>
    /// Whether the given opening tag matches the given closing tag.
    /// </summary>
    /// <param name="openTag">The open tag as a string.</param>
    /// <param name="closeTag">The closing tag as a string.</param>
    /// <returns>True if the tags match, false otherwise.</returns>
    private static bool IsMatching(string openTag, string closeTag) {
        if (openTag.StartsWith("<color", StringComparison.OrdinalIgnoreCase))
            return closeTag.StartsWith("color");
        if (openTag.Equals("<b>", StringComparison.OrdinalIgnoreCase))
            return closeTag == "b";
        if (openTag.Equals("<i>", StringComparison.OrdinalIgnoreCase))
            return closeTag == "i";
        return false;
    }

    /// <summary>
    /// Build a string containing closing tags that match the given open tags.
    /// </summary>
    /// <param name="openTags">The open tags as a list of strings.</param>
    /// <returns>A string of concatenated closing tags.</returns>
    private static string BuildClosingTags(List<string> openTags) {
        var sb = new StringBuilder();
        for (var i = openTags.Count - 1; i >= 0; i--) {
            if (openTags[i].StartsWith("<color", StringComparison.OrdinalIgnoreCase))
                sb.Append("</color>");
            else if (openTags[i].Equals("<b>", StringComparison.OrdinalIgnoreCase))
                sb.Append("</b>");
            else if (openTags[i].Equals("<i>", StringComparison.OrdinalIgnoreCase))
                sb.Append("</i>");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build a string containing the open tags given in the list.
    /// </summary>
    /// <param name="openTags">The open tags as a list of strings.</param>
    /// <returns>A string of concatenated open tags.</returns>
    private static string BuildOpeningTags(List<string> openTags) {
        var sb = new StringBuilder();
        foreach (var tag in openTags) {
            sb.Append(tag);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Filter out all color tags from the given list.
    /// </summary>
    /// <param name="openTags">The open tags as a list of strings.</param>
    /// <returns>A new list of strings containing only non-color tags.</returns>
    private static List<string> FilterOutColorTags(List<string> openTags) {
        var filtered = new List<string>(openTags.Count);
        foreach (var tag in openTags) {
            if (!tag.StartsWith("<color", StringComparison.OrdinalIgnoreCase)) {
                filtered.Add(tag);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Removes empty color tags from the given text.
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns>A new string that has all empty color tags removed.</returns>
    private static string RemoveEmptyColorTags(string text) {
        var searchFrom = 0;
        while (searchFrom < text.Length) {
            var open = text.IndexOf("<color", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (open == -1) break;

            var openEnd = text.IndexOf('>', open + 1);
            if (openEnd == -1) break;

            var close = text.IndexOf("</color>", openEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (close == -1) break;

            var inner = text.Substring(openEnd + 1, close - openEnd - 1);

            if (string.IsNullOrWhiteSpace(inner)) {
                text = text.Remove(close, 8); // "</color>".Length
                text = text.Remove(open, openEnd - open + 1);
                searchFrom = open;
            } else {
                searchFrom = close + 8;
            }
        }

        return text;
    }

    /// <summary>
    /// Trims all leading closing tags from the given text.
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns>A new string that has all leading closing tags removed or the same string as the input.</returns>
    private static string TrimLeadingClosingTags(string text) {
        var index = 0;
        while (index + 2 < text.Length && text[index] == '<' && text[index + 1] == '/') {
            var end = text.IndexOf('>', index + 2);
            if (end == -1) break;

            var name = text.Substring(index + 2, end - index - 2).Trim().ToLowerInvariant();
            if (name == "color" || name == "b" || name == "i") {
                index = end + 1;
            } else {
                break;
            }
        }

        return index > 0 ? text.Substring(index) : text;
    }

    /// <summary>
    /// Trims all leading dangling angle brackets from the given text.
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns>A new string with leading dangling angle brackets removed or the same string as the input.</returns>
    private static string TrimLeadingDanglingAngles(string text) {
        var idx = 0;
        while (idx < text.Length && text[idx] == '>') idx++;
        return idx > 0 ? text.Substring(idx) : text;
    }

    /// <summary>
    /// Normalize all leading color open tags at the start of the given text. This means that if multiple leading
    /// color opening tags are found, only the last will be included in the returned value, because all others would
    /// be overridden anyway.
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns></returns>
    private static string NormalizeLeadingColorOpens(string text) {
        var idx = 0;
        string? lastOpen = null;

        while (idx < text.Length && text[idx] == '<') {
            var end = text.IndexOf('>', idx + 1);
            if (end == -1) break;

            var content = text.Substring(idx + 1, end - idx - 1).Trim().ToLowerInvariant();
            if (content.StartsWith("color=") || content.StartsWith("color=#")) {
                lastOpen = text.Substring(idx, end - idx + 1);
                idx = end + 1;
            } else {
                break;
            }
        }

        return lastOpen != null ? lastOpen + text.Substring(idx) : text;
    }

    /// <summary>
    /// Whether the given text starts with a color tag after skipping prefixes (whitespace and certain other
    /// characters).
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns>True if the text starts with a color tag after skipping prefixes, false otherwise.</returns>
    private static bool StartsWithColorAfterSkippablePrefix(string text) {
        var i = 0;
        while (i < text.Length) {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '-' || c == ':' ||
                c == '•' || c == '–' || c == '—') {
                i++;
            } else {
                break;
            }
        }

        return i < text.Length &&
               text.IndexOf("<color", i, StringComparison.OrdinalIgnoreCase) == i;
    }

    /// <summary>
    /// Ensure that the given text starts with a zero-width space before the first angled bracket. This is to ensure
    /// that Unity does not see the angled bracket as a start for rich-text.
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns>The string with a zero-width space prefixed if necessary.</returns>
    private static string EnsureLeadingCharForRichText(string text) {
        if (string.IsNullOrEmpty(text) || text[0] != '<') return text;
        return "\u200B" + text; // Zero-width space
    }

    /// <summary>
    /// Whether the given text has any visible content (i.e. not only tags).
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns>True if the text has visible content, false otherwise.</returns>
    private static bool HasVisibleContent(string text) {
        return StripRichTextTags(text).Trim().Length > 0;
    }

    /// <summary>
    /// Strips rich-text tags from the given text.
    /// </summary>
    /// <param name="text">The text as a string.</param>
    /// <returns>A new string with the rich-text tags removed.</returns>
    private static string StripRichTextTags(string text) {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '<') {
                var end = text.IndexOf('>', i + 1);
                if (end != -1) {
                    var content = text.Substring(i + 1, end - i - 1).Trim().ToLowerInvariant();
                    // Skip only recognized rich-text tags; otherwise treat '<' as literal
                    if (IsTrackableTag(content) || IsClosingTagTrackable(content)) {
                        i = end; // jump past closing '>'
                        continue;
                    }
                }

                sb.Append('<');
            } else {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Game State Checks

    /// <summary>
    /// Set the 'Enabled' property of hero actions. This is used to ensure that input is completely disabled during
    /// chat text input.
    /// </summary>
    /// <param name="enabled">Whether the actions should be enabled or not.</param>
    private static void SetEnabledHeroActions(bool enabled) {
        var inputHandler = InputHandler.Instance;
        if (inputHandler?.inputActions == null) return;

        var actions = inputHandler.inputActions;
        actions.Left.Enabled = enabled;
        actions.Right.Enabled = enabled;
        actions.Up.Enabled = enabled;
        actions.Down.Enabled = enabled;
        actions.MenuSubmit.Enabled = enabled;
        actions.MenuCancel.Enabled = enabled;
        actions.MenuExtra.enabled = enabled;
        actions.MenuSuper.enabled = enabled;
        actions.RsUp.Enabled = enabled;
        actions.RsDown.Enabled = enabled;
        actions.RsLeft.Enabled = enabled;
        actions.RsRight.Enabled = enabled;
        actions.Jump.Enabled = enabled;
        actions.Evade.Enabled = enabled;
        actions.Dash.Enabled = enabled;
        actions.SuperDash.Enabled = enabled;
        actions.DreamNail.Enabled = enabled;
        actions.Attack.Enabled = enabled;
        actions.Cast.Enabled = enabled;
        actions.QuickMap.Enabled = enabled;
        actions.QuickCast.Enabled = enabled;
        actions.Taunt.Enabled = enabled;
        actions.PaneRight.Enabled = enabled;
        actions.PaneLeft.Enabled = enabled;
        actions.OpenInventory.Enabled = enabled;
        actions.OpenInventoryMap.Enabled = enabled;
        actions.OpenInventoryJournal.Enabled = enabled;
        actions.OpenInventoryTools.Enabled = enabled;
        actions.OpenInventoryQuests.Enabled = enabled;
        actions.SwipeInventoryMap.Enabled = enabled;
        actions.SwipeInventoryJournal.Enabled = enabled;
        actions.SwipeInventoryTools.Enabled = enabled;
        actions.SwipeInventoryQuests.Enabled = enabled;
    }

    /// <summary>
    /// Whether the inventory is open.
    /// </summary>
    /// <returns>True if the inventory is open, otherwise false.</returns>
    private static bool IsInventoryOpen() {
        var gameManager = GameManager.instance;
        if (gameManager == null) return false;

        var invFsm = gameManager.inventoryFSM;
        if (invFsm == null) return false;
        var stateName = invFsm.ActiveStateName;
        return stateName != "Closed" && stateName != "Can Open Inventory?";
    }

    /// <summary>
    /// Whether the GodHome menu is open.
    /// </summary>
    /// <returns>True if the GodHome menu is open, otherwise false.</returns>
    private static bool IsGodHomeMenuOpen() {
        var bossChallengeUi = Object.FindObjectsByType<BossChallengeUI>(FindObjectsSortMode.None);
        var bossDoorChallengeUi = Object.FindObjectsByType<BossDoorChallengeUI>(FindObjectsSortMode.None);
        return bossChallengeUi.Length != 0 || bossDoorChallengeUi.Length != 0;
    }

    #endregion
}
