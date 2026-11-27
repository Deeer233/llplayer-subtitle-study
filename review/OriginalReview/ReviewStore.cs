using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OriginalReview;

internal record Entry(string Word, string Meaning = "", string English = "", string Chinese = "", bool DictionaryReference = false);
internal record AudioExample(Guid Id, string English, string Chinese, string Audio);
internal record Card(Guid Id, Entry Entry, string? Audio, DateOnly Due, int Streak = 0,
    int Heard = 0, int Missed = 0, DateOnly? Retry = null, AudioExample[]? Alternates = null, Guid? LastExample = null)
{
    internal IEnumerable<AudioExample> Examples => Audio is null ? [] :
        new[] { new AudioExample(Id, Entry.English, Entry.Chinese, Audio) }.Concat(Alternates ?? []);
}

internal sealed class ReviewStore
{
    private readonly string _root, _cardsFile;
    private List<Card> _cards;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const int AudioLimit = 32 * 1024 * 1024;
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".m4a"];

    internal ReviewStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _cardsFile = Path.Combine(_root, "cards.json");
        _cards = File.Exists(_cardsFile)
            ? JsonSerializer.Deserialize<List<Card>>(File.ReadAllBytes(_cardsFile), Json)
                ?? throw new InvalidDataException("复习数据为空。") : [];
    }

    internal IReadOnlyList<Card> Cards => _cards;
    internal static Entry Clean(Entry entry)
    {
        var word = Regex.Replace(entry.Word.Trim(), @"\s+", " ").Replace('’', '\'');
        if (word.Length is 0 or > 120 || !word.Any(char.IsAsciiLetter) || word.Any(char.IsControl))
            throw new InvalidDataException("表达需包含英文字母，且不超过 120 个字符。");
        var result = new Entry(word, entry.Meaning.Trim(), entry.English.Trim(), entry.Chinese.Trim(), entry.DictionaryReference);
        if (result.Meaning.Length > MeaningLimit || result.English.Length > 1000 || result.Chinese.Length > 1000)
            throw new InvalidDataException("释义不能超过 400 字，例句和翻译各不能超过 1,000 字。");
        if (result.DictionaryReference && (result.Meaning.Length == 0 || HasContext(result)))
            throw new InvalidDataException("词典义只能作为无例句的文字卡释义。");
        return result;
    }

    internal const int MeaningLimit = 400;
    internal static bool HasContext(Entry entry) => entry.English.Length > 0 || entry.Chinese.Length > 0;
    internal static bool PendingDictionary(Card card) => card.Audio is null && card.Entry.Meaning.Length == 0 && !HasContext(card.Entry);
    internal static string DisplayMeaning(Entry entry) => entry.DictionaryReference ? "词典义：" + entry.Meaning : entry.Meaning;
    private static (string, string, string) Key(Entry e) => (e.Word.ToUpperInvariant(), e.Meaning, e.English.ToUpperInvariant());

    internal (int Added, int Duplicates) Import(IEnumerable<Entry> entries, DateOnly today)
    {
        var updated = _cards.ToList();
        var keys = _cards.Where(c => c.Audio is null).Select(c => Key(c.Entry)).ToHashSet();
        var knownWords = _cards.Where(c => c.Audio is null && c.Entry.Meaning.Length > 0 && !HasContext(c.Entry))
            .Select(c => Key(c.Entry).Item1).ToHashSet();
        var duplicates = 0;
        foreach (var row in entries)
        {
            var entry = Clean(row);
            var key = Key(entry);
            if ((entry.Meaning.Length == 0 && !HasContext(entry) && knownWords.Contains(key.Item1)) || !keys.Add(key))
            { duplicates++; continue; }
            if (entry.Meaning.Length > 0 && !HasContext(entry)) knownWords.Add(key.Item1);
            updated.Add(new(Guid.NewGuid(), entry, null, today));
        }
        var added = updated.Count - _cards.Count;
        if (added > 0) Save(updated);
        return (added, duplicates);
    }

    internal int ApplyDictionary(IReadOnlyDictionary<string, string> references, DateOnly today)
    {
        var updated = new List<Card>(_cards.Count);
        var keys = _cards.Where(c => c.Audio is null && c.Entry.Meaning.Length > 0).Select(c => Key(c.Entry)).ToHashSet();
        var filled = 0;
        foreach (var card in _cards)
        {
            if (!PendingDictionary(card) || !references.TryGetValue(card.Entry.Word, out var gloss) || gloss.Trim().Length is 0 or > MeaningLimit)
            { updated.Add(card); continue; }
            var entry = Clean(card.Entry with { Meaning = gloss, DictionaryReference = true });
            if (!keys.Add(Key(entry))) { updated.Add(card); continue; }
            updated.Add(card with { Entry = entry, Due = today });
            filled++;
        }
        if (filled > 0) Save(updated);
        return filled;
    }

    internal void Add(Entry row, string? audioPath, bool verified, DateOnly today)
    {
        if (audioPath is null)
        {
            if (Import([row], today).Added == 0) throw new InvalidDataException("这张文字卡已存在。");
            return;
        }
        var entry = Clean(row);
        if (!verified || entry.DictionaryReference || entry.English.Length == 0 || entry.Meaning.Length == 0)
            throw new InvalidDataException("原声卡需要本句义、英文原句，并勾选原句与原声一致。");
        if (_cards.Any(c => SameSense(c.Entry, entry) && ContainsExample(c, entry.English)))
            throw new InvalidDataException("这张原声卡已存在。");
        var id = Guid.NewGuid();
        WithAudio(id, audioPath, filename => Save([.. _cards, new(id, entry, filename, today)]));
    }

    internal void AddExample(Guid cardId, Entry row, string? audioPath, bool verified)
    {
        var card = _cards.SingleOrDefault(c => c.Id == cardId && c.Audio is not null)
            ?? throw new InvalidDataException("原声卡不存在。");
        var entry = Clean(row);
        if (audioPath is null || !verified || entry.English.Length == 0 || entry.DictionaryReference || !SameSense(card.Entry, entry))
            throw new InvalidDataException("需要同一词义的英文原句和原声，并勾选已核对。");
        if (_cards.Any(c => SameSense(c.Entry, entry) && ContainsExample(c, entry.English)))
            throw new InvalidDataException("这个词义已经有这条例句。");
        var id = Guid.NewGuid();
        WithAudio(id, audioPath, filename => Replace(card with
        {
            Alternates = [.. card.Alternates ?? [], new(id, entry.English, entry.Chinese, filename)]
        }));
    }

    private static bool SameSense(Entry a, Entry b) => a.Word.Equals(b.Word, StringComparison.OrdinalIgnoreCase) && a.Meaning == b.Meaning;

    private static bool ContainsExample(Card card, string sentence)
    {
        static string Normalized(string text) => Regex.Replace(text.Trim().Replace('’', '\''), @"\s+", " ");
        var needle = Normalized(sentence);
        return card.Examples.Any(e => Normalized(e.English).Equals(needle, StringComparison.OrdinalIgnoreCase));
    }

    private void WithAudio(Guid id, string audioPath, Action<string> save)
    {
        var extension = Path.GetExtension(audioPath).ToLowerInvariant();
        if (!AudioExtensions.Contains(extension)) throw new InvalidDataException("请选择 WAV、MP3 或 M4A 原声片段。");
        using var source = File.OpenRead(audioPath);
        if (source.Length is 0 or > AudioLimit) throw new InvalidDataException("原声文件必须在 1 字节到 32 MB 之间。");
        var filename = id.ToString("N") + extension;
        var destination = Path.Combine(_root, filename);
        var created = false;
        try
        {
            using (var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                source.CopyTo(output);
            }
            save(filename);
        }
        catch
        {
            if (created) TryDelete(destination);
            throw;
        }
    }

    internal string? AudioPath(AudioExample example) => AudioExtensions.Contains(Path.GetExtension(example.Audio)) &&
        example.Audio == example.Id.ToString("N") + Path.GetExtension(example.Audio) ? Path.Combine(_root, example.Audio) : null;

    internal AudioExample? ExampleFor(Card card, bool retry)
    {
        var examples = card.Examples.ToArray();
        if (examples.Length == 0) return null;
        var index = Array.FindIndex(examples, e => e.Id == card.LastExample);
        var first = retry && index >= 0 ? index : (index + 1) % examples.Length;
        for (var offset = 0; offset < examples.Length; offset++)
        {
            var example = examples[(first + offset) % examples.Length];
            if (AudioPath(example) is string path && File.Exists(path)) return example;
        }
        return examples[first];
    }

    internal void Edit(Card card, Entry row)
    {
        var entry = Clean(row);
        if (_cards.Any(c => c.Id != card.Id && (card.Audio is null
                ? c.Audio is null && Key(c.Entry) == Key(entry)
                : SameSense(c.Entry, entry) && card.Examples.Any(e => ContainsExample(c, e.English)))))
            throw new InvalidDataException("已存在相同的卡片。");
        if (card.Audio is not null && (card.Entry.Word != entry.Word || card.Entry.English != entry.English || entry.Meaning.Length == 0))
            throw new InvalidDataException("原声卡的目标词与原句已核对，不能直接更改。");
        if (card.Alternates is { Length: > 0 } && card.Entry.Meaning != entry.Meaning)
            throw new InvalidDataException("备选原声已按当前词义核对，这张卡不能直接更改词义。");
        var reset = card.Entry.Meaning != entry.Meaning || card.Entry.Word != entry.Word || card.Entry.English != entry.English;
        var updated = reset ? card with { Entry = entry, Due = DateOnly.FromDateTime(DateTime.Today), Streak = 0,
            Heard = 0, Missed = 0, Retry = null, LastExample = null } : card with { Entry = entry };
        Replace(updated);
    }

    internal void Remove(Card card)
    {
        Save(_cards.Where(c => c.Id != card.Id).ToList());
        if (card.Alternates is not { Length: > 0 } && card.Examples.SingleOrDefault() is { } example && AudioPath(example) is string path)
            TryDelete(path);
    }

    internal List<Card> Due(DateOnly today, bool audio) => _cards
        .Where(c => (c.Audio is not null) == audio && c.Entry.Meaning.Length > 0 && (c.Due <= today || c.Retry == today))
        .OrderBy(c => c.Retry == today).ThenBy(c => c.Due).ToList();

    internal Card Grade(Card card, DateOnly today, bool correct, bool hinted, AudioExample? example = null)
    {
        if ((card.Audio is not null) != (example is not null) || (example is not null && !card.Examples.Contains(example)))
            throw new InvalidDataException("当前例句不属于此卡片。");
        var independent = correct && !hinted;
        var streak = independent ? Math.Min(card.Streak + 1, 3) : 0;
        var days = streak switch { 2 => 3, 3 => 7, _ => 1 };
        var updated = card with
        {
            Streak = streak, Due = today.AddDays(days), Heard = card.Heard + (independent ? 1 : 0),
            Missed = card.Missed + (independent ? 0 : 1), Retry = independent ? null : today, LastExample = example?.Id
        };
        Replace(updated);
        return updated;
    }

    internal void FinishRetry(Card card)
        => Replace(card with { Retry = null });

    private void Replace(Card updated) => Save(_cards.Select(c => c.Id == updated.Id ? updated : c).ToList());

    private void Save(List<Card> updated)
    {
        var tmp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = File.Open(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, updated, Json);
            File.Move(tmp, _cardsFile, overwrite: true);
            _cards = updated;
        }
        finally { TryDelete(tmp); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
