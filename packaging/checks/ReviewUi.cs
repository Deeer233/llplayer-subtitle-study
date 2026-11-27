using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static Program;

internal static class ReviewUi
{
    private static readonly (string Field, string Value)[] ChangedFields =
    [
        ("_expression", "pitch the idea"), ("_meaning", "介绍"),
        ("_english", "They pitch the idea to us!"), ("_target", "idea"),
        ("_chinese", "测试译文"), ("_source", "Fixture edited"), ("_from", "1.1"), ("_to", "2.0"),
    ];

    internal static void Entry()
    {
        var menu = ProductType("LLPlayer.Resources.PopupMenu");
        var subtitles = ProductType("LLPlayer.Controls.SubtitlesControl");
        Guard(Get<Visibility>(menu, "PrivateReviewVisibility") == Visibility.Visible,
            "packaged right-click review menu is disabled");
        Guard(Get<Visibility>(subtitles, "PrivateReviewVisibility") == Visibility.Visible,
            "packaged subtitle-panel review entry is disabled");
        Guard(Service("StudyCloud").GetField("AnkiBuild", Members)?.GetRawConstantValue() is true,
            "package is not the local-only Anki build by default");
        Guard(!Get<bool>(New(Service("AppConfig")), "StudyBilingual"), "clean player defaults are not English-first");
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var network = new NoNetwork();
        var cloud = LibraryChecks.Local("entry-library", network);
        var window = ReviewWindow(cloud, null);
        try
        {
            foreach (var (owner, name) in new[] { (menu, "OpenPrivateReview"), (subtitles, "ShowCloud") })
                Guard(CalledMethods(owner.GetMethod(name, Members)!).Any(m => m.Name == "Open" && m.DeclaringType == window.GetType()),
                    $"packaged {owner.Name}.{name} does not route to {window.GetType().Name}");
            var tabs = Field<TabControl>(window, "_tabs");
            var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString() ?? "").ToArray();
            Guard(headers.Any(s => s.Contains("Anki", StringComparison.OrdinalIgnoreCase)) && tabs.Items.Count == 3,
                "expected three review/Anki/library tabs: " + string.Join(" / ", headers));
            // Generate the actual TabControl items without asking WPF to show its hosting Window.
            tabs.ApplyTemplate();
            tabs.Measure(new Size(600, 650));
            tabs.Arrange(new Rect(0, 0, 600, 650));
            tabs.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Guard(tabs.SelectedIndex == 0, $"default tab index was {tabs.SelectedIndex}, expected the first review tab");
            Guard(!Field<Button>(window, "_verify").IsEnabled && !Field<Button>(window, "_saveForPair").IsEnabled
                && !Field<Button>(window, "_export").IsEnabled, "empty library allowed confirmation or export");
            Guard(Field<ListBox>(window, "_rows").Items.Count == 0 && network.Calls == 0 && !window.IsVisible,
                "empty offscreen review window had unexpected data or I/O");
            Pass($"right-click and subtitle entries visible and routed to {window.GetType().Name}; English-first and local Anki defaults; first tab '{headers[0]}'; empty confirm/export disabled");
        }
        finally { window.Close(); }
    }

    internal static void Preview(bool cardButtons = false)
    {
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var network = new NoNetwork();
        var cloud = LibraryChecks.Local(cardButtons ? "card-ui-library" : "preview-library", network);
        Guid id = LibraryChecks.Observe(cloud);
        Guid second = cardButtons ? LibraryChecks.Observe(cloud, 1) : Guid.Empty;
        var window = ReviewWindow(cloud, id);
        var problems = new List<string>();
        try
        {
            var verify = Field<Button>(window, "_verify");
            var pair = Field<Button>(window, "_saveForPair");
            var rows = Field<ListBox>(window, "_rows");
            Guard(rows.SelectedItem is { } selected && Get<Guid>(selected, "Id") == id, "constructor did not select requested lookup row");
            Field<TextBox>(window, "_meaning").Text = Meaning;
            var preview = FindPreview(window);
            var audioField = window.GetType().GetFields(Members).SingleOrDefault(f => !f.IsStatic && f.FieldType == typeof(MediaPlayer))
                ?? throw new MissingMemberException("review window MediaPlayer is unavailable for offscreen playback");
            MediaPlayer? audio = null;
            int completed = 0;
            Exception? mediaFailure = null;
            var click = typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!;

            bool Locked() => !verify.IsEnabled && !pair.IsEnabled;
            void Record(bool pass, string message)
            {
                if (pass) Pass(message);
                else { Console.WriteLine("FAIL " + message); problems.Add(message); }
            }
            void Listen(bool recordPhases, bool interrupt = false)
            {
                var current = (MediaPlayer?)audioField.GetValue(window);
                Watch(current);
                int last = completed;
                mediaFailure = null;
                Guard(preview.IsEnabled, "preview button was unavailable on a valid selected row");
                click.Invoke(preview, null);
                Until(() =>
                {
                    Watch((MediaPlayer?)audioField.GetValue(window));
                    return mediaFailure is not null || completed != last ||
                        (audio?.NaturalDuration.HasTimeSpan == true && audio.Position > TimeSpan.Zero);
                }, "real preview did not start", 20);
                Guard(mediaFailure is null, "WPF preview failed: " + mediaFailure);
                var duration = audio?.NaturalDuration is { HasTimeSpan: true } known ? known.TimeSpan : TimeSpan.Zero;
                var position = audio?.Position ?? TimeSpan.Zero;
                if (interrupt)
                {
                    Guard(completed == last && duration > position && position > TimeSpan.Zero, "did not observe a partial preview to interrupt");
                    var field = Field<TextBox>(window, "_english");
                    string before = field.Text;
                    field.Text += "!";
                    field.Text = before;
                    bool unlocked = false;
                    var wait = Stopwatch.StartNew();
                    Until(() =>
                    {
                        unlocked |= !Locked();
                        return wait.Elapsed > duration + TimeSpan.FromMilliseconds(150);
                    }, "interrupted-media observation timed out", 5);
                    Record(!unlocked && completed == last && LibraryChecks.CardsOf(cloud).Length == 0,
                        "editing during a partial clip cancels its audition; no unlock or save after its former end time");
                    return;
                }
                if (recordPhases)
                {
                    Record(completed == last && duration > TimeSpan.Zero && position < duration
                        && Locked() && LibraryChecks.CardsOf(cloud).Length == 0,
                        $"both confirmation buttons locked during real playback ({position.TotalMilliseconds:F0} ms)");
                }
                bool earlyUnlock = false;
                Until(() =>
                {
                    if (completed != last || mediaFailure is not null) return true;
                    earlyUnlock |= !Locked();
                    return false;
                }, "no actual MediaEnded event", 8);
                Guard(mediaFailure is null, "WPF preview failed: " + mediaFailure);
                Until(() => verify.IsEnabled && pair.IsEnabled, "confirmation not enabled after actual MediaEnded", 3);
                if (recordPhases) Record(!earlyUnlock,
                    $"no early unlock through actual WPF MediaEnded; both enabled after a {duration.TotalMilliseconds:F0} ms clip");
            }

            void Watch(MediaPlayer? player)
            {
                if (player is null || ReferenceEquals(audio, player)) return;
                audio = player;
                audio.IsMuted = true;
                audio.MediaEnded += (_, _) => completed++;
                audio.MediaFailed += (_, e) => mediaFailure = e.ErrorException;
            }

            if (cardButtons)
            {
                Listen(recordPhases: false);
                click.Invoke(pair, null);
                Until(() => LibraryChecks.CardsOf(cloud).Length == 1 && Locked(), "two-dialogue button did not save first line");
                var draft = LibraryChecks.CardsOf(cloud).Single();
                Guard(Get<string>(draft, "Status") == "draft" && Get<int>(draft, "Count") == 1 && LibraryChecks.Jobs(cloud).Length == 0,
                    "two-dialogue button queued the first line instead of saving a draft");
                Call(window, "Select", second);
                Guard(Get<Guid>(rows.SelectedItem, "Id") == second, "could not select the second fixture line");
                Field<TextBox>(window, "_meaning").Text = Meaning;
                Listen(recordPhases: false);
                click.Invoke(pair, null);
                Until(() => LibraryChecks.Jobs(cloud).Length == 1 && Locked(), "two-dialogue button did not queue the second line");
                var active = LibraryChecks.CardsOf(cloud).Single();
                Guard(Get<string>(active, "Status") == "active" && Get<int>(active, "Count") == 2
                    && Get<Guid>(LibraryChecks.Jobs(cloud).Single(), "ExampleId") == second && network.Calls == 0,
                    "two-dialogue UI queued the wrong example or sense");
                Pass("actual two-dialogue save button kept the first as an unqueued draft and activated/queued the second");
                return;
            }

            Record(Locked(), "both confirmation buttons initially locked even after entering a meaning");
            Listen(recordPhases: false, interrupt: true);
            Listen(recordPhases: true);
            foreach (var (name, value) in ChangedFields)
            {
                var field = Field<TextBox>(window, name);
                string prior = field.Text;
                Guard(prior != value, "field change fixture is a no-op: " + name);
                field.Text = value;
                Record(Locked(), name + " change relocks both confirmation buttons");
                field.Text = prior;
                if (Locked()) Listen(recordPhases: false);
            }
            Guard(LibraryChecks.CardsOf(cloud).Length == 0, "preview or field edits alone persisted a card");
            click.Invoke(verify, null);
            Until(() => LibraryChecks.CardsOf(cloud).Any(c => Get<string>(c, "Status") == "active"),
                "actual one-dialogue verify button did not save the listened card");
            Guard(LibraryChecks.Jobs(cloud).Length == 1 && network.Calls == 0, "UI verify did not stage exactly one Anki job locally");
            Pass("actual confirm button saved a listened single-dialogue card and queued it locally");
        }
        finally { window.Close(); }
        Guard(problems.Count == 0, "preview regressions: " + string.Join("; ", problems));
    }

    private static Window ReviewWindow(object cloud, Guid? select)
    {
        Type type;
        try { type = Service("PolishedReviewWindow"); }
        catch (TypeLoadException) { type = Service("CloudReviewWindow"); }
        if (type.GetConstructor(Members & ~BindingFlags.Static, [cloud.GetType(), typeof(Guid?), typeof(bool)]) is not null)
            return (Window)New(type, cloud, select, true);
        return (Window)New(type, cloud, select);
    }

    // Check where the entries in the actual DLL route, without executing either profile-opening handler.
    private static IEnumerable<MethodBase> CalledMethods(MethodInfo caller)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(op => unchecked((ushort)op.Value));
        byte[] code = caller.GetMethodBody()!.GetILAsByteArray()!;
        for (int i = 0; i < code.Length;)
        {
            int prefix = code[i++];
            var op = opcodes[(ushort)(prefix == 0xfe ? 0xfe00 | code[i++] : prefix)];
            if ((op == OpCodes.Call || op == OpCodes.Callvirt) && caller.Module.ResolveMethod(BitConverter.ToInt32(code, i)) is { } callee)
                yield return callee;
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 * (1 + BitConverter.ToInt32(code, i)),
                _ => 4,
            };
        }
    }

    private static Button FindPreview(Window window)
    {
        var buttons = window.GetType().GetFields(Members).Where(f => !f.IsStatic && typeof(Button).IsAssignableFrom(f.FieldType))
            .Select(f => (Button?)f.GetValue(window)).OfType<Button>().Distinct().ToArray();
        var named = window.GetType().GetField("_previewButton", Members)?.GetValue(window) as Button;
        return named ?? buttons.FirstOrDefault(b => b.Content?.ToString() is { } s
            && (s.Contains("试听") || s.Contains("播放原声")) && !s.Contains("重听"))
            ?? throw new MissingMemberException("preview button is unavailable for offscreen playback");
    }
}
