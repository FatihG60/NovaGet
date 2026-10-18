using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MyIdm.Desktop.Services;
using MyIdm.Desktop.ViewModels;
using MyIdm.Engine.Models;

namespace MyIdm.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadItemViewModel> _downloads = [];
    private readonly ICollectionView _downloadsView;
    private readonly DispatcherTimer _uiTimer;
    private AppSettings _settings;
    private BrowserBridgeServer? _bridgeServer;
    private SystemTrayService? _trayService;
    private string _activeFilter = "all";
    private bool _isExplicitExit;
    private int _demoCounter = 1;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettingsStore.Load();
        ApplySettingsToUi();

        _downloadsView = CollectionViewSource.GetDefaultView(_downloads);
        _downloadsView.Filter = FilterDownloadItem;
        DownloadsListBox.ItemsSource = _downloadsView;

        _downloads.CollectionChanged += (_, _) =>
        {
            UpdateQueueState();
            DownloadHistoryStore.Save(_downloads);
        };
        UpdateQueueState();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) =>
        {
            double totalSpeed = _downloads.Sum(d => d.CurrentSpeedMBps);
            TotalSpeedBadge.Text = $"⚡ {totalSpeed:F2} MB/sn";
        };
        _uiTimer.Start();

        Loaded += OnMainWindowLoaded;
        StateChanged += OnWindowStateChanged;
        Closing += OnMainWindowClosing;
        Closed += OnMainWindowClosed;
    }

    private void ApplySettingsToUi()
    {
        SettingsDirBox.Text = _settings.DownloadDirectory;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTrayOnClose;
        NotifyOnCompleteCheck.IsChecked = _settings.ShowCompletionNotifications;
        AutoStartBrowserCheck.IsChecked = _settings.AutoStartBrowserDownloads;
        SegmentInspectorPanel.Visibility = _settings.ShowSegmentDetailsPanel
            ? Visibility.Visible
            : Visibility.Collapsed;

        SelectComboByTag(SettingsConnectionsCombo, _settings.DefaultConnections.ToString());
        SelectComboByTag(SettingsQualityCombo, _settings.DefaultQuality);
        SelectComboByTag(QualityCombo, _settings.DefaultQuality);
    }

    private static void SelectComboByTag(ComboBox combo, string tagValue)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private bool FilterDownloadItem(object obj)
    {
        if (obj is not DownloadItemViewModel vm) return false;
        return _activeFilter switch
        {
            "active" => !vm.IsCompleted,
            "completed" => vm.IsCompleted,
            _ => true
        };
    }

    private void UpdateQueueState()
    {
        if (EmptyStatePanel != null)
        {
            EmptyStatePanel.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (QueueCountBadge != null)
        {
            QueueCountBadge.Text = $"{_downloads.Count} Görev";
        }
    }

    private void AttachItemEvents(DownloadItemViewModel vm)
    {
        vm.StateChanged += _ =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _downloadsView.Refresh();
                DownloadHistoryStore.Save(_downloads);
            });
        };

        vm.DownloadCompleted += completedVm =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                _downloadsView.Refresh();
                DownloadHistoryStore.Save(_downloads);

                if (_settings.ShowCompletionNotifications)
                {
                    _trayService?.ShowNotification(
                        "✅ İndirme Tamamlandı — NovaGet",
                        $"{completedVm.FileName}\nBoyut: {completedVm.SizeText} • Hız: {completedVm.SpeedText}");
                }
            });
        };
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 1. Initialize System Tray Icon & Notifications
        try
        {
            _trayService = new SystemTrayService(
                this,
                onRestoreRequested: () => Dispatcher.BeginInvoke(RestoreFromTray),
                onContextMenuRequested: () => Dispatcher.BeginInvoke(ShowTrayContextMenu));
        }
        catch { }

        // 2. Restore Saved Download History
        var savedItems = DownloadHistoryStore.Load();
        foreach (var vm in savedItems)
        {
            AttachItemEvents(vm);
            _downloads.Add(vm);
        }
        if (_downloads.Count > 0)
        {
            DownloadsListBox.SelectedIndex = 0;
        }

        // 3. Start Localhost Browser Bridge Server
        try
        {
            _bridgeServer = new BrowserBridgeServer(
                onCaptureReceived: req =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        RestoreFromTray();

                        EnqueueAndStartDownload(
                            req.Url,
                            req.Filename,
                            req.Connections ?? _settings.DefaultConnections,
                            req.IsHls || IsHlsPlaylistUrl(req.Url),
                            req.Quality ?? GetSelectedQuality(),
                            new DownloadRequestHeaders
                            {
                                Referrer = req.Referrer,
                                Cookies = req.Cookies,
                                UserAgent = req.UserAgent
                            },
                            autoStart: _settings.AutoStartBrowserDownloads);
                    });
                },
                getActiveDownloadsCount: () => _downloads.Count(d => d.IsRunning),
                port: 10010);

            await _bridgeServer.StartAsync();
            BridgeStatusText.Text = "Tarayıcı Bağlı";
        }
        catch
        {
            BridgeStatusText.Text = "Köprü Meşgul";
        }
    }

    private void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ShowTrayContextMenu()
    {
        _trayService?.PrepareForContextMenu();

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("TrayContextMenuStyle"),
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        var itemStyle = (Style)FindResource("TrayMenuItemStyle");
        var dangerStyle = (Style)FindResource("TrayDangerMenuItemStyle");
        var sepStyle = (Style)FindResource("TraySeparatorStyle");

        var showItem = new MenuItem
        {
            Header = "⚡  NovaGet Arayüzünü Aç",
            Style = itemStyle,
            FontWeight = FontWeights.SemiBold
        };
        showItem.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(showItem);

        var folderItem = new MenuItem
        {
            Header = "📂  İndirilenler Klasörünü Aç",
            Style = itemStyle
        };
        folderItem.Click += (_, _) => OpenDownloadsFolder();
        menu.Items.Add(folderItem);

        var settingsItem = new MenuItem
        {
            Header = "⚙️  Ayarlar",
            Style = itemStyle
        };
        settingsItem.Click += (_, _) =>
        {
            RestoreFromTray();
            ApplySettingsToUi();
            SettingsModalOverlay.Visibility = Visibility.Visible;
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator { Style = sepStyle });

        var exitItem = new MenuItem
        {
            Header = "✕  Tamamen Çıkış Yap",
            Style = dangerStyle,
            FontWeight = FontWeights.SemiBold
        };
        exitItem.Click += (_, _) =>
        {
            _isExplicitExit = true;
            Close();
        };
        menu.Items.Add(exitItem);

        menu.Closed += (_, _) => _trayService?.CompleteContextMenu();
        menu.IsOpen = true;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
        }
        else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnMinimizeWindowClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreWindowClick(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            RootWindowBorder.Padding = new Thickness(7);
            MaxRestoreBtn.Content = "❐";
        }
        else
        {
            RootWindowBorder.Padding = new Thickness(0);
            MaxRestoreBtn.Content = "▢";
        }
    }

    private void OnCloseWindowClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
    {
        Hide();
        _trayService?.ShowNotification(
            "NovaGet Arka Planda Çalışıyor",
            "Tarayıcı yakalayıcısı ve aktif indirmeler devam ediyor. Açmak için sistem tepsisindeki ikona tıklayın.");
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        DownloadHistoryStore.Save(_downloads);

        if (!_isExplicitExit && _settings.MinimizeToTrayOnClose && _trayService is not null)
        {
            e.Cancel = true;
            Hide();
            _trayService.ShowNotification(
                "NovaGet Sistem Tepsisine Küçültüldü",
                "İndirmeler arka planda devam ediyor. Tamamen kapatmak için sağ alttaki NovaGet ikonuna sağ tıklayıp 'Çıkış Yap' diyebilirsiniz.");
        }
    }

    private async void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _uiTimer.Stop();
        foreach (var item in _downloads)
        {
            item.Pause();
        }
        DownloadHistoryStore.Save(_downloads);

        _trayService?.Dispose();
        if (_bridgeServer is not null)
        {
            await _bridgeServer.DisposeAsync();
        }
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi();
        SettingsModalOverlay.Visibility = Visibility.Visible;
    }

    private void OnCloseSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        string dir = SettingsDirBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
                _settings.DownloadDirectory = dir;
            }
            catch { }
        }

        if (SettingsConnectionsCombo.SelectedItem is ComboBoxItem connItem &&
            int.TryParse(connItem.Tag?.ToString(), out int conn))
        {
            _settings.DefaultConnections = conn;
        }

        if (SettingsQualityCombo.SelectedItem is ComboBoxItem qualItem &&
            qualItem.Tag is string q && !string.IsNullOrWhiteSpace(q))
        {
            _settings.DefaultQuality = q;
            SelectComboByTag(QualityCombo, q);
        }

        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
        _settings.ShowCompletionNotifications = NotifyOnCompleteCheck.IsChecked == true;
        _settings.AutoStartBrowserDownloads = AutoStartBrowserCheck.IsChecked == true;

        AppSettingsStore.Save(_settings);
        SettingsModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnUrlInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (UrlPlaceholderText != null)
        {
            UrlPlaceholderText.Visibility = string.IsNullOrEmpty(UrlInputBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnFilterTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clickedBtn || clickedBtn.Tag is not string filterTag)
            return;

        _activeFilter = filterTag;

        var modernStyle = (Style)FindResource("ModernButton");
        var ghostStyle = (Style)FindResource("GhostButton");

        FilterAllBtn.Style = filterTag == "all" ? modernStyle : ghostStyle;
        FilterActiveBtn.Style = filterTag == "active" ? modernStyle : ghostStyle;
        FilterCompletedBtn.Style = filterTag == "completed" ? modernStyle : ghostStyle;

        _downloadsView.Refresh();
    }

    private void OnToggleSegmentPanelClick(object sender, RoutedEventArgs e)
    {
        bool isVisible = SegmentInspectorPanel.Visibility == Visibility.Visible;
        SegmentInspectorPanel.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        _settings.ShowSegmentDetailsPanel = !isVisible;
        AppSettingsStore.Save(_settings);
    }

    private string GetSelectedQuality()
    {
        if (QualityCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string q && !string.IsNullOrWhiteSpace(q))
        {
            return q;
        }
        return _settings.DefaultQuality;
    }

    private void EnqueueAndStartDownload(
        string url,
        string? explicitFileName = null,
        int? connections = null,
        bool isHls = false,
        string? quality = null,
        DownloadRequestHeaders? headers = null,
        bool autoStart = true)
    {
        int effectiveConnections = connections ?? _settings.DefaultConnections;
        string effectiveQuality = !string.IsNullOrWhiteSpace(quality) ? quality : GetSelectedQuality();

        if (!string.IsNullOrWhiteSpace(explicitFileName))
        {
            explicitFileName = System.Text.RegularExpressions.Regex.Replace(explicitFileName, @"^\(\d+\)\s*", "").Trim();
        }
        string fileName = !string.IsNullOrWhiteSpace(explicitFileName)
            ? explicitFileName
            : GuessFileName(url, isHls, effectiveQuality);

        if (string.Equals(effectiveQuality, "audio", StringComparison.OrdinalIgnoreCase) &&
            (isHls || MyIdm.Engine.Engine.YtDlpStreamDownloader.ShouldUseYtDlp(url, headers?.Referrer)))
        {
            fileName = Path.ChangeExtension(fileName, ".mp3");
        }
        else if (isHls && (fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
                           fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                           !Path.HasExtension(fileName)))
        {
            fileName = Path.ChangeExtension(fileName, ".mp4");
        }

        // If an incomplete download with the same URL or same target filename already exists,
        // resume it from its cached .hls_cache / .part state instead of creating a duplicate (1) item!
        var existingIncomplete = _downloads.FirstOrDefault(d =>
            !d.IsCompleted &&
            (string.Equals(d.Url, url, StringComparison.OrdinalIgnoreCase) ||
             (d.IsHlsStream == isHls && string.Equals(d.FileName, fileName, StringComparison.OrdinalIgnoreCase))));

        if (existingIncomplete is not null)
        {
            existingIncomplete.Url = url;
            if (headers is not null)
            {
                existingIncomplete.Headers = headers;
            }
            DownloadsListBox.SelectedItem = existingIncomplete;
            if (!existingIncomplete.IsRunning && autoStart)
            {
                _ = existingIncomplete.StartOrResumeAsync();
            }
            return;
        }

        Directory.CreateDirectory(_settings.DownloadDirectory);
        string outputPath = EnsureUniqueOutputPath(_settings.DownloadDirectory, fileName);
        fileName = Path.GetFileName(outputPath);

        var vm = new DownloadItemViewModel
        {
            FileName = fileName,
            Url = url,
            OutputPath = outputPath,
            MaxConnections = effectiveConnections,
            IsHlsStream = isHls,
            Quality = effectiveQuality,
            Headers = headers
        };

        AttachItemEvents(vm);
        _downloads.Insert(0, vm);
        DownloadsListBox.SelectedItem = vm;

        if (autoStart)
        {
            _ = vm.StartOrResumeAsync();
        }
        else
        {
            vm.StatusText = "Duraklatıldı (Başlatmak için 'Devam Et'e tıklayın)";
            DownloadHistoryStore.Save(_downloads);
        }
    }

    private string EnsureUniqueOutputPath(string directory, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string candidate = Path.Combine(directory, fileName);
        int counter = 1;

        while (File.Exists(candidate) ||
               _downloads.Any(d => string.Equals(d.OutputPath, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = Path.Combine(directory, $"{baseName} ({counter++}){ext}");
        }
        return candidate;
    }

    private static string GuessFileName(string url, bool isHls, string quality = "best")
    {
        if (MyIdm.Engine.Engine.YtDlpStreamDownloader.ShouldUseYtDlp(url))
        {
            string ext = string.Equals(quality, "audio", StringComparison.OrdinalIgnoreCase) ? ".mp3" : ".mp4";
            return $"YouTube_Video_{DateTime.Now:HHmmss}{ext}";
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string seg = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(seg) && seg != "/" && !seg.Equals("watch", StringComparison.OrdinalIgnoreCase))
            {
                if (isHls)
                {
                    return Path.GetFileNameWithoutExtension(seg) + ".mp4";
                }
                return seg;
            }
        }
        return isHls ? $"hls_video_{DateTime.Now:HHmmss}.mp4" : $"download_{DateTime.Now:HHmmss}.bin";
    }

    private void OnPasteUrlClick(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
        {
            string text = Clipboard.GetText().Trim();
            if (!string.IsNullOrEmpty(text))
            {
                UrlInputBox.Text = text;
            }
        }
    }

    private static bool IsHlsPlaylistUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string clean = url.Split('?')[0];
        return clean.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               clean.EndsWith("master.txt", StringComparison.OrdinalIgnoreCase) ||
               clean.EndsWith("playlist.txt", StringComparison.OrdinalIgnoreCase) ||
               (clean.Contains("/hls/", StringComparison.OrdinalIgnoreCase) && clean.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
    }

    private void OnStartDownloadClick(object sender, RoutedEventArgs e)
    {
        string url = UrlInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        bool isHls = IsHlsPlaylistUrl(url);
        EnqueueAndStartDownload(url, null, _settings.DefaultConnections, isHls, GetSelectedQuality());
        UrlInputBox.Clear();
    }

    private void OnStart128MbDemoClick(object sender, RoutedEventArgs e)
    {
        SettingsModalOverlay.Visibility = Visibility.Collapsed;
        string demoUrl = "http://127.0.0.1:10010/demo/128mb.bin";
        EnqueueAndStartDownload(
            demoUrl,
            $"demo_128mb_test_{_demoCounter++}.bin",
            _settings.DefaultConnections,
            isHls: false);
    }

    private void OnStartHlsDemoClick(object sender, RoutedEventArgs e)
    {
        SettingsModalOverlay.Visibility = Visibility.Collapsed;
        string hlsUrl = "http://127.0.0.1:10010/demo/stream.m3u8";
        EnqueueAndStartDownload(
            hlsUrl,
            $"hls_video_stream_{_demoCounter++}.ts",
            _settings.DefaultConnections,
            isHls: true);
    }

    private void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.DownloadDirectory);
            Process.Start("explorer.exe", $"\"{_settings.DownloadDirectory}\"");
        }
        catch { }
    }

    private void OnOpenDownloadsFolderClick(object sender, RoutedEventArgs e)
    {
        OpenDownloadsFolder();
    }

    private void OnClearCompletedClick(object sender, RoutedEventArgs e)
    {
        var completed = _downloads.Where(d => d.IsCompleted).ToList();
        foreach (var item in completed)
        {
            _downloads.Remove(item);
        }
        DownloadHistoryStore.Save(_downloads);
    }

    private void OnRemoveItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadItemViewModel vm)
        {
            vm.Pause();
            _downloads.Remove(vm);
            DownloadHistoryStore.Save(_downloads);
        }
    }

    private void OnDownloadSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DownloadsListBox.SelectedItem is DownloadItemViewModel vm)
        {
            InspectorTitleText.Text = $"📊 Bağlantı Detayları — {vm.FileName} ({vm.SegmentsSummary})";
            SegmentsItemsControl.ItemsSource = vm.SegmentBars;
        }
    }

    private async void OnItemActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DownloadItemViewModel vm)
        {
            DownloadsListBox.SelectedItem = vm;
            await vm.TogglePauseResumeAsync();
        }
    }
}
