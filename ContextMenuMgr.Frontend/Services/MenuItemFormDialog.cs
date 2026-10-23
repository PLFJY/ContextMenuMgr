using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Controls.Modern.Scrolling;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

public partial class MenuItemFormData : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private string _iconPath = string.Empty;

    [ObservableProperty]
    private bool _runAsAdmin;

    [ObservableProperty]
    private string _groupName = string.Empty;
}

public partial class ShellNewFormData : ObservableObject
{
    [ObservableProperty]
    private string _extension = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _iconPath = string.Empty;

    [ObservableProperty]
    private string _command = string.Empty;

    [ObservableProperty]
    private bool _beforeSeparator;
}

public class MenuItemFormDialog
{
    private enum DialogType { Target, Folder, Icon }

    private readonly MenuItemFormData _data;
    private readonly string _title;
    private readonly bool _showGroupField;
    private readonly bool _showRunAsAdmin;
    private readonly bool _showWorkingDirectory;
    private readonly bool _showIconField;
    private readonly bool _targetPathReadOnly;
    private readonly bool _argumentsAsCommand;
    private readonly LocalizationService _localization;

    public MenuItemFormDialog(
        string title,
        MenuItemFormData? initialData,
        LocalizationService localization,
        bool showGroupField = false,
        bool showRunAsAdmin = true,
        bool showWorkingDirectory = true,
        bool showIconField = true,
        bool targetPathReadOnly = false,
        bool argumentsAsCommand = false)
    {
        _title = title;
        _showGroupField = showGroupField;
        _showRunAsAdmin = showRunAsAdmin;
        _showWorkingDirectory = showWorkingDirectory;
        _showIconField = showIconField;
        _targetPathReadOnly = targetPathReadOnly;
        _argumentsAsCommand = argumentsAsCommand;
        _localization = localization;
        _data = initialData ?? new MenuItemFormData();
    }

    public static async Task<MenuItemFormData?> ShowAddSendToAsync(string title, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, null, localization, false, true);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<MenuItemFormData?> ShowEditSendToAsync(string title, MenuItemFormData initialData, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, initialData, localization, false, true);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<MenuItemFormData?> ShowAddWinXAsync(string title, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, null, localization, true, true);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<MenuItemFormData?> ShowEditWinXAsync(string title, MenuItemFormData initialData, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, initialData, localization, true, true);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<MenuItemFormData?> ShowAddOpenWithAsync(string title, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, null, localization, false, false, false, false);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<MenuItemFormData?> ShowEditOpenWithAsync(string title, MenuItemFormData initialData, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, initialData, localization, false, false, false, false, true, true);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<MenuItemFormData?> ShowAddSceneMenuItemAsync(string title, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, null, localization, false, false);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<MenuItemFormData?> ShowEditSceneMenuItemAsync(string title, MenuItemFormData initialData, LocalizationService localization)
    {
        var dialog = new MenuItemFormDialog(title, initialData, localization, false, false);
        return await dialog.ShowDialogAsync();
    }

    public static async Task<ShellNewFormData?> ShowAddShellNewAsync(string title, LocalizationService localization)
    {
        var window = new ShellNewFormWindow(title, null, localization)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return Task.FromResult(window.ShowDialog() == true ? window.FormData : null).Result;
    }

    public static async Task<ShellNewFormData?> ShowEditShellNewAsync(string title, ShellNewFormData initialData, LocalizationService localization)
    {
        var window = new ShellNewFormWindow(title, initialData, localization)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return Task.FromResult(window.ShowDialog() == true ? window.FormData : null).Result;
    }

    private Task<MenuItemFormData?> ShowDialogAsync()
    {
        var window = new MenuItemFormWindow(_title, _data, _localization, _showGroupField, _showRunAsAdmin, _showWorkingDirectory, _showIconField, _targetPathReadOnly, _argumentsAsCommand)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        return Task.FromResult(window.ShowDialog() == true ? _data : null);
    }

