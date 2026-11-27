using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace OriginalReview;

internal record ImportBatch(List<Entry> Entries, int Invalid, List<string> Errors);

internal static class WordImport
{
    internal static ImportBatch Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("词表不能超过 4 MB。");
        using var text = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return Parse(text, Path.GetExtension(path).ToLowerInvariant());
    }

    private static string? Column(string name) => name.Trim().ToLowerInvariant() switch
    {
        "word" or "term" or "expression" or "单词" or "词语" or "表达" => "word",
        "meaning" or "definition" or "释义" or "词义" => "meaning",
        "example" or "sentence" or "english" or "英文" or "例句" or "英文原句" => "example",
        "translation" or "chinese" or "译文" or "整句中文" => "translation",
        _ => null
    };

    internal static ImportBatch Parse(TextReader text, string extension)
    {
        var entries = new List<Entry>();
        var errors = new List<string>();
        var invalid = 0;
        void Invalid(long line, string reason)
        {
            invalid++;
            if (errors.Count < 5) errors.Add($"第 {line} 行：{reason}");
        }
        void Add(long line, Entry input)
        {
            try { entries.Add(ReviewStore.Clean(input)); }
            catch (InvalidDataException e) { Invalid(line, e.Message); }
        }

        if (extension == ".txt")
        {
            for (var line = 1; text.ReadLine() is { } value; line++)
                if (!string.IsNullOrWhiteSpace(value)) Add(line, new(value));
        }
        else if (extension is ".csv" or ".tsv")
        {
            using var parser = new TextFieldParser(text) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
            parser.SetDelimiters(extension == ".csv" ? "," : "\t");
            string?[]? columns = null;
            var first = true;
            while (!parser.EndOfData)
            {
                var line = parser.LineNumber;
                string[] fields;
                try { fields = parser.ReadFields() ?? []; }
                catch (MalformedLineException) { Invalid(line, "分隔符或引号格式错误。"); first = false; continue; }
                if (fields.Length == 0 || fields.All(string.IsNullOrWhiteSpace)) continue;
                if (first)
                {
                    first = false;
                    var names = fields.Select(Column).ToArray();
                    if (names.Contains("word") && (names.Length == 1 || names.Skip(1).Any(n => n is not null) || names[0] is null))
                    {
                        if (names.Where(n => n is not null).GroupBy(n => n).Any(g => g.Count() > 1))
                            throw new InvalidDataException("词表表头包含重复列名。");
                        columns = names;
                        continue;
                    }
                }
                if (fields.Length > (columns?.Length ?? 4)) { Invalid(line, "列数与格式不符。"); continue; }
                string Value(string name, int fallback)
                {
                    var index = columns is null ? fallback : Array.IndexOf(columns, name);
                    return index >= 0 && index < fields.Length ? fields[index] : "";
                }
                Add(line, new(Value("word", 0), Value("meaning", 1), Value("example", 2), Value("translation", 3)));
            }
        }
        else throw new InvalidDataException("仅支持 TXT、CSV、TSV 词表。");
        return new(entries, invalid, errors);
    }
}
