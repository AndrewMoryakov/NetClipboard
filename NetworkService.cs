using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetClipboard;

public record PeerAnnounce(string Id, string Name, IPAddress Address, int Port);

public record IncomingMessage(
    string SenderId, string SenderName, int SenderPort,
    string Text, IPAddress SenderAddress, bool Encrypted = false);

public class NetworkService : IDisposable
{
    public const int DiscoveryPort = 9850;
    private const int DefaultTcpPort = 9851;

    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];
    private UdpClient? _udpListener;
    private TcpListener? _tcpListener;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _localIps = new();
    private readonly HashSet<string> _directPeers = new();
    private readonly object _directPeersLock = new();
    private byte[]? _broadcastPayload;
    private List<IPAddress>? _cachedBroadcastAddrs;
    private DateTime _broadcastAddrsCachedAt;

    public string InstanceId => _instanceId;
    public string MachineName { get; } = Environment.MachineName;
    public int TcpPort { get; private set; } = DefaultTcpPort;
    public string LocalAddress { get; private set; } = "?";

    public event Action<PeerAnnounce>? PeerSeen;
    public event Action<IncomingMessage>? MessageReceived;

    public void Start()
    {
        DetectLocalIp();
        BindTcp();
        StartUdpListener();
        Task.Run(() => BroadcastLoop(_cts.Token));
        Task.Run(() => UdpListenLoop(_cts.Token));
        Task.Run(() => TcpListenLoop(_cts.Token));
    }

    public void AddDirectPeer(IPAddress address)
    {
        var ip = address.ToString();
        if (_localIps.Contains(ip)) return;
        lock (_directPeersLock) { _directPeers.Add(ip); }
    }

    private void DetectLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 65530);
            LocalAddress = ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { LocalAddress = "127.0.0.1"; }

        // Collect all local IPs for self-detection filtering
        _localIps.Add("127.0.0.1");
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        _localIps.Add(ua.Address.ToString());
                }
            }
        }
        catch { }
    }

    private void BindTcp()
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, TcpPort);
                _tcpListener.Start();
                return;
            }
            catch (SocketException)
            {
                TcpPort++;
                _tcpListener = null;
            }
        }
        throw new InvalidOperationException("Cannot bind TCP port (tried 20 ports)");
    }

    private void StartUdpListener()
    {
        _udpListener = new UdpClient();
        _udpListener.Client.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
        _udpListener.Client.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
    }

    /// <summary>
    /// Compute the directed broadcast address for every active IPv4 interface,
    /// plus 255.255.255.255 as a fallback.
    /// </summary>
    private static List<IPAddress> GetBroadcastAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        bcast[i] = (byte)(ip[i] | ~mask[i]);

                    result.Add(new IPAddress(bcast));
                }
            }
        }
        catch { }

        // Always include global broadcast as fallback
        if (!result.Any(a => a.Equals(IPAddress.Broadcast)))
            result.Add(IPAddress.Broadcast);

        return result;
    }

    private async Task BroadcastLoop(CancellationToken ct)
    {
        // Cache payload — id/name/port never change after Start()
        _broadcastPayload ??= Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            id = _instanceId,
            name = MachineName,
            port = TcpPort
        }));

        // Reuse one UdpClient for all sends
        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Refresh broadcast addresses every 30s
                if (_cachedBroadcastAddrs == null
                    || (DateTime.UtcNow - _broadcastAddrsCachedAt).TotalSeconds > 30)
                {
                    _cachedBroadcastAddrs = GetBroadcastAddresses();
                    _broadcastAddrsCachedAt = DateTime.UtcNow;
                }

                foreach (var addr in _cachedBroadcastAddrs)
                {
                    try
                    {
                        await sender.SendAsync(_broadcastPayload, _broadcastPayload.Length,
                            new IPEndPoint(addr, DiscoveryPort));
                    }
                    catch { }
                }

                // Unicast to direct peers (for VPN/WireGuard networks)
                List<string> peers;
                lock (_directPeersLock) { peers = _directPeers.ToList(); }
                foreach (var peerIp in peers)
                {
                    try
                    {
                        await sender.SendAsync(_broadcastPayload, _broadcastPayload.Length,
                            new IPEndPoint(IPAddress.Parse(peerIp), DiscoveryPort));
                    }
                    catch { }
                }
            }
            catch { }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    private async Task UdpListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await _udpListener!.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(r.Buffer);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var id = root.GetProperty("id").GetString()!;
                if (id == _instanceId) continue;

                // Also skip if sender IP is one of our local IPs (catches stale processes)
                var senderIp = r.RemoteEndPoint.Address.ToString();
                if (_localIps.Contains(senderIp)) continue;

                // Auto-add sender to direct peers for bidirectional unicast
                lock (_directPeersLock) { _directPeers.Add(senderIp); }

                PeerSeen?.Invoke(new PeerAnnounce(
                    id,
                    root.GetProperty("name").GetString()!,
                    r.RemoteEndPoint.Address,
                    root.GetProperty("port").GetInt32()));
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task TcpListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener!.AcceptTcpClientAsync(ct);
                _ = HandleIncoming(client);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async Task HandleIncoming(TcpClient tcp)
    {
        try
        {
            using (tcp)
            {
                var stream = tcp.GetStream();
                stream.ReadTimeout = 10_000;

                var lenBuf = new byte[4];
                await stream.ReadExactlyAsync(lenBuf);
                int len = BitConverter.ToInt32(lenBuf);
                if (len is <= 0 or > 10_000_000) return;

                var buf = new byte[len];
                await stream.ReadExactlyAsync(buf);

                var json = Encoding.UTF8.GetString(buf);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var remoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;

                var encrypted = root.TryGetProperty("encrypted", out var encProp)
                    && encProp.GetBoolean();

                MessageReceived?.Invoke(new IncomingMessage(
                    root.GetProperty("senderId").GetString()!,
                    root.GetProperty("senderName").GetString()!,
                    root.GetProperty("senderPort").GetInt32(),
                    root.GetProperty("text").GetString()!,
                    remoteIp,
                    encrypted));
            }
        }
        catch { }
    }

    public async Task SendTextAsync(IPAddress address, int port, string text, bool encrypted = false)
    {
        var obj = new
        {
            senderId = _instanceId,
            senderName = MachineName,
            senderPort = TcpPort,
            text,
            encrypted
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        var lenBytes = BitConverter.GetBytes(bytes.Length);

        using var tcp = new TcpClient();
        tcp.SendTimeout = 5000;
        await tcp.ConnectAsync(address, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(lenBytes);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _tcpListener?.Stop();
        _udpListener?.Close();
        _cts.Dispose();
    }
}
