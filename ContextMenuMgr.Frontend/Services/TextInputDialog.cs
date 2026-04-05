using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

public static class TextInputDialog
{
    public static Task<string?> ShowAsync(string title, string label, string initialText)
    {
        var window = new TextInputFluentWindow(title, label, initialText)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return Task.FromResult(window.ShowDialog() == true ? window.ResultText : null);
    }

    private sealed class TextInputFluentWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly System.Windows.Controls.TextBox _textBox;

        public TextInputFluentWindow(string title, string label, string initialText)
        {
            Title = title;
            Width = 500;
            Height = 200;
            MinWidth = 440;
            MinHeight = 200;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            ExtendsContentIntoTitleBar = true;
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica;
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"];
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["ApplicationBackgroundBrush"];

            var titleBar = new TitleBar
            {
                Title = title,
                Margin = new System.Windows.Thickness(0, 0, 0, 8),
                Icon = new SymbolIcon { Symbol = SymbolRegular.TextEditStyle24 }
            };

            _textBox = new System.Windows.Controls.TextBox
            {
                Margin = new System.Windows.Thickness(0, 8, 0, 0),
                Text = initialText
            };

            var okButton = new Wpf.Ui.Controls.Button
            {
                 Content = "OK",
                MinWidth = 88,
                IsDefault = true,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Appearance = ControlAppearance.Primary,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24 }
            };

            var cancelButton = new Wpf.Ui.Controls.Button
            {
                Content = "Cancel",
                MinWidth = 88,
                IsCancel = true,
                Appearance = ControlAppearance.Secondary,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 }
            };

            okButton.Click += (_, _) => DialogResult = true;

            Content = new System.Windows.Controls.Grid
            {
                Margin = new System.Windows.Thickness(10),
                RowDefinitions =
                {
                    new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto },
                    new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto },
                    new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto },
                    new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) }
                },
                Children =
                {
                    titleBar,
                    new System.Windows.Controls.TextBlock
                    {
                        Text = label,
                        FontSize = 13,
                        FontWeight = System.Windows.FontWeights.SemiBold,
                        Margin = new System.Windows.Thickness(0, 8, 0, 0)
                    },
                    _textBox,
                    new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        Margin = new System.Windows.Thickness(0, 20, 0, 0),
                        Children = { okButton, cancelButton }
                    }
                }
            };

            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            System.Windows.Controls.Grid.SetRow((System.Windows.UIElement)((System.Windows.Controls.Grid)Content).Children[1], 1);
            System.Windows.Controls.Grid.SetRow(_textBox, 2);
            System.Windows.Controls.Grid.SetRow((System.Windows.UIElement)((System.Windows.Controls.Grid)Content).Children[3], 3);

            Loaded += (_, _) =>
            {
                _textBox.SelectAll();
                _textBox.Focus();
            };
        }

        public string ResultText => _textBox.Text;
    }
}
