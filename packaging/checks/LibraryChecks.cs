using System.IO;
using System.Net.Http;
using System.Text;
using static Program;

internal static class LibraryChecks
{
    internal static object Local(string name, HttpMessageHandler network, HttpMessageHandler? anki = null)
    {
        // Always inject both transports, even if the path being checked is not expected to contact Anki.
        var client = New(Service("AnkiConnectClient"), anki ?? network);
        return New(Service("StudyCloud"), InRun(name), network, false, true, client);
    }

    private static object State(object cloud) => Get(cloud, "Snapshot")!;
    internal static object[] Rows(object cloud) => Items(State(cloud), "Lookups");
    internal static object Row(object cloud, Guid id) => Rows(cloud).Single(r => Get<Guid>(r, "Id") == id);
    internal static object[] CardsOf(object cloud) => Items(State(cloud), "Cards");
    internal static object[] Jobs(object cloud) => Items(State(cloud), "AnkiJobs");

    internal static object Clip(double start, double end, int track = 2)
    {
        var file = new FileInfo(FixtureVideo);
        return New(Service("ClipSource"), file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, track, start, end);
    }

    internal static Guid Observe(object cloud, int index = 0, string? english = null, string source = "Fixture")
        => (Guid)Call(cloud, "Observe", index switch
        {
            0 => Clip(1, 2.1), 1 => Clip(4, 5.1), _ => Clip(5.2, 6.3),
        }, english ?? (index == 1 ? SecondSentence : Sentence), Target, "仅本机译文", source)!;

    private static string Save(object cloud, Guid id, bool manual, string meaning = Meaning, bool keep = false, bool corrected = false)
    {
        var row = Row(cloud, id);
        return Call(cloud, "Verify", id, Get(row, "Clip"), Target, meaning, Get(row, "English"), Target, Get(row, "Chinese"),
            Get(row, "Source"), File.ReadAllBytes(corrected ? Playback.SecondMp3 : Playback.FirstMp3), manual, keep, true)!.ToString()!;
    }

    internal static void Cards()
    {
        using var net = new NoNetwork();
        var cloud = Local("cards-library", net);
        var first = Observe(cloud);
        Guard(first == Observe(cloud), "same subtitle/target was recorded twice");
        Guard(Save(cloud, first, false) == "NeedsSecond" && Save(cloud, first, false) == "NeedsSecond",
            "one repeated cue satisfied the two-dialogue rule");
        var draft = CardsOf(cloud).Single();
        Guard(Get<string>(draft, "Status") == "draft" && Get<int>(draft, "Count") == 1 && Jobs(cloud).Length == 0,
            "one cue did not stay an unqueued draft");
        var second = Observe(cloud, 1);
        Guard(Save(cloud, second, false) == "Queued", "second distinct verified line was not queued");
        var active = CardsOf(cloud).Single();
        Guid cardId = Get<Guid>(active, "CardId");
        Guard(Get<string>(active, "Status") == "active" && Get<int>(active, "Count") == 2
            && Items(active, "Examples").Length == 2 && Get<Guid>(Jobs(cloud).Single(), "ExampleId") == second,
            "wrong example count or queued clip for the two-dialogue card");
        Pass("same cue remains one draft; a distinct second cue activates the sense and queues the second clip");

        string priorHash = Get<string>(Row(cloud, second), "Sha256");
        Guard(Save(cloud, second, false, "提出方案", keep: true, corrected: true) == "Queued", "correction did not queue the new revision");
        var edit = CardsOf(cloud).Single();
        Guard(Get<Guid>(edit, "CardId") == cardId && Get<int>(edit, "Revision") > Get<int>(active, "Revision")
            && Get<string>(edit, "Meaning") == "提出方案" && Get<int>(edit, "Count") == 2
            && Get<string>(Row(cloud, second), "Sha256") != priorHash, "correction did not preserve card identity and replace content");
        Call(cloud, "Withdraw", second);
        var withdrawn = CardsOf(cloud).Single();
        Guard(Get<Guid>(withdrawn, "CardId") == cardId && Get<string>(withdrawn, "Status") == "withdrawn"
            && Jobs(cloud).Length == 0 && Rows(cloud).Length == 2 && Rows(cloud).All(r => Get<bool>(r, "Rejected")),
            "withdrawal left a reviewable or queued card");
        var query = (string)Call(Service("AnkiExportService"), "PauseQuery", withdrawn)!;
        Guard(query == $"\"ID:{Get<Guid>(withdrawn, "LibraryId"):N}:{cardId:N}\"", "withdrawn card lost its stable suspension ID");
        Pass("same-sense correction preserves stable ID, replaces audio and increments revision; withdrawal cancels pending work");

        var manual = Observe(cloud, 2, "Pitch the tent.");
        Guard(Save(cloud, manual, true, "搭帐篷") == "Queued" && Jobs(cloud).Length == 1
            && CardsOf(cloud).Any(c => Get<string>(c, "Status") == "active" && Get<int>(c, "Count") == 1),
            "explicit one-dialogue confirmation did not activate an independent sense");
        Guard(net.Calls == 0, "card changes contacted network");
        Pass("explicit one-dialogue path activates a separate sense");
    }

