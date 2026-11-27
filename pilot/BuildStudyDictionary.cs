using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LLPlayer.Services;
using Microsoft.VisualBasic.FileIO;

public static class BuildStudyDictionary
{
    private static string Field(string field) => field.Trim().Replace("\t", " ").Replace("\r\n", "\\n")
        .Replace("\r", "\\n").Replace("\n", "\\n");

    public static void Run(string csv, string output)
    {
        using var parser = new TextFieldParser(csv, Encoding.UTF8);
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        var header = parser.ReadFields() ?? throw new InvalidDataException("Missing ECDICT header.");
        int Column(string name) => Array.IndexOf(header, name) is var index && index >= 0 ? index
            : throw new InvalidDataException($"Missing field: {name}");

        int wordAt = Column("word"), phoneticAt = Column("phonetic"), translationAt = Column("translation"), exchangeAt = Column("exchange");
        int requiredField = Math.Max(Math.Max(wordAt, phoneticAt), Math.Max(translationAt, exchangeAt));
        var buckets = new Dictionary<string, List<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int count = 0, malformed = 0;
        while (!parser.EndOfData)
        {
            string[]? row;
            try { row = parser.ReadFields(); }
            catch (MalformedLineException) { malformed++; continue; }
            if (row is null || row.Length <= requiredField) { malformed++; continue; }
            var word = Field(row[wordAt]);
            var key = StudyDictionary.NormalizeHeadword(word);
            var translation = Field(row[translationAt]);
            if (key.Length is 0 or > 128 || key != StudyDictionary.Key(word) || translation.Length is 0 or > 4096 || !seen.Add(key)) continue;
            var source = row[exchangeAt].Split('/').FirstOrDefault(v => v.StartsWith("0:", StringComparison.Ordinal));
            var lemma = source is null ? "" : StudyDictionary.Key(Field(source[2..]));
            var bucket = StudyDictionary.Bucket(key);
            if (!buckets.TryGetValue(bucket, out var entries)) buckets[bucket] = entries = [];
            entries.Add(string.Join('\t', key, word, lemma, Field(row[phoneticAt]), translation));
            count++;
        }

        if (count == 0) throw new InvalidDataException("No usable ECDICT entries.");
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (bucket, rows) in buckets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(bucket + ".tsv", CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)) { NewLine = "\n" };
            foreach (var row in rows) writer.WriteLine(row);
        }
        Console.WriteLine($"Dictionary: {count} searchable entries in {buckets.Count} buckets; malformed CSV records skipped: {malformed}");
    }
}
