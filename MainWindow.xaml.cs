using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NetClipboard;

public partial class MainWindow : Window
{
    private SolidColorBrush Brush(string key) => (SolidColorBrush)FindResource(key);

    private readonly NetworkService _net = new();
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private readonly Dictionary<string, TabItem> _peerTabs = new();
    private readonly Dictionary<string, (Ellipse dot, TextBlock badge, TextBlock name)> _peerHeaderParts = new();
    private readonly ObservableCollection<ClipboardEntry> _localItems = new();
    private readonly Dictionary<string, ClipboardFileEntry> _incomingFiles = new();
    private readonly Dictionary<ClipboardFileEntry, CancellationTokenSource> _localSendCts = new();
    private UpdateInfo? _availableUpdate;
    private const long LargeFileThresholdBytes = 300L * 1024 * 1024;
    private HwndSource? _hwndSource;
    private TrayIcon? _tray;
    private string _lastClipText = "";
    private string _lastClipFile = "";
    private bool _trayHintShown;
    private string? _masterPassword;
    private DateTime _statusLockedUntil = DateTime.MinValue;
    private const int MaxLocalItems = 200;
    private const int MaxPeerItems = 200;
    private const int StatusHoldSeconds = 4;
    private const int StatusHoldSecondsLong = 10;

    /// <summary>Set a user-facing status that survives the next 4s of background UpdateStatus() ticks.</summary>
    private string StatusMsg
    {
        set => SetStatus(value, StatusHoldSeconds);
    }

