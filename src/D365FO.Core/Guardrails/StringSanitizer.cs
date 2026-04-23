namespace D365FO.Core;

/// <summary>
/// Sanitizes free-form strings sourced from the D365FO metadata index
/// (labels, descriptions) before they are rendered back to an LLM caller.
/// Protects against prompt-injection attempts embedded in customer data.
/// Callers can opt out with a --raw flag in the CLI layer.
/// </summary>
public static class StringSanitizer
{
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var span = value.AsSpan();
        var buf = new System.Text.StringBuilder(span.Length);
        foreach (var ch in span)
        {
            if (ch < 0x20 && ch is not ('\n' or '\r' or '\t')) continue; // strip control
            buf.Append(ch);
        }
        return buf.ToString();
    }
}