    internal static void Queue(bool resume)
    {
        using var net = new NoNetwork();
        using var fake = new FakeAnki(InRun("fake-anki-state.json"));
        var cloud = Local("queue-library", net, fake);
        bool Tick() => (bool)Await(Call(cloud, "AnkiTick", CancellationToken.None))!;
        if (!resume)
        {
            var id = Observe(cloud);
            Guard(Save(cloud, id, true) == "Queued" && Jobs(cloud).Length == 1, "verified card was not queued");
            Guard(!Tick() && fake.Calls == 0, "unbound library attempted to contact Anki");
            Await(Call(cloud, "BindAnki", CancellationToken.None));
            fake.Offline = true;
            Guard(!Tick() && fake.Mutations == 0 && !Get<bool>(Jobs(cloud).Single(), "Written")
                && Get(Jobs(cloud).Single(), "Error") is null, "offline job was dropped, advanced, or marked a hard error");
            Pass("offline bound Anki left one pending job; no simulated writes occurred");
            fake.Offline = false;
            fake.DropAddAcknowledgement = true;
            Guard(!Tick() && fake.NoteCount == 1 && fake.Adds == 1 && fake.MediaCount == 1
                && !Get<bool>(Jobs(cloud).Single(), "Written"), "lost add receipt did not leave one remote note plus one pending local job");
            Pass("mock persisted addNote then went offline before acknowledgement; pending local job survived for next process");
        }
        else
        {
            Guard(Get<string>(State(cloud), "AnkiProfile") == FakeAnki.CorrectProfile && !Get<bool>(Jobs(cloud).Single(), "Written")
                && fake.NoteCount == 1 && fake.MediaCount == 1, "independent process did not reload local queue and persisted mock state");
            fake.Profile = "Other synthetic profile";
            Guard(!Tick() && fake.Mutations == 0, "the wrong Anki profile accepted a mutation");
            fake.Profile = FakeAnki.CorrectProfile;
            Guard(Tick() && !Tick() && Get<bool>(Jobs(cloud).Single(), "Written"), "reconnected queue did not mark the existing note as written");
            var row = Rows(cloud).Single();
            Guard(fake.NoteCount == 1 && fake.Mutations == 0
                && Save(cloud, Get<Guid>(row, "Id"), true) == "AlreadyWritten" && !Tick(), "restart or unchanged save created a duplicate");
            string priorKey = fake.StableKey;
            Guard(Save(cloud, Get<Guid>(row, "Id"), true, "介绍想法", keep: true, corrected: true) == "Queued" && Tick(),
                "same-card correction did not reach simulated Anki");
            Guard(fake.NoteCount == 1 && fake.Adds == 0 && fake.Updates == 1 && fake.MediaCount == 2 && fake.StableKey == priorKey,
                "correction did not update the existing stable-ID note and attach new audio exactly once");
            Pass("fresh PID loaded queue; wrong profile blocked; lost-reply retry and unchanged save did not duplicate; correction updated existing note");
        }
        Guard(net.Calls == 0, "the package used a non-Anki HTTP transport");
    }