    private sealed class MenuItemFormWindow : FluentWindow
    {
        private readonly System.Windows.Controls.TextBox _nameTextBox;
        private readonly System.Windows.Controls.TextBox _targetPathTextBox;
        private readonly System.Windows.Controls.TextBox _argumentsTextBox;
        private readonly System.Windows.Controls.TextBox _workingDirectoryTextBox;
        private readonly System.Windows.Controls.TextBox _iconPathTextBox;
        private readonly System.Windows.Controls.CheckBox _runAsAdminCheckBox;
        private readonly System.Windows.Controls.ComboBox _groupComboBox;
        private readonly LocalizationService _localization;
        private MenuItemFormData _formData = null!;

        public MenuItemFormWindow(string title, MenuItemFormData data, LocalizationService localization, bool showGroupField, bool showRunAsAdmin, bool showWorkingDirectory, bool showIconField, bool targetPathReadOnly, bool argumentsAsCommand)
        {
            _localization = localization;
            _formData = data;
            Title = title;
            Width = 580;
            Height = 520;
            MinWidth = 520;
            MinHeight = 460;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            ExtendsContentIntoTitleBar = true;
            WindowBackdropType = WindowBackdropType.Mica;
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"];
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["ApplicationBackgroundBrush"];
            WindowChromeTitleBarFactory.Apply(this);

            var titleBar = WindowChromeTitleBarFactory.CreateCloseOnlyTitleBar(
                this,
                title,
                new SymbolIcon { Symbol = SymbolRegular.WindowNew24 });

            var mainPanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16, 8, 16, 16) };

            _nameTextBox = CreateTextBoxWithLabel(
                mainPanel,
                localization.Translate("MenuFormName"),
                localization.Translate("MenuFormNameDescription"),
                data.Name);

            _targetPathTextBox = CreateTextBoxWithBrowseButton(
                mainPanel,
                localization.Translate("MenuFormTarget"),
                localization.Translate("MenuFormTargetDescription"),
                data.TargetPath,
                localization.Translate("MenuFormBrowse"),
                DialogType.Target);
            _targetPathTextBox.IsReadOnly = targetPathReadOnly;

            _argumentsTextBox = CreateTextBoxWithLabel(
                mainPanel,
                localization.Translate(argumentsAsCommand ? "MenuFormShellNewCommand" : "MenuFormArguments"),
                localization.Translate(argumentsAsCommand ? "MenuFormShellNewCommandDescription" : "MenuFormArgumentsDescription"),
                data.Arguments);

            _workingDirectoryTextBox = CreateTextBoxWithBrowseButton(
                mainPanel,
                localization.Translate("MenuFormWorkingDir"),
                localization.Translate("MenuFormWorkingDirDescription"),
                data.WorkingDirectory,
                localization.Translate("MenuFormBrowse"),
                DialogType.Folder);
            if (!showWorkingDirectory)
            {
                mainPanel.Children.RemoveAt(mainPanel.Children.Count - 1);
            }

            _iconPathTextBox = CreateTextBoxWithBrowseButton(
                mainPanel,
                localization.Translate("MenuFormIcon"),
                localization.Translate("MenuFormIconDescription"),
                data.IconPath,
                localization.Translate("MenuFormBrowse"),
                DialogType.Icon);
            if (!showIconField)
            {
                mainPanel.Children.RemoveAt(mainPanel.Children.Count - 1);
            }

            _runAsAdminCheckBox = new System.Windows.Controls.CheckBox
            {
                Content = localization.Translate("MenuFormRunAsAdmin"),
                IsChecked = data.RunAsAdmin,
                Margin = new System.Windows.Thickness(0, 8, 0, 0)
            };
            if (showRunAsAdmin)
            {
                mainPanel.Children.Add(_runAsAdminCheckBox);
            }