    /// <summary>Set a status message and hold it for the given seconds before background ticks may overwrite it.</summary>
    private void SetStatus(string msg, int holdSeconds)
    {
        StatusText.Text = msg;
        _statusLockedUntil = DateTime.Now.AddSeconds(holdSeconds);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    public MainWindow()
    {
        InitializeComponent();

        ThemeManager.ThemeChanged += OnThemeChanged;

        _net.PeerSeen += OnPeerSeen;
        _net.MessageReceived += OnMessageReceived;
        _net.FileStarted += OnFileStarted;
        _net.FileProgress += OnFileProgress;
        _net.FileCompleted += OnFileCompleted;
        _net.FileFailed += OnFileFailed;

        try
        {
            _net.Start();
            HeaderInfo.Text = $"{_net.MachineName}  |  {_net.LocalAddress}:{_net.TcpPort}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot start network: {ex.Message}",
                "NetClipboard", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        // "My Clipboard" tab — always first
        var localList = new ListBox
        {
            ItemsSource = _localItems,
            ItemTemplateSelector = new EntryTemplateSelector
            {
                TextTemplate = (DataTemplate)FindResource("LocalEntryTemplate"),
                FileTemplate = (DataTemplate)FindResource("LocalFileEntryTemplate")
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Style = (Style)FindResource("EntryListBox"),
            Tag = "Copy text or a file to get started."
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(localList, ScrollBarVisibility.Disabled);
        PeerTabs.Items.Add(new TabItem { Header = "My Clipboard", Content = localList });
        PeerTabs.SelectedIndex = 0;

        StatusMsg = "Monitoring clipboard. Discovering peers...";
        Closing += OnClosing;

        // Online/offline check timer
        var onlineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        onlineTimer.Tick += OnlineCheck_Tick;
        onlineTimer.Start();

        // System tray icon — owns its own lifetime, disposed on real exit.
        _tray = new TrayIcon(this);

        // Auto-update: clean up leftover .old from previous upgrade, then
        // fire a non-blocking check against GitHub Releases.
        Updater.CleanupOldVersion();
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            // Defer briefly so the user sees the main window first.
            await Task.Delay(TimeSpan.FromSeconds(5));

            var info = await Updater.CheckAsync();
            if (info == null) return;

            _availableUpdate = info;
            StatusMsg = $"Update v{info.Latest} available. Open About to install.";
            _tray?.ShowBalloon("NetClipboard update", $"Version {info.Latest} is available.");
        }
        catch
        {
            // Offline, GitHub down, parse error — silently ignore.
        }
    }

    // --- Theme toggle ---

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current switch
        {
            AppTheme.System => AppTheme.Dark,
            AppTheme.Dark => AppTheme.Light,
            _ => AppTheme.System
        };
        ThemeManager.Apply(next);
        UpdateThemeButton();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow(_availableUpdate) { Owner = this };
        about.ShowDialog();
    }

    private void UpdateThemeButton()
    {
        // E793 = brightness/auto, E708 = moon/dark, E706 = sun/light
        (ThemeToggleIcon.Text, ThemeToggleLabel.Text, ThemeToggleBtn.ToolTip) = ThemeManager.Current switch
        {
            AppTheme.Dark   => ("\uE708", "Dark", "Theme: Dark"),
            AppTheme.Light  => ("\uE706", "Light", "Theme: Light"),
            _               => ("\uE793", "Auto", "Theme: System")
        };
    }

    private void OnThemeChanged()
    {
        // Refresh code-behind brushes for peer dots, badges, name blocks
        foreach (var (id, parts) in _peerHeaderParts)
        {
            var online = _peers.TryGetValue(id, out var peer) && peer.IsOnline;
            parts.dot.Fill = online ? Brush("ThemeGreen") : Brush("ThemeOverlay0");
            parts.dot.ToolTip = online ? "Online" : "Offline";
            parts.badge.Foreground = Brush("ThemeBadgeText");
            parts.badge.Background = Brush("ThemeMauve");
            parts.name.Foreground = Brush("ThemeText");
        }

        // Refresh master password status color
        if (_masterPassword != null)
            MasterPwdStatus.Foreground = Brush("ThemeGreen");

        UpdateThemeButton();
    }

    // --- Clipboard monitoring via Win32 ---

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
        if (_hwndSource != null)
            AddClipboardFormatListener(_hwndSource.Handle);

        CaptureClipboard();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            CaptureClipboard();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void CaptureClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count == 0) return;
                var path = files[0]; // MVP: single file
                if (string.IsNullOrEmpty(path) || path == _lastClipFile) return;
                var info = new FileInfo(path);
                if (!info.Exists) return;
                if ((info.Attributes & FileAttributes.Directory) != 0) return;
                _lastClipFile = path;
                _lastClipText = "";

                _localItems.Insert(0, new ClipboardFileEntry
                {
                    FileId = Guid.NewGuid().ToString("N")[..12],
                    LocalPath = path,
                    FileName = info.Name,
                    FileSize = info.Length,
                    IsIncoming = false,
                    IsComplete = true
                });
                if (_localItems.Count > MaxLocalItems)
                    _localItems.RemoveAt(_localItems.Count - 1);

                if (files.Count > 1)
                    StatusMsg = $"Captured {info.Name}. {files.Count - 1} other file(s) ignored (MVP: single file).";
                else
                    UpdateStatus();
                return;
            }

            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipText) return;
            _lastClipText = text;
            _lastClipFile = "";

            _localItems.Insert(0, new ClipboardEntry { Text = text });
            if (_localItems.Count > MaxLocalItems)
                _localItems.RemoveAt(_localItems.Count - 1);

            UpdateStatus();
        }
        catch { }
    }

    // --- Master password ---

    private void SetMasterPassword_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new PasswordDialog { Owner = this, Prompt = "Set master password" };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Password))
        {
            _masterPassword = dlg.Password;
            MasterPwdStatus.Text = "Master password: set";
            MasterPwdStatus.Foreground = Brush("ThemeGreen");
            StatusMsg = "Master password set";
        }
    }

    // --- Add peer by IP ---

    private void AddPeer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "Add Peer",
            Width = 320, Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = Brush("ThemeBase")
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "Peer IP address (e.g. 100.64.0.2):", FontSize = 13, Foreground = Brush("ThemeText") });
        var tb = new TextBox
        {
            Margin = new Thickness(0, 6, 0, 8), FontSize = 13,
            Background = Brush("ThemeSurface0"),
            Foreground = Brush("ThemeText"),
            BorderBrush = Brush("ThemeSurface2"),
            CaretBrush = Brush("ThemeText")
        };
        sp.Children.Add(tb);

        var btn = new Button
        {
            Content = "Connect",
            IsDefault = true,
            Padding = new Thickness(16, 4, 16, 4),
            Background = Brush("ThemeSurface0"),
            Foreground = Brush("ThemeText"),
            BorderBrush = Brush("ThemeSurface2")
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, "Connect to peer");
        btn.Click += (_, _) => dlg.DialogResult = true;

        var cancelBtn = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(16, 4, 16, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brush("ThemeSurface0"),
            Foreground = Brush("ThemeText"),
            BorderBrush = Brush("ThemeSurface2")
        };
        System.Windows.Automation.AutomationProperties.SetName(cancelBtn, "Cancel");
        cancelBtn.Click += (_, _) => dlg.DialogResult = false;

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnRow.Children.Add(btn);
        btnRow.Children.Add(cancelBtn);
        sp.Children.Add(btnRow);

        // Direct peers management
        var peers = _net.GetDirectPeers();
        sp.Children.Add(new Separator
        {
            Margin = new Thickness(0, 10, 0, 6),
            Background = Brush("ThemeSurface2")
        });
        var countLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush("ThemeOverlay0"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        UpdatePeerCountLabel(countLabel, peers.Count);
        sp.Children.Add(countLabel);

        var listPanel = new StackPanel();
        foreach (var peerIp in peers)
            listPanel.Children.Add(BuildPeerRow(peerIp, listPanel, countLabel));
        sp.Children.Add(new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 110
        });

        dlg.Content = sp;
        tb.Focus();

        if (dlg.ShowDialog() == true
            && System.Net.IPAddress.TryParse(tb.Text.Trim(), out var ip))
        {
            _net.AddDirectPeer(ip);
            StatusMsg = $"Added direct peer {ip} — discovering...";
        }
    }

    private static void UpdatePeerCountLabel(TextBlock label, int count) =>
        label.Text = count > 0 ? $"Active direct peers ({count}):" : "No direct peers";

    private FrameworkElement BuildPeerRow(string ip, StackPanel list, TextBlock countLabel)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var removeBtn = new Button
        {
            Content = "", // Segoe MDL2 Cancel
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Width = 22, Height = 22,
            Padding = new Thickness(0),
            Background = Brush("ThemeSurface0"),
            Foreground = Brush("ThemeText"),
            BorderBrush = Brush("ThemeSurface2"),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"Stop unicasting to {ip}"
        };
        System.Windows.Automation.AutomationProperties.SetName(removeBtn, $"Stop unicasting to {ip}");
        DockPanel.SetDock(removeBtn, Dock.Right);
        removeBtn.Click += (_, _) =>
        {
            if (System.Net.IPAddress.TryParse(ip, out var addr))
                _net.RemoveDirectPeer(addr);
            list.Children.Remove(row);
            UpdatePeerCountLabel(countLabel, list.Children.Count);
        };
        row.Children.Add(removeBtn);
        row.Children.Add(new TextBlock
        {
            Text = ip,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("ThemeText"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0)
        });
        return row;
    }

    // --- Lock toggle (encrypt before sharing) ---

    private async void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle
            || toggle.DataContext is not ClipboardEntry entry)
            return;

        // Block re-entry: prevents double-click producing an inconsistent
        // (IsEncrypted, CipherText) state while async encrypt is in flight.
        toggle.IsEnabled = false;
        try
        {
            if (entry.IsEncrypted)
            {
                // Toggling ON — encrypt
                string? password;
                if (PerItemCheckBox.IsChecked == true || _masterPassword == null)
                {
                    var prompt = "Enter encryption password";
                    var dlg = new PasswordDialog { Owner = this, Prompt = prompt };
                    if (dlg.ShowDialog() != true)
                    {
                        entry.IsEncrypted = false;
                        return;
                    }
                    password = dlg.Password;
                }
                else
                {
                    password = _masterPassword;
                }

                var plaintext = entry.Text;
                entry.CipherText = await Task.Run(() => CryptoHelper.Encrypt(plaintext, password));
                // We hold the plaintext locally — keep it visible.
                entry.IsDecrypted = true;
            }
            else
            {
                // Toggling OFF — clear encryption, restore visibility
                entry.CipherText = null;
                entry.IsDecrypted = false;
            }
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    // --- Share button ---

    private async void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle
            || toggle.DataContext is not ClipboardEntry entry)
            return;

        if (_peers.IsEmpty)
        {
            entry.IsShared = false;
            StatusMsg = "No peers discovered yet";
            return;
        }

        // File entries: dispatch to dedicated path
        if (entry is ClipboardFileEntry fileEntry)
        {
            entry.IsShared = true;
            toggle.IsEnabled = false;
            try { await SendFileToPeers(fileEntry); }
            finally { toggle.IsEnabled = true; }
            return;
        }

        // Race guard: user can click Share while LockToggle_Click is awaiting
        // CryptoHelper.Encrypt. In that window IsEncrypted=true but CipherText=null.
        if (entry.IsEncrypted && entry.CipherText == null)
        {
            entry.IsShared = false;
            StatusMsg = "Encryption in progress — try Share again in a moment";
            return;
        }

        // Every click resends. Force the visual "shared" state on, regardless of
        // the ToggleButton's internal toggle (suppresses off→on blink).
        entry.IsShared = true;

        var textToSend = entry.IsEncrypted ? entry.CipherText! : entry.Text;
        var encrypted = entry.IsEncrypted;

        StatusMsg = $"Sending to {_peers.Count} peer(s)...";

        var sendTasks = _peers.Values.Select(async peer =>
            (peer.Name, await TrySendText(peer, textToSend, encrypted))
        ).ToList();
        var results = await Task.WhenAll(sendTasks);

        var label = encrypted ? "encrypted text" : "text";
        var anyFailed = results.Any(r => !r.Item2.ok);
        SetStatus(FormatSendOutcome(label, results, ownCancelled: false),
            anyFailed ? StatusHoldSecondsLong : StatusHoldSeconds);
    }

    private async Task<(bool ok, string? reason)> TrySendText(
        PeerInfo peer, string text, bool encrypted)
    {
        try
        {
            await _net.SendTextAsync(peer.Address, peer.Port, text, encrypted);
            return (true, null);
        }
        catch (OperationCanceledException)
        { return (false, "timeout"); }
        catch (System.Net.Sockets.SocketException)
        { return (false, "unreachable"); }
        catch (System.IO.IOException)
        { return (false, "interrupted"); }
        catch (Exception ex)
        { return (false, ex.GetType().Name); }
    }

    // --- Unlock button (decrypt received item) ---

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn
            || btn.DataContext is not ClipboardEntry entry)
            return;

        var cipher = entry.CipherText!;

        // Try master password first
        if (_masterPassword != null)
        {
            var pwd = _masterPassword;
            var auto = await Task.Run(() => CryptoHelper.Decrypt(cipher, pwd));
            if (auto != null)
            {
                entry.Text = auto;
                entry.IsDecrypted = true;
                StatusMsg = "Decrypted with master password";
                return;
            }
        }

        // Fallback to manual prompt
        var dlg = new PasswordDialog { Owner = this, Prompt = "Enter decryption password" };
        if (dlg.ShowDialog() != true) return;

        var manualPwd = dlg.Password;
        var plaintext = await Task.Run(() => CryptoHelper.Decrypt(cipher, manualPwd));
        if (plaintext != null)
        {
            entry.Text = plaintext;
            entry.IsDecrypted = true;
            StatusMsg = "Decrypted successfully";
        }
        else
        {
            SetStatus("Wrong password", StatusHoldSecondsLong);
        }
    }

    // --- Network events ---

    private void OnPeerSeen(PeerAnnounce p)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_peers.TryGetValue(p.Id, out var existing))
            {
                var displayChanged = existing.Name != p.Name || !existing.Address.Equals(p.Address);
                existing.Name = p.Name;
                existing.Address = p.Address;
                existing.Port = p.Port;
                existing.LastSeen = DateTime.Now;
                if (displayChanged && _peerHeaderParts.TryGetValue(p.Id, out var hp))
                    hp.name.Text = existing.DisplayName;
            }
            else
            {
                var peer = new PeerInfo
                {
                    InstanceId = p.Id,
                    Name = p.Name,
                    Address = p.Address,
                    Port = p.Port,
                    LastSeen = DateTime.Now
                };
                _peers[p.Id] = peer;
                AddPeerTab(peer);
                UpdateStatus();
            }
        });
    }

    private void OnMessageReceived(IncomingMessage msg)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (!_peers.ContainsKey(msg.SenderId))
            {
                var newPeer = new PeerInfo
                {
                    InstanceId = msg.SenderId,
                    Name = msg.SenderName,
                    Address = msg.SenderAddress,
                    Port = msg.SenderPort,
                    LastSeen = DateTime.Now
                };
                _peers[msg.SenderId] = newPeer;
                AddPeerTab(newPeer);
                UpdateStatus();
            }

            var peer = _peers[msg.SenderId];
            peer.LastSeen = DateTime.Now;
            if (!peer.IsOnline)
            {
                peer.IsOnline = true;
                if (_peerHeaderParts.TryGetValue(msg.SenderId, out var dotParts))
                {
                    dotParts.dot.Fill = Brush("ThemeGreen");
                    dotParts.dot.ToolTip = "Online";
                }
            }

            var entry = new ClipboardEntry
            {
                Text = msg.Encrypted ? "" : msg.Text,
                Timestamp = DateTime.Now,
                IsEncrypted = msg.Encrypted,
                CipherText = msg.Encrypted ? msg.Text : null
            };

            // Show immediately (encrypted entries blurred); decrypt is async below.
            peer.Items.Insert(0, entry);
            while (peer.Items.Count > MaxPeerItems)
                peer.Items.RemoveAt(peer.Items.Count - 1);

            // Update unread badge if tab not selected
            if (_peerTabs.TryGetValue(msg.SenderId, out var tab)
                && PeerTabs.SelectedItem != tab)
            {
                peer.UnreadCount++;
                if (_peerHeaderParts.TryGetValue(msg.SenderId, out var parts))
                {
                    parts.badge.Text = peer.UnreadCount.ToString();
                    parts.badge.Visibility = Visibility.Visible;
                }
            }

            // Auto-decrypt with master password if available — off UI thread
            if (msg.Encrypted && _masterPassword != null)
            {
                var pwd = _masterPassword;
                var cipher = msg.Text;
                var plaintext = await Task.Run(() => CryptoHelper.Decrypt(cipher, pwd));
                if (plaintext != null)
                {
                    entry.Text = plaintext;
                    entry.IsDecrypted = true;
                }
            }
        });
    }

    // --- UI helpers ---

    private void AddPeerTab(PeerInfo peer)
    {
        var listBox = new ListBox
        {
            ItemsSource = peer.Items,
            ItemTemplateSelector = new EntryTemplateSelector
            {
                TextTemplate = (DataTemplate)FindResource("RemoteEntryTemplate"),
                FileTemplate = (DataTemplate)FindResource("RemoteFileEntryTemplate")
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Style = (Style)FindResource("EntryListBox"),
            Tag = "No items received yet."
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);

        // Custom header: [dot] [name] [badge]
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = Brush("ThemeGreen"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            ToolTip = "Online"
        };
        var nameBlock = new TextBlock
        {
            Text = peer.DisplayName,
            Foreground = Brush("ThemeText"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Brush("ThemeBadgeText"),
            Background = Brush("ThemeMauve"),
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        headerPanel.Children.Add(dot);
        headerPanel.Children.Add(nameBlock);
        headerPanel.Children.Add(badge);

        _peerHeaderParts[peer.InstanceId] = (dot, badge, nameBlock);

        var tab = new TabItem
        {
            Header = headerPanel,
            Content = listBox,
            Tag = peer.InstanceId
        };
        PeerTabs.Items.Add(tab);
        _peerTabs[peer.InstanceId] = tab;
    }

    private void OnlineCheck_Tick(object? sender, EventArgs e)
    {
        foreach (var (id, peer) in _peers)
        {
            var online = (DateTime.Now - peer.LastSeen).TotalSeconds < 8;
            peer.IsOnline = online;
            if (_peerHeaderParts.TryGetValue(id, out var parts))
            {
                parts.dot.Fill = online ? Brush("ThemeGreen") : Brush("ThemeOverlay0");
                parts.dot.ToolTip = online ? "Online" : "Offline";
            }
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        // Don't clobber a recent user-action status.
        if (DateTime.Now < _statusLockedUntil) return;

        var online = _peers.Values.Count(p => p.IsOnline);
        var total = _peers.Count;
        StatusText.Text = total > 0
            ? $"{online}/{total} peer(s) online  |  {_localItems.Count} items"
            : $"Discovering peers...  |  {_localItems.Count} items";
    }

    // --- UI events ---

    private void ClearTab_Click(object sender, RoutedEventArgs e)
    {
        if (PeerTabs.SelectedIndex == 0)
        {
            foreach (var entry in _localItems.OfType<ClipboardFileEntry>().ToList())
                CleanupFileEntry(entry);
            CancelOutgoingFiles(_localItems);
            _localItems.Clear();
            StatusMsg = "Local clipboard cleared";
        }
        else if (PeerTabs.SelectedItem is TabItem tab && tab.Tag is string id
                 && _peers.TryGetValue(id, out var peer))
        {
            foreach (var entry in peer.Items.OfType<ClipboardFileEntry>().ToList())
                CleanupFileEntry(entry);
            peer.Items.Clear();
            peer.UnreadCount = 0;
            if (_peerHeaderParts.TryGetValue(id, out var parts))
                parts.badge.Visibility = Visibility.Collapsed;
            StatusMsg = $"Cleared items from {peer.Name}";
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Clear all local and received clipboard items?\n\n" +
            "Active file transfers will be cancelled and received temporary files will be removed.",
            "Clear all clipboard items",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        CancelOutgoingFiles(_localItems);
        foreach (var peer in _peers.Values)
            CancelOutgoingFiles(peer.Items);

        foreach (var entry in _localItems.OfType<ClipboardFileEntry>().ToList())
            CleanupFileEntry(entry);

        _localItems.Clear();

        foreach (var (id, peer) in _peers)
        {
            foreach (var entry in peer.Items.OfType<ClipboardFileEntry>().ToList())
                CleanupFileEntry(entry);

            peer.Items.Clear();
            peer.UnreadCount = 0;
            if (_peerHeaderParts.TryGetValue(id, out var parts))
                parts.badge.Visibility = Visibility.Collapsed;
        }

        _incomingFiles.Clear();
        StatusMsg = "All clipboard items cleared";
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ClipboardEntry entry)
            return;

        if (PeerTabs.SelectedIndex == 0)
        {
            _localItems.Remove(entry);
        }
        else if (PeerTabs.SelectedItem is TabItem tab && tab.Tag is string id
                 && _peers.TryGetValue(id, out var peer))
        {
            peer.Items.Remove(entry);
        }

        // Clean up received-file temp data
        if (entry is ClipboardFileEntry fe && fe.IsIncoming)
            CleanupFileEntry(fe);
    }

    private void CancelOutgoingFiles(IEnumerable<ClipboardEntry> entries)
    {
        foreach (var file in entries.OfType<ClipboardFileEntry>().Where(f => !f.IsIncoming).ToList())
        {
            if (_localSendCts.TryGetValue(file, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }
    }

    private void CleanupFileEntry(ClipboardFileEntry fe)
    {
        if (fe.IsIncoming)
        {
            _net.CancelIncomingFile(fe.FileId);
            _incomingFiles.Remove(fe.FileId);
            CleanupTempFile(fe);
        }
        else if (_localSendCts.TryGetValue(fe, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
    }

    private static void CleanupTempFile(ClipboardFileEntry fe)
    {
        if (string.IsNullOrEmpty(fe.FileId)) return;
        try
        {
            var dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "NetClipboard", fe.FileId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private void CopyItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string text) return;

        _lastClipText = text;
        // Clipboard may be locked by another process (RDP, OneNote, AVs) — retry briefly.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); StatusMsg = "Copied!"; return; }
            catch (System.Runtime.InteropServices.COMException) when (i < 4)
            { Thread.Sleep(10); }
        }
        StatusMsg = "Clipboard busy — try again";
    }

    private void PeerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PeerTabs.SelectedItem is TabItem tab && tab.Tag is string id)
        {
            if (_peers.TryGetValue(id, out var peer))
            {
                peer.UnreadCount = 0;
                if (_peerHeaderParts.TryGetValue(id, out var parts))
                    parts.badge.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // X / Alt+F4: minimize to tray instead of actually exiting.
        // Real exit comes from tray menu, Updater, or OS session-ending.
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloon("NetClipboard",
                    "Still running in the tray. Right-click the tray icon to exit.");
            }
            return;
        }

        ThemeManager.ThemeChanged -= OnThemeChanged;
        if (_hwndSource != null)
            RemoveClipboardFormatListener(_hwndSource.Handle);
        _net.Dispose();

        // Clean up temp files for incoming file entries
        foreach (var entry in _localItems.OfType<ClipboardFileEntry>().Where(f => f.IsIncoming))
            CleanupTempFile(entry);
        foreach (var peer in _peers.Values)
            foreach (var entry in peer.Items.OfType<ClipboardFileEntry>().Where(f => f.IsIncoming))
                CleanupTempFile(entry);

        _tray?.Dispose();
    }

    // --- File transfer: sender ---

    private async Task SendFileToPeers(ClipboardFileEntry fileEntry)
    {
        if (_peers.IsEmpty)
        {
            fileEntry.IsShared = false;
            StatusMsg = "No peers discovered yet";
            return;
        }
        if (string.IsNullOrEmpty(fileEntry.LocalPath) || !File.Exists(fileEntry.LocalPath))
        {
            fileEntry.IsShared = false;
            StatusMsg = "Source file not found";
            return;
        }

        // Confirm large outgoing files.
        if (fileEntry.FileSize > LargeFileThresholdBytes)
        {
            var sizeText = ClipboardFileEntry.FormatBytes(fileEntry.FileSize);
            var answer = MessageBox.Show(this,
                $"Send '{fileEntry.FileName}' ({sizeText}) to {_peers.Count} peer(s)?\n\n" +
                "This may take a while and use significant bandwidth.",
                "Large file outgoing",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                fileEntry.IsShared = false;
                StatusMsg = $"Not sending {fileEntry.FileName}";
                return;
            }
        }

        StatusMsg = $"Sending {fileEntry.FileName} to {_peers.Count} peer(s)...";

        var path = fileEntry.LocalPath!;
        var transferId = Guid.NewGuid().ToString("N")[..12];
        var cts = new CancellationTokenSource();
        _localSendCts[fileEntry] = cts;
        fileEntry.IsSending = true;

        try
        {
            var token = cts.Token;
            var sendTasks = _peers.Values.Select(async peer =>
                (peer.Name, await TrySendFile(peer, path, transferId, token, cts))
            ).ToList();
            var results = await Task.WhenAll(sendTasks);
            var anyFailed = !cts.IsCancellationRequested && results.Any(r => !r.Item2.ok);
            SetStatus(FormatSendOutcome(fileEntry.FileName, results, cts.IsCancellationRequested),
                anyFailed ? StatusHoldSecondsLong : StatusHoldSeconds);
        }
        finally
        {
            _localSendCts.Remove(fileEntry);
            fileEntry.IsSending = false;
            cts.Dispose();
        }
    }

    private async Task<(bool ok, string? reason)> TrySendFile(
        PeerInfo peer, string path, string transferId,
        CancellationToken token, CancellationTokenSource ownCts)
    {
        try
        {
            await _net.SendFileAsync(peer.Address, peer.Port, path, transferId, ct: token);
            return (true, null);
        }
        catch (OperationCanceledException) when (ownCts.IsCancellationRequested)
        { return (false, "cancelled"); }
        catch (OperationCanceledException)
        { return (false, "timeout"); }
        catch (System.Net.Sockets.SocketException)
        { return (false, "unreachable"); }
        catch (System.IO.IOException)
        { return (false, "interrupted"); }
        catch (Exception ex)
        { return (false, ex.GetType().Name); }
    }

    /// <summary>
    /// Per-peer outcome summary for the status bar. Distinguishes
    /// connect-fail, mid-stream interruption (likely receiver cancel),
    /// timeout, and own cancellation.
    /// </summary>
    private static string FormatSendOutcome(
        string what,
        (string Name, (bool ok, string? reason) result)[] results,
        bool ownCancelled)
    {
        if (ownCancelled) return $"Cancelled sending {what}";

        int ok = results.Count(r => r.result.ok);
        int total = results.Length;
        if (total == 0) return $"No peers for {what}";
        if (ok == total) return $"Sent {what} to {ok} peer(s)";

        var failed = results.Where(r => !r.result.ok).ToList();
        var shown = failed.Take(3).Select(r => $"{r.Name} ({r.result.reason})");
        var more = failed.Count > 3 ? $" +{failed.Count - 3} more" : "";
        var failList = string.Join(", ", shown) + more;

        return ok == 0
            ? $"Failed to send {what}: {failList}"
            : $"Sent {what} to {ok}/{total} — failed: {failList}";
    }

    private void CancelFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ClipboardFileEntry fe) return;

        if (fe.IsIncoming)
        {
            _net.CancelIncomingFile(fe.FileId);
            StatusMsg = $"Cancelling {fe.FileName}...";
        }
        else if (_localSendCts.TryGetValue(fe, out var cts))
        {
            try { cts.Cancel(); } catch { }
            StatusMsg = $"Cancelling {fe.FileName}...";
        }
    }

    // --- File transfer: receiver events ---

    private void OnFileStarted(IncomingFileStart f)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_peers.ContainsKey(f.SenderId))
            {
                var newPeer = new PeerInfo
                {
                    InstanceId = f.SenderId,
                    Name = f.SenderName,
                    Address = f.SenderAddress,
                    Port = f.SenderPort,
                    LastSeen = DateTime.Now
                };
                _peers[f.SenderId] = newPeer;
                AddPeerTab(newPeer);
                UpdateStatus();
            }
            var peer = _peers[f.SenderId];
            peer.LastSeen = DateTime.Now;
            if (!peer.IsOnline)
            {
                peer.IsOnline = true;
                if (_peerHeaderParts.TryGetValue(f.SenderId, out var dotParts))
                {
                    dotParts.dot.Fill = Brush("ThemeGreen");
                    dotParts.dot.ToolTip = "Online";
                }
            }

            var entry = new ClipboardFileEntry
            {
                FileId = f.FileId,
                FileName = f.FileName,
                FileSize = f.FileSize,
                IsIncoming = true
            };
            _incomingFiles[f.FileId] = entry;
            peer.Items.Insert(0, entry);
            while (peer.Items.Count > MaxPeerItems)
                peer.Items.RemoveAt(peer.Items.Count - 1);

            // Unread badge
            if (_peerTabs.TryGetValue(f.SenderId, out var tab)
                && PeerTabs.SelectedItem != tab)
            {
                peer.UnreadCount++;
                if (_peerHeaderParts.TryGetValue(f.SenderId, out var parts))
                {
                    parts.badge.Text = peer.UnreadCount.ToString();
                    parts.badge.Visibility = Visibility.Visible;
                }
            }

            // Large-file confirmation prompt (file is already being received in the
            // background — the dialog is just an opt-out, not a blocking accept).
            if (f.FileSize > LargeFileThresholdBytes)
            {
                var sizeText = ClipboardFileEntry.FormatBytes(f.FileSize);
                var answer = MessageBox.Show(this,
                    $"{f.SenderName} is sending '{f.FileName}' ({sizeText}).\n\n" +
                    "It is already being received in the background.\n" +
                    "Keep receiving this large file?",
                    "Large file incoming",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    _net.CancelIncomingFile(f.FileId);
            }
        });
    }

    private void OnFileProgress(IncomingFileProgress p)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_incomingFiles.TryGetValue(p.FileId, out var entry))
                entry.BytesTransferred = p.BytesReceived;
        });
    }

    private void OnFileCompleted(IncomingFileCompleted c)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_incomingFiles.TryGetValue(c.FileId, out var entry))
            {
                entry.LocalPath = c.LocalPath;
                entry.BytesTransferred = entry.FileSize;
                entry.IsComplete = true;
            }
        });
    }

    private void OnFileFailed(IncomingFileFailed f)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_incomingFiles.TryGetValue(f.FileId, out var entry)) return;
            entry.Reason = f.Reason;
            entry.IsFailed = true;
            if (f.Reason == "Cancelled")
                StatusMsg = $"Cancelled {entry.FileName}";
            else
                SetStatus($"Failed to receive {entry.FileName}: {f.Reason}", StatusHoldSecondsLong);
        });
    }

    // --- File actions (receiver-side buttons) ---

    private void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ClipboardFileEntry fe) return;
        if (string.IsNullOrEmpty(fe.LocalPath) || !File.Exists(fe.LocalPath))
        {
            StatusMsg = "File no longer available";
            return;
        }

        var paths = new System.Collections.Specialized.StringCollection { fe.LocalPath };
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetFileDropList(paths); StatusMsg = $"Copied {fe.FileName} to clipboard"; return; }
            catch (System.Runtime.InteropServices.COMException) when (i < 4)
            { Thread.Sleep(10); }
        }
        StatusMsg = "Clipboard busy — try again";
    }

    private async void SaveFileAs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ClipboardFileEntry fe) return;
        if (string.IsNullOrEmpty(fe.LocalPath) || !File.Exists(fe.LocalPath))
        {
            StatusMsg = "File no longer available";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = fe.FileName,
            Filter = "All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var source = fe.LocalPath;
        var target = dlg.FileName;
        StatusMsg = $"Saving {fe.FileName}...";
        try
        {
            await Task.Run(() => File.Copy(source, target, overwrite: true));
            StatusMsg = $"Saved to {target}";
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}", StatusHoldSecondsLong);
        }
    }
}
