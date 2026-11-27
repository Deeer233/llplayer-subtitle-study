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
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LLPlayer.Services;

internal sealed class CloudReviewWindow : Window
{
    private readonly StudyCloud _cloud;
    private readonly TextBlock _status=new(), _feedback=new() {TextWrapping=TextWrapping.Wrap};
    private readonly TextBox _site=new(), _expression=new(), _meaning=new(), _english=new(), _target=new(), _chinese=new(),
        _source=new(), _from=new(), _to=new();
    private readonly PasswordBox _pairCode=new();
    private readonly ListBox _rows=new() {Height=134};
    private readonly StackPanel _reviewEditor=new();
    private readonly TextBlock _reviewEmpty=ReviewHint("还没有对白。播放时点一个英文词，在词卡里打开核对窗口。");
    private readonly CheckBox _manual=new() {Content="手动加入：一条已核对对白即可入本"},
        _keep=new() {Content="本句义只是改写，保留原卡和复习进度"},
        _newSense=new() {Content="改归其他义项，不更新原 Anki 卡"};
    private readonly MediaPlayer _audio=new();
    private readonly CancellationTokenSource _closing=new();
    private readonly DispatcherTimer _timer=new() {Interval=TimeSpan.FromSeconds(2)};
    private readonly Button _verify=new() {Content="确认已听校并保存"}, _previewButton=new() {Content="截取并试听"},
        _replay=new() {Content="重听"}, _withdraw=new() {Content="撤回所选义项"},
        _saveForPair=new() {Content="按两句规则保存"}, _connect=new() {Content="连接桌面 Anki"},
        _queueExisting=new() {Content="自动加入已有原声"}, _retryAnki=new() {Content="重试待处理卡"};
    private readonly TabControl _tabs=new();
    private readonly TabItem _reviewTab=new() {Header="核对 / 纠错"};
    private readonly TabItem _ankiTab=new() {Header="Anki 连接"};
    private readonly ListBox _ankiRows=new() {SelectionMode=SelectionMode.Extended, Height=148};
    private readonly TextBox _pausedIds=new() {IsReadOnly=true, TextWrapping=TextWrapping.Wrap, AcceptsReturn=true,
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto, MaxHeight=90};
    private readonly Button _export=new() {Content="导出所选卡片…"};
    private CloudCard[]? _ankiCards;
    private CloudLookup[]? _lookups;
    private ClipSource? _previewClip;
    private string? _preview;
    private bool _heard, _busy, _closed, _rebinding;