            var groupPanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
            var groupLabel = new System.Windows.Controls.TextBlock
            {
                Text = localization.Translate("MenuFormGroup"),
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            _groupComboBox = new System.Windows.Controls.ComboBox
            {
                IsEditable = true,
                Text = data.GroupName,
                MinWidth = 200,
                ItemsSource = new[] { "Group1", "Group2", "Group3", "Group4", "Group5", "Group6", "Group7", "Group8" }
            };
            groupPanel.Children.Add(groupLabel);
            groupPanel.Children.Add(_groupComboBox);

            if (showGroupField)
            {
                mainPanel.Children.Add(groupPanel);
            }

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new System.Windows.Thickness(0, 20, 0, 0)
            };

            var okButton = new Button
            {
                Content = localization.Translate("DialogConfirm"),
                MinWidth = 100,
                IsDefault = true,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Appearance = ControlAppearance.Primary,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24 }
            };

            var cancelButton = new Button
            {
                Content = localization.Translate("DialogCancel"),
                MinWidth = 100,
                IsCancel = true,
                Appearance = ControlAppearance.Secondary,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 }
            };

            okButton.Click += OnOkClick;
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            mainPanel.Children.Add(buttonPanel);

            var root = new System.Windows.Controls.Grid
            {
                RowDefinitions =
                {
                    new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto },
                    new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) }
                }
            };
            root.Children.Add(titleBar);

            var scrollViewer = new ModernScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = mainPanel
            };
            System.Windows.Controls.Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);
            Content = root;

            Loaded += (_, _) =>
            {
                _nameTextBox.Focus();
                _nameTextBox.SelectAll();
            };
        }

        private System.Windows.Controls.TextBox CreateTextBoxWithLabel(System.Windows.Controls.StackPanel parentPanel, string labelText, string description, string initialValue)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = labelText,
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var descriptionText = new System.Windows.Controls.TextBlock
            {
                Text = description,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var textBox = new TextBox
            {
                Text = initialValue,
                Padding = new System.Windows.Thickness(10, 6, 10, 6),
                Width = 400,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            panel.Children.Add(label);
            panel.Children.Add(descriptionText);
            panel.Children.Add(textBox);
            parentPanel.Children.Add(panel);
            return textBox;
        }

        private System.Windows.Controls.TextBox CreateTextBoxWithBrowseButton(System.Windows.Controls.StackPanel parentPanel, string labelText, string description, string initialValue, string buttonText, DialogType dialogType)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = labelText,
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var descriptionText = new System.Windows.Controls.TextBlock
            {
                Text = description,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var textBox = new TextBox
            {
                Text = initialValue,
                Padding = new System.Windows.Thickness(10, 6, 10, 6),
                MinWidth = 400,
                Width = 400
            };
            var button = new Button
            {
                Content = buttonText,
                MinWidth = 80,
                Margin = new System.Windows.Thickness(8, 0, 0, 0),
                Icon = new SymbolIcon() { Symbol = SymbolRegular.SidebarSearchRtl20 }
            };
            button.Click += (_, _) => OnBrowseClick(textBox, dialogType);

            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(textBox);
            row.Children.Add(button);

            panel.Children.Add(label);
            panel.Children.Add(descriptionText);
            panel.Children.Add(row);
            parentPanel.Children.Add(panel);
            return textBox;
        }

        private void OnBrowseClick(System.Windows.Controls.TextBox textBox, DialogType dialogType)
        {
            switch (dialogType)
            {
                case DialogType.Target:
                    var openDialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "Executable files (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*",
                        Title = _localization.Translate("MenuFormSelectTarget")
                    };
                    if (openDialog.ShowDialog() == true)
                    {
                        textBox.Text = openDialog.FileName;
                        if (string.IsNullOrWhiteSpace(_iconPathTextBox.Text))
                        {
                            _iconPathTextBox.Text = openDialog.FileName;
                        }
                    }
                    break;
                case DialogType.Folder:
                    var folderDialog = new Microsoft.Win32.OpenFolderDialog
                    {
                        Title = _localization.Translate("MenuFormSelectFolder")
                    };
                    if (folderDialog.ShowDialog() == true)
                    {
                        textBox.Text = folderDialog.FolderName;
                    }
                    break;
                case DialogType.Icon:
                    var iconDialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "Icon files (*.ico)|*.ico|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                        Title = _localization.Translate("MenuFormSelectIcon")
                    };
                    if (iconDialog.ShowDialog() == true)
                    {
                        textBox.Text = iconDialog.FileName;
                    }
                    break;
            }
        }

        private void OnOkClick(object sender, System.Windows.RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_nameTextBox.Text.Trim()))
            {
                _ = System.Windows.MessageBox.Show(
                    _localization.Translate("TextCannotBeEmpty"),
                    Title,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            _formData.Name = _nameTextBox.Text.Trim();
            _formData.TargetPath = _targetPathTextBox.Text.Trim();
            _formData.Arguments = _argumentsTextBox.Text.Trim();
            _formData.WorkingDirectory = _workingDirectoryTextBox.Text.Trim();
            _formData.IconPath = _iconPathTextBox.Text.Trim();
            _formData.RunAsAdmin = _runAsAdminCheckBox.IsChecked == true;
            _formData.GroupName = _groupComboBox.Text.Trim();
            DialogResult = true;
        }
    }

    private sealed class ShellNewFormWindow : FluentWindow
    {
        private readonly System.Windows.Controls.TextBox _extensionTextBox;
        private readonly System.Windows.Controls.TextBox _displayNameTextBox;
        private readonly System.Windows.Controls.TextBox _iconPathTextBox;
        private readonly System.Windows.Controls.TextBox _commandTextBox;
        private readonly System.Windows.Controls.CheckBox _beforeSeparatorCheckBox;
        private readonly LocalizationService _localization;
        public ShellNewFormData FormData { get; }

        public ShellNewFormWindow(string title, ShellNewFormData? initialData, LocalizationService localization)
        {
            _localization = localization;
            FormData = initialData ?? new ShellNewFormData();
            Title = title;
            Width = 520;
            Height = 420;
            MinWidth = 480;
            MinHeight = 380;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            ExtendsContentIntoTitleBar = true;
            WindowBackdropType = WindowBackdropType.Mica;
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"];
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["ApplicationBackgroundBrush"];
            WindowChromeTitleBarFactory.Apply(this);

            var titleBar = WindowChromeTitleBarFactory.CreateCloseOnlyTitleBar(
                this,
                title,
                new SymbolIcon { Symbol = SymbolRegular.WindowNew24 });

            var mainPanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16, 8, 16, 16) };

            _extensionTextBox = CreateTextBoxWithLabel(
                mainPanel,
                localization.Translate("MenuFormShellNewExtension"),
                localization.Translate("MenuFormShellNewExtensionDescription"),
                initialData?.Extension ?? string.Empty);

            _displayNameTextBox = CreateTextBoxWithLabel(
                mainPanel,
                localization.Translate("MenuFormShellNewDisplayName"),
                localization.Translate("MenuFormShellNewDisplayNameDescription"),
                initialData?.DisplayName ?? string.Empty);

            _iconPathTextBox = CreateTextBoxWithBrowseButton(
                mainPanel,
                localization.Translate("MenuFormIcon"),
                localization.Translate("MenuFormIconDescription"),
                initialData?.IconPath ?? string.Empty,
                localization.Translate("MenuFormBrowse"),
                DialogType.Icon);

            _commandTextBox = CreateTextBoxWithLabel(
                mainPanel,
                localization.Translate("MenuFormShellNewCommand"),
                localization.Translate("MenuFormShellNewCommandDescription"),
                initialData?.Command ?? string.Empty);

            _beforeSeparatorCheckBox = new System.Windows.Controls.CheckBox
            {
                Content = localization.Translate("MenuFormShellNewBeforeSeparator"),
                IsChecked = initialData?.BeforeSeparator ?? false,
                Margin = new System.Windows.Thickness(0, 8, 0, 0)
            };
            mainPanel.Children.Add(_beforeSeparatorCheckBox);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new System.Windows.Thickness(0, 20, 0, 0)
            };

            var okButton = new Button
            {
                Content = localization.Translate("DialogConfirm"),
                MinWidth = 100,
                IsDefault = true,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Appearance = ControlAppearance.Primary,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Checkmark24 }
            };

            var cancelButton = new Button
            {
                Content = localization.Translate("DialogCancel"),
                MinWidth = 100,
                IsCancel = true,
                Appearance = ControlAppearance.Secondary,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss24 }
            };

            okButton.Click += OnOkClick;
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            mainPanel.Children.Add(buttonPanel);

            var root = new System.Windows.Controls.Grid
            {
                RowDefinitions =
                {
                    new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto },
                    new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) }
                }
            };
            root.Children.Add(titleBar);

            var scrollViewer = new ModernScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = mainPanel
            };
            System.Windows.Controls.Grid.SetRow(scrollViewer, 1);
            root.Children.Add(scrollViewer);
            Content = root;

            Loaded += (_, _) =>
            {
                _extensionTextBox.Focus();
                _extensionTextBox.SelectAll();
            };
        }

        private System.Windows.Controls.TextBox CreateTextBoxWithLabel(System.Windows.Controls.StackPanel parentPanel, string labelText, string description, string initialValue)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = labelText,
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var descriptionText = new System.Windows.Controls.TextBlock
            {
                Text = description,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = initialValue,
                Padding = new System.Windows.Thickness(10, 6, 10, 6),
                MinWidth = 400
            };
            panel.Children.Add(label);
            panel.Children.Add(descriptionText);
            panel.Children.Add(textBox);
            parentPanel.Children.Add(panel);
            return textBox;
        }

        private System.Windows.Controls.TextBox CreateTextBoxWithBrowseButton(System.Windows.Controls.StackPanel parentPanel, string labelText, string description, string initialValue, string buttonText, DialogType dialogType)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 8, 0, 0) };
            var label = new System.Windows.Controls.TextBlock
            {
                Text = labelText,
                FontSize = 12,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var descriptionText = new System.Windows.Controls.TextBlock
            {
                Text = description,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new System.Windows.Thickness(0, 0, 0, 4)
            };
            var textBox = new System.Windows.Controls.TextBox
            {
                Text = initialValue,
                Padding = new System.Windows.Thickness(10, 6, 10, 6),
                MinWidth = 400
            };
            var button = new Button
            {
                Content = buttonText,
                MinWidth = 80,
                Margin = new System.Windows.Thickness(8, 0, 0, 0)
            };
            button.Click += (_, _) => OnBrowseClick(textBox, dialogType);

            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            row.Children.Add(textBox);
            row.Children.Add(button);

            panel.Children.Add(label);
            panel.Children.Add(descriptionText);
            panel.Children.Add(row);
            parentPanel.Children.Add(panel);
            return textBox;
        }

        private void OnBrowseClick(System.Windows.Controls.TextBox textBox, DialogType dialogType)
        {
            if (dialogType == DialogType.Icon)
            {
                var iconDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Icon files (*.ico)|*.ico|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                    Title = _localization.Translate("MenuFormSelectIcon")
                };
                if (iconDialog.ShowDialog() == true)
                {
                    textBox.Text = iconDialog.FileName;
                }
            }
        }

        private void OnOkClick(object sender, System.Windows.RoutedEventArgs e)
        {
            var extension = _extensionTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(extension))
            {
                _ = System.Windows.MessageBox.Show(
                    _localization.Translate("TextCannotBeEmpty"),
                    Title,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            FormData.Extension = extension;
            FormData.DisplayName = _displayNameTextBox.Text.Trim();
            FormData.IconPath = _iconPathTextBox.Text.Trim();
            FormData.Command = _commandTextBox.Text.Trim();
            FormData.BeforeSeparator = _beforeSeparatorCheckBox.IsChecked == true;
            DialogResult = true;
        }
    }
}
