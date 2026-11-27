using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.Helpers.MfgUnlock;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static DLSS_Swapper.Helpers.MfgUnlock.MfgUnlockManager;

namespace DLSS_Swapper.UserControls;

/// <summary>
/// Per-game panel for installing the RTX 30/20 MFG unlock and choosing its multiplier.
/// Built in code so it does not need its own XAML page.
/// </summary>
public sealed class MfgUnlockControl : UserControl
{
    readonly Game _game;

    readonly TextBlock _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
    readonly TextBox _exeTextBox = new TextBox { IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _proxyComboBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly Button _installButton = new Button();
    readonly Button _uninstallButton = new Button();
    readonly Button _openFolderButton = new Button();
    readonly StackPanel _modePanel = new StackPanel { Spacing = 8 };
    readonly ComboBox _modeComboBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly NumberBox _targetFpsBox = new NumberBox { Minimum = 0, Maximum = 1000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    readonly Button _saveModeButton = new Button();
    readonly TextBlock _safeMaximumText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    readonly ProgressRing _progressRing = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
    readonly TextBlock _logText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };

    string? _installDirectory;
    bool _busy;

    record ModeOption(string Name, ControlMode Mode, int Multiplier)
    {
        public override string ToString() => Name;
    }

    readonly List<ModeOption> _modeOptions;

    public MfgUnlockControl(Game game)
    {
        _game = game;

        _modeOptions = new List<ModeOption>
        {
            new ModeOption(ResourceHelper.GetString("MfgUnlock_ModeFollowGame"), ControlMode.FollowGame, 2),
        };
        for (var i = 2; i <= 6; i++)
        {
            _modeOptions.Add(new ModeOption(ResourceHelper.GetFormattedResourceTemplate("MfgUnlock_ModeFixedTemplate", i), ControlMode.Fixed, i));
        }
        _modeOptions.Add(new ModeOption(ResourceHelper.GetString("MfgUnlock_ModeDynamic"), ControlMode.Dynamic, 2));

        Content = BuildLayout();
        LoadState();
    }

    static TextBlock Header(string text) => new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) };

    UIElement BuildLayout()
    {
        var root = new StackPanel { Spacing = 6, MinWidth = 560 };

        root.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false,
            Message = ResourceHelper.GetString("MfgUnlock_Warning"),
        });

        if (GameSupportsFrameGeneration(_game) == false)
        {
            root.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
                Message = ResourceHelper.GetString("MfgUnlock_NoFrameGeneration"),
            });
        }

        root.Children.Add(Header(ResourceHelper.GetString("MfgUnlock_Status")));
        root.Children.Add(_statusText);

        root.Children.Add(Header(ResourceHelper.GetString("MfgUnlock_GameExecutable")));
        var exeGrid = new Grid { ColumnSpacing = 8 };
        exeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        exeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var browseButton = new Button { Content = ResourceHelper.GetString("MfgUnlock_Browse") };
        browseButton.Click += (_, _) => BrowseForExecutable();
        Grid.SetColumn(browseButton, 1);
        exeGrid.Children.Add(_exeTextBox);
        exeGrid.Children.Add(browseButton);
        root.Children.Add(exeGrid);
        root.Children.Add(new TextBlock { Text = ResourceHelper.GetString("MfgUnlock_GameExecutableHelp"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 });

        root.Children.Add(Header(ResourceHelper.GetString("MfgUnlock_LoaderName")));
        _proxyComboBox.Items.Add(ResourceHelper.GetString("MfgUnlock_LoaderAutomatic"));
        foreach (var proxy in ProxyCandidates)
        {
            _proxyComboBox.Items.Add(proxy);
        }
        _proxyComboBox.SelectedIndex = 0;
        root.Children.Add(_proxyComboBox);

        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _installButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _installButton.Click += async (_, _) => await InstallAsync();
        _uninstallButton.Content = ResourceHelper.GetString("MfgUnlock_Uninstall");
        _uninstallButton.Click += (_, _) => Uninstall();
        _openFolderButton.Content = ResourceHelper.GetString("MfgUnlock_OpenFolder");
        _openFolderButton.Click += (_, _) => OpenFolder();
        actionPanel.Children.Add(_installButton);
        actionPanel.Children.Add(_uninstallButton);
        actionPanel.Children.Add(_openFolderButton);
        actionPanel.Children.Add(_progressRing);
        root.Children.Add(actionPanel);

        _modePanel.Children.Add(Header(ResourceHelper.GetString("MfgUnlock_Mode")));
        foreach (var option in _modeOptions)
        {
            _modeComboBox.Items.Add(option);
        }
        _modeComboBox.SelectionChanged += (_, _) => UpdateTargetFpsVisibility();
        _modePanel.Children.Add(_modeComboBox);
        _targetFpsBox.Header = ResourceHelper.GetString("MfgUnlock_DynamicTarget");
        _modePanel.Children.Add(_targetFpsBox);
        _saveModeButton.Content = ResourceHelper.GetString("MfgUnlock_SaveMode");
        _saveModeButton.Click += (_, _) => SaveMode();
        _modePanel.Children.Add(_saveModeButton);
        _modePanel.Children.Add(_safeMaximumText);
        root.Children.Add(_modePanel);

        root.Children.Add(new ScrollViewer { Content = _logText, MaxHeight = 160, Margin = new Thickness(0, 8, 0, 0) });

        return root;
    }

    void LoadState()
    {
        var install = MfgUnlockManager.Instance.FindInstall(_game);
        if (install is not null)
        {
            _installDirectory = install.Value.Directory;
            var exe = install.Value.Manifest.ExecutablePath;
            _exeTextBox.Text = string.IsNullOrEmpty(exe) ? _installDirectory : exe;
            _statusText.Text = ResourceHelper.GetFormattedResourceTemplate("MfgUnlock_StatusInstalledTemplate", install.Value.Manifest.ModTag, install.Value.Manifest.LoaderProxy);
            _installButton.Content = ResourceHelper.GetString("MfgUnlock_Update");
            _uninstallButton.IsEnabled = true;
            _modePanel.Visibility = Visibility.Visible;
            LoadMode();
            return;
        }

        _installDirectory = null;
        if (string.IsNullOrEmpty(_exeTextBox.Text))
        {
            _exeTextBox.Text = MfgUnlockManager.Instance.FindGameExecutable(_game) ?? string.Empty;
        }

        var exeDirectory = Path.GetDirectoryName(_exeTextBox.Text);
        if (string.IsNullOrEmpty(exeDirectory) == false && HasManualInstall(exeDirectory))
        {
            _statusText.Text = ResourceHelper.GetString("MfgUnlock_StatusManual");
        }
        else
        {
            _statusText.Text = ResourceHelper.GetString("MfgUnlock_StatusNotInstalled");
        }
        _installButton.Content = ResourceHelper.GetString("MfgUnlock_Install");
        _uninstallButton.IsEnabled = false;
        _modePanel.Visibility = Visibility.Collapsed;
    }

    void LoadMode()
    {
        if (_installDirectory is null)
        {
            return;
        }

        var settings = MfgUnlockManager.Instance.ReadControl(_installDirectory);
        _modeComboBox.SelectedItem = _modeOptions.FirstOrDefault(o => o.Mode == settings.Mode && (o.Mode != ControlMode.Fixed || o.Multiplier == settings.Multiplier))
            ?? _modeOptions[0];
        _targetFpsBox.Value = settings.DynamicTargetFrameRate;
        UpdateTargetFpsVisibility();

        var safeMaximum = MfgUnlockManager.Instance.ReadSafeMaximumMultiplier(_installDirectory);
        _safeMaximumText.Text = safeMaximum is null
            ? ResourceHelper.GetString("MfgUnlock_SafeMaximumUnknown")
            : ResourceHelper.GetFormattedResourceTemplate("MfgUnlock_SafeMaximumTemplate", safeMaximum.Value);
    }

    void UpdateTargetFpsVisibility()
    {
        _targetFpsBox.Visibility = (_modeComboBox.SelectedItem as ModeOption)?.Mode == ControlMode.Dynamic ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _progressRing.IsActive = busy;
        _installButton.IsEnabled = busy == false;
        _uninstallButton.IsEnabled = busy == false && _installDirectory is not null;
        _saveModeButton.IsEnabled = busy == false;
    }

    void ShowLog(IEnumerable<string> lines)
    {
        _logText.Text = string.Join(Environment.NewLine, lines);
    }

    void ShowError(Exception err)
    {
        Logger.Error(err);
        var message = err is UnauthorizedAccessException
            ? ResourceHelper.GetString("MfgUnlock_AccessDenied")
            : err.Message;
        _logText.Text = $"{ResourceHelper.GetString("General_Error")}: {message}";
    }

    void BrowseForExecutable()
    {
        try
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentApp.MainWindow);
            var filters = new List<FileSystemHelper.FileFilter>
            {
                new FileSystemHelper.FileFilter("Executable", "*.exe"),
            };
            var startPath = Path.GetDirectoryName(_exeTextBox.Text);
            if (string.IsNullOrEmpty(startPath) || Directory.Exists(startPath) == false)
            {
                startPath = _game.InstallPath;
            }
            var selected = FileSystemHelper.OpenFile(hWnd, filters, startPath);
            if (string.IsNullOrWhiteSpace(selected) == false)
            {
                _exeTextBox.Text = selected;
                if (_installDirectory is null)
                {
                    LoadState();
                }
            }
        }
        catch (Exception err)
        {
            ShowError(err);
        }
    }

    async Task InstallAsync()
    {
        if (_busy)
        {
            return;
        }

        var exePath = _exeTextBox.Text;
        if (string.IsNullOrWhiteSpace(exePath) || File.Exists(exePath) == false)
        {
            _logText.Text = ResourceHelper.GetString("MfgUnlock_PickExecutable");
            return;
        }

        // Moving the install to a different executable folder: remove the old one first.
        if (_installDirectory is not null && string.Equals(Path.GetDirectoryName(exePath), _installDirectory, StringComparison.OrdinalIgnoreCase) == false)
        {
            try
            {
                MfgUnlockManager.Instance.Uninstall(_installDirectory);
                _installDirectory = null;
            }
            catch (Exception err)
            {
                ShowError(err);
                return;
            }
        }

        var requestedProxy = _proxyComboBox.SelectedIndex > 0 ? _proxyComboBox.SelectedItem as string : null;

        SetBusy(true);
        _logText.Text = ResourceHelper.GetString("MfgUnlock_Installing");
        try
        {
            var log = await Task.Run(() => MfgUnlockManager.Instance.InstallAsync(exePath, requestedProxy));
            log.Add(ResourceHelper.GetString("MfgUnlock_InstallDone"));
            ShowLog(log);
            Logger.Info($"MFG unlock installed for {_game.Title}: {string.Join(" | ", log)}");
        }
        catch (Exception err)
        {
            ShowError(err);
        }
        finally
        {
            SetBusy(false);
            var logText = _logText.Text;
            LoadState();
            _logText.Text = logText;
        }
    }

    void Uninstall()
    {
        if (_busy || _installDirectory is null)
        {
            return;
        }

        try
        {
            var log = MfgUnlockManager.Instance.Uninstall(_installDirectory);
            log.Add(ResourceHelper.GetString("MfgUnlock_UninstallDone"));
            ShowLog(log);
            Logger.Info($"MFG unlock removed for {_game.Title}: {string.Join(" | ", log)}");
            var logText = _logText.Text;
            LoadState();
            _logText.Text = logText;
        }
        catch (Exception err)
        {
            ShowError(err);
        }
    }

    void OpenFolder()
    {
        var directory = _installDirectory ?? Path.GetDirectoryName(_exeTextBox.Text);
        if (string.IsNullOrEmpty(directory) || Directory.Exists(directory) == false)
        {
            directory = _game.InstallPath;
        }
        if (Directory.Exists(directory))
        {
            Process.Start("explorer.exe", directory);
        }
    }

    void SaveMode()
    {
        if (_installDirectory is null || _modeComboBox.SelectedItem is not ModeOption option)
        {
            return;
        }

        try
        {
            var target = double.IsNaN(_targetFpsBox.Value) ? 0 : (int)_targetFpsBox.Value;
            MfgUnlockManager.Instance.WriteControl(_installDirectory, new ControlSettings(option.Mode, option.Multiplier, target));
            _logText.Text = ResourceHelper.GetFormattedResourceTemplate("MfgUnlock_ModeSavedTemplate", option.Name);
        }
        catch (Exception err)
        {
            ShowError(err);
        }
    }

    /// <summary>
    /// Short status for the game panel.
    /// </summary>
    public static string GetSummary(Game game)
    {
        var install = MfgUnlockManager.Instance.FindInstall(game);
        if (install is not null)
        {
            return ResourceHelper.GetFormattedResourceTemplate("MfgUnlock_StatusInstalledTemplate", install.Value.Manifest.ModTag, install.Value.Manifest.LoaderProxy);
        }
        return GameSupportsFrameGeneration(game)
            ? ResourceHelper.GetString("MfgUnlock_StatusNotInstalled")
            : ResourceHelper.GetString("MfgUnlock_StatusNoFrameGeneration");
    }
}
