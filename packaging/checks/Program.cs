using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

internal static class Program
{
    internal const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal const string Sentence = "They pitch the idea to us.";
    internal const string SecondSentence = "We pitch the plan today.";
    internal const string Target = "pitch";
    internal const string Meaning = "推介";
    private static string _package = null!, _work = null!;
    private static Assembly _player = null!;
    internal static string FixtureVideo => InRun("fixture.mkv");
    internal static string Bundle(string name) => Path.Combine(Path.GetDirectoryName(_package)!, name);
    internal static string InRun(string name) => Path.Combine(_work, name);
    internal static Type Service(string name) => ProductType("LLPlayer.Services." + name);
    internal static Type ProductType(string name) => _player.GetType(name, true)!;

    private static readonly (string Label, string[] Steps)[] Checks =
    [
        ("入口 / 默认", ["entry"]),
        ("字幕查词及续播", ["lookup"]),
        ("英语轨截取", ["clip"]),
        ("试听到末尾 / 字段变动重锁", ["preview"]),
        ("一 / 两句、纠错和撤回", ["cards", "card-buttons"]),
        ("Anki 离线、进程重启及去重（模拟）", ["queue-stage", "queue-resume"]),
        ("安全导入", ["import"]),
        ("TSV / MP3", ["export"]),
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length is not (2 or 3)) throw new ArgumentException("Usage: PolishRegression.dll <LLPlayer.dll> <run-directory> [step]");
            _package = Path.GetFullPath(args[0]);
            _work = Path.GetFullPath(args[1]);
            Guard(Directory.Exists(_work) && Path.GetFileName(_work).StartsWith("polish-", StringComparison.Ordinal), "not a dedicated .pilot/polish-* directory");
            Guard(Path.GetFileName(Path.GetDirectoryName(_work)) == ".pilot", "run directory is not under .pilot");
            Guard(!File.GetAttributes(_work).HasFlag(FileAttributes.ReparsePoint), "run directory is a reparse point");
            Directory.SetCurrentDirectory(_work);
            if (args.Length == 2) return RunAll().GetAwaiter().GetResult();
            LoadPackage();
            switch (args[2])
            {
                case "fixture": Playback.MakeFixture(); break;
                case "entry": ReviewUi.Entry(); break;
                case "lookup": Playback.Lookup(); break;
                case "clip": Playback.Clip(); break;
                case "preview": ReviewUi.Preview(); break;
                case "cards": LibraryChecks.Cards(); break;
                case "card-buttons": ReviewUi.Preview(cardButtons: true); break;
                case "queue-stage": LibraryChecks.Queue(resume: false); break;
                case "queue-resume": LibraryChecks.Queue(resume: true); break;
                case "import": LibraryChecks.Import(); break;
                case "export": LibraryChecks.Export(); break;
                default: throw new ArgumentException("Unknown step: " + args[2]);
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return e is CheckFailure ? 1 : 2;
        }
    }

    private static void LoadPackage()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var file = Bundle(name.Name + ".dll");
            return File.Exists(file) ? ctx.LoadFromAssemblyPath(file) : null;
        };
        _player = AssemblyLoadContext.Default.LoadFromAssemblyPath(_package);
        Guard(Path.GetFullPath(_player.Location).Equals(_package, StringComparison.OrdinalIgnoreCase), "incorrect product assembly loaded");
        Console.WriteLine($"ACTUAL_PACKAGE pid={Environment.ProcessId} | {_player.Location}");
    }

    private static async Task<int> RunAll()
    {
        string hash = FileHash(_package);
        string report = InRun("report.md");
        using var log = new StreamWriter(new FileStream(report, FileMode.CreateNew)) { AutoFlush = true };
        log.WriteLine($"# 旧 / 新包八项回归\n\n- 实际包 DLL：`{_package}`\n- SHA-256：`{hash}`\n- 时间：{DateTimeOffset.Now:O}");
        log.WriteLine("- 依据：实际 DLL、配套 Flyleaf/FFmpeg、[合成素材](fixture.log)、离屏 WPF、进程内 Anki 模拟。此报告不含源码链接通过项。");
        log.WriteLine("- 边界：未启动产品入口，不打开文件或确认对话框；导入、撤回、导出走实际包服务。未安装产品或连接真实 Anki。\n");
        Console.WriteLine($"SHA-256: {hash}\nReport: {report}");
        var fixture = Child("fixture");
        var results = await Task.WhenAll(Checks.Select(async check =>
        {
            if (check.Steps[0] is not ("entry" or "import") && (await fixture).Code != 0)
                return (Code: 2, Steps: new[] { "fixture" });
            foreach (var step in check.Steps)
            {
                int code = (await Child(step)).Code;
                if (code != 0) return (Code: code, Steps: check.Steps.TakeWhile(s => s != step).Append(step).ToArray());
            }
            return (Code: 0, Steps: check.Steps);
        }));
        log.WriteLine("| # | 检查 | 结果 | 原始证据 |\n|---|---|---|---|");
        int failed = 0, blocked = 0;
        for (int i = 0; i < Checks.Length; i++)
        {
            var (code, steps) = results[i];
            string status = code == 0 ? "PASS" : code == 1 ? "FAIL" : "未完成";
            if (code == 1) failed++;
            if (code != 0 && code != 1) blocked++;
            Console.WriteLine($"{i+1:00} {status} {Checks[i].Label}");
            log.WriteLine($"| {i+1} | {Checks[i].Label} | {status} | {string.Join("、", steps.Select(s => $"[{s}]({s}.log)"))} |");
        }
        if (FileHash(_package) != hash)
        {
            log.WriteLine("\n包 DLL 在运行中发生变化，本次结果无效。\n");
            return 2;
        }
        log.WriteLine($"\n合计：{8-failed-blocked} PASS，{failed} FAIL，{blocked} 未完成。\n");
        log.WriteLine("## 逐项细节");
        foreach (var step in results.SelectMany(r => r.Steps).Distinct())
        {
            log.WriteLine($"\n### {step}\n\n```text");
            log.Write(File.ReadAllText(InRun(step + ".log")));
            log.WriteLine("\n```");
        }
        return blocked > 0 ? 2 : failed > 0 ? 1 : 0;
    }

    private static async Task<(int Code, string Text)> Child(string step)
    {
        var start = Hidden(Environment.ProcessPath!);
        foreach (var arg in new[] { typeof(Program).Assembly.Location, _package, _work, step }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch regression step: " + step);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        string text = $"{await stdout}{await stderr}";
        if (timeout.IsCancellationRequested) text += "\nTIMEOUT: step exceeded 75 seconds.\n";
        File.WriteAllText(InRun(step + ".log"), text);
        Console.WriteLine($"[{step}] exit={process.ExitCode}{(timeout.IsCancellationRequested ? " timeout" : "")}");
        return (timeout.IsCancellationRequested ? 2 : process.ExitCode, text);
    }

    internal static ProcessStartInfo Hidden(string executable) => new(executable)
    {
        CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };

    internal static object New(Type type, params object?[] args) => Reflect(() =>
        Activator.CreateInstance(type, Members & ~BindingFlags.Static, null, args, CultureInfo.InvariantCulture)!)!;

    internal static object? Call(object owner, string name, params object?[] args)
    {
        var type = owner as Type ?? owner.GetType();
        var target = owner is Type ? null : owner;
        var method = type.GetMethods(Members).SingleOrDefault(m => m.Name == name && m.IsStatic == (target is null)
            && m.GetParameters().Length == args.Length && m.GetParameters().Zip(args).All(p => p.Second is null
            || p.First.ParameterType.IsInstanceOfType(p.Second))) ?? throw new MissingMethodException(type.FullName, name);
        return Reflect(() => method.Invoke(target, args));
    }

    internal static FieldInfo FieldInfo(object owner, string name) => owner.GetType().GetField(name, Members)
        ?? throw new MissingFieldException(owner.GetType().FullName, name);
    internal static T Field<T>(object owner, string name) => (T)FieldInfo(owner, name).GetValue(owner)!;
    internal static T Get<T>(object owner, string name) => (T)Get(owner, name)!;
    internal static object? Get(object owner, string name)
    {
        var type = owner as Type ?? owner.GetType();
        return (type.GetProperty(name, Members) ?? throw new MissingMemberException(type.FullName, name))
            .GetValue(owner is Type ? null : owner);
    }
    internal static object[] Items(object owner, string name) => ((IEnumerable?)Get(owner, name))?.Cast<object>().ToArray() ?? [];
    internal static object? Await(object? awaitable)
    {
        var task = (Task)awaitable!;
        Until(() => task.IsCompleted, "package task did not complete", 35);
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    internal static string FileHash(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
    internal static void Until(Func<bool> done, string message, int seconds = 12)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(8);
        }
        Guard(done(), message);
    }
    internal static void Guard(bool condition, string message) { if (!condition) throw new CheckFailure(message); }
    internal static void Pass(string message) => Console.WriteLine("PASS " + message);
    internal static void Reject<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { Pass(label); return; }
        throw new CheckFailure("did not reject: " + label);
    }
    private static object? Reflect(Func<object?> f)
    {
        try { return f(); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
}

internal sealed class CheckFailure(string message) : Exception(message);

internal sealed class NoNetwork : HttpMessageHandler
{
    internal int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        throw new CheckFailure("unexpected request to " + request.RequestUri);
    }
}
