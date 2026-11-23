using System;
using System.IO;
using System.Text.RegularExpressions;
using Airi.Domain;

namespace Airi.Web;

public sealed partial record MetadataProductCode(string Value, bool IsUncensored)
{
    public bool Matches(string? value)
    {
        var match = MatchWholeCode(value);
        if (match is null || match.Groups["edition"].Success) return false;
        var code = Parse(match);
        return (!IsUncensored || code.IsUncensored) &&
            string.Equals(code.Value, Value, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsCodeShaped(string? value) => MatchWholeCode(value) is not null;

    private static Match? MatchWholeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        var match = CodePattern().Match(value);
        return match.Success && match.Length == value.Length ? match : null;
    }

    internal static MetadataProductCode? FromEntry(VideoEntry entry)
    {
        var fileCode = Find(Path.GetFileNameWithoutExtension(entry.Path), out var fileHasCode);
        return fileHasCode ? fileCode : Find(entry.ProductCode, out _) ?? Find(entry.Meta.Title, out _);
    }

    private static MetadataProductCode? Find(string? text, out bool hasCode)
    {
        hasCode = false;
        MetadataProductCode? code = null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (Match match in CodePattern().Matches(text))
        {
            hasCode = true;
            if (match.Groups["edition"].Success) return null;
            var next = Parse(match);
            if (code is not null && !string.Equals(code.Value, next.Value, StringComparison.Ordinal)) return null;
            if (code is null || next.IsUncensored) code = next;
        }
        return code;
    }

    private static MetadataProductCode Parse(Match match)
    {
        if (match.Groups["uncensored"].Success)
            return new(match.Value.ToUpperInvariant().Replace('_', '-'), true);

        var prefix = match.Groups["prefix"].Value.ToUpperInvariant();
        return new($"{prefix}-{match.Groups["number"].Value}", false);
    }

    [GeneratedRegex("""
        (?<![A-Z0-9])
        (?:
            (?<uncensored>HEYZO-[0-9]{4,5}|FC2-PPV-[0-9]{5,7}|[0-9]{6}[-_][0-9]{3}|[NK][0-9]{4,5})(?<edition>[A-Z]+)?
            |
            (?<!FC2-)(?<prefix>[A-Z]{2,6})
            (?:
                (?:[-_]|[ ]+)(?<number>[0-9]{2,5})(?<edition>[A-Z]+)?
                |
                (?<number>[0-9]{3,5})
            )
        )
        (?![A-Z0-9])
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex CodePattern();
}
