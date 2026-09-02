namespace D365FO.Core.Validation;

/// <summary>
/// Reads the shape of an AOT document by scanning tokens, without parsing it and without
/// rewriting it first.
/// </summary>
/// <remarks>
/// <para>
/// The rules that decide whether a document is a table, a report or an extension used to ask
/// whether the TEXT contained <c>&lt;AxTable</c>. Text is not structure: a comment above the
/// root — <c>&lt;!-- this was an &lt;AxTable&gt; before it was rewritten --&gt;</c> — made an
/// <c>AxClass</c> answer to every table rule, and so does the same name inside a CDATA X++
/// block or a string literal. The finding then names a rule the document cannot break.
/// </para>
/// <para>
/// Deleting the comments first and re-scanning is the other tempting shape, and it is worse: it
/// mutates the document being judged, and a <c>--&gt;</c> inside a CDATA section takes the
/// deletion with it. Scanning forward and skipping what cannot contain markup costs one pass and
/// changes nothing.
/// </para>
/// </remarks>
public static class XmlScan
{
    /// <summary>
    /// The name of the root element, or null when the text has no element outside a prolog,
    /// a comment or a processing instruction.
    /// </summary>
    public static string? RootElementName(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;

        var i = 0;
        while (i < xml.Length)
        {
            var lt = xml.IndexOf('<', i);
            if (lt < 0) return null;

            // <!-- comment -->, <![CDATA[ … ]]>, <!DOCTYPE …>
            if (Starts(xml, lt, "<!--"))
            {
                var end = xml.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 3;
                continue;
            }
            if (Starts(xml, lt, "<![CDATA["))
            {
                var end = xml.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 3;
                continue;
            }
            if (Starts(xml, lt, "<!"))
            {
                var end = xml.IndexOf('>', lt + 2);
                if (end < 0) return null;
                i = end + 1;
                continue;
            }
            // <?xml … ?>
            if (Starts(xml, lt, "<?"))
            {
                var end = xml.IndexOf("?>", lt + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 2;
                continue;
            }
            // </close> is not a root opening; malformed input, but do not answer with the name.
            if (Starts(xml, lt, "</")) return null;

            var start = lt + 1;
            var j = start;
            while (j < xml.Length && (char.IsLetterOrDigit(xml[j]) || xml[j] is '_' or '-' or '.' or ':')) j++;
            return j > start ? xml[start..j] : null;
        }
        return null;
    }

    /// <summary>Is the document's ROOT this element — not merely a text that mentions it?</summary>
    public static bool RootIs(string? xml, string elementName) =>
        string.Equals(RootElementName(xml), elementName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does an element with this name appear as real markup anywhere outside comments, CDATA
    /// and the prolog?
    /// </summary>
    /// <remarks>
    /// For the rules that ask about a CHILD (does this table declare an index?) rather than
    /// about the root. Same reason: an <c>&lt;AxTableIndex&gt;</c> written inside a comment is
    /// not an index.
    /// </remarks>
    public static bool ContainsElement(string? xml, string elementName)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(elementName)) return false;

        var i = 0;
        while (i < xml.Length)
        {
            var lt = xml.IndexOf('<', i);
            if (lt < 0) return false;

            if (Starts(xml, lt, "<!--"))
            {
                var end = xml.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 3;
                continue;
            }
            if (Starts(xml, lt, "<![CDATA["))
            {
                var end = xml.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 3;
                continue;
            }
            if (Starts(xml, lt, "<!") || Starts(xml, lt, "<?"))
            {
                var end = xml.IndexOf('>', lt + 1);
                if (end < 0) return false;
                i = end + 1;
                continue;
            }

            var start = Starts(xml, lt, "</") ? lt + 2 : lt + 1;
            var j = start;
            while (j < xml.Length && (char.IsLetterOrDigit(xml[j]) || xml[j] is '_' or '-' or '.' or ':')) j++;

            if (j - start == elementName.Length
                && string.Compare(xml, start, elementName, 0, elementName.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return true;

            i = j;
        }
        return false;
    }

    private static bool Starts(string s, int at, string token) =>
        at + token.Length <= s.Length && string.CompareOrdinal(s, at, token, 0, token.Length) == 0;
}