    internal static void Open(Guid? id = null)
    {
        try
        {
            var current = Application.Current.Windows.OfType<CloudReviewWindow>().FirstOrDefault();
            if (current is not null)
            {
                if (id.HasValue) current.Select(id.Value);
                current.Activate(); return;
            }
            var window = new CloudReviewWindow(StudyCloud.Shared,id);
            if (Application.Current.MainWindow is {IsVisible:true} owner) window.Owner=owner;
            window.Show();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { MessageBox.Show("私人词本无法打开，请检查本地数据或其他正在运行的播放器。","私人词本"); }
    }

    private CloudReviewWindow(StudyCloud cloud,Guid? select)
    {
        _cloud=cloud;
        Title="私人词本"; Width=640; Height=cloud.LocalOnly ? 510 : 730; MinWidth=450; MinHeight=430; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel {Margin=new Thickness(13)};
        _status.Margin=new Thickness(0,0,0,8); _status.TextWrapping=TextWrapping.Wrap;
        _feedback.Margin=new Thickness(0,8,0,0);
        DockPanel.SetDock(_status,Dock.Top); root.Children.Add(_status);
        DockPanel.SetDock(_feedback,Dock.Bottom); root.Children.Add(_feedback);
        root.Children.Add(_tabs); Content=root;

        var list=new StackPanel {Margin=new Thickness(cloud.LocalOnly ? 12 : 8)};
        var form=list;
        if (cloud.LocalOnly)
        {
            _reviewTab.Header="核对原声";
            _verify.Content="确认并加入 Anki";
            list.Children.Add(ReviewText("1  选择要核对的对白", bold:true));
            ConfigureReviewRows(_rows);
            list.Children.Add(_rows);
            list.Children.Add(_reviewEmpty);
            list.Children.Add(_reviewEditor);
            form=_reviewEditor;

            var fields=new Grid();
            fields.ColumnDefinitions.Add(new() {Width=new GridLength(1,GridUnitType.Star)});
            fields.ColumnDefinitions.Add(new() {Width=new GridLength(2,GridUnitType.Star)});
            var expression=new StackPanel {Margin=new Thickness(0,0,10,0)};
            var meaning=new StackPanel();
            ReviewField(expression,"单词 / 短语",_expression);
            _expression.FontWeight=FontWeights.SemiBold;
            ReviewField(meaning,"2  填写本句义（必填）",_meaning,primary:true);
            Grid.SetColumn(meaning,1);
            fields.Children.Add(expression); fields.Children.Add(meaning);
            form.Children.Add(fields);
            form.Children.Add(ReviewHint("写这个词在这句里的意思，例如：need → 需要。"));
            ReviewField(form,"英文原句",_english,wrap:true);
        }
        else
        {
            list.Children.Add(_rows);
            Field(list,"表达",_expression); Field(list,"本句义",_meaning); Field(list,"英文原句",_english);
        }
        var details=cloud.LocalOnly ? new StackPanel {Margin=new Thickness(8,0,0,0)} : list;
        void AddField(Panel panel,string title,TextBox box)
        { if (cloud.LocalOnly) ReviewField(panel,title,box); else Field(panel,title,box); }
        AddField(details,"目标词形（原句里实际读出的词）",_target);
        AddField(details,cloud.LocalOnly ? "整句中文（可空，仅保存在本机）" : "整句中文（可空）",_chinese);
        AddField(details,"简要出处（不填路径）",_source);
        var range = new Grid();
        range.ColumnDefinitions.Add(new()); range.ColumnDefinitions.Add(new());
        var begin = new StackPanel {Margin=new Thickness(0,0,8,0)};
        var end = new StackPanel();
        AddField(begin,"起点（秒）",_from); AddField(end,"终点（秒）",_to);
        Grid.SetColumn(end,1); range.Children.Add(begin); range.Children.Add(end); details.Children.Add(range);
        if (!cloud.LocalOnly) { list.Children.Add(_manual); list.Children.Add(_keep); }
        if (cloud.LocalOnly) form.Children.Add(ReviewHint("3  截取并试听。听完后，再确认加入 Anki。"));
        var controls=new WrapPanel {Margin=new Thickness(0,cloud.LocalOnly ? 0 : 10,0,0)};
        foreach (var button in new[]{_previewButton,_replay,_verify})
        {
            StyleButton(button);
            if (cloud.LocalOnly)
            {
                button.Style=null;
                button.Foreground=ReviewInk;
                button.Background=button==_verify ? ReviewBrush(220,235,250) : Brushes.White;
                button.BorderBrush=button==_verify ? ReviewAccent : ReviewEdge;
                button.MinHeight=32;
            }
            controls.Children.Add(button);
        }
        StyleButton(_withdraw);
        if (!cloud.LocalOnly) controls.Children.Add(_withdraw);
        form.Children.Add(controls);
        if (cloud.LocalOnly)
        {
            details.Children.Add(_newSense);
            details.Children.Add(ReviewHint("按两句规则：两条不同对白确认同一义项后，程序才加入 Anki。"));
            var extra=new WrapPanel();
            StyleButton(_saveForPair); extra.Children.Add(_saveForPair); extra.Children.Add(_withdraw);
            details.Children.Add(extra);
            form.Children.Add(new Expander {Header=ReviewText("调整原声与更多操作",bold:true),Content=details,
                Margin=new Thickness(0,7,0,0),Style=null,Foreground=ReviewInk});
        }
        var scroll=new ScrollViewer {Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        if (cloud.LocalOnly) _tabs.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        _reviewTab.Content=cloud.LocalOnly ? new Border {Background=ReviewPaper,Child=scroll} : scroll;
        _tabs.Items.Add(_reviewTab);

        var setup=new StackPanel {Margin=new Thickness(8)};
        _site.Text=cloud.Snapshot.Origin ?? "";
        Field(setup,"私人站点",_site); Field(setup,"一次性配对码",_pairCode);
        var pair = new Button {Content="配对"}; StyleButton(pair); setup.Children.Add(pair);
        var retry = new Button {Content="重试待传任务"}; StyleButton(retry); setup.Children.Add(retry);
        var import = new Button {Content="导入 TXT / CSV / TSV"}; StyleButton(import); setup.Children.Add(import);
        if (_cloud.LocalOnly)
        {
            setup.Children.Clear(); setup.Children.Add(Hint("可选功能。只保存文字，不生成原声卡，也不写入 Anki。"));
            setup.Children.Add(import);
            _ankiTab.Content=BuildAnkiPanel(); _tabs.Items.Add(_ankiTab);
            _tabs.Items.Add(new TabItem {Header="本地词表",Content=setup});
            var tabStyle=ReviewTabStyle.Create();
            foreach (var item in _tabs.Items.OfType<TabItem>()) item.Style=tabStyle;
            _ankiRows.SelectionChanged += (_,_)=>UpdateButtons();
            _export.Click += async (_,_)=>await Run(ExportAnki);
            _tabs.SelectionChanged += (sender,e)=>{ if (e.Source==sender && _tabs.SelectedItem==_ankiTab) RefreshAnki(); };
            _connect.Click += async (_,_)=>await Run(async ()=>{
                await _cloud.BindAnki(_closing.Token);
                _feedback.Text="已连接桌面 Anki，待处理卡会自动写入。手机复习前请在 Anki 确认同步完成。";
            });
            _queueExisting.Click += async (_,_)=>await Run(()=>{
                var count=_cloud.QueueExistingAnki();
                _feedback.Text=count==0 ? "没有新的合格原声卡要加入。" : $"已自动筛选并排队 {count} 张，只为每个义项选一段原声。";
                return Task.CompletedTask;
            });
            _retryAnki.Click += async (_,_)=>await Run(()=>{
                _cloud.RetryAnki(); _feedback.Text="已请求重试。";
                return Task.CompletedTask;
            });
        }
        else _tabs.Items.Add(new TabItem {Header="配对 / 导入",Content=setup});

        _rows.SelectionChanged += (_,_) => { if (!_rebinding) BindRow(); };
        _from.TextChanged += (_,_) => ResetPreview(); _to.TextChanged += (_,_) => ResetPreview();
        _english.TextChanged += (_,_) => ResetPreview(); _target.TextChanged += (_,_) => ResetPreview();
        _previewButton.Click += async (_,_) => await Run(Preview);
        _replay.Click += (_,_) => { if (_preview is not null) { _audio.Position=TimeSpan.Zero; _audio.Play(); } };
        _audio.MediaEnded += (_,_) => { if (_preview is not null) { _heard=true; UpdateButtons(); _feedback.Text="已播放完毕。只在词义、英语轨、完整对白和目标词都正确时确认。"; } };
        _audio.MediaFailed += (_,_) => { ResetPreview(); _feedback.Text="不能播放剪辑，未确认。"; };
        _verify.Click += async (_,_) => await Run(()=>Verify(directAnki:_cloud.LocalOnly));
        _saveForPair.Click += async (_,_) => await Run(()=>Verify(directAnki:false));
        _withdraw.Click += async (_,_) => await Run(() => {
            if (_rows.SelectedItem is CloudLookup row && MessageBox.Show(this,_cloud.LocalOnly ? "撤回所选义项，不再导出；若已导入 Anki，需手动暂停。" : "撤回所选整张义项；旧手机题目将失效。","确认撤回",MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                var card=_cloud.Snapshot.Cards.FirstOrDefault(c=>c.CardId==row.Card && c.Status=="active");
                _cloud.Withdraw(row.Id); Rebind(row.Id);
                _feedback.Text=_cloud.LocalOnly && card is not null
                    ? "已在本地撤回并取消排队。若已写入 Anki，请在 Anki 连接页查看暂停办法。"
                    : "已在本地撤回。";
            }
            return Task.CompletedTask;
        });
        pair.Click += async (_,_) => await Run(async () => {
            var code=_pairCode.Password.Trim(); _pairCode.Clear(); await _cloud.Pair(_site.Text,code); _feedback.Text="配对成功，后台继续上传。";
        });
        retry.Click += async (_,_) => await Run(() => { _cloud.RetryErrors(); _feedback.Text="已请求重试。"; return Task.CompletedTask; });
        import.Click += async (_,_) => await Run(Import);
        _timer.Tick += (_,_) => {
            _status.Text=_cloud.Status;
            if (!_busy && !ReferenceEquals(_lookups,_cloud.Snapshot.Lookups)) Rebind((_rows.SelectedItem as CloudLookup)?.Id, preserveEditor:true);
            if (!_busy && _tabs.SelectedItem==_ankiTab) { RefreshAnki(); UpdateButtons(); }
        };
        Closed += (_,_) => { _closed=true; _timer.Stop(); _closing.Cancel(); ResetPreview(); };
        Rebind(select); _status.Text=cloud.Status; _timer.Start();
        if (!cloud.LocalOnly && select is null && cloud.Snapshot.Origin is null && cloud.Snapshot.Lookups.Length == 0) _tabs.SelectedIndex=1;
    }

    internal void Select(Guid id) { if (!_busy) { _tabs.SelectedItem=_reviewTab; Rebind(id); } }
    private static TextBlock Hint(string text) => new() {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,6)};

    private static readonly Brush ReviewPaper=ReviewBrush(246,248,252), ReviewInk=ReviewBrush(26,40,59),
        ReviewMuted=ReviewBrush(69,88,111), ReviewEdge=ReviewBrush(120,139,161), ReviewAccent=ReviewBrush(38,95,156);

    private static Brush ReviewBrush(byte r,byte g,byte b)
    {
        var brush=new SolidColorBrush(Color.FromRgb(r,g,b));
        brush.Freeze();
        return brush;
    }

    private static TextBlock ReviewText(string text,bool bold=false) => new()
    {
        Text=text,Style=null,Foreground=ReviewInk,TextWrapping=TextWrapping.Wrap,
        FontWeight=bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static TextBlock ReviewHint(string text)
    {
        var hint=ReviewText(text);
        hint.Foreground=ReviewMuted;
        hint.Margin=new Thickness(0,4,0,6);
        return hint;
    }

    private static void ReviewField(Panel panel,string title,TextBox box,bool primary=false,bool wrap=false)
    {
        var caption=new Label {Content=ReviewText(title,bold:true),Target=box,Style=null,Foreground=ReviewInk,
            Padding=new Thickness(0,0,0,4)};
        AutomationProperties.SetName(box,title);
        box.Style=null;
        box.Foreground=box.CaretBrush=ReviewInk;
        box.Background=Brushes.White;
        box.BorderBrush=primary ? ReviewAccent : ReviewEdge;
        box.BorderThickness=new Thickness(primary ? 2 : 1);
        box.Padding=new Thickness(8,4,8,4);
        box.MinHeight=32;
        box.VerticalContentAlignment=VerticalAlignment.Center;
        if (wrap)
        {
            box.TextWrapping=TextWrapping.Wrap;
            box.MaxHeight=88;
            box.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        }
        var field=new StackPanel {Margin=new Thickness(0,0,0,8)};
        field.Children.Add(caption); field.Children.Add(box); panel.Children.Add(field);
    }

    private static void ConfigureReviewRows(ListBox rows)
    {
        rows.Style=null;
        rows.Height=double.NaN;
        rows.MaxHeight=110;
        rows.Margin=new Thickness(0,4,0,10);
        rows.BorderBrush=ReviewEdge;
        rows.BorderThickness=new Thickness(1);
        rows.Background=Brushes.White;
        rows.Foreground=ReviewInk;
        ScrollViewer.SetHorizontalScrollBarVisibility(rows,ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(rows,"选择要核对的对白");
        rows.ItemContainerStyle=(Style)XamlReader.Parse("""
            <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                   xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ListBoxItem}'>
                <Setter Property='Template'>
                    <Setter.Value>
                        <ControlTemplate TargetType='{x:Type ListBoxItem}'>
                            <Border x:Name='Row' Background='#FFFFFF' BorderBrush='Transparent' BorderThickness='2' Padding='6,3'>
                                <TextBlock x:Name='RowText' Style='{x:Null}' Foreground='#1A283B' Text='{Binding}'
                                           TextWrapping='NoWrap' TextTrimming='CharacterEllipsis' ToolTip='{Binding}'/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property='IsMouseOver' Value='True'>
                                    <Setter TargetName='Row' Property='Background' Value='#EDF4FC'/>
                                </Trigger>
                                <Trigger Property='IsSelected' Value='True'>
                                    <Setter TargetName='Row' Property='Background' Value='#DCEBFA'/>
                                    <Setter TargetName='RowText' Property='FontWeight' Value='SemiBold'/>
                                </Trigger>
                                <Trigger Property='IsKeyboardFocused' Value='True'>
                                    <Setter TargetName='Row' Property='BorderBrush' Value='#265F9C'/>
                                </Trigger>
                                <Trigger Property='IsEnabled' Value='False'>
                                    <Setter TargetName='RowText' Property='Foreground' Value='#45586F'/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """);
    }

    private ScrollViewer BuildAnkiPanel()
    {
        var panel=new StackPanel {Margin=new Thickness(8)};
        panel.Children.Add(Hint("首次：在桌面 Anki 的「工具 → 附加组件 → 获取附加组件」输入 2055492159，重启 Anki。回来点「连接桌面 Anki」，绑定当前 Anki 配置。"));
        StyleButton(_connect); panel.Children.Add(_connect);
        panel.Children.Add(Hint("此后原声自动写入桌面 Anki。Anki 没开也可继续核对，卡片会在本机等候。手机复习前，在 Anki 点同步并等媒体传完。"));
        var actions=new WrapPanel();
        foreach (var button in new[]{_queueExisting,_retryAnki}) { StyleButton(button); actions.Children.Add(button); }
        panel.Children.Add(actions);
        panel.Children.Add(Hint("只有点「自动加入已有原声」才会处理旧卡：跳过文字词表和未核对项，每个义项用一段原声。"));
        var backup=new StackPanel {Margin=new Thickness(8,0,0,0)};
        backup.Children.Add(Hint("仅在不能用自动写入时使用。按住 Ctrl 可多选，同一义项只选一段。"));
        backup.Children.Add(_ankiRows); StyleButton(_export); backup.Children.Add(_export);
        panel.Children.Add(new Expander {Header="备用：手动导出到 Anki",Content=backup,Margin=new Thickness(0,9,0,0)});
        var withdrawn=new StackPanel {Margin=new Thickness(8,0,0,0)};
        withdrawn.Children.Add(Hint("若已写入 Anki，可以在 Anki 浏览窗口按下列搜索条件查找，然后手动暂停。"));
        withdrawn.Children.Add(_pausedIds);
        panel.Children.Add(new Expander {Header="已撤回卡：在 Anki 暂停",Content=withdrawn,Margin=new Thickness(0,9,0,0)});
        return new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private static void Field(Panel parent,string title,Control field)
    {
        parent.Children.Add(new Label {Content=title,Target=field,Padding=new Thickness(0,5,0,1)});
        field.MinHeight=26; parent.Children.Add(field);
    }
    private static void StyleButton(Button b) { b.Margin=new Thickness(0,3,6,3); b.Padding=new Thickness(9,4,9,4); b.HorizontalAlignment=HorizontalAlignment.Left; }
    private void Rebind(Guid? id,bool preserveEditor=false)
    {
        _lookups=_cloud.Snapshot.Lookups;
        var rows=_lookups.Reverse().OrderBy(r=>r.Rejected ? 2 : r.Card is null ? 0 : 1).ToArray();
        _rebinding=true;
        _rows.ItemsSource=rows;
        _rows.SelectedItem=rows.FirstOrDefault(row=>row.Id==id) ?? rows.FirstOrDefault();
        if (_cloud.LocalOnly)
        {
            _rows.Visibility=_reviewEditor.Visibility=rows.Length==0 ? Visibility.Collapsed : Visibility.Visible;
            _reviewEmpty.Visibility=rows.Length==0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.SelectedItem is { } selected) _rows.ScrollIntoView(selected);
        }
        _rebinding=false;
        if (!preserveEditor || id is null || (_rows.SelectedItem as CloudLookup)?.Id != id) BindRow();
        _status.Text=_cloud.Status;
    }
    private void BindRow()
    {
        var row=_rows.SelectedItem as CloudLookup;
        var card=_cloud.Snapshot.Cards.FirstOrDefault(c=>c.CardId==row?.Card);
        _from.Text=row?.Clip.Start.ToString("0.###",CultureInfo.InvariantCulture) ?? "";
        _to.Text=row?.Clip.End.ToString("0.###",CultureInfo.InvariantCulture) ?? "";
        _expression.Text=card?.Expression ?? row?.Target ?? ""; _meaning.Text=card?.Meaning ?? "";
        _english.Text=row?.English ?? ""; _target.Text=row?.Target ?? ""; _chinese.Text=row?.Chinese ?? ""; _source.Text=row?.Source ?? "";
        _manual.IsChecked=card?.Reason=="manual"; _keep.IsChecked=false; _keep.Visibility=card is null ? Visibility.Collapsed : Visibility.Visible;
        _newSense.IsChecked=false; _newSense.Visibility=card is null ? Visibility.Collapsed : Visibility.Visible;
        ResetPreview();
    }
    private void ResetPreview()
    {
        _heard=false; _previewClip=null;
        if (_preview is { } file) { _preview=null; _audio.Close(); StudyClip.ForgetPreview(file); }
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        _rows.IsEnabled=!_busy;
        _ankiRows.IsEnabled=!_busy;
        _export.IsEnabled=!_busy && _ankiRows.SelectedItems.Count>0;
        _previewButton.IsEnabled=!_busy && _rows.SelectedItem is CloudLookup;
        _withdraw.IsEnabled=!_busy && _rows.SelectedItem is CloudLookup {Rejected:false};
        _replay.IsEnabled=!_busy && _preview is not null;
        _verify.IsEnabled=!_busy && _heard;
        _saveForPair.IsEnabled=_verify.IsEnabled;
        _connect.IsEnabled=_queueExisting.IsEnabled=_retryAnki.IsEnabled=!_busy;
        _connect.Content=_cloud.Snapshot.AnkiProfile is null ? "连接桌面 Anki" : "检查 Anki 连接";
    }
    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy=true; UpdateButtons();
        try { await action(); }
        catch (AnkiConnectException e) { if (!_closed) _feedback.Text=e.Message; }
        catch (InvalidDataException e) { _feedback.Text=e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException
            or System.Text.DecoderFallbackException or Win32Exception or System.Security.Cryptography.CryptographicException)
        { if (!_closed) _feedback.Text=_cloud.LocalOnly ? "未完成。请检查文件读取、存储权限或 ffmpeg。" : "未完成。请检查文件、ffmpeg 或站点连接。"; }
        finally { _busy=false; _status.Text=_cloud.Status; UpdateButtons(); }
    }
    private async Task Preview()
    {
        if (_rows.SelectedItem is not CloudLookup row) return;
        if (!double.TryParse(_from.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var begin)
            || !double.TryParse(_to.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var end))
            throw new InvalidDataException("请输入秒数，小数用点分隔。");
        if (string.IsNullOrWhiteSpace(_target.Text) || !StudyCloud.ContainsTarget(_english.Text,_target.Text))
            throw new InvalidDataException("英文原句中找不到目标词形。");
        ResetPreview();
        var clip=row.Clip with {Start=begin,End=end};
        var original=(_from.Text,_to.Text,_english.Text,_target.Text);
        _feedback.Text="正在截取英语原声…";
        var file=await StudyClip.Prepare(clip,_closing.Token);
        if (_closed || original != (_from.Text,_to.Text,_english.Text,_target.Text)) { StudyClip.ForgetPreview(file); return; }
        _preview=file; _previewClip=clip;
        _audio.Open(new Uri(file)); _audio.Play(); _feedback.Text="正在试听…";
    }
    private async Task Verify(bool directAnki)
    {
        if (!_heard || _preview is not { } file || _previewClip is not { } clip || _rows.SelectedItem is not CloudLookup row) return;
        var bytes=await File.ReadAllBytesAsync(file,_closing.Token);
        if (!_heard || _preview != file) return;
        var result=_cloud.Verify(row.Id,clip,_expression.Text,_meaning.Text,_english.Text,_target.Text,_chinese.Text,_source.Text,bytes,
            manual:directAnki || (!_cloud.LocalOnly && _manual.IsChecked==true),
            keepSense:_cloud.LocalOnly ? _newSense.IsChecked!=true : _keep.IsChecked==true,
            queueAnki:_cloud.LocalOnly);
        Rebind(row.Id);
        _feedback.Text=result switch
        {
            AnkiCapture.Queued when _cloud.Snapshot.AnkiProfile is null => "已保存并排队。首次使用请在「Anki 连接」页连接桌面 Anki。",
            AnkiCapture.Queued => "已排队，会自动写入桌面 Anki。",
            AnkiCapture.AlreadyWritten => "这张卡已写入桌面 Anki，不会重复添加。",
            AnkiCapture.NeedsSecond => "本句已核对，再核对一条同义对白后会自动加入。",
            AnkiCapture.Ineligible => "已保存到本机，但当前原声不符合加入条件。请检查台词、出处是否写入了本地路径。",
            _ => "已确认。本句只计一次；达到入本条件后自动同步。"
        };
    }
    private void RefreshAnki()
    {
        var state=_cloud.Snapshot;
        if (ReferenceEquals(_ankiCards,state.Cards)) return;
        var selected=_ankiRows.SelectedItems.OfType<AnkiChoice>().ToHashSet();
        var choices=AnkiExportService.Choices(state).OrderBy(c=>c.Card.Expression).ThenBy(c=>c.Card.Meaning).ToArray();
        _ankiCards=state.Cards;
        _ankiRows.ItemsSource=choices;
        foreach (var choice in choices)
            if (selected.Contains(choice)) _ankiRows.SelectedItems.Add(choice);
        _pausedIds.Text=string.Join("\n",state.Cards.Where(c=>c.Status=="withdrawn").Select(AnkiExportService.PauseQuery));
    }
    private async Task ExportAnki()
    {
        var selected=_ankiRows.SelectedItems.OfType<AnkiChoice>().ToArray();
        if (selected.Length==0) return;
        var dialog=new OpenFolderDialog {Title="选择 Anki 导出的上级文件夹（会新建子目录）"};
        if (dialog.ShowDialog(this)!=true) return;
        var state=_cloud.Snapshot;
        var current=AnkiExportService.Choices(state).ToHashSet();
        if (selected.Any(choice=>!current.Contains(choice)))
            throw new InvalidDataException("选中的卡片有变化，请重新选择后再导出。");
        var result=await Task.Run(()=>AnkiExportService.Export(state,_cloud.MediaPath,
            selected.Select(c=>(c.Card.CardId,c.Example.ExampleId)),dialog.FolderName));
        if (!_closed) _feedback.Text=$"{result.StatusText}\n导入表：{result.TsvPath}\n导入说明：{result.GuidePath}";
    }
    private async Task Import()
    {
        var dialog=new OpenFileDialog {Filter="词表（TXT、CSV、TSV）|*.txt;*.csv;*.tsv",CheckFileExists=true};
        if (dialog.ShowDialog(this)!=true) return;
        var batch=await Task.Run(()=>OriginalReview.WordImport.Read(dialog.FileName));
        if (_closed) return;
        if (batch.Entries.Count==0) { _feedback.Text="没有可导入的行。"+string.Join("；",batch.Errors); return; }
        if (batch.Entries.Count>5000) throw new InvalidDataException("一次最多导入 5000 行，请拆分词表。");
        var preview=string.Join("\n",batch.Entries.Take(5).Select(e=>e.Word+(e.Meaning.Length==0 ? "" : " · "+e.Meaning)));
        var scope=_cloud.LocalOnly ? "仅保存到本地，不上传，也不生成原声题。" : "仅上传所选词表的文字内容到配对站点，不生成原声题。";
        if (MessageBox.Show(this,$"有效 {batch.Entries.Count} 行，无效 {batch.Invalid} 行。\n\n{preview}\n\n{scope}确认导入？",
            "确认导入私人词库",MessageBoxButton.YesNo)!=MessageBoxResult.Yes) return;
        var added=_cloud.Import(batch.Entries.Select(e=>(e.Word,e.Meaning,e.English,e.Chinese)));
        _feedback.Text=$"已导入 {added} 条文字词卡，跳过重复 {batch.Entries.Count-added} 条、无效 {batch.Invalid} 行。";
    }
}
