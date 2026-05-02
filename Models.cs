using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace NetClipboard;

public class ClipboardEntry : INotifyPropertyChanged
{
    private bool _isShared;
    private bool _isEncrypted;
    private bool _isDecrypted;
    private string _text = "";
    private string? _cipherText;

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public DateTime Timestamp { get; init; } = DateTime.Now;

    public bool IsShared
    {
        get => _isShared;
        set { if (_isShared != value) { _isShared = value; OnPropertyChanged(); } }
    }

    public bool IsEncrypted
    {
        get => _isEncrypted;
        set
        {
            if (_isEncrypted != value)
            {
                _isEncrypted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public bool IsDecrypted
    {
        get => _isDecrypted;
        set
        {
            if (_isDecrypted != value)
            {
                _isDecrypted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string? CipherText
    {
        get => _cipherText;
        set { if (_cipherText != value) { _cipherText = value; OnPropertyChanged(); } }
    }

    public string TimestampStr => Timestamp.ToString("HH:mm:ss");

    public string Preview => Text.Length > 300 ? Text[..300] + "..." : Text;

    /// <summary>
    /// Shows preview text if plain or decrypted; placeholder if still encrypted.
    /// </summary>
    public string DisplayText =>
        IsEncrypted && !IsDecrypted
            ? "[Encrypted content]"
            : Preview;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class PeerInfo : INotifyPropertyChanged
{
    private int _unreadCount;
    private bool _isOnline = true;

    public string InstanceId { get; init; } = "";
    public string Name { get; set; } = "";
    public IPAddress Address { get; set; } = IPAddress.Any;
    public int Port { get; set; }
    public DateTime LastSeen { get; set; }
    public ObservableCollection<ClipboardEntry> Items { get; } = new();
    public string DisplayName => $"{Name} ({Address})";

    public int UnreadCount
    {
        get => _unreadCount;
        set { if (_unreadCount != value) { _unreadCount = value; OnPropertyChanged(); } }
    }

    public bool IsOnline
    {
        get => _isOnline;
        set { if (_isOnline != value) { _isOnline = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
