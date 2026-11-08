using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Companions.Matching;

/// <summary>
/// Normalises titles and names so that case, accents, punctuation, "&amp;"/"and", roman numerals and a
/// leading article do not matter: "The Lord of the Rings: The Fellowship of the Ring" and
/// "Lord of the Rings - The Fellowship of the Ring" give the same key.
/// </summary>
public static partial class TitleKeys
{
    private static readonly Dictionary<string, string> TokenMap = new(StringComparer.Ordinal)
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
        ["volume"] = "vol", ["pt"] = "part", ["and"] = "&", ["und"] = "&",

        // "3 Body Problem" == "The Three-Body Problem", "Die drei ???" == "Die 3 ???" ("elf" stays a word)
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6", ["seven"] = "7",
        ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12",
        ["eins"] = "1", ["zwei"] = "2", ["drei"] = "3", ["vier"] = "4", ["funf"] = "5", ["sechs"] = "6", ["sieben"] = "7",
        ["acht"] = "8", ["neun"] = "9", ["zehn"] = "10", ["zwolf"] = "12"
    };

    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "der", "die", "das", "le", "la", "les", "il", "el"
    };

    /// <summary>Lower-case word tokens without accents or punctuation.</summary>
    public static List<string> Tokens(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '‘' or '`' or '´')
            {
                continue;
            }

            if (c is '&' or '+')
            {
                sb.Append(" and ");
            }
            else if (c == 'ß')
            {
                sb.Append("ss");
            }
            else if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(' ');
            }
        }

        foreach (var t in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(TokenMap.TryGetValue(t, out var mapped) ? mapped : t);
        }

        return result;
    }

    /// <summary>The comparable key of a title: tokens joined by a space, without a leading article.</summary>
    public static string Key(string? text)
    {
        var tokens = Tokens(text);
        if (tokens.Count > 1 && Articles.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        return string.Join(' ', tokens);
    }

    /// <summary>All keys a movie or series is known by: name, original title, and both without their subtitle.</summary>
    public static List<string> TitleVariants(params string?[] titles)
    {
        var keys = new List<string>();
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            Add(keys, Key(title));
            var m = SubtitleSplit().Match(title);
            if (m.Success)
            {
                Add(keys, Key(m.Groups[1].Value));
                Add(keys, Key(m.Groups[2].Value));
            }
        }

        return keys;

        static void Add(List<string> list, string key)
        {
            // very short keys ("it", "up") would match far too much
            if (key.Length >= 3 && !list.Contains(key))
            {
                list.Add(key);
            }
        }
    }

    /// <summary>
    /// True if a book title fits one of the title keys: equal, or one contains the other on word
    /// boundaries ("fellowship of the ring" in "lord of the rings fellowship of the ring").
    /// </summary>
    public static bool TitleFits(string bookTitle, IReadOnlyList<string> titleKeys, bool allowContainment)
    {
        var stripped = StripDecoration(bookTitle);
        var book = Key(stripped);
        if (book.Length < 3)
        {
            return false;
        }

        // the book's own title, or its main title before a subtitle ("Dune: Der Wüstenplanet" -> "dune");
        // a longer title without a separator is another book ("Dune Messiah")
        var bookKeys = new List<string> { book };
        var m = SubtitleSplit().Match(stripped);
        if (m.Success)
        {
            bookKeys.Add(Key(m.Groups[1].Value));
        }

        // a volume of a series: "Witch Hat Atelier, Vol. 1", "Witch Hat Atelier 3", "Atelier of Witch Hat, Band 2"
        if (VolumeBase(stripped) is { } vb)
        {
            bookKeys.Add(vb);
        }

        foreach (var key in titleKeys)
        {
            if (bookKeys.Contains(key))
            {
                return true;
            }

            // the book is part of the movie's title: "Fellowship of the Ring" in "The Lord of the Rings: The Fellowship of
            // the Ring", "Dune" in "Dune: Part Two" (only with a confirmed author)
            if (allowContainment && book.Length >= 4 && ContainsWords(key, book))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes "(Roman)", "[Hörbuch]", ": A Novel", series numbering and similar decoration from a book title.</summary>
    public static string StripDecoration(string? title)
    {
        var s = Bracketed().Replace(title ?? string.Empty, " ");
        s = NovelSuffix().Replace(s, string.Empty);
        return s.Trim();
    }

    /// <summary>
    /// True if two person names are the same person: "J. R. R. Tolkien", "J.R.R. Tolkien" and
    /// "Tolkien, J. R. R." all match; so do "Frank Herbert" and "Herbert, Frank".
    /// </summary>
    public static bool SameAuthor(string? a, string? b)
    {
        var ta = NameTokens(a);
        var tb = NameTokens(b);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return false;
        }

        if (string.Concat(ta) == string.Concat(tb))
        {
            return true;
        }

        // the same names in another order: "Liu Cixin" (family name first, as TMDB lists it) == "Cixin Liu"
        if (ta.Count >= 2 && ta.Count == tb.Count && ta.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(tb.OrderBy(x => x, StringComparer.Ordinal)))
        {
            return true;
        }

        // last names must agree, and the other parts must not contradict (initials vs. full first names)
        var lastA = ta[^1];
        var lastB = tb[^1];
        if (lastA != lastB || lastA.Length < 2)
        {
            return false;
        }

        var restA = ta.Take(ta.Count - 1).ToList();
        var restB = tb.Take(tb.Count - 1).ToList();
        if (restA.Count == 0 || restB.Count == 0)
        {
            return false;
        }

        return restA[0][0] == restB[0][0];
    }

    /// <summary>The last name ("Tolkien" for "J.R.R. Tolkien" and "Tolkien, J. R. R."), for searching: book servers
    /// match names literally, and "J. R. R. Tolkien" would not find "J.R.R. Tolkien".</summary>
    public static string? Surname(string? name)
    {
        var tokens = NameTokens(name);
        return tokens.Count > 0 && tokens[^1].Length >= 3 ? tokens[^1] : null;
    }

    /// <summary>
    /// A title without a leading article or number, for searching: "3 Body Problem" -> "Body Problem",
    /// "The Name of the Rose" -> "Name of the Rose". Null when nothing distinctive is left.
    /// </summary>
    public static string? CoreQuery(string title)
    {
        var m = LeadingWord().Match(title.Trim());
        if (!m.Success)
        {
            return null;
        }

        var rest = title.Trim()[m.Length..].Trim();
        return rest.Length >= 6 && rest.Contains(' ', StringComparison.Ordinal) ? rest : null;
    }

    /// <summary>The key of a title without a trailing volume number ("Witch Hat Atelier, Vol. 3" -> "witch hat atelier"), or null.</summary>
    public static string? VolumeBase(string? title)
    {
        var s = StripDecoration(title);
        var m = VolumeSuffix().Match(s);
        if (!m.Success || m.Index == 0)
        {
            return null;
        }

        var key = Key(s[..m.Index]);
        return key.Length >= 3 ? key : null;
    }

    /// <summary>"3 Body Problem" -> "Three Body Problem" (1 to 12 as English words).</summary>
    public static string SpellNumbers(string text)
    {
        string[] words = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve" };
        return StandaloneNumber().Replace(text, m => int.TryParse(m.Value, out var n) && n >= 1 && n <= 12 ? words[n] : m.Value);
    }

    /// <summary>True if one of the words of a name is the given word ("cixin" for "Cixin Liu").</summary>
    public static bool NameHasWord(string? name, string? word)
    {
        var w = Key(word);
        return w.Length >= 3 && NameTokens(name).Contains(w);
    }

    private static List<string> NameTokens(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new List<string>();
        }

        var n = name.Trim();
        var comma = n.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && comma < n.Length - 1)
        {
            n = n[(comma + 1)..] + " " + n[..comma]; // "Tolkien, J. R. R." -> "J. R. R. Tolkien"
        }

        // "J.R.R." -> "j r r"
        n = n.Replace(".", ". ", StringComparison.Ordinal);
        return Tokens(n);
    }

    private static bool ContainsWords(string haystack, string needle)
    {
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            var startOk = i == 0 || haystack[i - 1] == ' ';
            var end = i + needle.Length;
            var endOk = end == haystack.Length || haystack[end] == ' ';
            if (startOk && endOk)
            {
                return true;
            }

            i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal);
        }

        return false;
    }

    [GeneratedRegex(@"^(?i:the|a|an|der|die|das|le|la|les|\d{1,3}|one|two|three|four|five|six|seven|eight|nine|ten|eins|zwei|drei|vier)[\s\-]+")]
    private static partial Regex LeadingWord();

    [GeneratedRegex(@"(?i)(?:\s*[,:\-–—]?\s*\b(?:vol(?:ume)?|band|bd|tome|t|book|teil|part|nr|no)\b\.?\s*|\s+#?\s*|\s*,\s*)\d{1,3}\s*$")]
    private static partial Regex VolumeSuffix();

    [GeneratedRegex(@"(?<![\w.])\d{1,2}(?![\w.])")]
    private static partial Regex StandaloneNumber();

    [GeneratedRegex(@"^(.+?)\s*(?::|\s[-–—]\s)\s*(.+)$")]
    private static partial Regex SubtitleSplit();

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"(?i)\s*[:\-–—]\s*(?:a\s+novel|roman|ein\s+roman|novel|unabridged|ungekürzt)\s*$")]
    private static partial Regex NovelSuffix();
}
