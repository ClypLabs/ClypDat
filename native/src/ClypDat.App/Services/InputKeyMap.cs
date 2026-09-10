namespace ClypDat.App.Services;

/// <summary>
/// Set-1 scan code to a physical key position, named the way the web names them.
///
/// Position, not letter, is what a keyboard overlay needs: the key next to Tab
/// is drawn "Q" on a QWERTY board and "A" on an AZERTY one, and it reports the
/// same scan code either way. Matching recorded input against the drawn label
/// would light the wrong cap on every non-US layout; matching against position
/// cannot.
/// </summary>
internal static class InputKeyMap
{
    // E0-prefixed codes share their byte with an unprefixed key (E0 1D is right
    // control, 1D alone is left), so the flag is part of the key.
    private static readonly Dictionary<int, string> Codes = new()
    {
        [0x01] = "Escape",
        [0x02] = "Digit1", [0x03] = "Digit2", [0x04] = "Digit3", [0x05] = "Digit4", [0x06] = "Digit5",
        [0x07] = "Digit6", [0x08] = "Digit7", [0x09] = "Digit8", [0x0A] = "Digit9", [0x0B] = "Digit0",
        [0x0C] = "Minus", [0x0D] = "Equal", [0x0E] = "Backspace", [0x0F] = "Tab",
        [0x10] = "KeyQ", [0x11] = "KeyW", [0x12] = "KeyE", [0x13] = "KeyR", [0x14] = "KeyT",
        [0x15] = "KeyY", [0x16] = "KeyU", [0x17] = "KeyI", [0x18] = "KeyO", [0x19] = "KeyP",
        [0x1A] = "BracketLeft", [0x1B] = "BracketRight", [0x1C] = "Enter", [0x1D] = "ControlLeft",
        [0x1E] = "KeyA", [0x1F] = "KeyS", [0x20] = "KeyD", [0x21] = "KeyF", [0x22] = "KeyG",
        [0x23] = "KeyH", [0x24] = "KeyJ", [0x25] = "KeyK", [0x26] = "KeyL",
        [0x27] = "Semicolon", [0x28] = "Quote", [0x29] = "Backquote", [0x2A] = "ShiftLeft", [0x2B] = "Backslash",
        [0x2C] = "KeyZ", [0x2D] = "KeyX", [0x2E] = "KeyC", [0x2F] = "KeyV", [0x30] = "KeyB",
        [0x31] = "KeyN", [0x32] = "KeyM",
        [0x33] = "Comma", [0x34] = "Period", [0x35] = "Slash", [0x36] = "ShiftRight",
        [0x37] = "NumpadMultiply", [0x38] = "AltLeft", [0x39] = "Space", [0x3A] = "CapsLock",
        [0x3B] = "F1", [0x3C] = "F2", [0x3D] = "F3", [0x3E] = "F4", [0x3F] = "F5", [0x40] = "F6",
        [0x41] = "F7", [0x42] = "F8", [0x43] = "F9", [0x44] = "F10", [0x57] = "F11", [0x58] = "F12",
        [0x45] = "NumLock", [0x46] = "ScrollLock",
        [0x4A] = "NumpadSubtract", [0x4E] = "NumpadAdd",
        [0x47] = "Numpad7", [0x48] = "Numpad8", [0x49] = "Numpad9",
        [0x4B] = "Numpad4", [0x4C] = "Numpad5", [0x4D] = "Numpad6",
        [0x4F] = "Numpad1", [0x50] = "Numpad2", [0x51] = "Numpad3",
        [0x52] = "Numpad0", [0x53] = "NumpadDecimal",
        // Extended (E0) - the arrow cluster, the right-hand modifiers and the
        // navigation block, all of which double up on numpad scan codes.
        [Extended | 0x1C] = "NumpadEnter", [Extended | 0x1D] = "ControlRight",
        [Extended | 0x35] = "NumpadDivide", [Extended | 0x38] = "AltRight",
        [Extended | 0x47] = "Home", [Extended | 0x48] = "ArrowUp", [Extended | 0x49] = "PageUp",
        [Extended | 0x4B] = "ArrowLeft", [Extended | 0x4D] = "ArrowRight",
        [Extended | 0x4F] = "End", [Extended | 0x50] = "ArrowDown", [Extended | 0x51] = "PageDown",
        [Extended | 0x52] = "Insert", [Extended | 0x53] = "Delete",
        [Extended | 0x5B] = "MetaLeft", [Extended | 0x5C] = "MetaRight", [Extended | 0x5D] = "ContextMenu",
    };

    private const int Extended = 0x100;

    /// <summary>The physical position for a scan code, or null when nothing on
    /// any drawn board corresponds to it.</summary>
    public static string? Code(ushort scanCode, bool extended) =>
        Codes.TryGetValue((extended ? Extended : 0) | scanCode, out var code) ? code : null;
}
