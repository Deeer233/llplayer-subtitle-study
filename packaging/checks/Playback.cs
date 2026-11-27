using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using static Program;

internal static class Playback
{
    private const string ClipExe = "ClipTools/ffmpeg.exe";
    private static string BundledFfmpeg => Bundle(ClipExe);
    internal static string FirstMp3 => InRun("fixture-english.mp3");
    internal static string SecondMp3 => InRun("fixture-corrected.mp3");

    internal static void MakeFixture()
    {
        if (!File.Exists(BundledFfmpeg)) throw new FileNotFoundException("Package has no bundled ffmpeg", BundledFfmpeg);
        string srt = InRun("fixture.srt");
        File.WriteAllText(srt, $"1\n00:00:01,000 --> 00:00:02,100\n{Sentence}\n\n2\n00:00:04,000 --> 00:00:05,100\n{SecondSentence}\n");
        Ffmpeg("-f", "lavfi", "-i", "color=c=black:s=128x72:r=25:d=7",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=7", "-f", "lavfi", "-i", "sine=frequency=880:duration=7",
            "-i", srt, "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s",
            "-c:v", "mpeg4", "-g", "25", "-c:a", "aac", "-c:s", "srt", "-threads", "1",
            "-metadata:s:a:0", "language=zho", "-disposition:a:0", "default",
            "-metadata:s:a:1", "language=eng", "-disposition:a:1", "0",
            "-metadata:s:s:0", "language=eng", FixtureVideo);
        Ffmpeg("-i", FixtureVideo, "-map", "0:2", "-t", "1.1", "-c:a", "libmp3lame", FirstMp3);
        Ffmpeg("-i", FixtureVideo, "-map", "0:2", "-t", "0.85", "-c:a", "libmp3lame", SecondMp3);
        // Supply a tiny dictionary to the package click handler without touching its installation or profile.
        string dict = Path.Combine(AppContext.BaseDirectory, "StudyDictionary.zip");
        using var stream = new FileStream(dict, FileMode.CreateNew);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("pi.tsv").Open());
        writer.WriteLine("pitch\tpitch\tpitch\t\tfixture dictionary entry");
        Pass("generated embedded English subtitles and two distinct audio tracks: zho stream 1/440 Hz; eng stream 2/880 Hz");
    }

    internal static void Lookup()
    {
        using var net = new NoNetwork();
        var cloud = LibraryChecks.Local("lookup-library", net);
        using var player = OpenPlayer();
        var controls = SubtitleHandlers(player, cloud);
        var word = New(ProductType("LLPlayer.Controls.WordClickedEventArgs"), Button.ClickEvent);
        Set(word, "Mouse", Enum.Parse(ProductType("LLPlayer.Controls.MouseClick"), "Left"));
        Set(word, "Words", Target);
        Set(word, "Text", Sentence);
        Set(word, "IsWord", true);
        Set(word, "WordOffset", Sentence.IndexOf(Target, StringComparison.Ordinal));
        Set(word, "SubIndex", 0);

        player.Play();
        Until(() => player.Status == Status.Playing && player.SubtitlesManager[0].GetCurrent()?.Text == Sentence,
            "embedded English cue did not appear while playing");
        Call(controls, "SelectableSubtitleText_OnWordClicked", new TextBlock(), word);
        Guard(player.Status == Status.Paused && Field<Border>(controls, "StudyCard").Visibility == Visibility.Visible,
            "word click did not pause and open the lookup card");
        Until(() => Field<TextBlock>(controls, "StudyDictionaryText").Text.Contains("fixture dictionary entry"),
            "the packaged lookup handler did not display the synthetic dictionary result");
        var captured = LibraryChecks.Rows(cloud).Single();
        Guard(Get<string>(captured, "English") == Sentence && Get<string>(captured, "Target") == Target,
            "lookup did not capture the current subtitle and selected word");
        Guard(Field<TextBlock>(controls, "StudyWord").Text == Target, "word label did not match selection");
        Pass("packaged word-click callback paused Flyleaf, rendered dictionary meaning and recorded the current English cue");

        long paused = player.CurTime;
        Call(controls, "CloseCard", new Button(), new RoutedEventArgs(Button.ClickEvent));
        Until(() => player.Status == Status.Playing && player.CurTime > paused + TimeSpan.FromMilliseconds(200).Ticks,
            "closing the word card did not resume advancing playback");
        Guard(Field<Border>(controls, "StudyCard").Visibility == Visibility.Collapsed && net.Calls == 0, "lookup card did not close cleanly");
        player.Pause();
        Seek(player, 1300);
        Call(controls, "SelectableSubtitleText_OnWordClicked", new TextBlock(), word);
        Until(() => Field<TextBlock>(controls, "StudyDictionaryText").Text.Contains("fixture dictionary entry"), "paused lookup did not complete");
        Call(controls, "CloseCard", new Button(), new RoutedEventArgs(Button.ClickEvent));
        Guard(player.Status == Status.Paused && player.CurTime < TimeSpan.FromSeconds(2.1).Ticks,
            "closing a lookup opened while paused wrongly resumed playback");
        Pass("close restored both originally playing and originally paused states");
    }

    internal static void Clip()
    {
        var cutter = Service("StudyClip");
        var actualTool = GetFullPath((string)Call(cutter, "Executable")!);
        Guard(actualTool.Equals(GetFullPath(BundledFfmpeg), StringComparison.OrdinalIgnoreCase),
            "package did not choose its bundled clip tool: " + actualTool);

        using var net = new NoNetwork();
        var cloud = LibraryChecks.Local("clip-library", net);
        using var player = OpenPlayer();
        Guard(player.VideoDemuxer.AudioStreams.Select(a => (a.StreamIndex, a.Language?.ISO6391))
            .SequenceEqual(new (int, string?)[] { (1, "zh"), (2, "en") }), "fixture must contain separate Chinese/English audio streams");
        var controls = SubtitleHandlers(player, cloud);
        var line = Call(controls, "CurrentDialogue") ?? throw new CheckFailure("no English dialogue at clip time");
        var rowId = (Guid?)Call(controls, "RecordCloudLookup", cloud, line, Target, "");
        Guard(rowId.HasValue, "production capture rejected a local English cue");
        var clip = Get(LibraryChecks.Row(cloud, rowId!.Value), "Clip")!;
        Guard(Get<int>(clip, "Track") == 2, "production capture did not choose the unique English audio stream 2");
        double start = Get<double>(clip, "Start"), end = Get<double>(clip, "End");
        Guard(Math.Abs(start - 1.0) < .05 && Math.Abs(end - 2.1) < .05, $"clip range was {start:F3}..{end:F3} seconds");

        string english = Prepare(clip);
        string chinese = Prepare(LibraryChecks.Clip(start, end, track: 1));
        try
        {
            var hi = Measure(english);
            var lo = Measure(chinese);
            Guard(Math.Abs(hi.Hz - 880) <= 6 && Math.Abs(lo.Hz - 440) <= 6, $"decoded English {hi.Hz:F1} Hz / control {lo.Hz:F1} Hz");
            Guard(Math.Abs(hi.Seconds - (end-start)) < .13 && Math.Abs(lo.Seconds - (end-start)) < .13, "decoded clip length did not match its subtitle");
            string artifact = InRun("actual-english-clip.mp3");
            File.Copy(english, artifact);
            Pass($"actual StudyClip output = {hi.Hz:F1} Hz / {hi.Seconds:F3}s; same-cut Chinese control = {lo.Hz:F1} Hz / {lo.Seconds:F3}s");
        }
        finally { Call(cutter, "ForgetPreview", english); Call(cutter, "ForgetPreview", chinese); }
        Reject<InvalidDataException>(() => Prepare(LibraryChecks.Clip(start, end, track: -1)), "refused unknown audio track");
        Guard(net.Calls == 0, "clip capture used network");
    }

    private static string GetFullPath(string path) => Path.GetFullPath(path);
    private static string Prepare(object clip)
    {
        string path = (string)Await(Call(Service("StudyClip"), "Prepare", clip, CancellationToken.None))!;
        Guard(Path.GetDirectoryName(path)!.Equals(InRun("temp"), StringComparison.OrdinalIgnoreCase), "clip escaped run TEMP directory");
        return path;
    }

    internal static (double Hz, double Seconds) Measure(string path)
    {
        byte[] pcm = Ffmpeg("-i", path, "-ac", "1", "-ar", "8000", "-f", "s16le", "pipe:1");
        int samples = pcm.Length / 2;
        Guard(samples > 3200, "MP3 did not yield enough decoded PCM");
        int rising = 0;
        short prev = BitConverter.ToInt16(pcm, 800 * 2);
        for (int i = 801; i < samples - 800; i++)
        {
            short value = BitConverter.ToInt16(pcm, i * 2);
            if (prev <= 0 && value > 0) rising++;
            prev = value;
        }
        return (8000d * rising / (samples - 1600 - 1), samples / 8000d);
    }

    private static byte[] Ffmpeg(params string[] args)
    {
        var start = Hidden(BundledFfmpeg);
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-n" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch fixture ffmpeg");
        using var bytes = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Fixture ffmpeg timed out");
        }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("Fixture ffmpeg: " + error.Result.Trim());
        return bytes.ToArray();
    }

    private static Player OpenPlayer()
    {
        Environment.SetEnvironmentVariable("OLLAMA_NO_CLOUD", null);
        Environment.SetEnvironmentVariable("LLPLAYER_STUDY_LOCAL_KEY", null);
        Guard(Get<bool>(Service("StudyLocalLookup"), "Enabled") == false, "model must remain disabled in regression");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var config = (EngineConfig)Call(Service("FlyleafLoader"), "DefaultEngineConfig")!;
        config.FFmpegPath = Bundle("FFmpeg");
        config.PluginsPath = InRun("no-plugins");
        config.UIRefresh = false;
        Engine.Start(config);
        Guard(typeof(Player).Assembly.Location.Equals(Bundle("FlyleafLib.dll"), StringComparison.OrdinalIgnoreCase), "wrong Flyleaf assembly");
        // Use the package loader only with an empty probe base directory, never from the installation.
        Guard(!File.Exists(Path.Combine(AppContext.BaseDirectory, "LLPlayer.PlayerConfig.json")), "unexpected player config in probe directory");
        var player = (Player)Call(Service("FlyleafLoader"), "CreateFlyleafPlayer")!;
        player.Config.Player.AutoPlay = false;
        player.Config.Audio.Enabled = false;
        player.Config.Video.VideoAcceleration = false;
        player.Config.Subtitles.SearchOnline = false;
        OpenCompletedArgs? completed = null;
        void Opened(object? sender, OpenCompletedArgs e) { if (!e.IsSubtitles) Volatile.Write(ref completed, e); }
        try
        {
            player.OpenCompleted += Opened;
            player.OpenAsync(FixtureVideo);
            Until(() => Volatile.Read(ref completed) is not null, "synthetic video did not open");
            Guard(completed!.Success && player.CanPlay, "synthetic video not playable: " + completed.Error);
            Until(() => player.SubtitlesManager[0].LanguageSource?.ISO6391 == "en", "packaged engine did not choose embedded English subtitles");
            Guard(player.Subtitles[0].StreamIndex == 3 && player.Playlist.Selected.ExternalSubtitlesStreams[0] is null,
                "wrong subtitle selected");
            Seek(player, 1250);
            Until(() => player.SubtitlesManager[0].GetCurrent()?.Text == Sentence, "no expected first subtitle after seek");
            return player;
        }
        catch { player.Dispose(); throw; }
        finally { player.OpenCompleted -= Opened; }
    }

    private static void Seek(Player player, int position)
    {
        int completed = int.MinValue;
        void Done(object? sender, int value) => Volatile.Write(ref completed, value);
        player.SeekCompleted += Done;
        try
        {
            player.SeekAccurate(position);
            Until(() => Volatile.Read(ref completed) == position, $"paused seek did not reach {position}ms");
        }
        finally { player.SeekCompleted -= Done; }
    }

    private static object SubtitleHandlers(Player player, object cloud)
    {
        // Inject only the state consumed by the handlers. The real constructor opens shared local notes/library.
        var control = RuntimeHelpers.GetUninitializedObject(ProductType("LLPlayer.Controls.SubtitlesControl"));
        var manager = RuntimeHelpers.GetUninitializedObject(Service("FlyleafManager"));
        FieldInfo(manager, "<Player>k__BackingField").SetValue(manager, player);
        FieldInfo(control, "<FL>k__BackingField").SetValue(control, manager);
        FieldInfo(control, "_cloud").SetValue(control, cloud);
        FieldInfo(control, "_replay").SetValue(control, New(Service("StudyLineReplay")));
        FieldInfo(control, "StudyCard").SetValue(control, new Border());
        FieldInfo(control, "StudyDictionarySection").SetValue(control, new Expander());
        foreach (string name in new[] { "StudyWord", "StudySense", "StudyCardChinese", "StudyDictionaryText" })
            FieldInfo(control, name).SetValue(control, new TextBlock());
        return control;
    }

    private static void Set(object item, string property, object value) => item.GetType().GetProperty(property, Members)!.SetValue(item, value);
}
