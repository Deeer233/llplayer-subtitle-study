using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OriginalReview;

public static class ReviewChecks
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    public static void Run(string root, string dictionary)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Test path must not exist.");
        Directory.CreateDirectory(root);
        var data = Path.Combine(root, "store");
        var today = new DateOnly(2026, 11, 24);
        var txt = Path.Combine(root, "words.txt");
        File.WriteAllText(txt, "Bail   on\n\nbail on\n1234\n", new UTF8Encoding(true));
        var list = WordImport.Read(txt);
        Check(list.Entries.Count == 2 && list.Invalid == 1 && list.Entries[0].Word == "Bail on", "TXT BOM, whitespace, blank and invalid rows");

        var csv = Path.Combine(root, "words.csv");
        File.WriteAllText(csv, "释义,单词,例句,整句中文\r\n保释,bail,He is out on bail.,他获准保释。\r\n放鸽子,bail,\"Sorry, I bailed on you.\",\"对不起，\"\"我放你鸽子了。\"\"\"\r\n缺词,,例句\r\n");
        var table = WordImport.Read(csv);
        Check(table.Entries.Count == 2 && table.Invalid == 1 && table.Entries[1].English == "Sorry, I bailed on you."
            && table.Entries[1].Chinese == "对不起，\"我放你鸽子了。\"", "CSV named reordered columns, quoted comma and escaped quotes");
        var tsv = Path.Combine(root, "words.tsv");
        File.WriteAllText(tsv, "walk away\t离开\tHe walked away.\t他走开了。\n", Encoding.Unicode);
        var tab = WordImport.Read(tsv);
        Check(tab.Entries is [{ Word: "walk away", Meaning: "离开" }], "headerless TSV and UTF-16 BOM");
        var bad = WordImport.Parse(new StringReader("word,meaning\na,b\nc,\"broken\"text\nd,e\n"), ".csv");
        Check(bad.Invalid == 1 && bad.Entries.Select(e => e.Word).SequenceEqual(["a", "d"]), "malformed CSV row does not hide following valid rows");

        var store = new ReviewStore(data);
        Check(store.Import(list.Entries, today) == (1, 1), "case-insensitive duplication within one import");
        Check(store.Import(table.Entries, today) == (2, 0) && store.Import(table.Entries, today) == (0, 2), "reimport is idempotent; same spelling keeps distinct senses");
        Check(store.Import(tab.Entries, today) == (1, 0), "headerless TSV imported");
        store = new ReviewStore(data);
        Check(store.Cards.Count == 4 && store.Due(today, false).Count == 3 && store.Due(today, true).Count == 0,
            "reopen with word-only stored but not quizzed before meaning is filled");

        var source = Path.Combine(root, "original.wav");
        WriteWave(source, 440);
        var original = File.ReadAllBytes(source);
        var entry = table.Entries[1];
        try { store.Add(entry, source, false, today); throw new Exception("Unverified! "); }
        catch (InvalidDataException) { Check(store.Due(today, true).Count == 0, "unverified audio card rejected"); }
        store.Add(entry, source, true, today);
        var moved = Path.Combine(root, "moved.wav");
        File.Move(source, moved);
        store = new ReviewStore(data);
        var audio = store.Due(today, true).Single();
        Check(!File.Exists(source) && original.SequenceEqual(File.ReadAllBytes(store.AudioPath(store.ExampleFor(audio, false)!)!)), "copied audio survives source move and reboot");
        Check(store.Due(today, false).All(c => c.Audio is null), "audio and text queues stay separate");
        audio = store.Grade(audio, today, true, true, store.ExampleFor(audio, false));
        Check(audio.Heard == 0 && audio.Due == today.AddDays(1) && audio.Retry == today, "hint never counts as independent; same-day retry plus tomorrow");
        store = new ReviewStore(data);
        audio = store.Due(today, true).Single();
        Check(audio.Retry == today, "retry survives restart");
        store.FinishRetry(audio);
        Check(store.Due(today, true).Count == 0, "retry clears for the day without changing tomorrow's due date");
        audio = store.Due(today.AddDays(1), true).Single();
        var first = store.Grade(audio, today.AddDays(1), true, false, store.ExampleFor(audio, false));
        var second = store.Grade(first, first.Due, true, false, store.ExampleFor(first, false));
        var third = store.Grade(second, second.Due, true, false, store.ExampleFor(second, false));
        Check(first.Due == today.AddDays(2) && second.Due == first.Due.AddDays(3) && third.Due == second.Due.AddDays(7)
            && third.Heard == 3 && third.Missed == 1, "independent outcomes schedule +1/+3/+7");
        store.Edit(store.Cards.Single(c => c.Entry.Meaning.Length == 0), new("bail on", "临时撇下"));
        Check(new ReviewStore(data).Cards.Any(c => c.Entry.Word == "bail on" && c.Entry.Meaning == "临时撇下"), "word-only card can be completed later");
        RunDictionary(root, dictionary, today);
        RunRotation(root, today, moved);
        Console.WriteLine("Test fixture: " + root);
    }

    private static void WriteWave(string path, int frequency)
    {
        using var wave = new BinaryWriter(File.Open(path, FileMode.CreateNew));
        wave.Write("RIFF"u8); wave.Write(36 + 16000);
        wave.Write("WAVEfmt "u8); wave.Write(16); wave.Write((short)1); wave.Write((short)1);
        wave.Write(8000); wave.Write(16000); wave.Write((short)2); wave.Write((short)16);
        wave.Write("data"u8); wave.Write(16000);
        for (var i = 0; i < 8000; i++) wave.Write((short)(Math.Sin(i * Math.PI * 2 * frequency / 8000) * 2048));
    }

    private static void RunRotation(string root, DateOnly today, string firstClip)
    {
        var path = Path.Combine(root, "rotation-store");
        var secondClip = Path.Combine(root, "second-original.wav");
        var secondMoved = Path.Combine(root, "second-moved.wav");
        WriteWave(secondClip, 660);
        var store = new ReviewStore(path);
        var main = new Entry("bail", "临时放鸽子", "I won't bail again.", "我不会再放鸽子了。");
        var other = new Entry("bail", "保释", "She was released on bail.", "她获准保释。");
        var alternate = main with { English = "Don't bail on us.", Chinese = "别放我们鸽子。" };
        store.Add(main, firstClip, true, today);
        store.Add(other, firstClip, true, today);
        var before = store.Cards.Single(c => c.Entry == main);
        var first = store.ExampleFor(before, false)!;
        before = store.Grade(before, today, true, false, first);
        void Rejected(Action action, string label)
        {
            try { action(); throw new Exception(label); }
            catch (InvalidDataException) { Check(true, label); }
        }
        Rejected(() => store.AddExample(before.Id, alternate, secondClip, false), "unverified alternate is not admitted");
        Rejected(() => store.AddExample(before.Id, other, secondClip, true), "same spelling with another sense cannot attach to this card");
        Rejected(() => store.AddExample(before.Id, main with { English = " I  WON’T BAIL AGAIN. " }, secondClip, true),
            "normalized duplicate transcript does not add another example");

        store.AddExample(before.Id, alternate, secondClip, true);
        var bytes = File.ReadAllBytes(secondClip);
        File.Move(secondClip, secondMoved);
        store = new ReviewStore(path);
        var withAlternate = store.Cards.Single(c => c.Id == before.Id);
        Check(store.Cards.Count == 2 && withAlternate.Examples.Count() == 2 && withAlternate.Due == before.Due &&
            withAlternate.Heard == before.Heard && withAlternate.Streak == before.Streak && withAlternate.LastExample == first.Id,
            "adding one alternate keeps the card, its schedule and recall history");
        var next = store.ExampleFor(withAlternate, false)!;
        Check(next.Id != first.Id && next.English == alternate.English && next.Chinese == alternate.Chinese &&
            !File.Exists(secondClip) && bytes.SequenceEqual(File.ReadAllBytes(store.AudioPath(next)!)) &&
            !bytes.SequenceEqual(File.ReadAllBytes(store.AudioPath(first)!)), "next due review pairs new audio with its own bilingual text after restart");
        var otherCard = store.Cards.Single(c => c.Entry == other);
        Rejected(() => store.Grade(otherCard, today, true, false, next), "a review result cannot use another sense's example");
        Rejected(() => store.Edit(withAlternate, main with { Meaning = "保释" }), "reviewed group cannot silently change its meaning");

        var repeat = store.Grade(withAlternate, before.Due, false, false, next);
        store = new ReviewStore(path);
        repeat = store.Due(before.Due, true).Single(c => c.Id == before.Id);
        Check(store.ExampleFor(repeat, true) == next, "same-day retry replays the same example after restart");
        store.FinishRetry(repeat);
        var tomorrow = store.Due(before.Due.AddDays(1), true).Single(c => c.Id == before.Id);
        Check(store.ExampleFor(tomorrow, false) == first, "next scheduled review rotates back to the other sentence");

        var missingCopy = store.AudioPath(first)!;
        var parkedCopy = Path.Combine(root, "first-copy-parked.wav");
        File.Move(missingCopy, parkedCopy);
        Check(store.ExampleFor(tomorrow, false) == next, "missing cached audio falls back to the remaining example");
        File.Move(parkedCopy, missingCopy);
        store.Remove(tomorrow);
        Check(store.Cards.Single().Id == otherCard.Id && File.Exists(firstClip) && File.Exists(secondMoved) && File.Exists(missingCopy)
            && File.Exists(store.AudioPath(next)), "removing a multi-example card keeps audio copies and leaves the other sense untouched");
    }

    private static void RunDictionary(string root, string dictionary, DateOnly today)
    {
        var path = Path.Combine(root, "dictionary-store");
        Directory.CreateDirectory(path);
        using (var legacy = File.Open(Path.Combine(path, "cards.json"), FileMode.CreateNew))
        using (var writer = new StreamWriter(legacy))
            writer.Write("[{\"Id\":\"aec00973-edf7-4680-b7fc-54bd20fb3a49\",\"Entry\":{\"Word\":\"walk away\",\"Meaning\":\"\",\"English\":\"\",\"Chinese\":\"\"},\"Due\":\"2026-11-24\"}]");
        var store = new ReviewStore(path);
        var legacyId = store.Cards.Single().Id;
        Check(ReviewStore.PendingDictionary(store.Cards.Single()), "legacy data without the provenance field still opens");

        const string missing = "zzqvx unicorn schema phrasal verb";
        var found = ReviewDictionary.Lookup(dictionary, ["walk away", "(WALK  AWAY!)", missing, "live"]);
        Check(found.TryGetValue("walk away", out var gloss) && gloss == found["(WALK  AWAY!)"] && gloss.Length > 0
            && found["live"].Contains("居住"), "packaged dictionary matches normalized exact words and multiword expressions");
        Check(!found.ContainsKey(missing), "unknown phrase does not fall back to a partial hit");
        try { ReviewDictionary.Lookup(dictionary + ".absent", ["live"]); throw new Exception("Missing must throw"); }
        catch (FileNotFoundException) { Check(true, "missing bundle reports a file error"); }

        store.Import([new(missing), new("live", "播出", "We are live."), new("live", "住"), new("walk away", "", "Just walk away."),
            new("long"), new("blank")], today);
        found.Add("long", new string('字', ReviewStore.MeaningLimit + 1));
        found.Add("blank", " ");
        Check(store.ApplyDictionary(found, today) == 1, "only blank text cards without context receive a usable reference");
        store = new ReviewStore(path);
        var filled = store.Cards.Single(c => c.Id == legacyId);
        Check(filled.Entry.DictionaryReference && filled.Entry.Meaning == gloss && ReviewStore.DisplayMeaning(filled.Entry).StartsWith("词典义：")
            && store.Due(today, false).Any(c => c.Id == legacyId) && store.Due(today, true).Count == 0,
            "provenance persists and the enriched card enters the text queue only");
        Check(store.Cards.Where(c => c.Entry.Word == "live").Select(c => c.Entry.Meaning).SequenceEqual(["播出", "住"])
            && store.Cards.Where(c => c.Id != legacyId && c.Entry.Word != "live").All(c => c.Entry.Meaning == ""),
            "existing distinct senses, context, missing and oversized matches are untouched");
        Check(store.Import([new("WALK AWAY")], today) == (0, 1) && store.ApplyDictionary(found, today) == 0,
            "repeat import and repeat fill do not recreate or overwrite the enriched card");
        try
        {
            store.Edit(filled, filled.Entry with { English = "Just walk away." });
            throw new Exception("Context cannot reuse dictionary provenance");
        }
        catch (InvalidDataException) { Check(true, "a dictionary reference cannot be stored as a contextual answer"); }
    }
}
