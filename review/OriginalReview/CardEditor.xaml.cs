using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace OriginalReview;

public partial class CardEditor : Window
{
    private readonly Action<Entry, string?, bool> _save;
    private readonly Entry? _previous;
    private string? _audio;
    private MediaPlayer? _preview;
    private CancellationTokenSource? _dictionaryRequest;

    internal CardEditor(Card? card, Action<Entry, string?, bool> save, bool alternate = false)
    {
        InitializeComponent();
        _save = save;
        _previous = card?.Entry;
        Closed += (_, _) => { _dictionaryRequest?.Cancel(); StopPreview(); };
        if (card is not null)
        {
            Word.Text = card.Entry.Word;
            Meaning.Text = card.Entry.Meaning;
            if (alternate)
            {
                Title = "添加同义原声例句";
                Word.IsReadOnly = Meaning.IsReadOnly = true;
                Verified.Content = "已核对：原句与原声一致，且属于此词义";
                return;
            }
            English.Text = card.Entry.English;
            Chinese.Text = card.Entry.Chinese;
            AudioFields.Visibility = Visibility.Collapsed;
            Word.IsReadOnly = English.IsReadOnly = card.Audio is not null;
        }
    }

    private void WordChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _dictionaryRequest?.Cancel();
        if (DictionaryResult is not null) DictionaryResult.Visibility = Visibility.Collapsed;
    }

    private async void FindDictionary(object sender, RoutedEventArgs e)
    {
        var word = Word.Text.Trim();
        if (word.Length == 0) return;
        using var request = new CancellationTokenSource();
        _dictionaryRequest = request;
        DictionaryButton.IsEnabled = false;
        DictionaryResult.Text = "正在查词典…";
        DictionaryResult.Visibility = Visibility.Visible;
        try
        {
            var found = await Task.Run(() => ReviewDictionary.Lookup(ReviewDictionary.Bundled, [word], request.Token), request.Token);
            if (!request.IsCancellationRequested)
                DictionaryResult.Text = found.TryGetValue(word, out var gloss) ? $"词典参考（非本句义）\n{gloss}" : "本地词典未收录该表达。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!request.IsCancellationRequested) DictionaryResult.Text = "本地词典不可用。";
        }
        finally
        {
            _dictionaryRequest = null;
            DictionaryButton.IsEnabled = true;
        }
    }

    private void PickAudio(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "本地原声 (*.wav;*.mp3;*.m4a)|*.wav;*.mp3;*.m4a" };
        if (dialog.ShowDialog(this) != true) return;
        StopPreview();
        _audio = dialog.FileName;
        ClipName.Text = Path.GetFileName(_audio);
        Verified.IsChecked = false;
        Verified.IsEnabled = ClearButton.IsEnabled = PreviewButton.IsEnabled = true;
    }

    private void ClearAudio(object sender, RoutedEventArgs e)
    {
        StopPreview();
        _audio = null;
        ClipName.Text = "未选原声";
        Verified.IsChecked = false;
        Verified.IsEnabled = ClearButton.IsEnabled = PreviewButton.IsEnabled = false;
    }

    private void PreviewAudio(object sender, RoutedEventArgs e)
    {
        if (_preview is not null) { StopPreview(); return; }
        if (_audio is not string path || !File.Exists(path)) { Feedback.Text = "原声文件缺失。"; return; }
        Feedback.Text = "";
        var player = _preview = new MediaPlayer();
        PreviewButton.Content = "停止";
        player.MediaEnded += (_, _) => { if (_preview == player) StopPreview(); };
        player.MediaFailed += (_, _) => { if (_preview == player) { StopPreview(); Feedback.Text = "原声无法播放。"; } };
        player.Open(new Uri(path));
        player.Play();
    }

    private void StopPreview()
    {
        var player = _preview;
        _preview = null;
        player?.Close();
        PreviewButton.Content = "试听";
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = ReviewStore.Clean(new(Word.Text, Meaning.Text, English.Text, Chinese.Text));
            if (_previous is { DictionaryReference: true } && _previous.Meaning == entry.Meaning)
            {
                if (!string.Equals(entry.Word, _previous.Word, StringComparison.OrdinalIgnoreCase) || ReviewStore.HasContext(entry))
                    throw new InvalidDataException("更改表达或添加例句时，请重新填写对应的释义。");
                entry = entry with { DictionaryReference = true };
            }
            _save(entry, _audio, Verified.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { Feedback.Text = error.Message; }
    }
}
