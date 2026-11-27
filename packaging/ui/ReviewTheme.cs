using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace LLPlayer.Services;

internal static class ReviewTheme
{
    internal static readonly Brush Backdrop = ColorBrush(245, 246, 248), Surface = Brushes.White,
        Ink = ColorBrush(31, 41, 55), Muted = ColorBrush(70, 83, 101), Edge = ColorBrush(202, 210, 221),
        Accent = ColorBrush(25, 78, 151), Error = ColorBrush(151, 42, 48);
    internal static readonly FontFamily TextFont = new("Microsoft YaHei UI"), Mono = new("Consolas");

    internal static Button Button(string text, PackIconKind kind, bool primary = false)
    {
        var label = new TextBlock { Text = text };
        var icon = new PackIcon { Kind = kind, Width = 16, Height = 16,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(label);
        var button = new Button { Content = content, ToolTip = text, Style = primary ? Primary : Action };
        icon.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    internal static void Rename(Button button, string text)
    {
        ((TextBlock)((StackPanel)button.Content).Children[1]).Text = text;
        button.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
    }

    internal static TextBlock Text(string text, bool secondary = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = secondary ? Muted : Ink, Style = null
    };

    private static Brush ColorBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Style Action = CreateButton(false), Primary = CreateButton(true);
    internal static Style TabStyle() => (Style)XamlReader.Parse("""
        <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
               xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type TabItem}'>
            <Setter Property='OverridesDefaultStyle' Value='True'/>
            <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='{x:Type TabItem}'>
                    <Border x:Name='Outer' Margin='0,0,8,0' Padding='8' MinHeight='32'
                            BorderBrush='#A9B6C8' BorderThickness='1,1,1,3' CornerRadius='4'
                            Background='#F1F3F6'>
                        <TextBlock x:Name='Label' Style='{x:Null}' Foreground='#243248'
                                   VerticalAlignment='Center' Text='{TemplateBinding Header}'/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'>
                            <Setter TargetName='Outer' Property='Background' Value='#E7EDF5'/>
                        </Trigger>
                        <Trigger Property='IsSelected' Value='True'>
                            <Setter TargetName='Outer' Property='Background' Value='#E0EDFF'/>
                            <Setter TargetName='Outer' Property='BorderBrush' Value='#194E97'/>
                            <Setter TargetName='Label' Property='Foreground' Value='#153D77'/>
                        </Trigger>
                        <Trigger Property='IsKeyboardFocused' Value='True'>
                            <Setter TargetName='Outer' Property='BorderBrush' Value='#153D77'/>
                            <Setter TargetName='Label' Property='TextDecorations' Value='Underline'/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value></Setter>
        </Style>
        """);

    private static Style CreateButton(bool primary) => (Style)XamlReader.Parse($$"""
        <Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
               xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Button}'>
            <Setter Property='OverridesDefaultStyle' Value='True'/>
            <Setter Property='Margin' Value='0,0,8,8'/>
            <Setter Property='Padding' Value='8,4'/>
            <Setter Property='MinHeight' Value='32'/>
            <Setter Property='HorizontalAlignment' Value='Left'/>
            <Setter Property='Background' Value='{{(primary ? "#194E97" : "#FFFFFF")}}'/>
            <Setter Property='Foreground' Value='{{(primary ? "#FFFFFF" : "#17365E")}}'/>
            <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='{x:Type Button}'>
                    <Border x:Name='Outer' Background='{TemplateBinding Background}' BorderBrush='#6D819F'
                            BorderThickness='1' CornerRadius='4' Padding='{TemplateBinding Padding}'>
                        <ContentPresenter x:Name='Label' VerticalAlignment='Center' HorizontalAlignment='Center'/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'>
                            <Setter TargetName='Outer' Property='Background' Value='{{(primary ? "#113D7B" : "#E9F1FF")}}'/>
                        </Trigger>
                        <Trigger Property='IsPressed' Value='True'>
                            <Setter TargetName='Outer' Property='Background' Value='{{(primary ? "#12335D" : "#DFEAF9")}}'/>
                        </Trigger>
                        <Trigger Property='IsKeyboardFocused' Value='True'>
                            <Setter TargetName='Outer' Property='BorderBrush' Value='{{(primary ? "#A9CEFF" : "#123D7B")}}'/>
                            <Setter TargetName='Outer' Property='BorderThickness' Value='2'/>
                        </Trigger>
                        <Trigger Property='IsEnabled' Value='False'>
                            <Setter TargetName='Outer' Property='Background' Value='#E8EBF0'/>
                            <Setter TargetName='Outer' Property='BorderBrush' Value='#C4CBD5'/>
                            <Setter Property='Foreground' Value='#525E70'/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value></Setter>
        </Style>
        """);
}