    internal static void Import()
    {
        using var net = new NoNetwork();
        var cloud = Local("import-library", net);
        var parser = ProductType("OriginalReview.WordImport");
        string csv = InRun("import-fixture.csv");
        File.WriteAllText(csv, "word,meaning,example,translation,audio\n"
            + "external,\"外部,仅文字\",An external list.,本地译文,C:\\Fixture\\not-real.mp3\n"
            + "EXTERNAL,\"外部,仅文字\",AN EXTERNAL LIST.,本地译文,https://fixture.invalid/not-real.mp3\n"
            + "无效,没有英文字母,,,\n", new UTF8Encoding(true));
        var batch = Call(parser, "Read", csv)!;
        var entries = Items(batch, "Entries");
        Guard(entries.Length == 2 && Get<int>(batch, "Invalid") == 1
            && Get<string>(entries[0], "Meaning") == "外部,仅文字", "CSV validation/quoting did not separate valid and invalid entries");
        var input = entries.Select(e => (Get<string>(e, "Word"), Get<string>(e, "Meaning"), Get<string>(e, "English"), Get<string>(e, "Chinese"))).ToArray();
        Guard((int)Call(cloud, "Import", input)! == 1 && (int)Call(cloud, "Import", input)! == 0, "import did not deduplicate new/reimported rows");
        var item = CardsOf(cloud).Single();
        Guard(Get<string>(item, "Status") == "text" && Get<string>(item, "Reason") == "import" && Items(item, "Examples").Length == 0
            && Jobs(cloud).Length == 0 && Items(State(cloud), "Pending").Length == 0
            && (int)Call(cloud, "QueueExistingAnki")! == 0 && !Directory.EnumerateFiles(InRun("import-library"), "*.mp3").Any()
            && !File.ReadAllText(InRun("import-library/state.json")).Contains("not-real.mp3", StringComparison.Ordinal),
            "text-only import consumed an ignored audio column or produced media, sync or Anki jobs");
        Pass("actual CSV parser and local import: invalid row reported, quoting parsed, case-insensitive duplicates skipped, ignored paths/URLs never used");

        string txt = InRun("import-fixture.txt"), tsv = InRun("import-fixture.tsv");
        File.WriteAllText(txt, "spare\n\n");
        File.WriteAllText(tsv, "表达\t词义\nretain\t保留\n");
        Guard(Items(Call(parser, "Read", txt)!, "Entries").Length == 1
            && Items(Call(parser, "Read", tsv)!, "Entries").Length == 1, "could not parse supported TXT/TSV");
        string duplicate = InRun("import-duplicate-header.csv");
        File.WriteAllText(duplicate, "word,expression,meaning\nexternal,external,duplicate\n");
        Reject<InvalidDataException>(() => Call(parser, "Read", duplicate), "rejected duplicate aliased columns");
        string invalidUtf8 = InRun("import-invalid-utf8.txt");
        File.WriteAllBytes(invalidUtf8, [0x66, 0x80, 0x0a]);
        Reject<DecoderFallbackException>(() => Call(parser, "Read", invalidUtf8), "rejected invalid UTF-8");
        string large = InRun("import-oversize.txt");
        using (var stream = new FileStream(large, FileMode.CreateNew)) stream.SetLength(4L * 1024 * 1024 + 1);
        Reject<InvalidDataException>(() => Call(parser, "Read", large), "rejected >4 MiB input");
        Guard(net.Calls == 0, "text import contacted internet or Anki");
    }

