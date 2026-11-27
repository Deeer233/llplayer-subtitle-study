using System.Windows;
using System.Windows.Markup;

namespace LLPlayer.Services;

internal static class ReviewTabStyle
{
    internal static Style Create() => (Style)XamlReader.Parse(Markup);

    private const string Markup = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type TabItem}'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Padding' Value='12,5'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
        <Setter.Value>
            <ControlTemplate TargetType='{x:Type TabItem}'>
                <Border x:Name='FocusRing' Margin='0,0,4,2' Padding='2'
                        Background='Transparent' BorderBrush='Transparent' BorderThickness='2' CornerRadius='7'>
                    <Border x:Name='Surface' Background='#232B37' BorderBrush='#65778E' BorderThickness='1'
                            CornerRadius='4' Padding='{TemplateBinding Padding}'>
                        <!-- Review tab headers are strings; do not inherit the application's text style. -->
                        <TextBlock x:Name='HeaderText' Style='{x:Null}' Text='{TemplateBinding Header}'
                                   Foreground='#F4F7FB' HorizontalAlignment='Center' VerticalAlignment='Center'/>
                    </Border>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='Surface' Property='Background' Value='#354459'/>
                        <Setter TargetName='Surface' Property='BorderBrush' Value='#8497B0'/>
                        <Setter TargetName='HeaderText' Property='Foreground' Value='#FFFFFF'/>
                    </Trigger>
                    <!-- A selected tab keeps its accent when the pointer is over it. -->
                    <Trigger Property='IsSelected' Value='True'>
                        <Setter TargetName='Surface' Property='Background' Value='#9BD1FF'/>
                        <Setter TargetName='Surface' Property='BorderBrush' Value='#9BD1FF'/>
                        <Setter TargetName='HeaderText' Property='Foreground' Value='#122237'/>
                    </Trigger>
                    <Trigger Property='IsKeyboardFocused' Value='True'>
                        <Setter TargetName='FocusRing' Property='Background' Value='#171F2A'/>
                        <Setter TargetName='FocusRing' Property='BorderBrush' Value='#FFD166'/>
                    </Trigger>
                    <Trigger Property='IsEnabled' Value='False'>
                        <Setter TargetName='Surface' Property='Background' Value='#292F39'/>
                        <Setter TargetName='Surface' Property='BorderBrush' Value='#576479'/>
                        <Setter TargetName='HeaderText' Property='Foreground' Value='#B5C1D2'/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";
}
