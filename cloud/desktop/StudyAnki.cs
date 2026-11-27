using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LLPlayer.Services;

internal sealed record AnkiJob(Guid CardId, Guid ExampleId, int Revision, bool Written = false, string? Error = null);
internal enum AnkiCapture { NotRequested, NeedsSecond, Ineligible, Queued, AlreadyWritten }

internal sealed partial class StudyCloud
{
    private readonly IAnkiConnectClient? _anki;
    private volatile string? _ankiNotice;
    private AnkiWrite? _ankiWrite;
    private sealed record AnkiWrite(AnkiJob Job, CancellationTokenSource Cancel)
    {
        internal Task? Cancellation;
    }

    private static AnkiJob[] Jobs(CloudState state) => state.AnkiJobs ?? [];
    private static bool HasPendingAnki(CloudState state, AnkiJob job) => Jobs(state).Contains(job);

    private static string DescribeAnki(CloudState state, string? notice = null)
    {
        var jobs=Jobs(state);
        var waiting=jobs.Count(j=>!j.Written && j.Error is null);
        var written=jobs.Count(j=>j.Written);
        var error=jobs.FirstOrDefault(j=>j.Error is not null)?.Error;
        var connection=state.AnkiProfile is null ? "未连接桌面 Anki" : "已绑定桌面 Anki";
        return $"{connection} · 待写入 {waiting} · 已写入 {written}"
            + (error is null ? "" : $" · 需处理 {jobs.Count(j=>j.Error is not null)}：{error}")
            + (waiting>0 && notice is not null ? $" · {notice}" : "");
    }

    private static (CloudState State, AnkiCapture Result) QueueVerified(CloudState state, Guid cardId, Guid exampleId)
    {
        var card=state.Cards.Single(c=>c.CardId==cardId);
        if (card.Status=="draft") return (state,AnkiCapture.NeedsSecond);
        if (!AnkiExportService.Choices(state.LibraryId,card).Any(c=>c.Example.ExampleId==exampleId))
            return (state,AnkiCapture.Ineligible);
        var job=new AnkiJob(cardId,exampleId,card.Revision);
        var existing=Jobs(state).SingleOrDefault(j=>j.CardId==cardId);
        if (existing==job with {Written=true}) return (state,AnkiCapture.AlreadyWritten);
        if (existing==job) return (state,AnkiCapture.Queued);
        return (state with {AnkiJobs=[..Jobs(state).Where(j=>j.CardId!=cardId),job]},AnkiCapture.Queued);
    }

    // A withdrawal or replacement must invalidate any in-flight request for the old revision.
    // Retain the same authorised example on edits; never silently switch to a different clip.
    private static CloudState ReconcileAnki(CloudState state, CloudCard card)
    {
        var jobs=Jobs(state);
        var prior=jobs.SingleOrDefault(j=>j.CardId==card.CardId);
        if (prior is null || prior.Revision==card.Revision && card.Status=="active") return state;
        var eligible=AnkiExportService.Choices(state.LibraryId,card).Any(c=>c.Example.ExampleId==prior.ExampleId);
        return state with {AnkiJobs=jobs.Where(j=>j.CardId!=card.CardId || eligible)
            .Select(j=>j.CardId==card.CardId ? new AnkiJob(j.CardId,j.ExampleId,card.Revision) : j).ToArray()};
    }

    internal int QueueExistingAnki()
    {
        if (!LocalOnly) return 0;
        int count;
        lock (_gate)
        {
            var jobs=Jobs(_state);
            var seen=jobs.Select(j=>j.CardId).ToHashSet();
            var newJobs=AnkiExportService.Choices(_state)
                .Where(c=>seen.Add(c.Card.CardId))
                .Select(c=>new AnkiJob(c.Card.CardId,c.Example.ExampleId,c.Card.Revision)).ToArray();
            count=newJobs.Length;
            if (count!=0) Commit(_state with {AnkiJobs=[..jobs,..newJobs]});
        }
        if (count!=0) Wake();
        return count;
    }

    internal async Task BindAnki(CancellationToken token)
    {
        var client=_anki ?? throw new InvalidDataException("此版本不支持自动加入 Anki。");
        var profile=await client.GetProfileAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state.AnkiProfile is { } previous && previous!=profile)
                throw new InvalidDataException("Anki 当前不是原绑定配置，请切回原配置。");
            if (_state.AnkiProfile is null) Commit(_state with {AnkiProfile=profile});
        }
        RetryAnki();
    }

    internal void RetryAnki()
    {
        if (!LocalOnly) return;
        lock (_gate)
        {
            var jobs=Jobs(_state);
            if (jobs.Any(j=>j.Error is not null))
                Commit(_state with {AnkiJobs=jobs.Select(j=>j.Error is null ? j : j with {Error=null}).ToArray()});
            _ankiNotice=null;
        }
        Wake();
    }

    private async Task AnkiWorker()
    {
        while (true)
        {
            try
            {
                if (await AnkiTick().ConfigureAwait(false)) continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _ankiNotice="本地队列暂时无法保存，保留待处理项。";
            }
            await _wake.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
    }

    // Processes at most one item. True means the queue advanced; false means wait/retry later.
    internal async Task<bool> AnkiTick(CancellationToken stopping=default)
    {
        var client=_anki;
        if (client is null) return false;
        string profile;
        AnkiWrite write;
        AnkiChoice? choice;
        lock (_gate)
        {
            if (_ankiWrite is not null || _state.AnkiProfile is not { } bound
                || Jobs(_state).FirstOrDefault(j=>!j.Written && j.Error is null) is not { } job) return false;
            profile=bound;
            var card=_state.Cards.SingleOrDefault(c=>c.CardId==job.CardId && c.Revision==job.Revision);
            choice=card is null ? null : AnkiExportService.Choices(_state.LibraryId,card)
                .FirstOrDefault(c=>c.Example.ExampleId==job.ExampleId);
            write=new(job,CancellationTokenSource.CreateLinkedTokenSource(stopping));
            _ankiWrite=write;
        }
        var token=write.Cancel.Token;
        try
        {
            string? error=null, notice=null;
            var written=false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (choice is null) throw new InvalidDataException("原声已撤回或有变化，请重新核对。");
                var note=AnkiExportService.BuildNote(choice,MediaPath);
                var active=await client.GetProfileAsync(token).ConfigureAwait(false);
                if (active!=profile) notice="Anki 配置已切换，请切回原配置。";
                else
                {
                    token.ThrowIfCancellationRequested();
                    await client.WriteAsync(profile,note,token).ConfigureAwait(false);
                    written=true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (AnkiConnectException e) when (e.Unavailable) { notice=e.Message; }
            catch (AnkiConnectException e) { error=e.Message; }
            catch (InvalidDataException e) { error=e.Message; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { error="无法读取原声，请重新核对。"; }

            lock (_gate)
            {
                if (token.IsCancellationRequested || !HasPendingAnki(_state,write.Job)) return false;
                _ankiNotice=notice;
                if (!written && error is null) return false;
                _ankiWrite=null;
                Commit(_state with {AnkiJobs=Jobs(_state).Select(j=>j==write.Job
                    ? j with {Written=written,Error=error} : j).ToArray()});
                return true;
            }
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_ankiWrite,write)) _ankiWrite=null;
            try
            {
                if (write.Cancellation is { } cancellation) await cancellation.ConfigureAwait(false);
            }
            finally { write.Cancel.Dispose(); }
        }
    }
}
