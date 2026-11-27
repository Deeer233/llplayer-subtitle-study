using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LLPlayer.Services;

public static class StudyModelProbe
{
    private sealed record Sample(string Id, string Line, string? Target, string[]? Before, string[]? After, bool Verified);
    private sealed record Answer(string? Chinese, string? Expression, string? Lemma, string? Sense);
    private sealed record Row(string Id, string Line, string? Target, string[] Before, string[] After, Answer? Answer, long Milliseconds,
        long? TranslationMilliseconds, long? SenseMilliseconds);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static Sample Case(string id, string line, string? target = null, string[]? before = null, string[]? after = null)
        => new(id, line, target, before, after, false);

    public static async Task<string> Run(string? inputJson, bool unverifiedPilot, bool probe8B, CancellationToken cancellation)
    {
        bool smoke = string.IsNullOrEmpty(inputJson);
        Sample[] samples = smoke ?
        [
            Case("bail-on", "I thought you were going to bail on me.", "bail", ["We agreed to meet here at eight."]),
            Case("bail-legal", "He's out on bail until his hearing.", "bail"),
            Case("negation", "I didn't tell him you stole it.", before: ["What did you tell him?"]),
            Case("relationship", "She's my brother's ex, not mine."),
            Case("cover", "I'll cover for you if the boss asks.", "cover", ["I need to leave early today."]),
            Case("no-spoilers", "I'll call you when I get there.", after: ["The car broke down outside town."]),
            Case("keep-in-mind", "Keep in mind that we're not alone.", "Keep in mind"),
            Case("bail-inflected", "He bailed on us at the last minute.", "bailed")
        ] : JsonSerializer.Deserialize<Sample[]>(inputJson!, Json) ?? [];
        if ((smoke && unverifiedPilot) || samples.Length == 0 || samples.Any(s => s is null || (!smoke && !unverifiedPilot && !s.Verified)
            || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Line)
            || !ValidContext(s.Before) || !ValidContext(s.After) || (s.Target is not null &&
                (string.IsNullOrWhiteSpace(s.Target) || !s.Line.Contains(s.Target, StringComparison.OrdinalIgnoreCase))))
            || samples.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != samples.Length)
            throw new InvalidDataException("Expected unique IDs, verified dialogue, a present target if provided, and up to two context lines per side.");

        var rows = new List<Row>(samples.Length);
        foreach (var s in samples)
        {
            cancellation.ThrowIfCancellationRequested();
            var dialogue = new StudyDialogue(TimeSpan.Zero, TimeSpan.FromSeconds(1), s.Line, s.Before ?? [], s.After ?? []);
            Task<StudyAnswer?> Lookup(StudyDialogue input, string target, CancellationToken token) => probe8B
                ? StudyLocalLookup.Probe8B(input, target, token)
                : StudyLocalLookup.Get(input, target, token);
            var timer = Stopwatch.StartNew();
            var singleRequest = smoke ? await Lookup(dialogue, s.Target ?? "", cancellation) : null;
            var chinese = smoke ? singleRequest?.Chinese : probe8B
                ? await dialogue.TranslateTurns(async (turn, token) => (await Lookup(turn, "", token))?.Chinese, cancellation)
                : await StudyLocalLookup.TranslatePlayback(dialogue, cancellation);
            long firstMs = timer.ElapsedMilliseconds;
            var lexical = smoke ? singleRequest : s.Target is { } target && dialogue.ContextForTarget(target) is { } context
                ? await Lookup(context, StudyDialogue.Normalize(target), cancellation) : null;
            long totalMs = timer.ElapsedMilliseconds;
            var answer = chinese is null && lexical?.Sense is null ? null
                : new Answer(chinese, lexical?.Expression, lexical?.Lemma, lexical?.Sense);
            rows.Add(new(s.Id, s.Line, s.Target, s.Before ?? [], s.After ?? [], answer, totalMs, smoke ? null : firstMs,
                !smoke && s.Target is not null ? totalMs - firstMs : null));
        }
        return JsonSerializer.Serialize(new
        {
            dataset = smoke ? "synthetic-smoke" : unverifiedPilot ? "unverified-episode-pilot" : "operator-marked-verified",
            requestMode = smoke ? "single-request-smoke" : "playback-and-lookup-separated",
            qualityScored = false,
            translated = rows.Count(r => r.Answer?.Chinese is not null),
            requested = rows.Count,
            contextualCandidates = rows.Count(r => r.Answer?.Sense is not null),
            contextualRequested = rows.Count(r => r.Target is not null),
            rows
        }, Json);
    }

    private static bool ValidContext(string[]? lines) => lines is null || (lines.Length <= 2 && lines.All(line => line is not null));
}
