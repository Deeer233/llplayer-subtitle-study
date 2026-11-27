using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using LLPlayer.Services;

if (args is ["package-flavor", var dll, var expected])
{
    var player=Assembly.LoadFrom(Path.GetFullPath(dll));
    var menu=player.GetType("LLPlayer.Resources.PopupMenu", throwOnError:true)!;
    var actual=menu.GetProperty("PrivateReviewVisibility", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString();
    if (actual!=expected) throw new InvalidOperationException($"Expected private review menu {expected}, got {actual ?? "missing"}.");
    Console.WriteLine($"PASS private review menu: {actual}");
    return;
}

if (args is ["anki-export", var testRoot, var a, var b, var c])
{
    Console.WriteLine(await AnkiExportChecks.Run(testRoot,a,b,c));
    return;
}

if (args is ["anki-queue", var queuePhase, var queueRoot, var firstTone, var secondTone])
{
    await AnkiQueueChecks.Run(queueRoot,queuePhase,firstTone,secondTone);
    return;
}

if (args is ["anki-connect"])
{
    await AnkiConnectChecks.RunAsync();
    Console.WriteLine("PASS AnkiConnect request contract, stable-ID writes, recovery, media and profile guards (simulated Anki)");
    return;
}

if (args is ["anki-window", var package, var tempRoot])
{
    var failure=(Exception?)null;
    var thread=new Thread(()=>{
        try
        {
            var assembly=Assembly.LoadFrom(Path.GetFullPath(package));
            var cloudType=assembly.GetType("LLPlayer.Services.StudyCloud",true)!;
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            if (cloudType.GetField("AnkiBuild",BindingFlags.Static|BindingFlags.NonPublic)?.GetRawConstantValue() is not true)
                throw new InvalidOperationException("Package is not an Anki-only build");
            var cloud=Activator.CreateInstance(cloudType,flags,null,[tempRoot,null,false,true,null],null);
            var windowType=assembly.GetType("LLPlayer.Services.CloudReviewWindow",true)!;
            var window=Activator.CreateInstance(windowType,flags,null,[cloud,null],null);
            var tabs=(System.Windows.Controls.TabControl)windowType.GetField("_tabs",flags)!.GetValue(window)!;
            var items=tabs.Items.Cast<System.Windows.Controls.TabItem>().ToArray();
            if (!items.Select(t=>t.Header).SequenceEqual(new object[]{"核对原声","Anki 连接","本地词表"}))
                throw new InvalidOperationException("Missing Anki tab or old sync controls still visible");
            for (int i=0;i<items.Length;i++)
            {
                tabs.SelectedIndex=(i+1)%items.Length;
                CheckTabContrast(items[i], selected:false);
                tabs.SelectedIndex=i;
                CheckTabContrast(items[i], selected:true);
            }
            var confirm=(System.Windows.Controls.Button)windowType.GetField("_verify",flags)!.GetValue(window)!;
            if (confirm.IsEnabled || !Equals(confirm.Content,"确认并加入 Anki"))
                throw new InvalidOperationException("The main button must require completed audio preview");
            var export=(System.Windows.Controls.Button)windowType.GetField("_export",flags)!.GetValue(window)!;
            if (export.IsEnabled) throw new InvalidOperationException("Empty selection can be exported");
            ((System.Windows.Window)window!).Close();
        }
        catch (Exception e) { failure=e; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start(); thread.Join();
    if (failure is not null) throw new InvalidOperationException("Anki WPF window check failed",failure);
    Console.WriteLine("PASS simplified WPF flow, normal/selected label contrast, preview and selection gates");
    return;
}

if (args is ["anki-clip", var playerPackage, var audioFixture])
{
    Environment.SetEnvironmentVariable("REVIEW_FFMPEG",null);
    var dllPath=Path.GetFullPath(playerPackage);
    var assembly=Assembly.LoadFrom(dllPath);
    var cutter=assembly.GetType("LLPlayer.Services.StudyClip",true)!;
    var methods=BindingFlags.Static | BindingFlags.NonPublic;
    var bundled=Path.Combine(Path.GetDirectoryName(dllPath)!,"ClipTools","ffmpeg.exe");
    if (!File.Exists(bundled) || !string.Equals((string?)cutter.GetMethod("Executable",methods)!.Invoke(null,null),bundled,StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Packaged audio cutter did not resolve the bundled ffmpeg.exe");
    var media=new FileInfo(audioFixture);
    var clipType=assembly.GetType("LLPlayer.Services.ClipSource",true)!;
    var clip=Activator.CreateInstance(clipType,[media.FullName,media.Length,media.LastWriteTimeUtc.Ticks,0,.03,.30]);
    var file=await (Task<string>)cutter.GetMethod("Prepare",methods)!.Invoke(null,[clip,CancellationToken.None])!;
    try
    {
        var bytes=new FileInfo(file).Length;
        if (bytes<=0 || bytes>600_000) throw new InvalidOperationException("Expected a bounded MP3 from packaged clip extraction");
        Console.WriteLine($"PASS bundled ffmpeg.exe selected and clipped a synthetic tone ({bytes} bytes)");
    }
    finally { cutter.GetMethod("ForgetPreview",methods)!.Invoke(null,[file]); }
    return;
}

static void CheckTabContrast(System.Windows.Controls.TabItem item, bool selected)
{
    // Layout the template off-screen. Never show or activate a real window.
    item.ApplyTemplate();
    item.Measure(new System.Windows.Size(400,80));
    item.Arrange(new System.Windows.Rect(item.DesiredSize));
    if (item.IsSelected!=selected || item.IsMouseOver)
        throw new InvalidOperationException($"Unexpected tab state for {item.Header}: selected={item.IsSelected}, hover={item.IsMouseOver}");
    if (item.Template.FindName("Surface",item) is not System.Windows.Controls.Border surface
        || item.Template.FindName("HeaderText",item) is not System.Windows.Controls.TextBlock header
        || header.Foreground is not System.Windows.Media.SolidColorBrush text
        || surface.Background is not System.Windows.Media.SolidColorBrush background)
        throw new InvalidOperationException($"Missing tab template or brushes: {item.Header}");
    // IsVisible is false by design without a displayed Window. Check the element's own layout instead.
    if (!Equals(header.Text,item.Header) || header.Visibility!=System.Windows.Visibility.Visible || header.ActualWidth<=0)
        throw new InvalidOperationException($"Tab did not lay out its text: {item.Header}, value '{header.Text}', width {header.ActualWidth}, visibility {header.Visibility}");
    static double Linear(byte channel) => channel<=10 ? channel/255.0/12.92 : Math.Pow((channel/255.0+.055)/1.055,2.4);
    static double Luminance(System.Windows.Media.Color c) => .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);
    double a=Luminance(text.Color), b=Luminance(background.Color);
    var ratio=(Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    if (ratio<4.5)
        throw new InvalidOperationException($"Insufficient contrast for {item.Header} ({(selected ? "selected" : "normal")}): {text.Color} against {background.Color}");
    Console.WriteLine($"PASS {item.Header} {(selected ? "selected" : "normal")}, contrast {ratio:F2}:1");
}

var input=JsonSerializer.Deserialize<Input>(Console.In.ReadToEnd(),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
var fault=new FaultHandler();
var cloud=new StudyCloud(input.Root,fault,startWorker:false);
void Check(bool ok,string label) { if (!ok) throw new InvalidOperationException(label); }
if (input.Phase=="capture")
{
    await cloud.Pair(input.Origin,input.Code!);
    var file=new FileInfo(input.Video!);
    ClipSource Span(double start,double end,int track=2)=>new(file.FullName,file.Length,file.LastWriteTimeUtc.Ticks,track,start,end);
    var cues=new[]{new StudyCue(TimeSpan.FromSeconds(.1),TimeSpan.FromSeconds(.5),"Please don't"),
        new StudyCue(TimeSpan.FromSeconds(.6),TimeSpan.FromSeconds(1.1),"bail on me.")};
    var line=StudyDialogue.Build(cues,0);
    var same=StudyDialogue.Build(cues,1);
    var span=Span(line.Start.TotalSeconds,line.End.TotalSeconds);
    var first=cloud.Observe(span,line.Text,"bail on","","Fixture A");
    var duplicate=cloud.Observe(Span(same.Start.TotalSeconds,same.End.TotalSeconds),same.Text,"bail on","","Fixture A");
    Check(first==duplicate,"Split subtitles were counted twice");
    var secondSpan=Span(1.2,2.1);
    var second=cloud.Observe(secondSpan,"Don't bail on us.","bail on","","Fixture B");
    Check(cloud.Snapshot.Pending.Length==0,"Unverified lookup was queued");
    async Task Verify(Guid id,ClipSource source,string english)
    {
        var mp3=await StudyClip.Prepare(source,CancellationToken.None);
        try { cloud.Verify(id,source,"bail on","放鸽子",english,"bail on","","Fixture",await File.ReadAllBytesAsync(mp3),false,false); }
        finally { StudyClip.ForgetPreview(mp3); }
    }
    try { await StudyClip.Prepare(Span(.1,1.1,1),CancellationToken.None); throw new Exception("Silent track accepted"); }
    catch (InvalidDataException) { }
    await Verify(first,span,line.Text);
    Check(cloud.Snapshot.Pending.Length==0,"One confirmed dialogue was queued automatically");
    await Verify(second,secondSpan,"Don't bail on us.");
    Check(cloud.Snapshot.Pending.Single().Card.Count==2,"Distinct confirmed dialogues not counted");
    Check(!File.ReadAllText(Path.Combine(input.Root,"state.json")).Contains(input.Code!,StringComparison.Ordinal),"Pair code persisted");
    fault.Mode="before";
    try { await cloud.Tick(); throw new Exception("Offline failure missing"); } catch(HttpRequestException) { }
    Check(cloud.Snapshot.Pending.Length==1,"Offline work lost");
    Console.WriteLine("validated-capture-and-offline");
}
else if(input.Phase=="lost-reply")
{
    Check(cloud.Snapshot.Pending.Length==1,"Fresh desktop process did not recover queued work");
    fault.Mode="after";
    try { await cloud.Tick(); throw new Exception("Lost reply failure missing"); } catch(HttpRequestException) { }
    Check(cloud.Snapshot.Pending.Length==1,"Unconfirmed publication was acknowledged");
    Console.WriteLine("validated-restart-and-lost-reply");
}
else if(input.Phase=="resend")
{
    Check(await cloud.Tick() && cloud.Snapshot.Pending.Length==0,"Retry was not acknowledged");
    var prior=cloud.Snapshot.Cards.Single();
    var row=cloud.Snapshot.Lookups.First();
    cloud.Verify(row.Id,row.Clip,"bail on","爽约",row.English,row.Target,row.Chinese,row.Source,File.ReadAllBytes(cloud.MediaPath(row.Sha256!)),false,true);
    Check(cloud.Snapshot.Cards.Single().CardId==prior.CardId,"Same sense correction changed identity");
    Check(await cloud.Tick(),"Correction not uploaded");
    var words=OriginalReview.WordImport.Read(input.Words!).Entries.Select(e=>(e.Word,e.Meaning,e.English,e.Chinese)).ToArray();
    Check(words.Length==2 && cloud.Import(words)==2 && cloud.Import(words)==0,"External import or dedup failed");
    while(await cloud.Tick()) { }
    Check(cloud.Snapshot.Pending.Length==0,"Imported entries did not sync");
    Console.WriteLine("validated-resend-correction-import");
}
else if(input.Phase=="revoke")
{
    cloud.Withdraw(cloud.Snapshot.Lookups.First().Id);
    Check(!await cloud.Tick() && cloud.Snapshot.NeedsPair && cloud.Snapshot.Pending.Length==1,"Revocation did not preserve pending work");
    await cloud.Pair(input.Origin,input.Code!);
    Check(await cloud.Tick() && cloud.Snapshot.Pending.Length==0,"Withdrawal did not resume after pairing");
    Console.WriteLine("validated-repair-and-withdrawal");
}
else throw new ArgumentException("Unexpected phase");

record Input(string Phase,string Root,string Origin,string? Code,string? Video,string? Words);
sealed class FaultHandler : DelegatingHandler
{
    internal string? Mode;
    internal FaultHandler() : base(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        if (request.Method==HttpMethod.Put && Mode=="before") throw new HttpRequestException("Simulated offline");
        var response=await base.SendAsync(request,token);
        if (request.Method==HttpMethod.Put && Mode=="after")
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Expected server acceptance before disconnect");
            response.Dispose(); throw new HttpRequestException("Simulated lost acknowledgement");
        }
        return response;
    }
}
