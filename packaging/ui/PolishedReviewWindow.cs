using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LLPlayer.Services;

// This view is injected only into the Anki release. Library, media and Anki rules stay in their existing services.
internal sealed partial class PolishedReviewWindow : Window
{
    private enum OperationState { Idle, Running, Success, Failure }
    private readonly StudyCloud _cloud;
    private readonly string _dataFolder;
    private readonly bool _mutePreview;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) },
        _audioClock = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private CloudLookup[]? _lookups;
    private CloudCard[]? _ankiCards;
    private CancellationTokenSource? _operation;
    private Guid? _pendingSelection;
    private string? _preview;
    private ClipSource? _clip;
    private MediaPlayer? _audio;
    private bool _busy, _canCancel, _playing, _heard, _closed, _rebinding;

    internal static void Open(Guid? id = null)
    {
        try
        {
            var current = Application.Current.Windows.OfType<PolishedReviewWindow>().FirstOrDefault();
            if (current is not null)
            {
                if (id is Guid lookup) current.Select(lookup);
                if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal;
                current.Activate();
                return;
            }
            var window = new PolishedReviewWindow(StudyCloud.Shared, id);
            if (Application.Current.MainWindow is { IsVisible: true } owner) window.Owner = owner;
            window.Show();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { MessageBox.Show("无法打开原声卡。请检查本地数据或其他正在运行的播放器。", "LLPlayer 原声卡"); }
    }

    internal PolishedReviewWindow(StudyCloud cloud, Guid? select) : this(cloud, select, mutePreview: false) { }

    internal PolishedReviewWindow(StudyCloud cloud, Guid? select, bool mutePreview)
    {
        _cloud = cloud;
        _mutePreview = mutePreview;
        _dataFolder = Path.GetDirectoryName(cloud.MediaPath("preview"))!;
        BuildLayout();
        var prefs = ReviewPreferences.Load(_dataFolder);
        (Width, Height) = prefs.Fit(SystemParameters.WorkArea);
        Rebind(select ?? prefs.Lookup);
        _tabs.SelectedIndex = select.HasValue ? 0 : prefs.Tab;
        RefreshAnki();
        WireEvents();
        SetState(OperationState.Idle, "请选择对白。");
        UpdateButtons();
    }

    private void WireEvents()
    {
        _rows.SelectionChanged += (_, _) => { if (!_rebinding) BindRow(); };
        foreach (var field in new[] { _expression, _meaning, _english, _target, _chinese, _source, _from, _to })
            field.TextChanged += (_, _) => OnEdit();
        _newSense.Checked += (_, _) => OnEdit();
        _newSense.Unchecked += (_, _) => OnEdit();
        _ankiRows.SelectionChanged += (_, _) => UpdateButtons();
        _tabs.SelectionChanged += (sender, e) => { if (e.Source == sender && _tabs.SelectedItem == _ankiTab) RefreshAnki(); };
        _previewButton.Click += async (_, _) => await Run("正在截取英语原声…", true, Preview);
        _replay.Click += (_, _) => { if (!_busy && !_playing && _preview is not null) PlayPreview(); };
        _verify.Click += async (_, _) => await Run("正在检查试听文件…", true, token => Verify(true, token));
        _saveForPair.Click += async (_, _) => await Run("正在检查试听文件…", true, token => Verify(false, token));
        _cancel.Click += (_, _) => CancelCurrent();
        _withdraw.Click += async (_, _) => await Withdraw();
        _import.Click += async (_, _) => await Import();
        _export.Click += async (_, _) => await Export();
        _connect.Click += async (_, _) => await Run("正在检查并绑定 Anki 配置…", false, async token =>
        {
            await _cloud.BindAnki(token);
            SetState(OperationState.Success, "桌面 Anki 已连接。待处理卡会自动写入。");
        });
        _queueExisting.Click += async (_, _) => await Run("正在将已有原声加入队列…", false, async _ =>
        {
            var count = await Task.Run(_cloud.QueueExistingAnki);
            SetState(OperationState.Success, count == 0 ? "没有新的合格原声卡。" : $"已排队 {count} 张，每个义项只选一段原声。");
        });
        _retryAnki.Click += async (_, _) => await Run("正在更新重试队列…", false, async _ =>
        {
            await Task.Run(_cloud.RetryAnki);
            SetState(OperationState.Success, "已请求重试；请查看 Anki 队列状态。");
        });
        _refresh.Tick += (_, _) => RefreshFromLibrary();
        _audioClock.Tick += (_, _) => { if (_audio is { } audio && _playing) UpdateAudioProgress(audio); };
        Loaded += (_, _) => _refresh.Start();
        Closing += (_, e) => { if (_busy && !_canCancel) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _refresh.Stop();
            _operation?.Cancel();
            ResetPreview();
            PersistPreferences();
        };
    }

    private void PersistPreferences()
    {
        var restore = RestoreBounds;
        var prefs = new ReviewPreferences(restore.IsEmpty ? Width : restore.Width, restore.IsEmpty ? Height : restore.Height,
            Math.Max(0, _tabs.SelectedIndex), (_rows.SelectedItem as CloudLookup)?.Id);
        prefs.Save(_dataFolder);
    }

    private void Select(Guid id)
    {
        if (_busy)
        {
            _pendingSelection = id;
            if (_canCancel) CancelCurrent();
            return;
        }
        _tabs.SelectedItem = _reviewTab;
        Rebind(id);
        SetState(OperationState.Idle, "已打开指定词卡。");
    }

    private void RefreshFromLibrary()
    {
        if (!_busy)
        {
            if (!ReferenceEquals(_lookups, _cloud.Snapshot.Lookups)) Rebind((_rows.SelectedItem as CloudLookup)?.Id, preserveEditor: true);
            if (_tabs.SelectedItem == _ankiTab) RefreshAnki();
        }
        var status = _cloud.Status;
        _status.Text = status;
        _status.ToolTip = status;
        var connect = _cloud.Snapshot.AnkiProfile is null ? "连接桌面 Anki" : "检查 Anki 连接";
        if (!Equals(_connect.ToolTip, connect)) ReviewTheme.Rename(_connect, connect);
    }

    private void Rebind(Guid? id, bool preserveEditor = false)
    {
        _lookups = _cloud.Snapshot.Lookups;
        var rows = _lookups.Reverse().OrderBy(r => r.Rejected ? 2 : r.Card is null ? 0 : 1).ToArray();
        _rebinding = true;
        _rows.ItemsSource = rows;
        _rows.SelectedItem = rows.FirstOrDefault(row => row.Id == id) ?? rows.FirstOrDefault();
        _rebinding = false;
        if (!preserveEditor || id is null || (_rows.SelectedItem as CloudLookup)?.Id != id) BindRow();
        if (_rows.SelectedItem is { } selected) _rows.ScrollIntoView(selected);
        _status.Text = _cloud.Status;
        _status.ToolTip = _status.Text;
    }

    private void BindRow()
    {
        var row = _rows.SelectedItem as CloudLookup;
        var card = _cloud.Snapshot.Cards.FirstOrDefault(c => c.CardId == row?.Card);
        _rebinding = true;
        _from.Text = row?.Clip.Start.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        _to.Text = row?.Clip.End.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        _expression.Text = card?.Expression ?? row?.Target ?? "";
        _meaning.Text = card?.Meaning ?? "";
        _english.Text = row?.English ?? "";
        _target.Text = row?.Target ?? "";
        _chinese.Text = row?.Chinese ?? "";
        _source.Text = row?.Source ?? "";
        _newSense.IsChecked = false;
        _newSense.Visibility = card is null ? Visibility.Collapsed : Visibility.Visible;
        _rebinding = false;
        ResetPreview();
        if (!_busy) SetState(OperationState.Idle, row is null ? "暂无待核对白。" : "本条对白尚未试听。");
    }

    private void OnEdit()
    {
        if (_rebinding || _closed) return;
        var wasAuditioned = _preview is not null;
        ResetPreview();
        if (_busy && _canCancel) _operation?.Cancel();
        if (wasAuditioned && !_busy) SetState(OperationState.Idle, "字段已改，请重新试听。");
    }

    private void UpdateButtons()
    {
        var idle = !_busy && !_playing;
        _editor.IsEnabled = !_busy;
        _ankiRows.IsEnabled = !_busy;
        _previewButton.IsEnabled = idle && _rows.SelectedItem is CloudLookup;
        _replay.IsEnabled = idle && _preview is not null;
        _verify.IsEnabled = _saveForPair.IsEnabled = idle && _heard;
        _withdraw.IsEnabled = idle && _rows.SelectedItem is CloudLookup { Rejected: false };
        _export.IsEnabled = idle && _ankiRows.SelectedItems.Count > 0;
        _connect.IsEnabled = _queueExisting.IsEnabled = _retryAnki.IsEnabled = _import.IsEnabled = idle;
        var allowCancel = _playing || (_busy && _canCancel && _operation?.IsCancellationRequested == false);
        _cancel.IsEnabled = allowCancel;
        _cancel.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetState(OperationState state, string message, string? detail = null, bool indeterminate = false)
    {
        var (label, shape) = state switch
        {
            OperationState.Running => ("运行中", RunningIcon),
            OperationState.Success => ("成功", CheckIcon),
            OperationState.Failure => ("失败", CancelIcon),
            _ => ("空闲", IdleIcon)
        };
        _stateIcon.Data = Geometry.Parse(shape);
        _stateIcon.Stroke = state == OperationState.Failure ? ReviewTheme.Error : state == OperationState.Idle ? ReviewTheme.Muted : ReviewTheme.Accent;
        _feedback.Text = $"{label} · {message}";
        _feedback.ToolTip = message;
        _detail.Text = detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = indeterminate;
        _progress.Visibility = indeterminate ? Visibility.Visible : Visibility.Collapsed;
        _progressText.Visibility = Visibility.Collapsed;
    }

    private async Task Run(string description, bool canCancel, Func<CancellationToken, Task> action)
    {
        if (_busy || _playing || _closed) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        _busy = true;
        _canCancel = canCancel;
        SetState(OperationState.Running, description, indeterminate: true);
        UpdateButtons();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            operation.Token.ThrowIfCancellationRequested();
            await action(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_closed)
            {
                ResetPreview();
                SetState(OperationState.Idle, "已取消，未保存。");
            }
        }
        catch (OperationCanceledException)
        { if (!_closed) SetState(OperationState.Failure, "操作超时，请检查后重试。"); }
        catch (Exception e) when (e is AnkiConnectException or InvalidDataException)
        { if (!_closed) SetState(OperationState.Failure, "未能完成操作。", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException
            or System.Text.DecoderFallbackException or Win32Exception or System.Security.Cryptography.CryptographicException)
        { if (!_closed) SetState(OperationState.Failure, "操作未完成。请检查文件、权限和桌面 Anki。", $"{e.GetType().Name} · 0x{e.HResult:X8}"); }
        finally
        {
            _busy = _canCancel = false;
            _operation = null;
            if (!_closed)
            {
                if (_pendingSelection is Guid pending) { _pendingSelection = null; Select(pending); }
                RefreshFromLibrary();
                UpdateButtons();
            }
        }
    }

    private void CancelCurrent()
    {
        if (_busy && _canCancel && _operation?.IsCancellationRequested == false)
        {
            _operation.Cancel();
            UpdateButtons();
        }
        else if (_playing)
        {
            ResetPreview();
            SetState(OperationState.Idle, "已取消试听，未保存。");
        }
    }

    private async Task BeginWrite(string description, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _canCancel = false;
        SetState(OperationState.Running, description, indeterminate: true);
        UpdateButtons();
        await Dispatcher.Yield(DispatcherPriority.Background);
    }
}
