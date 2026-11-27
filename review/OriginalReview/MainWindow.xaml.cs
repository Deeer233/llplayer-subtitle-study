using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OriginalReview;

public partial class MainWindow : Window
{
    private readonly ReviewStore? _store;
    private readonly Queue<(Card Card, bool Retry, AudioExample? Example)> _queue = new();
    private MediaPlayer? _audio;
    private bool _hinted, _revealed, _heardAudio;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var path = Environment.GetEnvironmentVariable("ORIGINAL_REVIEW_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OriginalReview");
            _store = new ReviewStore(path);
            Refresh();
        }
        catch (Exception e) when (Expected(e))
        {
            MessageBox.Show(this, $"无法打开复习数据。原文件未覆盖。\n{e.Message}", Title);
            Content = null;
        }
        Closed += (_, _) => StopAudio();
    }

    private static bool Expected(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException
        or DecoderFallbackException or JsonException;

    private async void ImportWords(object sender, RoutedEventArgs e)
    {
        if (_store is null || sender is not Button button) return;
        var dialog = new OpenFileDialog { Filter = "词表 (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv" };
        if (dialog.ShowDialog(this) != true) return;
        button.IsEnabled = false;
        SetStatus("正在读取词表…");
        try
        {
            var batch = await Task.Run(() => WordImport.Read(dialog.FileName));
            var note = $"读取到 {batch.Entries.Count} 条有效记录，无效 {batch.Invalid} 条。";
            if (batch.Errors.Count > 0) note += "\n\n" + string.Join("\n", batch.Errors);
            if (batch.Entries.Count == 0)
            {
                MessageBox.Show(this, note, "没有可导入的单词");
                SetStatus("没有导入新卡片。");
                return;
            }
            note += "\n\n预览：\n" + string.Join("\n", batch.Entries.Take(5).Select(x =>
                $"{x.Word}　{new string(x.Meaning.Take(60).ToArray())}"));
            if (MessageBox.Show(this, note, "导入为文字卡", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            {
                SetStatus("已取消导入。");
                return;
            }
            var (added, duplicates) = _store.Import(batch.Entries, Today);
            Filter.Clear();
            Tabs.SelectedItem = Library;
            SetStatus($"新增 {added} 张，跳过重复 {duplicates} 条，无效 {batch.Invalid} 条。");
        }
        catch (Exception error) when (Expected(error)) { SetStatus($"导入失败：{error.Message}"); }
        finally { button.IsEnabled = true; }
    }

    private void AddCard(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void AddAlternate(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        if (Rows.SelectedItem is not LibraryRow { Card.Audio: not null } row)
        { SetStatus("请先选中一张原声卡。"); return; }
        var editor = new CardEditor(row.Card, (entry, audio, verified) => _store.AddExample(row.Card.Id, entry, audio, verified), alternate: true)
            { Owner = this };
        if (editor.ShowDialog() == true)
        {
            SetStatus("已保存备选原声。复习日期不变。");
            Rows.SelectedItem = Rows.Items.OfType<LibraryRow>().FirstOrDefault(item => item.Card.Id == row.Card.Id);
        }
    }

    private async void FillDictionary(object sender, RoutedEventArgs e)
    {
        if (_store is null || sender is not Button button) return;
        var words = _store.Cards.Where(ReviewStore.PendingDictionary).Select(c => c.Entry.Word).Distinct(StringComparer.Ordinal).ToArray();
        if (words.Length == 0) { SetStatus("没有可批量补义的纯单词卡。带例句的卡请在词库编辑。"); return; }
        button.IsEnabled = false;
        Status.Text = "正在查本地词典…";
        try
        {
            var glosses = await Task.Run(() => ReviewDictionary.Lookup(ReviewDictionary.Bundled, words));
            var count = glosses.Values.Count(g => g.Length <= ReviewStore.MeaningLimit);
            if (count == 0) { SetStatus("没有可直接补入的词典义，待补卡未改动。"); return; }
            var note = $"{words.Length} 个待补表达中，{count} 个有可用词典义。\n\n只填没有例句的文字卡，不改已有释义。补入后可做文字复习。";
            if (MessageBox.Show(this, note, "批量补词典义", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            { SetStatus("已取消补义，词库未改动。"); return; }
            var filled = _store.ApplyDictionary(glosses, Today);
            SetStatus($"已补 {filled} 张文字卡。未补的卡可手动编辑。");
        }
        catch (Exception error) when (Expected(error)) { SetStatus($"无法补词典义：{error.Message}"); }
        finally { button.IsEnabled = true; }
    }

    private void EditCard(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is LibraryRow row) OpenEditor(row.Card);
        else SetStatus("先选一张卡片。");
    }

    private void OpenEditor(Card? card)
    {
        if (_store is null) return;
        var editor = new CardEditor(card, (entry, audio, verified) =>
        {
            if (card is null) _store.Add(entry, audio, verified, Today);
            else _store.Edit(card, entry);
        }) { Owner = this };
        if (editor.ShowDialog() == true) SetStatus(card is null ? "已保存新卡片。" : "已保存修改。");
    }

    private void RemoveCard(object sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        if (Rows.SelectedItem is not LibraryRow row) { SetStatus("先选一张卡片。"); return; }
        var note = $"移除「{row.Word}」这张卡片？" + (row.Card.Alternates is { Length: > 0 } ? "\n本地音频副本将保留。" : "");
        if (MessageBox.Show(this, note, "移除卡片", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try { _store.Remove(row.Card); SetStatus("已移除卡片。"); }
        catch (Exception error) when (Expected(error)) { SetStatus($"无法移除：{error.Message}"); }
    }

    private void SetStatus(string message) { Status.Text = message; Refresh(); }

    private void Refresh()
    {
        if (_store is null) return;
        Counts.Text = $"今日待复习：原声 {_store.Due(Today, true).Count} 张 · 文字 {_store.Due(Today, false).Count} 张"
            + $"　｜　待补释义 {_store.Cards.Count(c => c.Entry.Meaning.Length == 0)} 张";
        UpdateRows();
    }

    private void UpdateRows()
    {
        if (_store is null || Rows is null || Filter is null) return;
        var query = Filter.Text.Trim();
        Rows.ItemsSource = _store.Cards.Where(c => OnlyPending?.IsChecked != true || c.Entry.Meaning.Length == 0)
            .Where(c => query.Length == 0 || c.Entry.Word.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.Entry.Meaning.Contains(query, StringComparison.OrdinalIgnoreCase)).Reverse().Select(c => new LibraryRow(c)).ToArray();
    }

    private void FilterChanged(object sender, RoutedEventArgs e) => UpdateRows();
    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem != Library) return;
        _queue.Clear();
        StopAudio();
        Quiz.Visibility = Visibility.Collapsed;
        Question.Text = AnswerText.Text = "";
    }

    private void StartAudio(object sender, RoutedEventArgs e) => Start(true);
    private void StartText(object sender, RoutedEventArgs e) => Start(false);
    private void Start(bool withAudio)
    {
        if (_store is null) return;
        _queue.Clear();
        var today = Today;
        foreach (var card in _store.Due(today, withAudio))
        {
            var retry = card.Retry == today;
            _queue.Enqueue((card, retry, _store.ExampleFor(card, retry)));
        }
        Status.Text = _queue.Count == 0 ? "这一类今天没有到期的卡片。" : "";
        Next();
    }

    private void Next()
    {
        StopAudio();
        _hinted = _revealed = _heardAudio = false;
        Answer.Visibility = Visibility.Collapsed;
        AnswerText.Text = "";
        MediaStatus.Text = "";
        if (!_queue.TryPeek(out var item)) { Question.Text = ""; Quiz.Visibility = Visibility.Collapsed; return; }
        var audio = item.Example is not null;
        Question.Text = audio ? "听原声，回想词义" : item.Card.Entry.Word;
        CorrectButton.Content = audio ? "听懂了" : "想起来了";
        RevealButton.IsEnabled = !audio;
        ReplayButton.Visibility = HintButton.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        HintButton.IsEnabled = true;
        Quiz.Visibility = Visibility.Visible;
        if (item.Example is { } example) Play(example);
    }

    private void Play(AudioExample example)
    {
        StopAudio();
        if (_store?.AudioPath(example) is not string path || !File.Exists(path)) { MediaStatus.Text = "原声文件缺失。"; return; }
        var player = new MediaPlayer();
        _audio = player;
        MediaStatus.Text = "正在打开原声…";
        player.MediaOpened += (_, _) => { if (_audio == player) MediaStatus.Text = "正在播放原声"; };
        player.MediaEnded += (_, _) =>
        {
            if (_audio != player) return;
            _heardAudio = true;
            RevealButton.IsEnabled = !_revealed;
            MediaStatus.Text = "原声播放结束";
        };
        player.MediaFailed += (_, _) => { if (_audio == player) MediaStatus.Text = "无法播放这个原声文件。"; };
        player.Open(new Uri(path));
        player.Play();
    }

    private void StopAudio() { var player = _audio; _audio = null; player?.Close(); }
    private void Replay(object sender, RoutedEventArgs e) { if (_queue.TryPeek(out var item) && item.Example is { } example) Play(example); }
    private void Hint(object sender, RoutedEventArgs e)
    {
        if (!_revealed && _queue.TryPeek(out var item)) { _hinted = true; HintButton.IsEnabled = false; Question.Text = item.Card.Entry.Word; }
    }

    private void Reveal(object sender, RoutedEventArgs e)
    {
        if (!_queue.TryPeek(out var item) || (item.Example is not null && !_heardAudio)) return;
        _revealed = true;
        RevealButton.IsEnabled = HintButton.IsEnabled = false;
        var entry = item.Card.Entry;
        Question.Text = entry.Word;
        AnswerText.Text = string.Join("\n\n", new[] { ReviewStore.DisplayMeaning(entry), item.Example?.English ?? entry.English,
            item.Example?.Chinese ?? entry.Chinese }.Where(s => s.Length > 0));
        CorrectButton.IsEnabled = item.Example is null || _heardAudio;
        Answer.Visibility = Visibility.Visible;
    }

    private void Correct(object sender, RoutedEventArgs e) => Grade(true);
    private void Incorrect(object sender, RoutedEventArgs e) => Grade(false);
    private void Grade(bool correct)
    {
        if (_store is null || !_revealed || !_queue.TryPeek(out var item)) return;
        if (correct && item.Example is not null && !_heardAudio) return;
        try
        {
            if (item.Retry) _store.FinishRetry(item.Card);
            else
            {
                var result = _store.Grade(item.Card, Today, correct, _hinted, item.Example);
                if (result.Retry is not null && _queue.Count > 1) _queue.Enqueue((result, true, item.Example));
            }
            _queue.Dequeue();
            SetStatus(_queue.Count == 0 ? "本轮结束。" : "");
            Next();
        }
        catch (Exception e) when (Expected(e)) { SetStatus($"无法保存复习结果：{e.Message}"); }
    }

    private void Skip(object sender, RoutedEventArgs e)
    {
        if (_queue.TryDequeue(out _)) { Status.Text = _queue.Count == 0 ? "本轮结束。" : ""; Next(); }
    }

    public sealed class LibraryRow
    {
        internal Card Card { get; }
        internal LibraryRow(Card card) => Card = card;
        public string Word => Card.Entry.Word;
        public string Meaning => Card.Entry.Meaning.Length == 0 ? "待补释义" : ReviewStore.DisplayMeaning(Card.Entry).Replace('\n', ' ');
        public string Kind => Card.Audio is null ? "文字" : $"原声 · {1 + (Card.Alternates?.Length ?? 0)} 句";
        public string Due => Card.Entry.Meaning.Length == 0 ? "—" : (Card.Retry == Today ? Today : Card.Due).ToString("yyyy-MM-dd");
    }
}
