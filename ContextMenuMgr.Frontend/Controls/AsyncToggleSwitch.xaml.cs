using System.Windows;
using System.Windows.Controls;

namespace ContextMenuMgr.Frontend.Controls;

public partial class AsyncToggleSwitch : UserControl
{
    public AsyncToggleSwitch() => InitializeComponent();

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(AsyncToggleSwitch),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(AsyncToggleSwitch));
    public static readonly DependencyProperty CanToggleProperty = DependencyProperty.Register(
        nameof(CanToggle), typeof(bool), typeof(AsyncToggleSwitch), new PropertyMetadata(true));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(AsyncToggleSwitch), new PropertyMetadata(string.Empty));

    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public bool CanToggle { get => (bool)GetValue(CanToggleProperty); set => SetValue(CanToggleProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
}
