using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace OffscreenUiQa;

internal static partial class Program
{
    private static string TestInterfaces(Product api)
    {
        Require(api.HasLegacyConstructor, "The two-parameter constructor has been removed.");
        var fixture = api.Seed("interface");
        using var opened = api.Open(fixture);
        var window = opened.Window;
        Require(Read<bool>(window, "_mutePreview"), "The three-parameter constructor did not enable mutePreview.");
        Require(Read<string>(window, "_dataFolder") == fixture.Root, "Window data directory is not the sandbox fixture.");
        Require(Near(window.MinWidth, SmallWidth) && Near(window.MinHeight, SmallHeight), "Small-window minimum dimensions changed.");
        return "Both overloads present; window constructed with mute enabled, no HWND; explicit fixture root, startWorker=false, localOnly=true.";
    }

    private static string TestLayout(Product api, double width, double height, double scale, string filename)
    {
        using var opened = api.Open(api.Seed(scale == 1 ? "regular" : "small"));
        var window = opened.Window;
        using var surface = new Surface(window, width, height, scale);
        var image = surface.Snapshot(filename);
        var feedback = InView(surface, Read<TextBlock>(window, "_feedback"), "idle feedback");
        var tabs = new[] { "_reviewTab", "_ankiTab", "_importTab" }
            .Select(name => InView(surface, Read<TabItem>(window, name), name)).ToArray();
        NoOverlap(tabs, "Tab headers");
        var buttons = new[] { "_previewButton", "_replay", "_verify" };
        var commands = buttons.Select(name => CheckButton(surface, window, name)).ToArray();
        NoOverlap(commands, "Primary buttons");

        var editor = Read<StackPanel>(window, "_editor");
        var scroll = Ancestor<ScrollViewer>(editor);
        var viewport = InView(surface, scroll, "review editor scroll viewer");
        _ = InView(surface, Read<ListBox>(window, "_rows"), "lookup list");
        Require(commands.Max(rect => rect.Bottom) <= viewport.Top + 1 && viewport.Bottom <= feedback.Top + 1,
            $"Primary actions, editor and feedback need separate rows (editor {Format(viewport)}, feedback {Format(feedback)}).");
        Require(Near(VisualTreeHelper.GetDpi(Read<TextBlock>(window, "_feedback")).DpiScaleX, scale), "Status label did not inherit WPF DPI.");
        if (scale > 1)
        {
            Require(scroll.ScrollableHeight > 1 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible && scroll.ScrollableWidth < 1,
                $"Small editor needs a vertical scroll path without horizontal overflow (range {scroll.ScrollableWidth:0.##}x{scroll.ScrollableHeight:0.##}).");
            scroll.ScrollToEnd();
            surface.Layout();
            Require(Near(scroll.VerticalOffset, scroll.ScrollableHeight, 1), "Small editor cannot scroll to its last field.");
            scroll.ScrollToHome();
            surface.Layout();
            Require(Near(scroll.VerticalOffset, 0, 1), "Small editor cannot return to its first field.");
        }
        return $"{image.WidthPx}x{image.HeightPx} PNG, content DPI {image.ContentDpiX:0.0}; editor viewport {Format(viewport)}, vertical scroll range {scroll.ScrollableHeight:0.##} DIP.";
    }

    private static Rect CheckButton(Surface surface, Window window, string name)
    {
        var button = Read<Button>(window, name);
        var bounds = InView(surface, button, name);
        _ = InView(surface, Descendant<TextBlock>(button), name + " label");
        return bounds;
    }

    private static string TestExtraTabs(Product api)
    {
        using var opened = api.Open(api.Seed("extra-tabs"));
        var window = opened.Window;
        using var surface = new Surface(window, SmallWidth, SmallHeight, 2);
        var tabs = Read<TabControl>(window, "_tabs");
        tabs.SelectedIndex = 1;
        surface.Layout();
        var buttons = new[] { "_connect", "_retryAnki", "_queueExisting", "_export" };
        var controls = buttons.Select(name => CheckButton(surface, window, name)).ToArray();
        NoOverlap(controls, "Anki buttons");
        var listScroll = Ancestor<ScrollViewer>(Read<ListBox>(window, "_ankiRows"));
        var viewport = InView(surface, listScroll, "Anki scroll viewer");
        Require(controls.Max(rect => rect.Bottom) <= viewport.Top + 1, "Anki buttons overlap the list viewport.");
        tabs.SelectedIndex = 2;
        surface.Layout();
        _ = CheckButton(surface, window, "_import");
        return $"Four Anki commands and the import command fit; Anki viewport {Format(viewport)}. No dialog or network command invoked.";
    }

