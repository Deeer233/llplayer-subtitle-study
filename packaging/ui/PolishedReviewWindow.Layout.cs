using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace LLPlayer.Services;

internal sealed partial class PolishedReviewWindow
{
    private const string CheckIcon = "M 2 8 L 6 12 14 4", CancelIcon = "M 3 3 L 13 13 M 13 3 L 3 13",
        IdleIcon = "M 3 8 L 13 8", RunningIcon = "M 8 2 A 6 6 0 1 1 2 8 M 8 5 L 8 8 11 8";

    private readonly TabControl _tabs = new();
    private readonly TabItem _reviewTab = new() { Header = "核对原声" }, _ankiTab = new() { Header = "Anki 连接" },
        _importTab = new() { Header = "本地词表" };
    private readonly ListBox _rows = new() { Height = 112 }, _ankiRows = new() { Height = 144, SelectionMode = SelectionMode.Extended };
    private readonly TextBox _expression = new(), _meaning = new(), _english = new(), _target = new(), _chinese = new(), _source = new(),
        _from = new() { FontFamily = ReviewTheme.Mono }, _to = new() { FontFamily = ReviewTheme.Mono };
    private readonly CheckBox _newSense = new() { Content = "改归其他义项，不更新原 Anki 卡" };
    private readonly StackPanel _editor = new();
    private readonly Button _previewButton = ReviewTheme.Button("截取并试听", PackIconKind.Play), _replay = ReviewTheme.Button("重听", PackIconKind.Replay),
        _verify = ReviewTheme.Button("确认并加入 Anki", PackIconKind.Check, primary: true), _saveForPair = ReviewTheme.Button("按两句规则保存", PackIconKind.Check),
        _withdraw = ReviewTheme.Button("撤回义项", PackIconKind.Close), _connect = ReviewTheme.Button("连接桌面 Anki", PackIconKind.Link),
        _queueExisting = ReviewTheme.Button("自动加入已有原声", PackIconKind.Plus), _retryAnki = ReviewTheme.Button("重试待处理卡", PackIconKind.Refresh),
        _export = ReviewTheme.Button("导出所选 TSV / MP3…", PackIconKind.Download), _import = ReviewTheme.Button("导入 TXT / CSV / TSV…", PackIconKind.File),
        _cancel = ReviewTheme.Button("取消", PackIconKind.Close);
    private readonly TextBlock _status = ReviewTheme.Text("", secondary: true), _feedback = ReviewTheme.Text(""),
        _progressText = ReviewTheme.Text("", secondary: true);
    private readonly TextBox _detail = ReadOnlyMono(48), _pausedIds = ReadOnlyMono(96);
    private readonly System.Windows.Shapes.Path _stateIcon = new()
    {
        Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 2,
        StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };
    private readonly ProgressBar _progress = new() { Height = 4, Visibility = Visibility.Collapsed, Focusable = false,
        Foreground = ReviewTheme.Accent, Background = ReviewTheme.Edge, Margin = new Thickness(0, 4, 0, 0), Style = null };

    private void BuildLayout()
    {
        Title = "LLPlayer 原声卡";
        FontFamily = ReviewTheme.TextFont;
        FontSize = 14;
        Foreground = ReviewTheme.Ink;
        Background = ReviewTheme.Backdrop;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = ReviewPreferences.MinWidth;
        MinHeight = ReviewPreferences.MinHeight;

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var title = ReviewTheme.Text("原声卡");
        title.FontSize = 18;
        title.FontWeight = FontWeights.SemiBold;
        header.Children.Add(title);
        var location = ReviewTheme.Text($"本地数据  {_dataFolder}", secondary: true);
        location.FontFamily = ReviewTheme.Mono;
        location.TextWrapping = TextWrapping.NoWrap;
        location.TextTrimming = TextTrimming.CharacterEllipsis;
        location.ToolTip = _dataFolder;
        header.Children.Add(location);
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = BuildFeedback();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _tabs.Style = null;
        _tabs.Background = ReviewTheme.Surface;
        _tabs.BorderBrush = ReviewTheme.Edge;
        var tabs = ReviewTheme.TabStyle();
        foreach (var item in new[] { _reviewTab, _ankiTab, _importTab })
        {
            item.Style = tabs;
            _tabs.Items.Add(item);
        }
        _reviewTab.Content = BuildReviewPanel();
        _ankiTab.Content = BuildAnkiPanel();
        var importPanel = new StackPanel { Margin = new Thickness(8) };
        importPanel.Children.Add(_import);
        _importTab.Content = Scroll(importPanel);
        root.Children.Add(_tabs);
        Content = root;
    }

