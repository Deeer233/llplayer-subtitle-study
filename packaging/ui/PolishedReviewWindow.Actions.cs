using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LLPlayer.Services;

internal sealed partial class PolishedReviewWindow
{
    private async Task Preview(CancellationToken token)
    {
        if (_rows.SelectedItem is not CloudLookup row) return;
        ResetPreview();
        if (!double.TryParse(_from.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
            || !double.TryParse(_to.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
            throw new InvalidDataException("起止点必须是以小数点表示的秒数。");
        if (string.IsNullOrWhiteSpace(_target.Text) || !StudyCloud.ContainsTarget(_english.Text, _target.Text))
            throw new InvalidDataException("英文原句中找不到目标词形。");
        var clip = row.Clip with { Start = start, End = end };
        var file = await StudyClip.Prepare(clip, token);
        if (_closed || token.IsCancellationRequested)
        {
            StudyClip.ForgetPreview(file);
            token.ThrowIfCancellationRequested();
            return;
        }
        _preview = file;
        _clip = clip;
        PlayPreview();
    }

    private void PlayPreview()
    {
        if (_preview is not { } file) return;
        StopAudio();
        _heard = false;
        _playing = true;
        // Each playback owns its events; a late event from an older clip cannot unlock confirmation.
        var audio = new MediaPlayer { IsMuted = _mutePreview };
        _audio = audio;
        audio.MediaOpened += (_, _) => { if (_audio == audio && _playing) UpdateAudioProgress(audio); };
        audio.MediaEnded += (_, _) =>
        {
            if (_audio != audio || !_playing) return;
            var total = audio.NaturalDuration.HasTimeSpan ? audio.NaturalDuration.TimeSpan.TotalSeconds : 0;
            StopAudio();
            _heard = true;
            SetState(OperationState.Success, "试听完毕。核对内容后可确认。");
            if (total > 0)
            {
                _progressText.Text = FormattableString.Invariant($"{total:0.0} / {total:0.0} s");
                _progressText.Visibility = Visibility.Visible;
                _progress.Maximum = total;
                _progress.Value = total;
                _progress.Visibility = Visibility.Visible;
            }
            UpdateButtons();
        };
        audio.MediaFailed += (_, _) =>
        {
            if (_audio != audio) return;
            ResetPreview();
            SetState(OperationState.Failure, "无法播放原声，未确认。");
        };
        SetState(OperationState.Running, "正在打开试听…", indeterminate: true);
        UpdateButtons();
        try
        {
            audio.Open(new Uri(file, UriKind.Absolute));
            audio.Play();
            _audioClock.Start();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ResetPreview();
            SetState(OperationState.Failure, "无法播放原声，未确认。");
        }
    }

    private void UpdateAudioProgress(MediaPlayer audio)
    {
        if (!audio.NaturalDuration.HasTimeSpan || audio.NaturalDuration.TimeSpan <= TimeSpan.Zero) return;
        var total = audio.NaturalDuration.TimeSpan.TotalSeconds;
        var elapsed = Math.Clamp(audio.Position.TotalSeconds, 0, total);
        if (_progressText.Visibility != Visibility.Visible)
            SetState(OperationState.Running, "正在试听…");
        _progress.IsIndeterminate = false;
        _progress.Maximum = total;
        _progress.Value = elapsed;
        _progress.Visibility = Visibility.Visible;
        _progressText.Text = FormattableString.Invariant($"{elapsed:0.0} / {total:0.0} s");
        _progressText.Visibility = Visibility.Visible;
    }

    private void StopAudio()
    {
        _audioClock.Stop();
        var old = _audio;
        _audio = null;
        _playing = false;
        if (old is not null) old.Close();
    }

    private void ResetPreview()
    {
        _heard = false;
        _clip = null;
        StopAudio();
        if (_preview is { } file) StudyClip.ForgetPreview(file);
        _preview = null;
        UpdateButtons();
    }

    private async Task Verify(bool directAnki, CancellationToken token)
    {
        if (!_heard || _preview is not { } file || _clip is not { } clip || _rows.SelectedItem is not CloudLookup row) return;
        var bytes = await File.ReadAllBytesAsync(file, token);
        token.ThrowIfCancellationRequested();
        if (!_heard || _preview != file) return;
        var (expression, meaning, english, target, chinese, source, keepSense) =
            (_expression.Text, _meaning.Text, _english.Text, _target.Text, _chinese.Text, _source.Text, _newSense.IsChecked != true);
        await BeginWrite("正在保存词卡及本地队列…", token);
        var result = await Task.Run(() => _cloud.Verify(row.Id, clip, expression, meaning, english, target, chinese, source, bytes,
            manual: directAnki, keepSense: keepSense, queueAnki: true));
        Rebind(row.Id);
        SetState(OperationState.Success, result switch
        {
            AnkiCapture.Queued when _cloud.Snapshot.AnkiProfile is null => "已保存并排队。等待首次连接 Anki。",
            AnkiCapture.Queued => "已保存并排队。等待桌面 Anki 写入。",
            AnkiCapture.AlreadyWritten => "这张卡已写入 Anki，不会重复添加。",
            AnkiCapture.NeedsSecond => "本句已保存。再核对一条同义对白后才会排队。",
            AnkiCapture.Ineligible => "仅已保存至本地。原声暂不符合加入条件。",
            _ => "已确认，本句只计一次。"
        });
    }

    private async Task Withdraw()
    {
        if (_busy || _playing || _rows.SelectedItem is not CloudLookup { Rejected: false } row) return;
        if (MessageBox.Show(this, "撤回所选义项并取消待写入项？已写入 Anki 的卡需手动暂停。", "确认撤回",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run("正在撤回并保存…", false, async _ =>
        {
            await Task.Run(() => _cloud.Withdraw(row.Id));
            Rebind(row.Id);
            SetState(OperationState.Success, "已撤回。已写入 Anki 的卡可按卡 ID 手动暂停。");
        });
    }

    private void RefreshAnki()
    {
        var state = _cloud.Snapshot;
        if (ReferenceEquals(_ankiCards, state.Cards)) return;
        _ankiCards = state.Cards;
        var selected = _ankiRows.SelectedItems.OfType<AnkiChoice>().Select(Key).ToHashSet();
        var choices = AnkiExportService.Choices(state).OrderBy(c => c.Card.Expression).ThenBy(c => c.Card.Meaning).ToArray();
        _ankiRows.ItemsSource = choices;
        foreach (var choice in choices)
            if (selected.Contains(Key(choice))) _ankiRows.SelectedItems.Add(choice);
        _pausedIds.Text = string.Join("\n", state.Cards.Where(c => c.Status == "withdrawn").Select(AnkiExportService.PauseQuery));
    }

    private static (Guid CardId, Guid ExampleId) Key(AnkiChoice choice) => (choice.Card.CardId, choice.Example.ExampleId);

    private async Task Export()
    {
        var selected = _ankiRows.SelectedItems.OfType<AnkiChoice>().Select(Key).ToArray();
        if (_busy || _playing || selected.Length == 0) return;
        var dialog = new OpenFolderDialog { Title = "选择导出位置（程序会新建子文件夹）" };
        if (dialog.ShowDialog(this) != true) return;
        await Run("正在写入 TSV 和 MP3…", false, async _ =>
        {
            var state = _cloud.Snapshot;
            var result = await Task.Run(() => AnkiExportService.Export(state, _cloud.MediaPath, selected, dialog.FolderName));
            SetState(OperationState.Success, $"已导出 {result.CardCount} 张卡、{result.MediaCount} 个音频；未写入 Anki。",
                $"TSV: {result.TsvPath}\n说明: {result.GuidePath}");
        });
    }

    private async Task Import()
    {
        if (_busy || _playing) return;
        var dialog = new OpenFileDialog { Filter = "词表（TXT、CSV、TSV）|*.txt;*.csv;*.tsv", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        await Run("正在读取词表…", true, async token =>
        {
            var batch = await Task.Run(() => OriginalReview.WordImport.Read(dialog.FileName), token);
            token.ThrowIfCancellationRequested();
            if (batch.Entries.Count == 0)
            {
                SetState(OperationState.Failure, "没有可导入的行。", string.Join("；", batch.Errors));
                return;
            }
            if (batch.Entries.Count > 5000) throw new InvalidDataException("一次最多导入 5000 行。请拆分词表。");
            var sample = string.Join("\n", batch.Entries.Take(5).Select(e => e.Word + (e.Meaning.Length == 0 ? "" : " · " + e.Meaning)));
            if (MessageBox.Show(this, $"有效 {batch.Entries.Count} 行，无效 {batch.Invalid} 行。\n\n{sample}\n\n仅保存文字，不生成原声卡。确认导入？",
                "确认导入本地词表", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                SetState(OperationState.Idle, "未导入。");
                return;
            }
            var entries = batch.Entries.Select(e => (e.Word, e.Meaning, e.English, e.Chinese)).ToArray();
            await BeginWrite("正在写入本地词表…", token);
            var added = await Task.Run(() => _cloud.Import(entries));
            SetState(OperationState.Success, $"已导入 {added} 条；重复 {entries.Length - added} 条，无效 {batch.Invalid} 条。");
        });
    }
}