    internal static void Export()
    {
        using var net = new NoNetwork();
        var cloud = Local("export-library", net);
        var first = Observe(cloud, english: "They pitch <the idea> to us.");
        var second = Observe(cloud, 1);
        const string privateText = "仅本机译文";
        const string safeMeaning = "介绍 [sound:other.mp3]";
        Guard(Save(cloud, first, true, safeMeaning) == "Queued" && Save(cloud, second, true, "另一义项", corrected:true) == "Queued",
            "could not stage export choices");
        var pathRow = Observe(cloud, 2, @"Pitch C:\Fixture\private.mkv.");
        Guard(Save(cloud, pathRow, true, "路径不应外泄") == "Ineligible", "path-bearing card was eligible");
        Call(cloud, "Import", new[] { ("excluded", "只存文字", "Only text.", "译文") });
        var card = CardsOf(cloud).Single(c => Get<string>(c, "Meaning") == safeMeaning);
        Guid id = Get<Guid>(card, "CardId");
        string state = InRun("export-library/state.json");
        string stateHash = FileHash(state);
        string dest = InRun("export-files");
        Directory.CreateDirectory(dest);
        string expectedSha = FileHash(Playback.FirstMp3);
        var service = Service("AnkiExportService");
        Func<string, string> media = sha => (string)Call(cloud, "MediaPath", sha)!;
        object ExportOne(Guid cardId, Guid example, Func<string,string>? readMedia = null) =>
            Call(service, "Export", State(cloud), readMedia ?? media, new[] { (cardId, example) }, dest)!;
        var unsafeCard = CardsOf(cloud).Single(c => Get<string>(c, "Meaning") == "路径不应外泄");
        Reject<InvalidDataException>(() => ExportOne(Get<Guid>(unsafeCard, "CardId"), pathRow), "rejected export of path-bearing example");
        var textCard = CardsOf(cloud).Single(c => Get<string>(c, "Status") == "text");
        Reject<InvalidDataException>(() => ExportOne(Get<Guid>(textCard, "CardId"), Guid.NewGuid()), "rejected text-only export");
        Reject<InvalidDataException>(() => ExportOne(id, first, _ => Playback.SecondMp3), "rejected mismatched MP3 digest");
        Guard(!Directory.EnumerateFileSystemEntries(dest).Any(), "rejected export left files on disk");
        var result = ExportOne(id, first);
        string tsv = Get<string>(result, "TsvPath");
        string[] lines = File.ReadAllLines(tsv);
        string[] row = lines.Where(l => !l.StartsWith('#')).Single().Split('\t');
        string expectedKey = $"{Get<Guid>(card, "LibraryId"):N}:{id:N}";
        string mediaName = $"llplayer-{expectedSha}.mp3";
        Guard(lines.Contains("#separator:Tab") && lines.Contains("#html:true") && lines.Contains("#columns:ID\t音频\t表达\t本句义\t英文原句\t出处")
            && row.Length == 6 && row[0] == expectedKey && row[1] == $"[sound:{mediaName}]"
            && row[3].Contains("&#91;sound:") && row[4].Contains("&lt;the idea&gt;")
            && !lines.Any(l => l.Contains(privateText) || l.Contains("private.mkv")), "TSV columns, stable ID, escaping or privacy field filtering failed");
        string mp3 = Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(tsv)!, "media"), "*.mp3").Single();
        var pcm = Playback.Measure(mp3);
        Guard(Path.GetFileName(mp3) == mediaName && FileHash(mp3) == expectedSha && Math.Abs(pcm.Hz-880) < 6
            && File.Exists(Get<string>(result,"GuidePath")), "selected MP3 or import guide was incorrect");
        Guard(FileHash(Get<string>(ExportOne(id, first), "TsvPath")) == FileHash(tsv) && FileHash(state) == stateHash,
            "repeat export changed TSV or mutated the library");
        Save(cloud, first, true, "纠正后的意义", keep:true, corrected:true);
        string[] corrected = File.ReadAllLines(Get<string>(ExportOne(id, first), "TsvPath")).Single(l => !l.StartsWith('#')).Split('\t');
        Guard(corrected[0] == row[0] && corrected[1] != row[1], "corrected export did not maintain stable ID and replace the MP3 reference");
        Call(cloud, "Withdraw", first);
        Reject<InvalidDataException>(() => ExportOne(id, first), "rejected export after withdrawal");
        Guard(net.Calls == 0 && File.Exists(tsv) && File.Exists(mp3), "export/withdrawal caused external I/O or removed an earlier export");
        Pass($"six-column escaped TSV + one selected digest-matched MP3 ({pcm.Hz:F1} Hz), stable on repeat/correction; {tsv}");
    }
}
