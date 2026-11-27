using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace OriginalReview;

internal static class ReviewDictionary
{
    internal const string FileName = "StudyDictionary.zip";
    internal static string Bundled => Path.Combine(AppContext.BaseDirectory, FileName);
    private static readonly Regex Space = new(@"\s+");
    private static readonly Regex Edge = new(@"^[^a-z0-9']+|[^a-z0-9']+$");
    private static readonly Regex Han = new(@"[\u3400-\u9fff]");

    private static string Key(string word) => Edge.Replace(Space.Replace(word.Trim().Replace('\u2019', '\'').ToLowerInvariant(), " "), "");
    private static string Bucket(string key) => key.Length >= 2 && char.IsAsciiLetterLower(key[0]) && char.IsAsciiLetterLower(key[1]) ? key[..2] : "other";

    internal static Dictionary<string, string> Lookup(string path, IEnumerable<string> words, CancellationToken token = default)
    {
        var source = words.Distinct(StringComparer.Ordinal).Select(w => (Word: w, Key: Key(w)))
            .Where(w => w.Key.Length is > 0 and <= 128).ToArray();
        if (source.Length == 0) return new(StringComparer.Ordinal);
        var matches = new Dictionary<string, string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        foreach (var bucket in source.Select(w => w.Key).Distinct(StringComparer.Ordinal).GroupBy(Bucket))
        {
            token.ThrowIfCancellationRequested();
            var member = archive.GetEntry(bucket.Key + ".tsv");
            if (member is null) continue;
            if (member.Length > 16 * 1024 * 1024) throw new InvalidDataException("词典分区超过大小限制。");
            var pending = bucket.ToHashSet(StringComparer.Ordinal);
            using var reader = new StreamReader(member.Open(), Encoding.UTF8);
            while (pending.Count > 0 && reader.ReadLine() is { } row)
            {
                token.ThrowIfCancellationRequested();
                var tab = row.IndexOf('\t');
                if (tab < 1) continue;
                var key = row[..tab];
                if (!pending.Remove(key)) continue;
                var fields = row.Split('\t', 5);
                if (fields.Length != 5) throw new InvalidDataException("词典条目格式错误。");
                var gloss = fields[4].Replace("\\n", "\n").Trim();
                if (Han.IsMatch(gloss)) matches.Add(key, gloss);
            }
        }
        return source.Where(w => matches.ContainsKey(w.Key)).ToDictionary(w => w.Word, w => matches[w.Key], StringComparer.Ordinal);
    }
}
