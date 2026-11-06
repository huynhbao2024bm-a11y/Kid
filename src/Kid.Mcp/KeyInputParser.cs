namespace Kid.Mcp;

internal static class KeyInputParser
{
    internal static IReadOnlyList<KeyboardEvent> Parse(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key names cannot be empty.", nameof(key));
        }

        var parts = key.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException($"Invalid key chord '{key}'.", nameof(key));
        }

        var modifiers = new List<ushort>();
        foreach (var part in parts[..^1])
        {
            var modifier = GetModifier(part);
            if (modifiers.Contains(modifier))
            {
                throw new ArgumentException($"Duplicate modifier in key chord '{key}'.", nameof(key));
            }

            modifiers.Add(modifier);
        }

        var virtualKey = GetVirtualKey(parts[^1]);
        if (modifiers.Contains(virtualKey))
        {
            throw new ArgumentException($"A modifier cannot be the final key in '{key}'.", nameof(key));
        }

        var events = new List<KeyboardEvent>(modifiers.Count * 2 + 2);
        events.AddRange(modifiers.Select(modifier => new KeyboardEvent(modifier, KeyUp: false)));
        events.Add(new KeyboardEvent(virtualKey, KeyUp: false));
        events.Add(new KeyboardEvent(virtualKey, KeyUp: true));
        events.AddRange(modifiers.AsEnumerable().Reverse().Select(modifier => new KeyboardEvent(modifier, KeyUp: true)));
        return events;
    }

    private static ushort GetModifier(string modifier) => modifier.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => 0x11,
        "ALT" => 0x12,
        "SHIFT" => 0x10,
        "WIN" or "WINDOWS" => 0x5B,
        _ => throw new ArgumentException($"Unsupported key modifier '{modifier}'.")
    };

    private static ushort GetVirtualKey(string key)
    {
        var normalized = key.ToUpperInvariant();
        if (normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return (ushort)normalized[0];
        }

        if (normalized.Length is 2 or 3
            && normalized[0] == 'F'
            && int.TryParse(normalized.AsSpan(1), out var functionKey)
            && functionKey is >= 1 and <= 12)
        {
            return (ushort)(0x70 + functionKey - 1);
        }

        return normalized switch
        {
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => throw new ArgumentException($"Unsupported key '{key}'.")
        };
    }

    internal readonly record struct KeyboardEvent(ushort VirtualKey, bool KeyUp);
}
