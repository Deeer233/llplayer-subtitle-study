using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LLPlayer.Services;

internal sealed record ClipSource(string Path, long Bytes, long Modified, int Track, double Start, double End);
internal sealed record CloudLookup(Guid Id, ClipSource Clip, string English, string Target, string Chinese, string Source,
    Guid? Card = null, string? Sha256 = null, bool Rejected = false, ClipSource? Original = null)
{
    public override string ToString() => $"{(Rejected ? "已撤回" : Card is null ? "待核对" : "已核对")} · {Target} · {English}";
}
internal sealed record CloudExample(Guid ExampleId, string English, string Target, string Chinese, string Source, bool Verified, string Sha256);
internal sealed record CloudCard(Guid LibraryId, Guid CardId, int Revision, string Status, string Expression, string Meaning,
    int Count, string Reason, CloudExample[] Examples, string? English = null, string? Chinese = null);
internal sealed record PendingCard(CloudCard Card, string? Error = null);
internal sealed record CloudState(Guid LibraryId, CloudLookup[] Lookups, CloudCard[] Cards, PendingCard[] Pending,
    string? Origin = null, string? ProtectedToken = null, bool NeedsPair = false,
    string? AnkiProfile = null, AnkiJob[]? AnkiJobs = null);

internal sealed partial class StudyCloud
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _root, _stateFile;
    private readonly FileStream _ownership;
    private readonly HttpClient _http;
    internal bool LocalOnly { get; }
    private readonly SemaphoreSlim _wake = new(0,1);
    private CloudState _state;
    private volatile bool _networkFailed;
    private static readonly Lazy<StudyCloud> Instance = new(() => new StudyCloud(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLPlayer", "PrivateReview"), localOnly: AnkiBuild));
#if STUDY_ANKI_EXPORT
    private const bool AnkiBuild = true;
#else
    private const bool AnkiBuild = false;
#endif
    internal static StudyCloud Shared => Instance.Value;

    internal StudyCloud(string root, HttpMessageHandler? handler = null, bool startWorker = true, bool localOnly = false,
        IAnkiConnectClient? anki = null)
    {
        LocalOnly = localOnly;
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _ownership = new FileStream(Path.Combine(_root,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        _stateFile = Path.Combine(_root, "state.json");
        try
        {
            _state = File.Exists(_stateFile) ? JsonSerializer.Deserialize<CloudState>(File.ReadAllBytes(_stateFile),Json)
                ?? throw new InvalidDataException("同步数据损坏。") : new(Guid.NewGuid(),[],[],[]);
        }
        catch { _ownership.Dispose(); throw; }
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
        if (LocalOnly) _anki = anki ?? new AnkiConnectClient();
        if (startWorker)
        {
            if (LocalOnly) _ = Task.Run(AnkiWorker);
            else _ = Task.Run(Worker);
        }
    }

    internal CloudState Snapshot { get { lock (_gate) return _state; } }
    internal string Status
    {
        get
        {
            var s = Snapshot;
            var needs = s.Lookups.Count(l => l.Card is null && !l.Rejected);
            if (LocalOnly) return $"{DescribeAnki(s,_ankiNotice)} · 待核对 {needs}";
            var waiting = s.Pending.Count(p => p.Error is null);
            var failed = s.Pending.Length-waiting;
            var sync = s.NeedsPair ? "需要处理：请重新配对" : s.Origin is null ? "需要配对" :
                waiting > 0 ? (_networkFailed ? "等待联网" : "等待同步") : failed > 0 ? "需要处理" : "已同步";
            return $"{sync} · 待传 {waiting} · 待核对 {needs}" + (failed == 0 ? "" : $" · 失败 {failed}：{s.Pending.First(p => p.Error is not null).Error}");
        }
    }

    private void Commit(CloudState next)
    {
        var tmp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        using (var file = new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            JsonSerializer.Serialize(file,next,Json);
            file.Flush(flushToDisk:true);
        }
        File.Move(tmp,_stateFile,overwrite:true);
        _state = next;
        if (_ankiWrite is { } write && !HasPendingAnki(next,write.Job))
            write.Cancellation ??= write.Cancel.CancelAsync();
    }

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private static string Clean(string s) => Regex.Replace(s.Trim().Replace('’','\''),@"\s+"," ");
    internal static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    internal static bool ContainsTarget(string english, string target) => Regex.IsMatch(Clean(english),
        $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(Clean(target))}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool Same(string a,string b) => string.Equals(Clean(a),Clean(b),StringComparison.OrdinalIgnoreCase);
    private static bool SameLine(ClipSource a, ClipSource b) => string.Equals(a.Path,b.Path,StringComparison.OrdinalIgnoreCase) && a.Bytes == b.Bytes && a.Modified == b.Modified
        && Math.Abs(a.Start-b.Start) <= .12 && Math.Abs(a.End-b.End) <= .12;

    internal Guid Observe(ClipSource clip,string english,string target,string chinese,string source)
    {
        lock (_gate)
        {
            var prior = _state.Lookups.FirstOrDefault(l => SameLine(l.Original ?? l.Clip,clip) && Same(l.Target,target));
            if (prior is not null)
            {
                if (prior.Clip.Track != clip.Track && clip.Track >= 0)
                    Commit(_state with {Lookups=_state.Lookups.Select(l => l.Id == prior.Id
                        ? l with {Original=l.Original ?? l.Clip,Clip=l.Clip with {Track=clip.Track}} : l).ToArray()});
                return prior.Id;
            }
            var row = new CloudLookup(Guid.NewGuid(),clip,Clean(english),Clean(target),chinese,source);
            Commit(_state with {Lookups=[.._state.Lookups,row]});
            return row.Id;
        }
    }

    internal string MediaPath(string sha) => Path.Combine(_root, sha + ".mp3");
    internal AnkiCapture Verify(Guid id, ClipSource clip, string expression, string meaning, string english, string target, string chinese,
        string source, byte[] mp3, bool manual, bool keepSense, bool queueAnki = false)
    {
        expression=Clean(expression); meaning=Clean(meaning); english=Clean(english); target=Clean(target); source=Clean(source);
        if (expression.Length is 0 or >120 || meaning.Length is 0 or >400 || english.Length is 0 or >1000 || target.Length is 0 or >120
            || chinese.Length > 1000 || source.Length is 0 or >160 || Regex.IsMatch(source,@"[\\/]|[a-z]:",RegexOptions.IgnoreCase)
            || !ContainsTarget(english,target) || mp3.Length is <128 or >600000)
            throw new InvalidDataException("请检查表达、目标词形、本句义、原声和简要出处（不要填写路径）。");
        var sha = Hash(mp3);
        var result = AnkiCapture.NotRequested;
        lock (_gate)
        {
            var s = _state;
            var row = s.Lookups.Single(l => l.Id == id);
            var original = s.Cards.FirstOrDefault(c => c.CardId == row.Card && c.Status != "withdrawn");
            var match = s.Cards.FirstOrDefault(c => c.Reason != "import" && c.Status != "withdrawn" && Same(c.Expression,expression) && Same(c.Meaning,meaning));
            if (keepSense && original is not null && match is not null && match.CardId != original.CardId)
                throw new InvalidDataException("已存在同名义项，不能保留两个义项 ID。");
            var card = (keepSense ? original ?? match : match) ?? new(s.LibraryId,Guid.NewGuid(),0,"draft",expression,meaning,0,"auto",[]);
            card = card with {Expression=expression,Meaning=meaning,Reason=manual || card.Reason == "manual" ? "manual" : "auto"};
            var path = MediaPath(sha);
            if (!File.Exists(path))
            {
                var tmp = Path.Combine(_root,Guid.NewGuid().ToString("N")+".tmp");
                using (var f = new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { f.Write(mp3); f.Flush(true); }
                File.Move(tmp,path);
            }
            var updated = row with {Clip=clip,Original=row.Original ?? row.Clip,English=english,Target=target,Chinese=chinese,Source=source,Card=card.CardId,Sha256=sha,Rejected=false};
            s = s with {Lookups=s.Lookups.Select(l => l.Id == id ? updated : l).ToArray()};
            if (original is not null && original.CardId != card.CardId) s = Rebuild(s,original);
            s = Rebuild(s,card);
            if (LocalOnly && queueAnki) (s,result) = QueueVerified(s,card.CardId,id);
            Commit(s);
        }
        Wake();
        return result;
    }

    private CloudState Rebuild(CloudState s, CloudCard card)
    {
        var unique = new List<CloudLookup>();
        foreach (var item in s.Lookups.Where(l => l.Card == card.CardId && !l.Rejected && l.Sha256 is not null))
        {
            var index = unique.FindIndex(l => SameLine(l.Original ?? l.Clip,item.Original ?? item.Clip) || SameLine(l.Clip,item.Clip));
            if (index < 0) unique.Add(item);
            else unique[index] = item;
        }
        var count = unique.Count;
        var active = count >= (card.Reason == "manual" ? 1 : 2);
        var examples = active ? unique.Take(8).Select(l => new CloudExample(l.Id,l.English,l.Target,l.Chinese,l.Source,true,l.Sha256!)).ToArray() : [];
        var status = active ? "active" : card.Revision > 0 ? "withdrawn" : "draft";
        if (status == "withdrawn")
            s = s with {Lookups=s.Lookups.Select(l => l.Card == card.CardId ? l with {Card=null,Rejected=true} : l).ToArray()};
        var previous = s.Cards.FirstOrDefault(c => c.CardId == card.CardId);
        var replacement = card with {Status=status,Count=count,Examples=examples};
        if (previous is not null && previous.Status == replacement.Status && previous.Expression == replacement.Expression
            && previous.Meaning == replacement.Meaning && previous.Count == count && previous.Reason == replacement.Reason
            && previous.Examples.SequenceEqual(examples)) return s;
        if (status != "draft") replacement = replacement with {Revision=checked(card.Revision+1)};
        return Upsert(s,replacement);
    }

    private CloudState Upsert(CloudState s, CloudCard card)
    {
        s = s with {
            Cards=[..s.Cards.Where(c => c.CardId != card.CardId),card],
            Pending=LocalOnly || card.Revision == 0 ? s.Pending : [..s.Pending.Where(p => p.Card.CardId != card.CardId),new(card)]
        };
        return LocalOnly ? ReconcileAnki(s,card) : s;
    }

    internal void Withdraw(Guid id)
    {
        lock (_gate)
        {
            var s = _state;
            var row = s.Lookups.Single(l => l.Id == id);
            if (row.Card is not { } cardId)
            {
                Commit(s with {Lookups=s.Lookups.Select(l => l.Id == id ? l with {Rejected=true} : l).ToArray()});
                return;
            }
            var card = s.Cards.Single(c => c.CardId == cardId);
            var withdrawn = s with {Lookups=s.Lookups.Select(l => l.Card == cardId ? l with {Card=null,Rejected=true} : l).ToArray()};
            Commit(Rebuild(withdrawn,card));
        }
        Wake();
    }

    internal int Import(IEnumerable<(string Expression,string Meaning,string English,string Chinese)> rows)
    {
        lock (_gate)
        {
            var s = _state;
            var existing = s.Cards.Select(c => c.CardId).ToHashSet();
            var added = new List<CloudCard>();
            foreach (var (expression,meaning,english,chinese) in rows)
            {
                var key = JsonSerializer.Serialize(new[]{Clean(expression).ToUpperInvariant(),Clean(meaning),Clean(english).ToUpperInvariant()});
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0,16));
                if (existing.Add(id)) added.Add(new(s.LibraryId,id,1,"text",expression,meaning,0,"import",[],english,chinese));
            }
            if (added.Count == 0) return 0;
            Commit(s with {Cards=[..s.Cards,..added],Pending=LocalOnly ? s.Pending : [..s.Pending,..added.Select(c => new PendingCard(c))]});
            Wake();
            return added.Count;
        }
    }

    internal async Task Pair(string site,string code)
    {
        if (LocalOnly) throw new InvalidDataException("本地词本不连接原站点。");
        if (!Uri.TryCreate(site.Trim(),UriKind.Absolute,out var uri) || uri.UserInfo.Length > 0 || uri.PathAndQuery != "/" || uri.Fragment.Length > 0
            || !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) || !Regex.IsMatch(code,@"^[\w-]{43}$"))
            throw new InvalidDataException("请输入 HTTPS 站点和未过期的配对码；仅本机测试允许 HTTP。");
        var origin = uri.GetLeftPart(UriPartial.Authority);
        lock (_gate)
            if (_state.Origin is not null && _state.Origin != origin) throw new InvalidDataException("当前词库已绑定另一个站点，不能静默迁移。");
        using var response = await _http.PostAsJsonAsync(origin+"/api/pair", new {code});
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("配对失败。请检查站点和配对码。");
        var reply = await response.Content.ReadFromJsonAsync<PairReply>(Json);
        if (reply?.UploadToken is not { } credential || !Regex.IsMatch(credential,@"^[\w-]{43}$")) throw new InvalidDataException("站点未返回有效凭据。");
        var encrypted = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(credential),null,DataProtectionScope.CurrentUser));
        lock (_gate) Commit(_state with {Origin=origin,ProtectedToken=encrypted,NeedsPair=false});
        Wake();
    }

    internal void RetryErrors()
    {
        lock (_gate) if (_state.Pending.Any(p => p.Error is not null))
            Commit(_state with {Pending=_state.Pending.Select(p => p with {Error=null}).ToArray()});
        Wake();
    }

    private async Task Worker()
    {
        var delay = 6;
        while (true)
        {
            try
            {
                if (await Tick()) { _networkFailed=false; delay=6; continue; }
                await _wake.WaitAsync(TimeSpan.FromSeconds(delay));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or JsonException)
            {
                _networkFailed=true;
                await _wake.WaitAsync(TimeSpan.FromSeconds(delay));
                delay=Math.Min(delay*2,300);
            }
        }
    }

    internal async Task<bool> Tick()
    {
        var s = Snapshot;
        if (LocalOnly || s.Origin is null || s.ProtectedToken is null || s.NeedsPair || s.Pending.FirstOrDefault(p => p.Error is null) is not { } pending) return false;
        var card = pending.Card;
        string credential;
        try { credential=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.ProtectedToken),null,DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { MarkPair(s.ProtectedToken); return false; }
        var media = new List<object>();
        foreach (var e in card.Examples)
        {
            var file = new FileInfo(MediaPath(e.Sha256));
            if (!file.Exists || file.Length > 600000) { Finish(card,"缺少本地原声，请重新核对"); return true; }
            var bytes = await File.ReadAllBytesAsync(file.FullName);
            if (Hash(bytes) != e.Sha256) { Finish(card,"原声摘要不符，请重新核对"); return true; }
            media.Add(new {id=e.ExampleId,mp3=Convert.ToBase64String(bytes)});
        }
        using var request = new HttpRequestMessage(HttpMethod.Put,s.Origin+"/api/cards") {Content=JsonContent.Create(new {card,media},options:Json)};
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",credential);
        using var response = await _http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { MarkPair(s.ProtectedToken); return false; }
        if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429) throw new HttpRequestException("服务暂不可用。");
        if (!response.IsSuccessStatusCode)
        {
            Finish(card,response.StatusCode == HttpStatusCode.Conflict ? "版本冲突，请修正后重传" : "站点拒绝此卡，请重新核对");
            return true;
        }
        var reply = await response.Content.ReadFromJsonAsync<SyncReply>(Json);
        if (reply is {Result:"ready"} && reply.Revision == card.Revision) Finish(card,null);
        else if (reply is {Result:"superseded"} && reply.Revision > card.Revision) Finish(card,"服务器版本更新，请检查本地词库");
        else throw new HttpRequestException("未收到发布确认。");
        return true;
    }

    private void MarkPair(string protectedToken)
    {
        lock (_gate) if (_state.ProtectedToken == protectedToken) Commit(_state with {NeedsPair=true});
    }

    private void Finish(CloudCard card,string? error)
    {
        lock (_gate)
        {
            if (!_state.Pending.Any(p => p.Card.CardId == card.CardId && p.Card.Revision == card.Revision)) return;
            Commit(_state with {Pending=_state.Pending.SelectMany(p => p.Card.CardId != card.CardId ? new[]{p}
              : error is null ? System.Array.Empty<PendingCard>() : new[]{p with {Error=error}}).ToArray()});
        }
    }
    private sealed record PairReply(string UploadToken);
    private sealed record SyncReply(string Result,int Revision);
}
