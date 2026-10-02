using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SCFA.ContentCenter.DesignSystem;

public enum UiState { Idle, Loading, Empty, Offline, Error, Warning, Success, Syncing, Disabled, PartialFailure, Info }
public enum ListDensity { Default, Comfortable, Compact }
public enum EnterMotion { None, Page, Modal }

public static class Density
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(ListDensity), typeof(Density), new FrameworkPropertyMetadata(ListDensity.Default, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetMode(DependencyObject target, ListDensity value) => target.SetValue(ModeProperty,value);
    public static ListDensity GetMode(DependencyObject target) => (ListDensity)target.GetValue(ModeProperty);
}
public static class Tone
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(UiState), typeof(Tone), new FrameworkPropertyMetadata(UiState.Idle, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetState(DependencyObject target, UiState value) => target.SetValue(StateProperty,value);
    public static UiState GetState(DependencyObject target) => (UiState)target.GetValue(StateProperty);
}
public sealed class StateNotice : Control
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State),typeof(UiState),typeof(StateNotice),new PropertyMetadata(UiState.Idle));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message),typeof(string),typeof(StateNotice),new PropertyMetadata(""));
    public UiState State {get=>(UiState)GetValue(StateProperty);set=>SetValue(StateProperty,value);}
    public string Message {get=>(string)GetValue(MessageProperty);set=>SetValue(MessageProperty,value);}
}
public sealed class StateLabelConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture) => value switch
    {
        UiState.Loading=>"正在读取", UiState.Empty=>"暂无内容", UiState.Offline=>"离线模式",
        UiState.Error=>"操作失败", UiState.Warning=>"需要注意", UiState.Success=>"状态正常",
        UiState.Syncing=>"同步进行中", UiState.Disabled=>"暂不可用", UiState.PartialFailure=>"部分失败",
        UiState.Info=>"按需检查", _=>"等待开始"
    };
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
/// <summary>Presentation only: interpret existing messages without changing commands or services.</summary>
public sealed class MessageStateConverter : IValueConverter
{
    public static UiState Classify(string message)
    {
        if(message.Contains("部分失败") || message.Contains("部分内容")) return UiState.PartialFailure;
        if(message.Contains("离线")) return UiState.Offline;
        if(message.Contains("失败") || message.Contains("错误") || message.Contains("失效") || message.Contains("无法")) return UiState.Error;
        if(message.Contains("不可用") || message.Contains("禁用")) return UiState.Disabled;
        if(message.Contains("正在") || message.Contains("恢复登录")) return UiState.Loading;
        if(message.Contains("需要") || message.Contains("请重新") || message.Contains("请输入") || message.Contains("待设置")) return UiState.Warning;
        if(message.Contains("暂无") || message.Contains("没有内容")) return UiState.Empty;
        if(message.Contains("完成") || message.Contains("正常") || message.Contains("成功") || message.Contains("已删除")) return UiState.Success;
        return UiState.Idle;
    }
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>Classify(value?.ToString()??"");
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
public sealed class DashboardStateConverter : IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)
    {
        var state=MessageStateConverter.Classify(values.ElementAtOrDefault(0)?.ToString()??"");
        if(state is UiState.Error or UiState.PartialFailure or UiState.Disabled) return state;
        if(values.ElementAtOrDefault(3) is int running && running>0) return UiState.Syncing;
        if(values.ElementAtOrDefault(1)?.ToString()?.Contains("离线")==true) return UiState.Offline;
        if(state==UiState.Loading) return state;
        if(values.ElementAtOrDefault(2) is int issues && issues>0) return UiState.Warning;
        if(values.ElementAtOrDefault(4) is int maps && maps==0 && values.ElementAtOrDefault(5) is int mods && mods==0) return UiState.Empty;
        return state==UiState.Warning?state:UiState.Success;
    }
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)=>targetTypes.Select(_=>Binding.DoNothing).ToArray();
}
public sealed class HealthStateConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value?.ToString() switch
    {
        "正常"=>UiState.Success, "离线"=>UiState.Offline, "已配置"=>UiState.Info,
        "不可用"=>UiState.Error, "待设置"=>UiState.Warning, _=>UiState.Idle
    };
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
public static class Motion
{
    public static readonly DependencyProperty HoverProperty=DependencyProperty.RegisterAttached("Hover",typeof(bool),typeof(Motion),new PropertyMetadata(false,HoverChanged));
    public static readonly DependencyProperty EnterProperty=DependencyProperty.RegisterAttached("Enter",typeof(EnterMotion),typeof(Motion),new PropertyMetadata(EnterMotion.None,EnterChanged));
    public static void SetHover(DependencyObject o,bool value)=>o.SetValue(HoverProperty,value);
    public static bool GetHover(DependencyObject o)=>(bool)o.GetValue(HoverProperty);
    public static void SetEnter(DependencyObject o,EnterMotion value)=>o.SetValue(EnterProperty,value);
    public static EnterMotion GetEnter(DependencyObject o)=>(EnterMotion)o.GetValue(EnterProperty);
    private static bool Enabled=>SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    private static void HoverChanged(DependencyObject o,DependencyPropertyChangedEventArgs e)
    {
        if(o is not FrameworkElement element)return;
        element.MouseEnter-=MouseEnter; element.MouseLeave-=MouseLeave; element.IsEnabledChanged-=EnabledChanged;
        if((bool)e.NewValue){element.MouseEnter+=MouseEnter;element.MouseLeave+=MouseLeave;element.IsEnabledChanged+=EnabledChanged;}
    }
    private static void EnabledChanged(object sender,DependencyPropertyChangedEventArgs e)
    {
        if(!(bool)e.NewValue) ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty,null);
    }
    private static void MouseEnter(object sender,System.Windows.Input.MouseEventArgs e)=>AnimateHover((FrameworkElement)sender,true);
    private static void MouseLeave(object sender,System.Windows.Input.MouseEventArgs e)=>AnimateHover((FrameworkElement)sender,false);
    private static void AnimateHover(FrameworkElement element,bool hover)
    {
        if(!Enabled || !element.IsEnabled)return;
        element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation((double)element.FindResource(hover?"OpacityHover":"OpacityFull"),(Duration)element.FindResource("MotionHover")));
    }
    private static void EnterChanged(DependencyObject o,DependencyPropertyChangedEventArgs e)
    {
        if(o is not FrameworkElement element)return;
        element.Loaded-=EnterLoaded;
        if((EnterMotion)e.NewValue!=EnterMotion.None)element.Loaded+=EnterLoaded;
    }
    private static void EnterLoaded(object sender,RoutedEventArgs e)
    {
        if(!Enabled)return;
        var element=(FrameworkElement)sender;
        var duration=(Duration)element.FindResource(GetEnter(element)==EnterMotion.Modal?"MotionModal":"MotionPage");
        var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
        element.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation((double)element.FindResource("OpacityEnter"),(double)element.FindResource("OpacityFull"),duration){EasingFunction=ease});
        if(element.RenderTransform is not null && !element.RenderTransform.Value.IsIdentity)return;
        var transform=new TranslateTransform();element.RenderTransform=transform;
        transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation((double)element.FindResource("MotionTranslate"),0,duration){EasingFunction=ease});
    }
}
