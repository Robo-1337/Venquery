using System.Text;

namespace Venquery.Services;

/// <summary>Text escaping for the PowerShell scripts Venquery generates. Every value that isn't a fixed string
/// (a folder the user picked, a PC model read from WMI, a task title) goes through <see cref="Lit"/> before it
/// is placed inside a single-quoted literal.</summary>
public static class PsText
{
    /// <summary>Escapes <paramref name="s"/> for use INSIDE a PowerShell single-quoted string ('...'). PowerShell
    /// treats the typographic quotes ‘ ’ ‚ ‛ exactly like the ASCII apostrophe, so a folder named "Mom’s
    /// Backups" would end the literal early (breaking the script, or worse, letting the rest of the name run as
    /// code). Each quote character is doubled, which PowerShell reads as one literal quote. Line breaks become
    /// spaces so a value can never start a new statement.</summary>
    public static string Lit(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s!.Length + 8);
        foreach (var ch in s!)
        {
            switch (ch)
            {
                case '\'': case '\u2018': case '\u2019': case '\u201A': case '\u201B':
                    sb.Append(ch).Append(ch); break;
                case '\r': case '\n': case '\u0085': case '\u2028': case '\u2029':
                    sb.Append(' '); break;
                default:
                    sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
