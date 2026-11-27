using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace OffscreenUiQa;

internal static partial class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ServicePrefix = "LLPlayer.Services.";
    private const string PrefsName = "polish-window.json";
    private const double SmallWidth = 450, SmallHeight = 430, RegularWidth = 640, RegularHeight = 730;
    private static readonly List<CaseResult> Results = [];
    private static readonly List<ImageResult> Images = [];
    private static string RunDirectory = null!;
    private static PackageInfo? Input;
    private static string? Problem;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Run via Invoke-OffscreenUiQa.ps1 with -Package or -SelfTest.");
            return 2;
        }
        var bin = new DirectoryInfo(AppContext.BaseDirectory);
        var run = bin.Parent;
        if (bin.Name != "bin" || run?.Parent?.Name != ".pilot" || !run.Name.StartsWith("ui-qa-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Refusing to run outside the probe's own .pilot output tree.");
            return 2;
        }
        RunDirectory = run.FullName;
        var selfTest = args[0] == "--self-test";
        try
        {
            Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Expected an STA thread.");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            if (selfTest)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Check("WPF host: no HWND, detached DPI and PNG", TestHost);
            }
            else
                RunPackage(args[0]);
        }
        catch (Exception error)
        {
            Problem = Unwrap(error).ToString();
            Console.Error.WriteLine(Problem);
        }

        using var report = File.Open(Path.Combine(RunDirectory, "report.json"), FileMode.CreateNew);
        JsonSerializer.Serialize(report, new
        {
            Mode = selfTest ? "harness-self-test" : "package",
            Input,
            Scope = "Detached WPF client content, not a real monitor or non-client chrome. No product startup, playback, dialog, worker, or live data.",
            Fatal = Problem,
            Cases = Results,
            Images
        }, new JsonSerializerOptions { WriteIndented = true });
        var failed = Results.Count(item => !item.Passed);
        Console.WriteLine($"Cases: {Results.Count - failed} passed, {failed} failed; images: {Images.Count}; report: {report.Name}");
        return Problem is not null ? 2 : failed == 0 ? 0 : 1;
    }

    private static void RunPackage(string file)
    {
        var dll = Path.GetFullPath(file);
        Require(File.Exists(dll) && Path.GetFileName(dll).Equals("LLPlayer.dll", StringComparison.OrdinalIgnoreCase),
            "Expected the supplied package's LLPlayer.dll.");
        using var stream = File.OpenRead(dll);
        var context = AssemblyLoadContext.Default;
        var dependencies = new AssemblyDependencyResolver(dll);
        Assembly? Resolve(AssemblyLoadContext loadContext, AssemblyName name) => dependencies.ResolveAssemblyToPath(name) is { } path
            ? loadContext.LoadFromAssemblyPath(path) : null;
        context.Resolving += Resolve;
        try
        {
            var assembly = context.LoadFromAssemblyPath(dll);
            Require(Path.GetFullPath(assembly.Location).Equals(dll, StringComparison.OrdinalIgnoreCase), "Loaded assembly path differs from input.");
            Input = new(dll, Convert.ToHexStringLower(SHA256.HashData(stream)), assembly.GetName().Version?.ToString(), assembly.ManifestModule.ModuleVersionId);
            Console.WriteLine($"Package: {Input.Path}\nSHA-256: {Input.Sha256}\nMVID: {Input.Mvid}");
            SetUpPackageResources(assembly);
            var api = new Product(assembly);
            Check("Package interfaces: legacy constructor, muted constructor, isolated StudyCloud", () => TestInterfaces(api));
            Check("Regular review layout (640x730 DIP, 100%)", () => TestLayout(api, RegularWidth, RegularHeight, 1, "01-regular-100-idle.png"));
            Check("Small review layout (450x430 DIP, 200%) and scroll", () => TestLayout(api, SmallWidth, SmallHeight, 2, "02-small-200-idle.png"));
            Check("Small Anki and word-list tabs (450x430 DIP, 200%)", () => TestExtraTabs(api));
            Check("Four feedback states and 235% small screenshot", () => TestStates(api));
            Check("Cancel actual confirm command before write, preserving local state", () => TestCancel(api));
            Check("Preferences round-trip and explicit lookup overrides stored lookup/tab", () => TestPrefs(api));
            Check("Malformed and out-of-range preferences fall back to defaults", () => TestBadPrefs(api));
        }
        finally { context.Resolving -= Resolve; }
    }

    private static void SetUpPackageResources(Assembly assembly)
    {
        // Generic.xaml uses application-rooted pack URIs. Bind them to the package without invoking its entry point.
        Assembly.SetEntryAssembly(assembly);
        Require(ReferenceEquals(Application.ResourceAssembly, assembly), "WPF application pack resources do not resolve to LLPlayer.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _ = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("MaterialDesignThemes.Wpf"));
        // These are the theme entries used by the package App.xaml; do not instantiate LLPlayer.App.
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
            <CustomColorTheme xmlns='clr-namespace:MaterialDesignThemes.Wpf;assembly=MaterialDesignThemes.Wpf'
                              BaseTheme='Dark' PrimaryColor='#D23D6F' SecondaryColor='#00B8D4'/>
            """));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml")
        });
    }

    private static void Check(string name, Func<string> action)
    {
        try
        {
            var evidence = action();
            Results.Add(new(name, true, evidence));
            Console.WriteLine($"PASS {name}: {evidence}");
        }
        catch (Exception error)
        {
            var detail = Unwrap(error);
            Results.Add(new(name, false, detail.ToString()));
            Console.Error.WriteLine($"FAIL {name}: {detail.Message}");
        }
    }

    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : error;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static FieldInfo Field(object obj, string name) => obj.GetType().GetField(name, Instance)
        ?? throw new MissingFieldException(obj.GetType().FullName, name);
    private static T Read<T>(object obj, string name) => (T)Field(obj, name).GetValue(obj)!;
    private static void Set(object obj, string name, object? value) => Field(obj, name).SetValue(obj, value);
    private static T Property<T>(object obj, string name) => (T)(obj.GetType().GetProperty(name, Instance)
        ?? throw new MissingMemberException(obj.GetType().FullName, name)).GetValue(obj)!;
    private static object? Call(object obj, string name, params object?[] args) => (obj.GetType().GetMethod(name, Instance)
        ?? throw new MissingMethodException(obj.GetType().FullName, name)).Invoke(obj, args);

    private static void AssertNoWindow(Window window)
    {
        Require(!window.IsVisible && !window.IsLoaded && new WindowInteropHelper(window).Handle == IntPtr.Zero
            && PresentationSource.FromVisual(window) is null, "Unexpected visible or HWND-backed WPF window.");
    }

    private static void Click(Button button)
    {
        Require(button.IsEnabled, $"Button is disabled: {button.ToolTip}");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static void WaitUntil(Func<bool> condition, string description)
    {
        if (condition()) return;
        var timedOut = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (condition() || timedOut.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(condition(), $"Dispatcher timed out: {description}");
    }

    private sealed class BlockHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected cloud HTTP request in an offline QA run.");
    }

    private sealed class Product
    {
        private readonly Type _clip;
        private readonly ConstructorInfo _cloud;
        private readonly ConstructorInfo _window;
        public bool HasLegacyConstructor { get; }

        internal Product(Assembly assembly)
        {
            Type Find(string type) => assembly.GetType(ServicePrefix + type, throwOnError: true)!;
            var cloud = Find("StudyCloud");
            var window = Find("PolishedReviewWindow");
            _clip = Find("ClipSource");
            _cloud = cloud.GetConstructor(Instance, null, [typeof(string), typeof(HttpMessageHandler), typeof(bool), typeof(bool), Find("IAnkiConnectClient")], null)
                ?? throw new MissingMethodException(cloud.FullName, "explicit root/worker constructor");
            _window = window.GetConstructor(Instance, null, [cloud, typeof(Guid?), typeof(bool)], null)
                ?? throw new MissingMethodException(window.FullName, "muted constructor");
            HasLegacyConstructor = window.GetConstructor(Instance, null, [cloud, typeof(Guid?)], null) is not null;
        }

        internal Fixture Seed(string name)
        {
            var root = Path.Combine(RunDirectory, "fixtures", name);
            Require(!Directory.Exists(root), $"Fixture must be new: {root}");
            Directory.CreateDirectory(root);
            var cloud = _cloud.Invoke([root, new BlockHttp(), false, true, null]);
            Require(Property<bool>(cloud, "LocalOnly") && (string)Call(cloud, "MediaPath", "preview")! == Path.Combine(root, "preview.mp3"),
                "StudyCloud is not using the explicit isolated local-only root.");

            Guid Observe(string name, string english, string target, string chinese, double start)
            {
                var clip = _clip.GetConstructor(Instance, null, [typeof(string), typeof(long), typeof(long), typeof(int), typeof(double), typeof(double)], null)!
                    .Invoke([Path.Combine(root, name + ".missing.mkv"), 0L, 0L, 0, start, start + 2.5]);
                return (Guid)Call(cloud, "Observe", clip, english, target, chinese, "离屏 QA 合成对白")!;
            }

            var a = Observe("a", "Could you hold on for a moment?", "hold on", "你能等一下吗？", 2.5);
            var b = Observe("b", "We should look up the next stop.", "look up", "我们应该查一下下一站。", 9);
            Require(a != b && a != Guid.Empty && b != Guid.Empty, "Fixture needs two distinct rows.");
            return new(root, cloud, a, b);
        }

        internal NeverShown Open(Fixture fixture, Guid? selected = null)
        {
            var window = (Window)_window.Invoke([fixture.Cloud, selected, true]);
            AssertNoWindow(window);
            return new(window);
        }
    }

    private sealed class NeverShown(Window window) : IDisposable
    {
        private bool _closed;
        internal Window Window { get; } = window;
        public void Dispose()
        {
            if (_closed) return;
            AssertNoWindow(Window);
            Window.Close();
            _closed = true;
        }
    }

    private sealed record Fixture(string Root, object Cloud, Guid A, Guid B)
    {
        internal string PrefsPath => Path.Combine(Root, PrefsName);
        internal string StatePath => Path.Combine(Root, "state.json");
    }

    private sealed record PackageInfo(string Path, string Sha256, string? AssemblyVersion, Guid Mvid);
    private sealed record CaseResult(string Name, bool Passed, string Evidence);
    private sealed record ImageResult(string File, double WidthDip, double HeightDip, double Scale, int WidthPx, int HeightPx,
        double PngDpiX, double PngDpiY, double ContentDpiX, double ContentDpiY);
}
