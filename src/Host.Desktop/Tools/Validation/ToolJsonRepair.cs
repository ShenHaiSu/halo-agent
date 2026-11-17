using System.Text;
using System.Text.Json;

namespace Host.Desktop.Tools.Validation;

/// <summary>Conservative JSON repair: bare control chars inside strings plus stray trailing commas.</summary>
public static class ToolJsonRepair
{
    public static bool TryRepair(string raw, out string repaired)
    {
        repaired = raw;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string text = StripBom(raw).Trim();
        if (TryParse(text, out _))
        {
            return false;
        }

        string fixedText = EscapeBareControls(text);
        fixedText = RemoveTrailingCommas(fixedText);
        if (string.Equals(fixedText, text, StringComparison.Ordinal))
        {
            return false;
        }

        if (TryParse(fixedText, out _))
        {
            repaired = fixedText;
            return true;
        }

        return false;
    }

    private static string StripBom(string s)
    {
        if (s.Length > 0 && s[0] == '\uFEFF')
        {
            return s.Substring(1);
        }

        return s;
    }

    private static bool TryParse(string text, out JsonDocument? doc)
    {
        try
        {
            doc = JsonDocument.Parse(text);
            doc.Dispose();
            doc = null;
            return true;
        }
        catch
        {
            doc = null;
            return false;
        }
    }

    private static string EscapeBareControls(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        bool inString = false;
        bool escaped = false;
        foreach (char c in text)
        {
            if (inString)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    sb.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append(c);
                    inString = false;
                    continue;
                }

                if (c == '\n')
                {
                    sb.Append("\\n");
                    continue;
                }

                if (c == '\r')
                {
                    sb.Append("\\r");
                    continue;
                }

                if (c == '\t')
                {
                    sb.Append("\\t");
                    continue;
                }

                if (c < 0x20)
                {
                    sb.Append("\\u");
                    sb.Append(((int)c).ToString("x4"));
                    continue;
                }

                sb.Append(c);
                continue;
            }

            sb.Append(c);
            if (c == '"')
            {
                inString = true;
                escaped = false;
            }
        }

        return sb.ToString();
    }

    private static string RemoveTrailingCommas(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inString = false;
        bool escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            if (c == ',')
            {
                int j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                {
                    j++;
                }

                if (j < text.Length && (text[j] == '}' || text[j] == ']'))
                {
                    continue;
                }

                sb.Append(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
