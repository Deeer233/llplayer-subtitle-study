using System.IO;
using LLPlayer.Services;

internal static class AnkiQueueChecks
{
    private const string FirstMeaning="推销";
    private const string SecondMeaning="搭起";

    internal static async Task Run(string root,string phase,string original,string corrected)
    {
        var a=File.ReadAllBytes(original);
        var b=File.ReadAllBytes(corrected);
        var bridge=new Bridge();
        var cloud=new StudyCloud(root,startWorker:false,localOnly:true,anki:bridge);
        if (phase=="stage")
        {
            var old=Observe(1,"Pitch the tent here.");
            Save(old,SecondMeaning,a,manual:true,enqueue:false);
            var first=Observe(2,"Pitch the idea to them.");
            Check(Save(first,FirstMeaning,a,manual:false)==AnkiCapture.NeedsSecond,"one dialogue must not trigger the two-dialogue rule");
            Check(Save(first,FirstMeaning,a,manual:false)==AnkiCapture.NeedsSecond,"playing the same line twice counted as two");
            var second=Observe(3,"They pitch the plan tomorrow.");
            Check(Save(second,FirstMeaning,b,manual:false)==AnkiCapture.Queued,"second verified dialogue was not queued");
            Check(cloud.Snapshot.AnkiJobs is [{ExampleId:var picked,Written:false}] && picked==second,"queued a clip other than the one confirmed");
            var privateLine=Observe(4,@"Pitch the idea in C:\Users\Fixture\notes.txt.");
            Check(Save(privateLine,"私有路径",b,manual:true)==AnkiCapture.Ineligible,"local path entered the queue");
            Observe(5,"Don't pitch this yet.");
            cloud.Import([("external","仅文字","","不导出")]);
            Check(!await cloud.AnkiTick() && bridge.Calls==0,"unbound library contacted Anki");
            await cloud.BindAnki(default);
            bridge.Offline=true;
            Check(!await cloud.AnkiTick() && cloud.Status.Contains("测试：Anki 未打开"),"offline job lost its waiting status");
            Check(cloud.QueueExistingAnki()==1 && cloud.QueueExistingAnki()==0,"existing-card selection included drafts, text or duplicates");
            Check(cloud.Snapshot.AnkiJobs?.Length==2 && cloud.Snapshot.Pending.Length==0,"wrong queue or original outbox changed");
            Console.WriteLine("PASS explicit old-card selection, verification gates, binding and persistent offline queue");
            return;
        }
        if (phase!="resume") throw new ArgumentException("Expected stage or resume");
        Check(cloud.Snapshot.AnkiProfile=="Fixture A" && cloud.Snapshot.AnkiJobs?.Length==2,"jobs/profile did not survive process restart");
        bridge.Profile="Fixture B";
        Check(!await cloud.AnkiTick() && bridge.Writes.Count==0 && cloud.Status.Contains("配置已切换"),"a different Anki profile received a note");
        try { await cloud.BindAnki(default); throw new Exception("Rebinding to a different profile was permitted"); }
        catch (InvalidDataException) { }
        bridge.Profile="Fixture A";
        Check(await cloud.AnkiTick() && await cloud.AnkiTick() && !await cloud.AnkiTick(),"queue did not drain after reconnect");
        Check(bridge.Writes.Count==2 && bridge.Writes.Select(n=>n.Key).Distinct().Count()==2,"two senses did not keep separate identities");
        Check(bridge.Writes.All(n=>!n.English.Contains("Fixture\\") && n.Expression=="pitch"),"wrong card fields shared");
        var originalNote=bridge.Writes.Single(n=>n.Meaning==System.Net.WebUtility.HtmlEncode(FirstMeaning));
        var row=cloud.Snapshot.Lookups.Single(l=>l.English=="They pitch the plan tomorrow.");
        Check(Save(row.Id,FirstMeaning,b,manual:false,keep:true)==AnkiCapture.AlreadyWritten && !await cloud.AnkiTick(),"unchanged re-confirmation queued duplicate work");
        const string newMeaning="推介";
        Check(Save(row.Id,newMeaning,a,manual:false,keep:true)==AnkiCapture.Queued,"correction not queued");
        bridge.Reject=true;
        Check(await cloud.AnkiTick() && !await cloud.AnkiTick() && cloud.Status.Contains("需处理 1"),"failed job did not pause for retry");
        bridge.Reject=false;
        cloud.RetryAnki();
        Check(await cloud.AnkiTick(),"explicit retry did not write the note");
        var update=bridge.Writes.Last();
        Check(update.Key==originalNote.Key && update.Meaning!=originalNote.Meaning && update.MediaName!=originalNote.MediaName,
            "correction changed identity instead of fields and media");

        var stale=Observe(8,"Pitch the original.”");
        Check(Save(stale,"撤回测试",b,manual:true)==AnkiCapture.Queued,"manual confirm should queue a single verified clip");
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.BeforeWrite=async token=>{ started.SetResult(); await Task.Delay(Timeout.Infinite,token); };
        var inFlight=cloud.AnkiTick();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cloud.Withdraw(stale);
        Check(!await inFlight.WaitAsync(TimeSpan.FromSeconds(3)) && bridge.Writes.Count==3,"withdrawal failed to cancel an in-flight queue item");
        Check(!await cloud.AnkiTick() && cloud.Snapshot.AnkiJobs?.Length==2,"withdrawn card was scheduled again");
        Console.WriteLine("PASS restart, profile guard, recoverable failure, idempotency, corrected media and withdrawal cancellation");

        Guid Observe(int cue,string english) => cloud.Observe(new(@"C:\Fixture\video.mkv",100,123,1,cue,cue+.4),english,"pitch","仅本机译文","Fixture");
        AnkiCapture Save(Guid id,string meaning,byte[] bytes,bool manual,bool keep=false,bool enqueue=true)
        {
            var item=cloud.Snapshot.Lookups.Single(l=>l.Id==id);
            return cloud.Verify(id,item.Clip,"pitch",meaning,item.English,item.Target,item.Chinese,item.Source,bytes,manual,keep,enqueue);
        }
    }

    private static void Check(bool valid,string message) { if (!valid) throw new InvalidOperationException(message); }

    private sealed class Bridge : IAnkiConnectClient
    {
        internal bool Offline, Reject;
        internal int Calls;
        internal string Profile="Fixture A";
        internal readonly List<AnkiNotePayload> Writes=[];
        internal Func<CancellationToken,Task>? BeforeWrite;

        public Task<string> GetProfileAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (Offline) throw new AnkiConnectException("测试：Anki 未打开",unavailable:true);
            return Task.FromResult(Profile);
        }

        public async Task WriteAsync(string profile,AnkiNotePayload payload,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(profile==Profile,"queue wrote outside its bound profile");
            if (Reject) throw new AnkiConnectException("测试：插件暂时拒绝写入");
            if (BeforeWrite is { } start) await start(token);
            Writes.Add(payload);
        }
    }
}
