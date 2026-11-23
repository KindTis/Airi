using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Airi.Domain;

namespace Airi.Web;

internal static partial class JapaneseActorNames
{
    private static readonly IReadOnlyDictionary<char, char> Syllables = new[]
    {
        ("あいうえお", "아이우에오"), ("かきくけこ", "카키쿠케코"),
        ("がぎぐげご", "가기구게고"), ("さしすせそ", "사시스세소"),
        ("ざじずぜぞ", "자지즈제조"), ("たちつてと", "타치츠테토"),
        ("だぢづでど", "다지즈데도"), ("なにぬねの", "나니누네노"),
        ("はひふへほ", "하히후헤호"), ("ばびぶべぼ", "바비부베보"),
        ("ぱぴぷぺぽ", "파피푸페포"), ("まみむめも", "마미무메모"),
        ("やゆよ", "야유요"), ("らりるれろ", "라리루레로"),
        ("わゐゑを", "와이에오"), ("ぁぃぅぇぉ", "아이우에오"), ("ゔ", "부")
    }.SelectMany(row => row.Item1.Zip(row.Item2)).ToDictionary(pair => pair.First, pair => pair.Second);

    private static readonly IReadOnlyDictionary<char, string> Yoon = new Dictionary<char, string>
    {
        ['き'] = "캬큐쿄", ['ぎ'] = "갸규교", ['し'] = "샤슈쇼", ['じ'] = "자주조",
        ['ち'] = "차추초", ['ぢ'] = "자주조", ['に'] = "냐뉴뇨", ['ひ'] = "햐휴효",
        ['び'] = "뱌뷰뵤", ['ぴ'] = "퍄퓨표", ['み'] = "먀뮤묘", ['り'] = "랴류료"
    };

    public static ActorName Parse(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new(text);
        if (parts.Length == 1) return Kana().IsMatch(text) ? WithReading(text, text) : new(text);
        var reading = parts[^1];
        if (!Kana().IsMatch(reading)) return new(text);
        var name = string.Join(' ', parts[..^1]);
        // Some cards put an initial before the name as part of the link text.
        if (parts.Length == 3 && parts[0].Length == 1 && parts[1].StartsWith(parts[0], StringComparison.Ordinal)) name = parts[1];
        else if (parts.Length > 2 && !name.All(character => char.IsAsciiLetter(character) || character is ' ' or '-' or '.')) return new(text);
        return WithReading(name, reading);
    }

    private static ActorName WithReading(string name, string reading)
    {
        var kana = Hiragana(reading);
        var suffix = KanaSuffix().Match(Hiragana(name));
        if (suffix.Success && suffix.Index > 0 && kana.EndsWith(suffix.Value, StringComparison.Ordinal) && suffix.Length < kana.Length)
            kana = kana.Insert(kana.Length - suffix.Length, " ");
        var korean = Transcribe(kana);
        return new(name, reading, korean);
    }

    private static string Hiragana(string value) => new(value.Normalize(NormalizationForm.FormKC)
        .Select(character => character is >= 'ァ' and <= 'ヶ' ? (char)(character - 0x60) : character).ToArray());

    private static string? Transcribe(string kana)
    {
        var result = new StringBuilder();
        for (var index = 0; index < kana.Length; index++)
        {
            var current = kana[index];
            if (current is ' ' or '・') { result.Append(' '); continue; }
            if (current == 'ー') continue;
            if (current is 'ん' or 'っ')
            {
                if (result.Length == 0 || result[^1] is < '가' or > '힣' || (result[^1] - '가') % 28 != 0) return null;
                result[^1] = (char)(result[^1] + (current == 'ん' ? 4 : 19));
                continue;
            }
            if (index + 1 < kana.Length && "ゃゅょ".IndexOf(kana[index + 1]) is var small && small >= 0 && Yoon.TryGetValue(current, out var combined))
            {
                result.Append(combined[small]);
                index++;
                continue;
            }
            if (!Syllables.TryGetValue(current, out var syllable)) return null;
            if (current is 'う' or 'お' && result.Length > 0 && result[^1] is >= '가' and <= '힣'
                && (result[^1] - '가') % 28 == 0 && (result[^1] - '가') / 28 % 21 == 8) continue;
            result.Append(syllable);
        }
        return result.Length == 0 ? null : result.ToString();
    }

    [GeneratedRegex(@"^[\u3041-\u3096\u30A1-\u30FAー・]+$")]
    private static partial Regex Kana();
    [GeneratedRegex(@"[\u3041-\u3096ー]+$")]
    private static partial Regex KanaSuffix();
}
