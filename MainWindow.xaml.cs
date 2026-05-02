using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
    private HwndSource? _hwndSource;
    private string _lastClipText = "";
    private string? _masterPassword;
    private const int MaxLocalItems = 200;

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
            ItemTemplate = (DataTemplate)FindResource("LocalEntryTemplate"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(localList, ScrollBarVisibility.Disabled);
        PeerTabs.Items.Add(new TabItem { Header = "My Clipboard", Content = localList });
        PeerTabs.SelectedIndex = 0;

        StatusText.Text = "Monitoring clipboard. Discovering peers...";
        Closing += OnClosing;

        // Online/offline check timer
        var onlineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        onlineTimer.Tick += OnlineCheck_Tick;
        onlineTimer.Start();
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

    private void UpdateThemeButton()
    {
        // E793 = brightness/auto, E706 = moon/dark, E706 reuse, E2AD = sun/light
        (ThemeToggleIcon.Text, ThemeToggleBtn.ToolTip) = ThemeManager.Current switch
        {
            AppTheme.Dark   => ("\uE708", "Theme: Dark"),
            AppTheme.Light  => ("\uE706", "Theme: Light"),
            _               => ("\uE793", "Theme: System")
        };
    }

    private void OnThemeChanged()
    {
        // Refresh code-behind brushes for peer dots, badges, name blocks
        foreach (var (id, parts) in _peerHeaderParts)
        {
            var online = _peers.TryGetValue(id, out var peer) && peer.IsOnline;
            parts.dot.Fill = online ? Brush("ThemeGreen") : Brush("ThemeSurface2");
            parts.badge.Foreground = Brush("ThemeBadgeText");
            parts.badge.Background = Brush("ThemeRed");
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
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipText) return;
            _lastClipText = text;

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
            StatusText.Text = "Master password set";
        }
    }

    // --- Add peer by IP ---

    private void AddPeer_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "Add Peer",
            Width = 320, Height = 140,
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
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 4, 16, 4),
            Background = Brush("ThemeSurface0"),
            Foreground = Brush("ThemeText"),
            BorderBrush = Brush("ThemeSurface2")
        };
        btn.Click += (_, _) => dlg.DialogResult = true;
        sp.Children.Add(btn);
        dlg.Content = sp;
        tb.Focus();

        if (dlg.ShowDialog() == true
            && System.Net.IPAddress.TryParse(tb.Text.Trim(), out var ip))
        {
            _net.AddDirectPeer(ip);
            StatusText.Text = $"Added direct peer {ip} — discovering...";
        }
    }

    // --- Lock toggle (encrypt before sharing) ---

    private void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle
            || toggle.DataContext is not ClipboardEntry entry)
            return;

        if (entry.IsEncrypted)
        {
            // Toggling ON — encrypt
            if (PerItemCheckBox.IsChecked == true)
            {
                // Per-item mode: always prompt
                var dlg = new PasswordDialog { Owner = this, Prompt = "Enter encryption password" };
                if (dlg.ShowDialog() == true)
                {
                    entry.CipherText = CryptoHelper.Encrypt(entry.Text, dlg.Password);
                }
                else
                {
                    entry.IsEncrypted = false;
                    return;
                }
            }
            else if (_masterPassword != null)
            {
                // Use master password silently
                entry.CipherText = CryptoHelper.Encrypt(entry.Text, _masterPassword);
            }
            else
            {
                // No master password — fallback to prompt
                var dlg = new PasswordDialog { Owner = this, Prompt = "Enter encryption password" };
                if (dlg.ShowDialog() == true)
                {
                    entry.CipherText = CryptoHelper.Encrypt(entry.Text, dlg.Password);
                }
                else
                {
                    entry.IsEncrypted = false;
                    return;
                }
            }
        }
        else
        {
            // Toggling OFF — clear encryption, restore visibility
            entry.CipherText = null;
            entry.IsDecrypted = false;
        }
    }

    // --- Share button ---

    private async void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle
            || toggle.DataContext is not ClipboardEntry entry)
            return;

        // Only act when toggled ON
        if (!entry.IsShared) return;

        if (_peers.IsEmpty)
        {
            entry.IsShared = false;
            StatusText.Text = "No peers discovered yet";
            return;
        }

        var textToSend = entry.IsEncrypted ? entry.CipherText! : entry.Text;
        var encrypted = entry.IsEncrypted;

        int ok = 0;
        foreach (var peer in _peers.Values.ToList())
        {
            try
            {
                await _net.SendTextAsync(peer.Address, peer.Port, textToSend, encrypted);
                ok++;
            }
            catch { }
        }

        StatusText.Text = ok > 0
            ? $"Synced to {ok} peer(s)" + (encrypted ? " (encrypted)" : "")
            : "Sync failed — peers unreachable";
    }

    // --- Unlock button (decrypt received item) ---

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn
            || btn.DataContext is not ClipboardEntry entry)
            return;

        // Try master password first
        if (_masterPassword != null)
        {
            var auto = CryptoHelper.Decrypt(entry.CipherText!, _masterPassword);
            if (auto != null)
            {
                entry.Text = auto;
                entry.IsDecrypted = true;
                StatusText.Text = "Decrypted with master password";
                return;
            }
        }

        // Fallback to manual prompt
        var dlg = new PasswordDialog { Owner = this, Prompt = "Enter decryption password" };
        if (dlg.ShowDialog() != true) return;

        var plaintext = CryptoHelper.Decrypt(entry.CipherText!, dlg.Password);
        if (plaintext != null)
        {
            entry.Text = plaintext;
            entry.IsDecrypted = true;
            StatusText.Text = "Decrypted successfully";
        }
        else
        {
            StatusText.Text = "Wrong password";
        }
    }

    // --- Network events ---

    private void OnPeerSeen(PeerAnnounce p)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_peers.TryGetValue(p.Id, out var existing))
            {
                existing.Address = p.Address;
                existing.Port = p.Port;
                existing.LastSeen = DateTime.Now;
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
        Dispatcher.BeginInvoke(() =>
        {
            if (!_peers.ContainsKey(msg.SenderId))
            {
                var peer = new PeerInfo
                {
                    InstanceId = msg.SenderId,
                    Name = msg.SenderName,
                    Address = msg.SenderAddress,
                    Port = msg.SenderPort,
                    LastSeen = DateTime.Now
                };
                _peers[msg.SenderId] = peer;
                AddPeerTab(peer);
                UpdateStatus();
            }

            var entry = new ClipboardEntry
            {
                Text = msg.Encrypted ? "" : msg.Text,
                Timestamp = DateTime.Now,
                IsEncrypted = msg.Encrypted,
                CipherText = msg.Encrypted ? msg.Text : null
            };

            // Auto-decrypt with master password if available
            if (msg.Encrypted && _masterPassword != null)
            {
                var plaintext = CryptoHelper.Decrypt(msg.Text, _masterPassword);
                if (plaintext != null)
                {
                    entry.Text = plaintext;
                    entry.IsDecrypted = true;
                }
            }

            _peers[msg.SenderId].Items.Insert(0, entry);

            // Update unread badge if tab not selected
            if (_peerTabs.TryGetValue(msg.SenderId, out var tab)
                && PeerTabs.SelectedItem != tab)
            {
                var peer = _peers[msg.SenderId];
                peer.UnreadCount++;
                if (_peerHeaderParts.TryGetValue(msg.SenderId, out var parts))
                {
                    parts.badge.Text = peer.UnreadCount.ToString();
                    parts.badge.Visibility = Visibility.Visible;
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
            ItemTemplate = (DataTemplate)FindResource("RemoteEntryTemplate"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);

        // Custom header: [dot] [name] [badge]
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = Brush("ThemeGreen"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
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
            Background = Brush("ThemeRed"),
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
                parts.dot.Fill = online ? Brush("ThemeGreen") : Brush("ThemeSurface2");
            }
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
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
            _localItems.Clear();
            StatusText.Text = "Local clipboard cleared";
        }
        else if (PeerTabs.SelectedItem is TabItem tab && tab.Tag is string id
                 && _peers.TryGetValue(id, out var peer))
        {
            peer.Items.Clear();
            peer.UnreadCount = 0;
            if (_peerHeaderParts.TryGetValue(id, out var parts))
                parts.badge.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Cleared items from {peer.Name}";
        }
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
    }

    private void CopyItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string text)
        {
            _lastClipText = text;
            Clipboard.SetText(text);
            StatusText.Text = "Copied!";
        }
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
        ThemeManager.ThemeChanged -= OnThemeChanged;
        if (_hwndSource != null)
            RemoveClipboardFormatListener(_hwndSource.Handle);
        _net.Dispose();
    }
}