    private FrameworkElement BuildReviewPanel()
    {
        var body = new DockPanel { Margin = new Thickness(8) };
        var primary = Commands(_previewButton, _replay, _verify);
        DockPanel.SetDock(primary, Dock.Top);
        body.Children.Add(primary);
        _editor.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(_rows, "待核对的对白");
        _rows.BorderBrush = ReviewTheme.Edge;
        _rows.Foreground = ReviewTheme.Ink;
        _rows.Background = ReviewTheme.Surface;
        _rows.Style = null;
        _editor.Children.Add(_rows);
        Field(_editor, "表达", _expression);
        Field(_editor, "本句义", _meaning);
        Field(_editor, "英文原句", _english);
        var details = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        details.Children.Add(Commands(_saveForPair, _withdraw));
        Field(details, "目标词形", _target);
        Field(details, "整句中文（可空，本机保存）", _chinese);
        Field(details, "简要出处（不填路径）", _source);
        var range = new Grid();
        range.ColumnDefinitions.Add(new());
        range.ColumnDefinitions.Add(new());
        var start = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var end = new StackPanel();
        Field(start, "起点（秒）", _from);
        Field(end, "终点（秒）", _to);
        Grid.SetColumn(end, 1);
        range.Children.Add(start);
        range.Children.Add(end);
        details.Children.Add(range);
        _newSense.Margin = new Thickness(0, 8, 0, 0);
        details.Children.Add(_newSense);
        _editor.Children.Add(new Expander { Header = "编辑时间、按两句保存与撤回", Content = details,
            Margin = new Thickness(0, 8, 0, 0), Foreground = ReviewTheme.Ink, Style = null });
        body.Children.Add(Scroll(_editor));
        return body;
    }

    private FrameworkElement BuildAnkiPanel()
    {
        var body = new DockPanel { Margin = new Thickness(8) };
        var actions = Commands(_connect, _retryAnki, _queueExisting, _export);
        DockPanel.SetDock(actions, Dock.Top);
        body.Children.Add(actions);

        var listing = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var label = new Label { Content = "可导出原声（每个义项选一段）", Target = _ankiRows, Padding = new Thickness(0, 0, 0, 8),
            Foreground = ReviewTheme.Ink, Style = null };
        listing.Children.Add(label);
        _ankiRows.BorderBrush = ReviewTheme.Edge;
        _ankiRows.Foreground = ReviewTheme.Ink;
        _ankiRows.Background = ReviewTheme.Surface;
        _ankiRows.Style = null;
        listing.Children.Add(_ankiRows);
        var addOn = ReviewTheme.Text("AnkiConnect 附加组件：2055492159", secondary: true);
        addOn.Margin = new Thickness(0, 8, 0, 0);
        listing.Children.Add(addOn);
        AutomationProperties.SetName(_pausedIds, "已经撤回的原声卡 ID");
        listing.Children.Add(new Expander { Header = "撤回卡 ID（在 Anki 手动暂停）", Content = _pausedIds,
            Margin = new Thickness(0, 8, 0, 0), Foreground = ReviewTheme.Ink, Style = null });
        body.Children.Add(Scroll(listing));
        return body;
    }

    private FrameworkElement BuildFeedback()
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new());
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var headline = new DockPanel();
        DockPanel.SetDock(_stateIcon, Dock.Left);
        headline.Children.Add(_stateIcon);
        _feedback.TextTrimming = TextTrimming.CharacterEllipsis;
        _feedback.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Polite);
        headline.Children.Add(_feedback);
        text.Children.Add(headline);
        _progressText.FontFamily = ReviewTheme.Mono;
        _progressText.Visibility = Visibility.Collapsed;
        text.Children.Add(_progressText);
        text.Children.Add(_progress);
        _detail.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_detail, "操作详情和路径");
        _detail.Margin = new Thickness(0, 8, 0, 0);
        text.Children.Add(_detail);
        layout.Children.Add(text);
        Grid.SetColumn(_cancel, 1);
        _cancel.Margin = new Thickness(8, 0, 0, 0);
        _cancel.VerticalAlignment = VerticalAlignment.Center;
        layout.Children.Add(_cancel);
        return new Border { Child = layout, Background = ReviewTheme.Surface, BorderBrush = ReviewTheme.Edge,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8),
            Margin = new Thickness(0, 8, 0, 0), MinHeight = 48 };
    }

    private static TextBox ReadOnlyMono(double maxHeight) => new()
    {
        IsReadOnly = true, AcceptsReturn = true, FontFamily = ReviewTheme.Mono, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = maxHeight, Foreground = ReviewTheme.Ink,
        BorderBrush = ReviewTheme.Edge, Background = ReviewTheme.Surface, Style = null
    };

    private static ScrollViewer Scroll(UIElement content) => new() { Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private static WrapPanel Commands(params Button[] buttons)
    {
        var panel = new WrapPanel();
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }

    private static void Field(Panel panel, string title, TextBox field)
    {
        field.MinHeight = 32;
        field.Padding = new Thickness(8, 4, 8, 4);
        field.Style = null;
        field.Foreground = ReviewTheme.Ink;
        field.Background = ReviewTheme.Surface;
        field.BorderBrush = ReviewTheme.Edge;
        panel.Children.Add(new Label { Content = title, Target = field, Foreground = ReviewTheme.Ink,
            Style = null, Padding = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(field);
    }
}
