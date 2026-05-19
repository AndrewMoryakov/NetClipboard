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
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ClipboardFileEntry : ClipboardEntry
{
    private long _bytesTransferred;
    private bool _isComplete;
    private bool _isFailed;
    private bool _isSending;
    private string _reason = "";
    private string? _localPath;

    public string FileId { get; init; } = "";
    public string FileName { get; init; } = "";
    public long FileSize { get; init; }
    public bool IsIncoming { get; init; }

    public string? LocalPath
    {
        get => _localPath;
        set { if (_localPath != value) { _localPath = value; OnPropertyChanged(); } }
    }

    public long BytesTransferred
    {
        get => _bytesTransferred;
        set
        {
            if (_bytesTransferred != value)
            {
                _bytesTransferred = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (_isComplete != value)
            {
                _isComplete = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(ShowActions));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public bool IsFailed
    {
        get => _isFailed;
        set
        {
            if (_isFailed != value)
            {
                _isFailed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public bool IsSending
    {
        get => _isSending;
        set
        {
            if (_isSending != value)
            {
                _isSending = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowSendCancel));
                OnPropertyChanged(nameof(ShowShareToggle));
            }
        }
    }

    public string Reason
    {
        get => _reason;
        set
        {
            if (_reason != value)
            {
                _reason = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public double ProgressPercent =>
        FileSize > 0 ? (double)BytesTransferred / FileSize * 100 : 0;
    public string ProgressText =>
        IsFailed && !string.IsNullOrEmpty(Reason) ? Reason :
        IsFailed   ? "Failed" :
        IsComplete ? SizeText :
                     $"{FormatBytes(BytesTransferred)} / {FormatBytes(FileSize)}";
    public string SizeText => FormatBytes(FileSize);
    public bool ShowProgress => IsIncoming && !IsComplete && !IsFailed;
    public bool ShowActions => IsIncoming && IsComplete;
    public bool ShowSendCancel => !IsIncoming && IsSending;
    public bool ShowShareToggle => !IsIncoming && !IsSending;

    public static string FormatBytes(long n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = n;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{n} B" : $"{v:0.##} {units[u]}";
    }
}

public class PeerInfo : INotifyPropertyChanged
{
    private bool _isOnline = true;

    public string InstanceId { get; init; } = "";
    public string Name { get; set; } = "";
    public IPAddress Address { get; set; } = IPAddress.Any;
    public int Port { get; set; }
    public DateTime LastSeen { get; set; }
    public ObservableCollection<ClipboardEntry> Items { get; } = new();
    public string DisplayName => $"{Name} ({Address})";

    public int UnreadCount { get; set; }

    public bool IsOnline
    {
        get => _isOnline;
        set { if (_isOnline != value) { _isOnline = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