    private static string TestStates(Product api)
    {
        using var opened = api.Open(api.Seed("states"));
        var window = opened.Window;
        using var surface = new Surface(window, SmallWidth, SmallHeight, 2.35);
        var cancel = Read<Button>(window, "_cancel");
        var preview = Read<Button>(window, "_previewButton");
        var detail = Read<TextBox>(window, "_detail");
        var feedback = Read<TextBlock>(window, "_feedback");
        var progress = Read<ProgressBar>(window, "_progress");
        var shapes = new HashSet<string> { AssertState(window, "空闲", busy: false) };
        Require(preview.IsEnabled && !Read<Button>(window, "_verify").IsEnabled, "Unexpected initial audition/confirm button state.");
        Require(AutomationProperties.GetLiveSetting(feedback) == AutomationLiveSetting.Polite, "Status is not a polite live region.");

        // Fail validation before StudyClip/FFmpeg; no source file or playback is needed.
        Read<TextBox>(window, "_from").Text = "not-a-time";
        Click(preview);
        shapes.Add(AssertState(window, "运行中", busy: true));
        Require(cancel.IsEnabled && cancel.Visibility == Visibility.Visible && !Read<StackPanel>(window, "_editor").IsEnabled,
            "Running preview must allow cancel and lock the editor.");
        surface.Layout();
        NoOverlap([CheckButton(surface, window, "_cancel"), InView(surface, feedback, "running feedback"),
            InView(surface, progress, "running progress")], "Running footer");
        _ = surface.Snapshot("04-small-235-running.png");

        WaitUntil(() => !Read<bool>(window, "_busy"), "preview validation did not complete");
        shapes.Add(AssertState(window, "失败", busy: false));
        Require(detail.IsReadOnly && detail.Visibility == Visibility.Visible && detail.Text.Contains("秒数", StringComparison.Ordinal),
            "Failed preview is missing a visible, readable validation detail.");
        var failure = surface.Snapshot("03-small-235-failure.png");
        NoOverlap([InView(surface, feedback, "failure feedback"), InView(surface, detail, "failure detail")], "Failure footer");
        _ = CheckButton(surface, window, "_verify");

        // An empty local queue gives a real UI success transition without connecting to Anki or writing a card.
        Read<TabControl>(window, "_tabs").SelectedIndex = 1;
        Click(Read<Button>(window, "_queueExisting"));
        WaitUntil(() => !Read<bool>(window, "_busy"), "local no-op queue command did not complete");
        shapes.Add(AssertState(window, "成功", busy: false));
        Require(feedback.Text.Contains("没有新的合格原声卡", StringComparison.Ordinal), "Success did not report the expected no-op queue result.");
        Require(shapes.Count == 4, $"Expected four distinct state glyphs; got {shapes.Count}.");
        return $"Idle -> running -> input validation failure -> local no-op success; four glyphs; failure and running PNG {failure.WidthPx}x{failure.HeightPx}.";
    }

    private static string AssertState(Window window, string label, bool busy)
    {
        var feedback = Read<TextBlock>(window, "_feedback");
        var progress = Read<ProgressBar>(window, "_progress");
        var detail = Read<TextBox>(window, "_detail");
        var cancel = Read<Button>(window, "_cancel");
        Require(feedback.Text.StartsWith(label + " · ", StringComparison.Ordinal), $"Expected {label}; found {feedback.Text}.");
        Require(Read<bool>(window, "_busy") == busy && progress.IsIndeterminate == busy
            && progress.Visibility == (busy ? Visibility.Visible : Visibility.Collapsed), $"Inconsistent busy/progress state for {label}.");
        Require(busy || (!cancel.IsEnabled && cancel.Visibility == Visibility.Collapsed), $"Cancel is still available in {label}.");
        Require(label == "失败" || detail.Visibility == Visibility.Collapsed, $"Stale failure detail in {label}.");
        return Read<ShapePath>(window, "_stateIcon").Data?.ToString() ?? throw new InvalidOperationException($"No icon for {label}.");
    }

