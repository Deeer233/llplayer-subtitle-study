using System.IO;
using System.Net.Http;
using System.Text.Json;
using LLPlayer.Services;

internal static class AnkiExportChecks
{
    internal static async Task<string> Run(string root,string a,string b,string c)
    {
        var store=Path.Combine(root,"library"); Directory.CreateDirectory(store);
        var library=Guid.NewGuid();
        var pending=new CloudCard(library,Guid.NewGuid(),1,"text","prior","",0,"import",[]);
        var path=Path.Combine(store,"state.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new CloudState(library,[],[pending],[new(pending)],
            "https://example.invalid","not-a-token"),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var handler=new NoNetwork();
        var cloud=new StudyCloud(store,handler,localOnly:true);
        var originalPending=cloud.Snapshot.Pending;
        var mp3a=File.ReadAllBytes(a); var mp3b=File.ReadAllBytes(b); var mp3c=File.ReadAllBytes(c);
        var first=Observe(1,"I pitch an idea."); var second=Observe(2,"We pitch a tent.");
        var extra=Observe(3,"They pitch ideas."); var draft=Observe(4,"Will you pitch this?");
        var uncheckedId=Observe(5,"Do not export.");
        Save(first,"推销 <想法>",mp3a); Save(second,"搭起",mp3b); Save(extra,"推销 <想法>",mp3c);
        Save(draft,"拟定",mp3c,manual:false);
        Check(cloud.Import([("text only","文字","","old words")])==1,"Word list import failed");
        Check(originalPending.Length==1 && originalPending[0].Card.CardId==pending.CardId && ReferenceEquals(cloud.Snapshot.Pending,originalPending) && !await cloud.Tick(),"Old outbox changed or processed");
        try { await cloud.Pair("https://example.invalid",new string('a',43)); throw new Exception("Pairing enabled in Anki mode"); }
        catch (InvalidDataException) { }
        Check(handler.Requests==0,"Local mode made a network request");
        var initial=cloud.Snapshot;
        var pick=AnkiExportService.Choices(initial).Where(x=>x.Example.ExampleId!=extra).ToArray();
        Check(pick.Length==2 && pick[0].Card.CardId!=pick[1].Card.CardId,"Different senses were merged");
        var firstChoice=pick.Single(x=>x.Example.ExampleId==first);
        var secondChoice=pick.Single(x=>x.Example.ExampleId==second);
        var firstId=firstChoice.Card.CardId;
        var firstKey=AnkiExportService.Key(firstChoice.Card);
        var selected=new[]{(firstId,first),(secondChoice.Card.CardId,second)};
        Reject(initial,[]);
        Reject(initial,[(firstId,first),(firstId,extra)]);
        Reject(initial,[(Guid.NewGuid(),uncheckedId)]);
        Reject(initial,[(initial.Lookups.Single(x=>x.Id==draft).Card!.Value,draft)]);
        Reject(initial,[(initial.Cards.Single(x=>x.Expression=="text only").CardId,Guid.NewGuid())]);
        var bad=firstChoice.Card with {Examples=[firstChoice.Example with {Verified=false}]};
        Reject(Swap(initial,bad),[(firstId,first)]);
        Reject(Swap(initial,bad with {Examples=[firstChoice.Example with {English=@"C:\fixture\private.mkv"}]}),[(firstId,first)]);
        Reject(Swap(initial,bad with {Examples=[firstChoice.Example with {Sha256="../invalid"}]}),[(firstId,first)]);
        Reject(Swap(initial,bad with {Examples=[firstChoice.Example with {Sha256=new string('f',64)}]}),[(firstId,first)],_=>a);
        Check(!Directory.EnumerateDirectories(root,"llplayer-anki-*").Any(),"Rejected export emitted files");

        Reject(initial,[(firstId,first)],_=>Path.Combine(root,"nonexistent.mp3"));
        var previousState=File.ReadAllBytes(path);
        var original=AnkiExportService.Export(initial,cloud.MediaPath,selected,root);
        var repeated=AnkiExportService.Export(initial,cloud.MediaPath,selected,root);
        Check(File.ReadAllBytes(path).SequenceEqual(previousState),"Export modified the library");
        Check(File.ReadAllBytes(original.TsvPath).SequenceEqual(File.ReadAllBytes(repeated.TsvPath)),"Repeated export not stable");
        var rows=ReadRows(original);
        Check(rows.Length==2 && rows.All(x=>x.Length==6) && rows[0][0]==firstKey && rows[0][2]==rows[1][2],"Unexpected TSV identity or structure");
        Check(rows[0][3].Contains("&lt;") && rows[0][5]=="Fixture" && !File.ReadAllText(original.TsvPath).Contains("private.mkv") && !File.ReadAllText(original.TsvPath).Contains("excluded translation"),"Unescaped HTML or leaked path");
        Check(original.CardCount==2 && original.MediaCount==2 && !File.Exists(Media(original,mp3c)),"Unselected audio exported");
        Check(File.ReadAllBytes(Media(original,mp3a)).SequenceEqual(mp3a),"Export media does not match source");
        Check(original.StatusText.Contains("待手动导入") && original.StatusText.Contains("待手动同步"),"Status conflates disk and cloud");
        Save(first,"推介 [sound:foreign.mp3]",mp3c,keep:true);
        var updated=AnkiExportService.Export(cloud.Snapshot,cloud.MediaPath,[(firstId,first)],root);
        var changed=ReadRows(updated).Single();
        Check(changed[0]==firstKey && changed[1]!=rows[0][1] && changed[3].Contains("&#91;sound:"),"Correction did not keep key or change media, or allowed extra audio");
        Check(File.ReadAllBytes(Media(updated,mp3c)).SequenceEqual(mp3c),"Corrected audio mismatch");
        cloud.Withdraw(first);
        var withdrawn=cloud.Snapshot.Cards.Single(x=>x.CardId==firstId);
        Check(withdrawn.Status=="withdrawn" && AnkiExportService.PauseQuery(withdrawn)==$"\"ID:{firstKey}\"", "Missing manual suspension search");
        Reject(cloud.Snapshot,[(firstId,first)]);
        Check(File.Exists(original.TsvPath) && handler.Requests==0,"Withdrawal had external effects");
        return JsonSerializer.Serialize(new {original=original.TsvPath,repeated=repeated.TsvPath,updated=updated.TsvPath,
            query=AnkiExportService.PauseQuery(withdrawn),front=AnkiExportService.Front,back=AnkiExportService.Back,noteType=AnkiExportService.NoteType});

        Guid Observe(int n,string sentence)=>cloud.Observe(new(@"C:\fixture\private.mkv",100,42,1,n,n+.5),sentence,"pitch","excluded translation","Fixture");
        void Save(Guid id,string meaning,byte[] bytes,bool manual=true,bool keep=false)
        {
            var row=cloud.Snapshot.Lookups.Single(x=>x.Id==id);
            cloud.Verify(id,row.Clip,"pitch",meaning,row.English,row.Target,row.Chinese,row.Source,bytes,manual,keep);
        }
        void Reject(CloudState state,(Guid,Guid)[] ids,Func<string,string>? pickMedia=null)
        {
            try { AnkiExportService.Export(state,pickMedia ?? cloud.MediaPath,ids,root); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid card selection was exported");
        }
    }

    private static string Media(AnkiExportResult result,byte[] bytes)=>Path.Combine(Path.GetDirectoryName(result.TsvPath)!,"media",$"llplayer-{StudyCloud.Hash(bytes)}.mp3");
    private static CloudState Swap(CloudState state,CloudCard card)=>state with {Cards=state.Cards.Select(c=>c.CardId==card.CardId?card:c).ToArray()};
    private static string[][] ReadRows(AnkiExportResult result)=>File.ReadAllLines(result.TsvPath).Where(l=>!l.StartsWith('#')).Select(l=>l.Split('\t')).ToArray();
    private static void Check(bool pass,string message) { if (!pass) throw new InvalidOperationException(message); }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        { Requests++; throw new InvalidOperationException("Network forbidden in Anki test"); }
    }
}
