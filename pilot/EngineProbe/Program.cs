using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using LLPlayer.Services;

internal static class Program
{
    private const int TargetMs = 4250;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["packaged-model-batch", var samples, var verification])
        {
            var input = File.ReadAllBytes(samples);
            using var inputStream = new MemoryStream(input);
            using var reader = new StreamReader(inputStream);
            var unverified = verification switch
            {
                "verified" => false,
                "unverified" => true,
                _ => throw new ArgumentException("Expected verified or unverified samples."),
            };
            if (!StudyLocalLookup.Enabled) throw new InvalidOperationException("Packaged local model is not enabled.");
            var report = System.Text.Json.Nodes.JsonNode.Parse(StudyModelProbe.Run(reader.ReadToEnd(), unverified, false,
                CancellationToken.None).GetAwaiter().GetResult())!.AsObject();
            using var assembly = File.OpenRead(typeof(StudyLocalLookup).Assembly.Location);
            report["playerAssemblySha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(assembly));
            report["inputSha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input));
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(report.ToJsonString());
            return;
        }

        var (mode, root, ffmpeg) = (args[0], args[1], args[2]);
        var config = FlyleafLoader.DefaultEngineConfig();
        config.FFmpegPath = ffmpeg;
        config.UIRefresh = false;
        if (mode is "episode" or "episode-model")
        {
            config.LogOutput = Path.Combine(root, "episode-engine-" + Guid.NewGuid().ToString("N") + ".log");
            config.LogLevel = LogLevel.Info;
            config.LogCachedLines = 1;
            Console.WriteLine("Engine log: " + config.LogOutput);
        }
        Engine.Start(config);

        using var player = FlyleafLoader.CreateFlyleafPlayer();
        player.Config.Player.AutoPlay = mode == "autoplay";
        player.Config.Audio.Enabled = mode == "episode";
        player.Config.Video.VideoAcceleration = false;
        // Bind the real manager but isolate progress from the user's profile.
        _ = Activator.CreateInstance(typeof(FlyleafManager), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [player, null, Path.Combine(root, "progress")], null);
        string embedded = Path.Combine(root, "embedded.mp4");
        string sidecar = Path.Combine(root, "sidecar.mkv");
        string noEnglish = Path.Combine(root, "no-english.mkv");
        if (mode == "episode-model")
        {
            Open(player, args[3]);
            Until(() => HasEnglish(player), "episode English loaded for translation");
            Check(player.Subtitles[0].StreamIndex == int.Parse(args[4]), "uses the expected embedded English track for translation");
            CheckPackagedModel(player);
            return;
        }
        if (mode == "episode")
        {
            var path = args[3];
            var expected = int.Parse(args[4]);
            var target = int.Parse(args[5]);
            player.Audio.Mute = true;
            Open(player, path);
            Until(() => HasEnglish(player), "episode English loaded");
            Check(player.Subtitles[0].StreamIndex == expected && player.Playlist.Selected.ExternalSubtitlesStreams[0] is null,
                $"episode uses embedded English stream {expected}");
            var audio = player.Audio.Streams.FirstOrDefault(s => s.StreamIndex == player.Audio.StreamIndex);
            Check(player.Audio.IsOpened && audio?.Language?.ISO6391 == "en", $"episode uses English audio stream {player.Audio.StreamIndex}");
            if (args.Length > 6 && args[6] == "pilot-dialogue") CheckPilotDialogue(player);
            Seek(player, target);
            Until(() => player.SubtitlesManager[0].GetCurrent() is not null, "real subtitle visible at target time");
            var cue = player.SubtitlesManager[0].GetCurrent()!;
            var replay = new StudyLineReplay();
            var dispatcher = Dispatcher.CurrentDispatcher;
            var firstAudio = long.MinValue;
            var lastAudio = long.MinValue;
            var nonSilentFrames = 0;
            var audioBytes = new byte[0];
            player.Audio.SamplesAdded += (_, frame) =>
            {
                Interlocked.CompareExchange(ref firstAudio, frame.Timestamp, long.MinValue);
                Interlocked.Exchange(ref lastAudio, frame.Timestamp);
                if (frame.dataLen <= 0 || frame.dataPtr == IntPtr.Zero) return;
                if (audioBytes.Length < frame.dataLen) audioBytes = new byte[frame.dataLen];
                Marshal.Copy(frame.dataPtr, audioBytes, 0, frame.dataLen);
                if (audioBytes.AsSpan(0, frame.dataLen).ContainsAnyExcept((byte)0)) Interlocked.Increment(ref nonSilentFrames);
            };
            int replaySeekCompletions = 0;
            player.SeekCompleted += (_, ms) => dispatcher.InvokeAsync(() =>
            {
                replay.SeekCompleted(player, ms);
                replaySeekCompletions++;
            });
            player.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Player.CurTime)) dispatcher.InvokeAsync(() => replay.TimeChanged(player));
            };
            Check(replay.Start(player, cue.StartTime, cue.EndTime, 0), "real episode starts the production replay controller");
            Until(() => Volatile.Read(ref nonSilentFrames) > 0, "muted replay decodes nonzero PCM from the English track");
            Until(() => player.Status == Status.Paused && player.CurTime >= cue.EndTime.Ticks, "replay pauses at the end of the real subtitle");
            var first = TimeSpan.FromTicks(Volatile.Read(ref firstAudio));
            var last = TimeSpan.FromTicks(Volatile.Read(ref lastAudio));
            var stopped = TimeSpan.FromTicks(player.CurTime);
            Console.WriteLine($"Replay timings: first submitted audio {first.TotalMilliseconds:F0} ms, last {last.TotalMilliseconds:F0} ms, paused {stopped.TotalMilliseconds:F0} ms");
            Check(Math.Abs((first - cue.StartTime).TotalMilliseconds) <= 120 && last - cue.EndTime <= TimeSpan.FromMilliseconds(200),
                $"submitted English audio frames {first.TotalMilliseconds:F0}..{last.TotalMilliseconds:F0} ms for subtitle {cue.StartTime.TotalMilliseconds:F0}..{cue.EndTime.TotalMilliseconds:F0} ms");
            Check(stopped - cue.EndTime <= TimeSpan.FromMilliseconds(200), $"paused at {stopped.TotalMilliseconds:F0} ms; nonzero PCM frames: {nonSilentFrames}");

            var beforeSeek = replaySeekCompletions;
            var beforeAudio = Volatile.Read(ref lastAudio);
            Check(replay.Start(player, cue.StartTime, cue.EndTime, 0), "review starts a pending replay");
            replay.FinishReview(player, sameTrack: true, resume: false);
            Until(() => replaySeekCompletions > beforeSeek, "pending seek completion delivered after closing review");
            Check(player.Status == Status.Paused && Volatile.Read(ref lastAudio) == beforeAudio,
                "closing a paused review cancels a pending replay without starting playback");

            Check(replay.Start(player, cue.StartTime, cue.EndTime, 0), "review starts another replay");
            Until(() => player.Status == Status.Playing, "review replay is playing before closing it");
            replay.FinishReview(player, sameTrack: true, resume: false);
            Check(player.Status == Status.Paused, "closing review during replay restores the original paused state");

            var shortEnd = cue.StartTime + TimeSpan.FromMilliseconds(450);
            Check(replay.Start(player, cue.StartTime, shortEnd, 0), "review starts a short replay before testing resume");
            Until(() => player.Status == Status.Playing, "short review replay reaches playing state");
            replay.FinishReview(player, sameTrack: true, resume: true);
            Until(() => player.Status == Status.Playing && player.CurTime > shortEnd.Ticks,
                "closing review from playing mode restores playback past the canceled clip boundary");
            player.Pause();

            replay.FinishReview(player, sameTrack: false, resume: true);
            Check(player.Status == Status.Paused, "closing review after a track change does not restart playback");
            return;
        }
        if (mode is "resume" or "autoplay")
        {
            Open(player, embedded);
            Check(NearTarget(player), $"{mode}: engine opened at {TimeSpan.FromTicks(player.CurTime).TotalMilliseconds:F0} ms");
            if (mode == "autoplay")
            {
                Until(() => player.CurTime > TimeSpan.FromMilliseconds(TargetMs + 180).Ticks, "autoplay advances after resume");
                Check(player.IsPlaying, "autoplay resumes playback");
            }
            else Check(player.Status == Status.Paused, "paused open remains paused");
            return;
        }

        Open(player, embedded);
        Until(() => HasEnglish(player), "embedded English loads");
        Check(player.Playlist.Selected.ExternalSubtitlesStreams[0] is null, "embedded English wins over an available external English track");
        Seek(player, TargetMs);
        Open(player, sidecar);
        Until(() => HasEnglish(player), "same-name local English loads");
        Check(player.Playlist.Selected.ExternalSubtitlesStreams[0]?.Url == Path.Combine(root, "sidecar.srt"), "same-name English sidecar is selected");
        Check(player.CurTime == 0, "opening another episode does not reuse prior progress");
        Open(player, noEnglish);
        Check(player.CanPlay && !HasEnglish(player), "non-English media still opens without an English learning track");
        Open(player, embedded);
        Check(NearTarget(player), "reopening after a media switch restores prior progress");
    }

    private static bool HasEnglish(Player player) => player.SubtitlesManager[0].LanguageSource?.ISO6391 == "en";
    private static bool NearTarget(Player player) => Math.Abs(player.CurTime - TimeSpan.FromMilliseconds(TargetMs).Ticks) <= TimeSpan.FromMilliseconds(110).Ticks;

    private static void CheckPilotDialogue(Player player)
    {
        OpenChinese(player);
        var address = DialogueAt(player, 137000);
        Until(() => StudySubtitleAccess.ChineseFor(player, address)?.Contains("87104", StringComparison.Ordinal) == true,
            "actual Chinese subtitle with the current address, not its neighboring lines");
        var firstHalf = DialogueAt(player, 173500);
        var secondHalf = DialogueAt(player, 175750);
        Check(firstHalf.Start == TimeSpan.FromMilliseconds(172214) && firstHalf.End == TimeSpan.FromMilliseconds(176760)
            && firstHalf.Text == "Things that you'll come to learn about me in the next few days."
            && secondHalf.Start == firstHalf.Start && secondHalf.End == firstHalf.End && secondHalf.Text == firstHalf.Text,
            "both real subtitle halves yield the same complete sentence (172214..176760 ms)");
        var unaligned = DialogueAt(player, 376500);
        Check(unaligned.Text == "What the hell is this?" && StudySubtitleAccess.ChineseFor(player, unaligned) is null,
            "when the real Chinese cue fails timing selection, it returns no neighboring subtitle");
        var current = new StudySubtitleLine(unaligned, unaligned, StudyCheck.Unchecked);
        Check(StudyTranslationQueue.Plan(current, null, [], _ => null, false).Single() == unaligned,
            "can request the actual unmatched current English sentence, not an earlier one");
    }

    private static void CheckPackagedModel(Player player)
    {
        Check(StudyLocalLookup.Enabled, "packaged local model is explicitly enabled for this probe");
        OpenChinese(player);
        var address = DialogueAt(player, 137000);
        Until(() => StudySubtitleAccess.ChineseFor(player, address)?.Contains("87104", StringComparison.Ordinal) == true,
            "current sentence can use embedded Chinese");
        var gap = DialogueAt(player, 376500);
        Check(gap.Text == "What the hell is this?" && StudySubtitleAccess.ChineseFor(player, gap) is null,
            "current sentence without a matching reference is the expected real dialogue");
        var exchange = DialogueAt(player, 379000);
        var turns = exchange.SpeakerTurns();
        Check(turns.Select(turn => turn.Text).SequenceEqual(["Hey.", "It's veggie bacon."])
            && exchange.ContextForTarget("veggie")?.Text == "It's veggie bacon.",
            "real same-cue speakers split, and veggie is grounded only in the second speaker");

        var previousContext = SynchronizationContext.Current;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var requested = new List<string>();
            var updated = false;
            var queue = new StudyTranslationQueue(async (line, token) =>
            {
                requested.Add(line.Text);
                return await StudyLocalLookup.TranslatePlayback(line, token);
            }, () => updated = true);

            void Prepare(StudyDialogue line)
            {
                var current = new StudySubtitleLine(line, line, StudyCheck.Unchecked);
                queue.Prepare(StudyTranslationQueue.Plan(current, StudySubtitleAccess.ChineseFor(player, line), [], _ => null, false));
            }

            Prepare(address);
            Check(requested.Count == 0 && !queue.Pending(address), "embedded Chinese reference does not call the model");
            Prepare(gap);
            Until(() => updated, "real model completes the packaged playback translation queue", 40);
            Check(requested.SequenceEqual([gap.Text]) && !string.IsNullOrWhiteSpace(queue.Find(gap)) && queue.Find(address) is null,
                "real model translation is cached only for the current unmatched dialogue");

            using var exchangeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            var exchangeTranslation = StudyLocalLookup.TranslatePlayback(exchange, exchangeTimeout.Token);
            Until(() => exchangeTranslation.IsCompleted, "real model completes both separate speaker requests", 81);
            var exchangeChinese = exchangeTranslation.GetAwaiter().GetResult();
            Check(exchangeChinese?.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)) == 2,
                "real exchange receives two distinct Chinese lines in speaker order");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var word = StudyLocalLookup.Get(address, "live", timeout.Token);
            Until(() => word.IsCompleted, "real model completes the separate packaged word lookup", 41);
            var answer = word.GetAwaiter().GetResult();
            Check(answer is { Sense.Length: > 0, Lemma.Length: > 0 } && answer.Lemma.Contains("live", StringComparison.OrdinalIgnoreCase),
                "real word lookup returns a contextual candidate for live in the actual address dialogue");
            Console.WriteLine($"CANDIDATE playback: {queue.Find(gap)}");
            Console.WriteLine($"CANDIDATE exchange:\n{exchangeChinese}");
            Console.WriteLine($"CANDIDATE lookup: {answer!.Expression} / {answer.Lemma} / {answer.Sense}");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void OpenChinese(Player player)
    {
        StudySubtitleAccess.OpenSimplifiedChinese(player);
        Until(() => player.Subtitles[1].IsOpened && player.Subtitles[1].StreamIndex == 2
            && player.SubtitlesManager[1].LanguageSource?.ISO6391 == "zh", "actual simplified Chinese track 2, not traditional track 3");
    }

    private static StudyDialogue DialogueAt(Player player, int millis)
    {
        Seek(player, millis);
        Until(() => StudySubtitleAccess.EnglishWindow(player).Index >= 0, $"cached English visible at {millis} ms");
        var (cues, index) = StudySubtitleAccess.EnglishWindow(player);
        return StudyDialogue.Build(cues, index);
    }

    private static void Open(Player player, string path)
    {
        OpenCompletedArgs? result = null;
        void Finished(object? sender, OpenCompletedArgs args) { if (!args.IsSubtitles) Volatile.Write(ref result, args); }
        player.OpenCompleted += Finished;
        try
        {
            player.OpenAsync(path);
            Until(() => Volatile.Read(ref result) is not null, "media opens: " + Path.GetFileName(path));
            Check(result!.Success && player.CanPlay, "playable: " + Path.GetFileName(path) + (result.Error is { } error ? ": " + error : ""));
        }
        finally { player.OpenCompleted -= Finished; }
    }

    private static void Seek(Player player, int ms)
    {
        int result = int.MinValue;
        void Finished(object? sender, int value) => Volatile.Write(ref result, value);
        player.SeekCompleted += Finished;
        try
        {
            player.SeekAccurate(ms);
            Until(() => Volatile.Read(ref result) != int.MinValue, "paused seek completes");
            Check(result == ms && Math.Abs(player.CurTime - TimeSpan.FromMilliseconds(ms).Ticks) <= TimeSpan.FromMilliseconds(110).Ticks,
                $"decoded seek hits {ms} ms");
        }
        finally { player.SeekCompleted -= Finished; }
    }

    private static void Until(Func<bool> success, string label, int seconds = 12)
    {
        var timeout = Stopwatch.StartNew();
        while (!success() && timeout.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
        Check(success(), label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