    private static string TestCancel(Product api)
    {
        var fixture = api.Seed("cancel");
        using var opened = api.Open(fixture, fixture.A);
        var window = opened.Window;
        var snapshot = Property<object>(fixture.Cloud, "Snapshot");
        var before = File.ReadAllBytes(fixture.StatePath);
        string[] Files() => Directory.EnumerateFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fixture.Root, path)).Order(StringComparer.Ordinal).ToArray();
        var existing = Files();
        var preview = Path.Combine(fixture.Root, "synthetic-audition.mp3");
        using (var stream = File.Open(preview, FileMode.CreateNew)) stream.Write([0x50, 0x49, 0x4c, 0x4f, 0x54]);

        // Seed only the audition latch; exercise the actual confirm and cancel click handlers, without decoding audio.
        var row = Read<ListBox>(window, "_rows").SelectedItem!;
        Read<TextBox>(window, "_meaning").Text = "等一下（合成）";
        Set(window, "_clip", Property<object>(row, "Clip"));
        Set(window, "_preview", preview);
        Set(window, "_heard", true);
        Call(window, "UpdateButtons");
        var confirm = Read<Button>(window, "_verify");
        var cancel = Read<Button>(window, "_cancel");
        Click(confirm);
        _ = AssertState(window, "运行中", busy: true);
        Require(cancel.Visibility == Visibility.Visible && cancel.IsEnabled, "Confirm did not expose pre-write cancellation.");
        Click(cancel); // Do not pump the dispatcher between confirm and cancel.
        Require(cancel.Visibility == Visibility.Collapsed && !cancel.IsEnabled, "Cancel remains available after cancellation was requested.");
        WaitUntil(() => !Read<bool>(window, "_busy"), "confirm cancellation did not return to idle");
        _ = AssertState(window, "空闲", busy: false);
        Require(Read<TextBlock>(window, "_feedback").Text.Contains("已取消，未保存", StringComparison.Ordinal), "Missing no-save feedback.");
        Require(!confirm.IsEnabled && !Read<bool>(window, "_heard") && Read<string?>(window, "_preview") is null,
            "Cancelled confirmation did not clear the audition gate.");
        Require(!File.Exists(preview) && existing.SequenceEqual(Files()), "Cancellation left a file or directory behind.");
        Require(ReferenceEquals(snapshot, Property<object>(fixture.Cloud, "Snapshot")) && before.SequenceEqual(File.ReadAllBytes(fixture.StatePath)),
            "Cancellation changed the state snapshot or the on-disk state.json.");
        return "Actual confirm/cancel events, pre-write only; state.json byte-identical, in-memory snapshot unchanged, no new files and synthetic audition discarded.";
    }

    private static string TestPrefs(Product api)
    {
        var fixture = api.Seed("preferences");
        const double width = 731, height = 664;
        const int tab = 2;
        using (var opened = api.Open(fixture))
        {
            var window = opened.Window;
            window.Width = width;
            window.Height = height;
            var rows = Read<ListBox>(window, "_rows");
            rows.SelectedItem = rows.Items.Cast<object>().Single(item => Property<Guid>(item, "Id") == fixture.A);
            Read<TabControl>(window, "_tabs").SelectedIndex = tab;
        }
        using (var json = JsonDocument.Parse(File.ReadAllBytes(fixture.PrefsPath)))
        {
            var prefs = json.RootElement;
            Require(Near(prefs.GetProperty("Width").GetDouble(), width) && Near(prefs.GetProperty("Height").GetDouble(), height)
                && prefs.GetProperty("Tab").GetInt32() == tab && prefs.GetProperty("Lookup").GetGuid() == fixture.A,
                "Closing the never-shown window did not persist size, tab and lookup.");
        }
        using var restored = api.Open(fixture);
        AssertPrefs(restored.Window, width, height, tab, fixture.A);
        using var explicitLookup = api.Open(fixture, fixture.B);
        AssertPrefs(explicitLookup.Window, width, height, 0, fixture.B);
        return $"Saved {width}x{height} DIP, tab {tab}, row A; restored size {restored.Window.Width:0.#}x{restored.Window.Height:0.#} DIP after WorkArea fit. Explicit row B forces tab 0.";
    }

    private static string TestBadPrefs(Product api)
    {
        foreach (var kind in new[] { "malformed", "out-of-range" })
        {
            var fixture = api.Seed(kind);
            var input = kind == "malformed" ? "{\"Width\":"
                : JsonSerializer.Serialize(new { Width = 99, Height = 900, Tab = 4, Lookup = fixture.A });
            File.WriteAllText(fixture.PrefsPath, input);
            using var opened = api.Open(fixture);
            AssertPrefs(opened.Window, RegularWidth, RegularHeight, 0, fixture.B);
        }
        return "Both malformed JSON and out-of-range values fall back to 640x730 DIP (WorkArea-clamped), review tab and newest lookup.";
    }

    private static void AssertPrefs(Window window, double width, double height, int tab, Guid lookup)
    {
        var area = SystemParameters.WorkArea; // Product preference clamping uses the host WorkArea; render sizes do not.
        var fittedWidth = Math.Clamp(width, SmallWidth, Math.Max(SmallWidth, area.Width));
        var fittedHeight = Math.Clamp(height, SmallHeight, Math.Max(SmallHeight, area.Height));
        var rows = Read<ListBox>(window, "_rows");
        Require(Near(window.Width, fittedWidth) && Near(window.Height, fittedHeight),
            $"Expected {fittedWidth:0.#}x{fittedHeight:0.#} DIP after WorkArea clamp; got {window.Width:0.#}x{window.Height:0.#}.");
        Require(Read<TabControl>(window, "_tabs").SelectedIndex == tab, $"Expected restored/explicit tab {tab}.");
        Require(rows.SelectedItem is { } row && Property<Guid>(row, "Id") == lookup, "Unexpected selected lookup.");
    }
}
