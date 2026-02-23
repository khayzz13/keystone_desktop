// TextEntry - Global text input state for inline text fields
// Any UI can activate via Focus(), receive typed characters, and read the buffer.

namespace Keystone.Core.UI;

public class TextEntry
{
    private static TextEntry? _active;
    public static TextEntry? Active => _active;

    public string Buffer { get; set; } = "";
    public int Cursor { get; set; }
    public string? Tag { get; set; } // Identifies which field is active (e.g. "preset_name")
    public Action<string>? OnSubmit { get; set; }
    public Action? OnCancel { get; set; }

    public void Focus(string initialText = "", string? tag = null,
        Action<string>? onSubmit = null, Action? onCancel = null)
    {
        Buffer = initialText;
        Cursor = initialText.Length;
        Tag = tag;
        OnSubmit = onSubmit;
        OnCancel = onCancel;
        _active = this;
    }

    public void Blur()
    {
        if (_active == this) _active = null;
    }

    public bool IsFocused => _active == this;

    /// <summary>Called by WindowManager when NSEvent characters arrive and a TextEntry is active.</summary>
    public static bool HandleCharacters(string chars)
    {
        if (_active == null) return false;
        foreach (char c in chars)
            _active.InsertChar(c);
        return true;
    }

    /// <summary>Called by WindowManager for special keys (backspace, enter, escape, arrows).</summary>
    public static bool HandleKeyCode(ushort keyCode)
    {
        if (_active == null) return false;
        switch (keyCode)
        {
            case 51: // Backspace
                if (_active.Cursor > 0)
                {
                    _active.Buffer = _active.Buffer.Remove(_active.Cursor - 1, 1);
                    _active.Cursor--;
                }
                return true;
            case 117: // Forward delete
                if (_active.Cursor < _active.Buffer.Length)
                    _active.Buffer = _active.Buffer.Remove(_active.Cursor, 1);
                return true;
            case 123: // Left arrow
                if (_active.Cursor > 0) _active.Cursor--;
                return true;
            case 124: // Right arrow
                if (_active.Cursor < _active.Buffer.Length) _active.Cursor++;
                return true;
            case 36: // Return
                _active.OnSubmit?.Invoke(_active.Buffer);
                _active.Blur();
                return true;
            case 53: // Escape
                _active.OnCancel?.Invoke();
                _active.Blur();
                return true;
        }
        return false;
    }

    void InsertChar(char c)
    {
        if (c < 32 && c != '\t') return; // Ignore control chars
        Buffer = Buffer.Insert(Cursor, c.ToString());
        Cursor++;
    }
}
