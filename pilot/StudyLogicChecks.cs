using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LLPlayer.Services;

public static class StudyLogicChecks
{
    private static StudyCue Cue(int start, int end, string text) =>
        new(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end), text);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    public static void Run()
    {
        StudyCue[] cues = [Cue(0, 700, "Come on."), Cue(1000, 2000, "I thought you were going"),
            Cue(2100, 3000, "to bail on me."), Cue(3100, 4000, "Why would I?"), Cue(4000, 4900, "I promised.")];
        var line = StudyDialogue.Build(cues, 1);
        var secondHalf = StudyDialogue.Build(cues, 2);
        Check(line.Start == cues[1].Start && line.End == cues[2].End && line.Text == "I thought you were going to bail on me."
            && secondHalf.Start == line.Start && secondHalf.End == line.End && secondHalf.Text == line.Text, "split cue shares one dialogue");
        Check(line.Before.SequenceEqual(new[] { "Come on." }) && line.After.SequenceEqual(new[] { "Why would I?", "I promised." }),
            "context stays separate from the displayed sentence");
        StudyCue[] response = [Cue(1000, 2000, "Can you"), Cue(2100, 3000, "- not now.")];
        Check(StudyDialogue.Build(response, 0).End == response[0].End && StudyDialogue.Build(response, 1).Start == response[1].Start,
            "explicit speaker change is never merged");
        var wrapping = StudyDialogue.Build([Cue(1000, 3000, "I will see you\r\nin a few days.")], 0);
        Check(wrapping.SpeakerTurns().Single().Text == "I will see you in a few days.", "a normal wrapped sentence remains one request");
        var exchange = StudyDialogue.Build([Cue(1000, 3000, "- Hey, is this yours?\r\n- Yes, this is mine.\nThanks.")], 0);
        var turns = exchange.SpeakerTurns();
        Check(turns.Select(t => t.Text).SequenceEqual(["Hey, is this yours?", "Yes, this is mine. Thanks."])
            && turns[0].After.Single() == turns[1].Text && turns[1].Before.Single() == turns[0].Text,
            "explicit same-cue speakers remain separate and each sees the other only as context");
        Check(exchange.ContextForTarget("mine")?.Text == turns[1].Text && exchange.ContextForTarget("this") is null
            && exchange.ContextForTarget("is this yours? - Yes") is null,
            "word lookup uses only a unique speaker and declines ambiguous or cross-speaker selections");
        var sent = new List<string>();
        var splitTranslation = exchange.TranslateTurns((turn, _) =>
        {
            sent.Add(turn.Text);
            return Task.FromResult<string?>(sent.Count == 1 ? "第一句" : "第二句");
        }, CancellationToken.None).GetAwaiter().GetResult();
        Check(sent.SequenceEqual(turns.Select(t => t.Text)) && splitTranslation == "- 第一句\n- 第二句",
            "both model requests produce their own lines in speaker order");
        Check(exchange.TranslateTurns((turn, _) => Task.FromResult(turn.Text == turns[0].Text ? "第一句" : null), CancellationToken.None)
            .GetAwaiter().GetResult() is null, "one missing translation does not display a half-translated exchange");
        StudyCue[] ellipsis = [Cue(1000, 2000, "You will find out..."), Cue(2200, 3000, "\u2026in a few days.")];
        var continuation = StudyDialogue.Build(ellipsis, 0);
        var trailing = StudyDialogue.Build(ellipsis, 1);
        Check(continuation.Start == ellipsis[0].Start && continuation.End == ellipsis[1].End
            && continuation.Text == "You will find out in a few days." && trailing.Text == continuation.Text
            && trailing.Start == continuation.Start && trailing.End == continuation.End, "paired continuation is one dialogue from either cue");
        var differentSpeaker = ellipsis[1] with { Text = "- ...in a few days?" };
        Check(StudyDialogue.Build([ellipsis[0], differentSpeaker], 0).Text == ellipsis[0].Text,
            "explicit speaker change prevents ellipsis merge");
        var differentSentence = ellipsis[1] with { Text = "In a few days?" };
        Check(StudyDialogue.Build([ellipsis[0], differentSentence], 0).Text == ellipsis[0].Text,
            "a trailing ellipsis alone does not merge a new sentence");
        var unicodePause = ellipsis[0] with { Text = "Don't bother\u2026" };
        Check(StudyDialogue.Build([unicodePause, ellipsis[1] with { Text = "why?" }], 0).Text == unicodePause.Text,
            "a one-sided Unicode ellipsis is not silently treated as a continuation");

        const string reference = "\u6211\u4ee5\u4e3a\u4f60\u8981\u653e\u6211\u9e3d\u5b50";
        Check(line.MatchChinese([Cue(1250, 2860, reference)]) == reference, "Chinese can start after the English cue");
        Check(line.MatchChinese([Cue(500, 1140, reference), Cue(2900, 3650, reference)]) is null,
            "previous and next Chinese cues do not replace the translation");
        Check(line.MatchChinese([Cue(300, 4500, reference)]) is null, "Chinese spanning multiple lines is not silently substituted");

        static string Reply(string expression, bool certain) => JsonSerializer.Serialize(new
        {
            zh = reference, expression, lemma = "bail on", sense = "\u653e\u9e3d\u5b50", certain
        });
        Check(StudyLocalLookup.Parse(Reply("bail on", true), line.Text, "bail") is { Expression: "bail on", Sense: not null },
            "contextual answer keeps the grounded phrase");
        Check(StudyLocalLookup.Parse(Reply("bail on", false), line.Text, "bail") is { Sense: null, Expression: null },
            "uncertain answer withholds the contextual sense");
        Check(StudyLocalLookup.Parse(Reply("bail out", true), line.Text, "bail") is { Sense: null },
            "phrase missing from the line is rejected");
        Check(StudyLocalLookup.Parse(JsonSerializer.Serialize(new { zh = reference, certain = true, expression = "bail on", lemma = "bail out", sense = reference }),
            line.Text, "bail") is { Chinese: reference, Sense: null }, "replacing a two-word expression's particle triggers dictionary fallback");
        Check(StudyLocalLookup.Parse(Reply("bailed on", true), "He bailed on us.", "bailed") is { Lemma: "bail on", Sense: not null },
            "inflecting a two-word expression may change its first word but preserves the particle");
        static string Lexical(string expression, string lemma) => JsonSerializer.Serialize(new { zh = reference, certain = true, expression, lemma, sense = reference });
        Check(StudyLocalLookup.Parse(Lexical("your dumb-ass knows", "know"), "Your dumb-ass knows it.", "dumb-ass") is { Chinese: reference, Sense: null },
            "a neighboring word cannot replace the clicked target as lemma");
        Check(StudyLocalLookup.Parse(Lexical("gonna", "going to"), "I'm gonna call.", "gonna") is { Lemma: "going to", Sense: not null },
            "the approved spoken form expands to its English lemma");
        Check(StudyLocalLookup.Parse(Lexical("got busted", "get busted"), "He got busted yesterday.", "got busted") is { Lemma: "get busted", Sense: not null },
            "common irregular forms continue to ground the target");
        Check(StudyLocalLookup.Parse(JsonSerializer.Serialize(new { zh = reference }), line.Text, "bail")
            is { Chinese: reference, Expression: null, Lemma: null, Sense: null }, "missing lexical confidence does not discard valid Chinese");
        Check(StudyLocalLookup.Parse(JsonSerializer.Serialize(new { zh = reference, certain = true, expression = "bail on", lemma = true }),
            line.Text, "bail") is { Chinese: reference, Sense: null }, "malformed lexical fields fall back to Chinese without a contextual sense");
        Check(StudyLocalLookup.Parse(Reply(line.Text, true), line.Text, "bail") is { Chinese: reference, Sense: null },
            "an overlong expression does not replace the dictionary fallback");
        Check(StudyLocalLookup.Parse(JsonSerializer.Serialize(new { zh = reference, certain = true, expression = "bail on", lemma = reference, sense = reference }),
            line.Text, "bail") is { Chinese: reference, Sense: null }, "a Chinese gloss cannot become the English lemma");
        Check(StudyLocalLookup.Parse("[]", line.Text, "bail") is null && StudyLocalLookup.Parse("{\"zh\":17}", line.Text, "bail") is null,
            "unsupported reply shapes cannot be shown as a translation");
        var plain = StudyLocalLookup.InstructionsFor("We are waiting.");
        Check(StudyLocalLookup.InstructionsFor("I bombed the interview.") == plain &&
            StudyLocalLookup.InstructionsFor("I heard the bomb explode.") == plain &&
            StudyLocalLookup.InstructionsFor("He bailed on us.") == plain, "unrelated and standalone bomb uses do not change the prompt");
        Check(StudyLocalLookup.InstructionsFor("That’d be the bomb.").Contains("非常棒") &&
            StudyLocalLookup.InstructionsFor("This is the bomb.").Contains("按字面义"), "predicate bomb hint explicitly preserves the literal alternative");
        Check(StudyLocalLookup.InstructionsFor("He pinched a loaf in his pants.").Contains("不能出现面包") &&
            StudyLocalLookup.InstructionsFor("The baker pinched a loaf of dough.").Contains("烘焙") &&
            !StudyLocalLookup.InstructionsFor("The baker pinched a loaf of dough.").Contains("不能出现面包"),
            "loaf hint distinguishes bathroom and baking context");
    }

    public static void RunPlaybackProgress(string root)
    {
        Directory.CreateDirectory(root);
        var a = Path.Combine(root, "a.mkv");
        var b = Path.Combine(root, "b.MP4");
        var subtitles = Path.Combine(root, "a.srt");
        File.WriteAllText(a, "sample video A");
        File.WriteAllText(b, "sample video B");
        File.WriteAllText(subtitles, "sample subtitles");
        var aKey = StudyPlaybackProgress.Key(a)!;
        var bKey = StudyPlaybackProgress.Key(b)!;
        Check(aKey != bKey && aKey == StudyPlaybackProgress.Key(new Uri(a).AbsoluteUri)
            && StudyPlaybackProgress.Key("https://example.invalid/episode.mkv") is null
            && StudyPlaybackProgress.Key(subtitles) is null, "progress supports local MP4/MKV only and normalizes file URLs");

        var folder = Path.Combine(root, "state");
        var store = new StudyPlaybackProgress(folder);
        long duration = TimeSpan.FromMinutes(40).Ticks;
        long first = TimeSpan.FromMinutes(10).Ticks;
        long second = TimeSpan.FromMinutes(20).Ticks;
        Check(store.Read(aKey, duration) is null, "unseen media starts without a seek");
        store.Save(aKey, first, duration, false);
        store.Save(bKey, second, duration, false);
        store = new StudyPlaybackProgress(folder);
        Check(store.Read(aKey, duration) == first && store.Read(bKey, duration) == second,
            "progress survives reopening storage and separates episodes");
        store.Save(aKey, first * 3, duration, false);
        store.Save(aKey, -1, duration, false);
        store.Save(aKey, duration, duration, false);
        Check(store.Read(aKey, duration) == first * 3 && store.Read(aKey, first) is null,
            "the latest valid seek replaces old progress and invalid positions are ignored");
        store.Save(bKey, duration, duration, true);
        Check(store.Read(bKey, duration) is null, "finished media restarts from the beginning");
        File.AppendAllText(a, "replacement");
        var changedKey = StudyPlaybackProgress.Key(a)!;
        Check(changedKey != aKey && store.Read(changedKey, duration) is null, "a replacement at the same path does not inherit progress");
        File.WriteAllText(Path.Combine(folder, aKey + ".position"), "bad data");
        Check(store.Read(aKey, duration) is null, "corrupt progress cannot block opening media");
    }

    public static void RunEpisode(string json)
    {
        using var data = JsonDocument.Parse(json);
        static StudyCue[] Track(JsonElement rows) => rows.EnumerateArray().Select(c =>
            Cue(c.GetProperty("startMs").GetInt32(), c.GetProperty("endMs").GetInt32(), c.GetProperty("text").GetString()!)).ToArray();
        var english = Track(data.RootElement.GetProperty("english"));
        var chinese = Track(data.RootElement.GetProperty("chinese"));
        Check(english.Length > 0 && chinese.Length > 0, "real episode has both subtitle tracks");
        int matched = 0;
        for (int i = 0; i < english.Length; i++)
        {
            int first = Math.Max(0, i - 6);
            var window = english[first..Math.Min(english.Length, i + 7)];
            var line = StudyDialogue.Build(window, i - first);
            if (line.MatchChinese(chinese) is not null) matched++;
        }
        Check(matched > 0, "real episode yields time-matched Chinese");
        Console.WriteLine($"Tracks: EN={english.Length}, ZH={chinese.Length}; matched EN cues={matched}/{english.Length} (timing heuristic only)");
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        internal void Drain()
        {
            while (_callbacks.TryDequeue(out var next)) next.Callback(next.State);
        }
    }

    public static void RunQueue()
    {
        var calls = new List<(StudyDialogue Line, CancellationToken Token, TaskCompletionSource<string?> Result)>();
        var changes = 0;
        var queue = new StudyTranslationQueue((line, token) =>
        {
            var result = new TaskCompletionSource<string?>();
            calls.Add((line, token, result));
            return result.Task;
        }, () => changes++);
        var previous = SynchronizationContext.Current;
        var context = new QueueContext();
        SynchronizationContext.SetSynchronizationContext(context);
        void Complete(string? value)
        {
            calls[^1].Result.SetResult(value);
            context.Drain();
        }
        try
        {
            var a = StudyDialogue.Build([Cue(1000, 1800, "One.")], 0);
            var b = StudyDialogue.Build([Cue(2000, 2800, "Two.")], 0);
            var skipped = StudyDialogue.Build([Cue(3000, 3800, "Skip.")], 0);
            var anotherMissing = StudyDialogue.Build([Cue(4000, 4800, "Missing.")], 0);
            var distant = StudyDialogue.Build([Cue(32000, 32800, "Too far ahead.")], 0);
            StudySubtitleLine Entry(StudyDialogue line, StudyCheck state = StudyCheck.Unchecked) => new(line, line, state);
            StudySubtitleLine[] upcoming = [Entry(a), Entry(b), Entry(skipped, StudyCheck.Skipped), Entry(anotherMissing), Entry(distant)];
            string? Reference(StudySubtitleLine candidate) => candidate.Dialogue == b ? "reference" : null;
            Check(StudyTranslationQueue.Plan(Entry(a), null, upcoming, Reference, true).SequenceEqual([a, anotherMissing]),
                "bilingual fills current and upcoming gaps without retranslating matching Chinese or skipped lines");
            Check(StudyTranslationQueue.Plan(Entry(a), "reference", upcoming, Reference, true).SequenceEqual([anotherMissing]),
                "current Chinese reference is kept while the next missing line is prefetched");
            Check(StudyTranslationQueue.Plan(Entry(a), null, upcoming, Reference, false).SequenceEqual([a])
                && StudyTranslationQueue.Plan(Entry(a, StudyCheck.Skipped), null, upcoming, Reference, false).Length == 0,
                "on-demand mode never schedules upcoming or skipped dialogue");
            var c = StudyDialogue.Build([Cue(3000, 3800, "Three.")], 0);
            var another = StudyDialogue.Build([Cue(10000, 10800, "One.")], 0);
            queue.Prepare([a, a, b, c]);
            Check(calls.Count == 1 && calls[0].Line == a && queue.Pending(a), "local translation starts current line first without duplicate calls");
            Complete("current");
            Check(calls.Count == 2 && calls[1].Line == b && queue.Find(a) == "current" && queue.Find(b) is null,
                "upcoming line is prefetched, never filled with previous translation");
            queue.Prepare([c]);
            Check(calls[1].Token.IsCancellationRequested, "jump cancels obsolete prefetch");
            Complete("stale");
            Check(calls.Count == 3 && calls[2].Line == c && queue.Find(b) is null && changes == 1,
                "late canceled response cannot reach cache or refresh the UI");
            Complete("third");
            queue.Prepare([another]);
            Check(queue.Find(another) is null && calls.Count == 4 && calls[3].Line == another,
                "same line text at a different time does not reuse the old result");
            queue.Reset();
            queue.Prepare([a]);
            Check(queue.Find(a) is null && calls[3].Token.IsCancellationRequested, "media change clears cache and cancels in-flight work");
            Complete("from former media");
            Check(calls.Count == 5 && calls[4].Line == a && queue.Find(another) is null, "new media schedules fresh request even for an identical cue");
            Complete(null);
            queue.Prepare([a]);
            Check(calls.Count == 5 && !queue.Pending(a), "missing result does not loop on refresh");
            queue.Prepare([a], retryFailed: true);
            Check(calls.Count == 6, "explicit reveal may retry missing result");
            Complete("new media");
            Check(queue.Find(a) == "new media", "current line uses only its own result after reset");

            queue.Prepare([b]);
            var oldRequest = calls[^1];
            var corrected = b with { Text = "Too late." };
            queue.Reset();
            queue.Prepare([corrected]);
            Check(oldRequest.Token.IsCancellationRequested && queue.Find(a) is null && queue.Find(corrected) is null,
                "subtitle edit clears stored translations and cancels the old request");
            Complete("old text");
            Check(queue.Find(b) is null && calls[^1].Line == corrected, "late old translation is discarded and corrected text is requested");
            Complete("corrected text");
            Check(queue.Find(corrected) == "corrected text" && queue.Find(b) is null, "only the corrected line owns the new translation");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    public static void RunNotes(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "notes.json");
        var notes = new StudySubtitleNotes(path);
        var otherWriter = new StudySubtitleNotes(path);
        var video = Path.Combine(root, "fixture.mkv");
        var external = Path.Combine(root, "fixture.en.srt");
        File.WriteAllText(video, "fixture");
        File.WriteAllText(external, "fixture");
        var media = StudySubtitleNotes.FileIdentity(video)!;
        var outside = StudySubtitleNotes.FileIdentity(external)!;
        Check(media == StudySubtitleNotes.FileIdentity(new Uri(video).AbsoluteUri) && media != outside
            && StudySubtitleNotes.FileIdentity("https://example.invalid/video.mkv") is null, "file identity is stable across local URL syntax but not network URLs");

        var track = new StudySubtitleTrack(media, "embedded", 4);
        var otherTrack = track with { Stream = 5 };
        var externalTrack = track with { Source = outside };
        StudyCue[] cues = [Cue(1000, 1900, "I thought you were going"), Cue(1920, 2600, "to bail on me.")];
        var line = StudyDialogue.Build(cues, 0);
        var secondHalf = StudyDialogue.Build(cues, 1);
        Check(notes.Resolve(track, line) is { Check: StudyCheck.Unchecked, UseReference: true }, "embedded track is not automatically verified");
        notes.Save(track, line, line.Text, false);
        Check(new StudySubtitleNotes(path).Resolve(track, line) is { Check: StudyCheck.Confirmed, UseReference: true },
            "confirmation survives reopen without changing subtitle or reference");
        otherWriter.Save(otherTrack, line, line.Text, false);
        notes.Save(track, line, "  I thought you were going to call me. ", false);
        notes = new StudySubtitleNotes(path);
        var correction = notes.Resolve(track, secondHalf);
        Check(correction is { Check: StudyCheck.Corrected, UseReference: false } && correction.Dialogue.Text == "I thought you were going to call me."
            && correction.Dialogue.Start == line.Start && correction.Dialogue.End == line.End,
            "both halves share one correction, preserve replay timing and reject the old Chinese reference");
        Check(notes.Resolve(otherTrack, line).Check == StudyCheck.Confirmed && notes.Resolve(externalTrack, line).Check == StudyCheck.Unchecked
            && notes.Resolve(track with { Media = outside }, line).Check == StudyCheck.Unchecked
            && notes.Resolve(track, line with { Text = "A different original sentence." }).Check == StudyCheck.Unchecked,
            "notes are isolated by track, subtitle file, episode and original text");
        File.AppendAllText(external, "changed");
        Check(StudySubtitleNotes.FileIdentity(external) != outside, "replaced sidecar does not reuse its old track identity");
        notes.Save(track, line, null, true);
        var skipped = new StudySubtitleNotes(path).Resolve(track, line);
        Check(skipped.Skipped && !skipped.UseReference && skipped.Dialogue.Text == line.Text, "skipped line keeps the source text, but does not reuse Chinese");
        notes.Save(track, line, line.Text, false);
        Check(notes.Resolve(track, line) is { Skipped: false, Check: StudyCheck.Confirmed, UseReference: true }, "a skipped line can be checked again");
        try { notes.Save(track, line, "   ", false); throw new InvalidOperationException("empty edit accepted"); }
        catch (InvalidDataException) { Check(notes.Resolve(track, line).Check == StudyCheck.Confirmed, "invalid edit does not overwrite the existing note"); }
        Check(File.ReadAllText(video) == "fixture" && File.ReadAllText(external) == "fixturechanged", "note writes only modify local note storage");
    }

    public static void RunDictionary(string path)
    {
        var live = StudyDictionary.Lookup(path, "(LIVE!)");
        Check(live is { Headword: "live" } && live.Translation.Contains("\u5c45\u4f4f") && live.Translation.Contains('\n'),
            "bundled dictionary returns Chinese glosses for a case-insensitive lookup");
        Check(StudyDictionary.Lookup(path, "went") is { Lemma: "go" } went && went.Translation.Contains("\u8fc7\u53bb\u5f0f"),
            "inflected word keeps its own gloss and dictionary-provided lemma");
        Check(StudyDictionary.Lookup(path, "out  on bail.") is { Headword: "out on bail" }, "selected phrase uses an exact dictionary entry");
        Check(StudyDictionary.Lookup(path, "you\u2019re") is { Headword: "you're" }, "curly apostrophe finds its dictionary entry");
        Check(StudyDictionary.Lookup(path, "give up a purple spaceship") is null, "unknown phrase never falls back to a partial hit");
        try
        {
            StudyDictionary.Lookup(path + ".absent", "live");
            throw new InvalidOperationException("Expected a missing dictionary to signal a file error");
        }
        catch (FileNotFoundException) { Console.WriteLine("PASS missing dictionary has a distinct error"); }
    }
}
