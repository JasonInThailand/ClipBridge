using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ClipBridge.Core;
using ClipBridge.Models;

namespace ClipBridge.Sync;

/// <summary>
/// Peer-to-peer LAN sync. Every machine listens on SyncPort, announces itself by UDP broadcast on DiscoveryPort,
/// and connects to any announced peer that shares the same passphrase. All traffic is AES-GCM encrypted.
/// </summary>
public sealed class SyncService : IDisposable
{
    private sealed class Peer
    {
        public string Name = "";
        public bool Initiated;
        public TcpClient Client = null!;
        public NetworkStream Stream = null!;
        public readonly Channel<Msg> Outbox = Channel.CreateUnbounded<Msg>();
        public readonly CancellationTokenSource Cts = new();
        public string Endpoint = "";
    }

    private readonly ClipService _svc;
    private readonly Settings _s;
    private readonly FrameCrypto _crypto;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastAttempt = new();
    private readonly object _regGate = new();
    private readonly Dictionary<string, List<Peer>> _alive = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private string _lastConnectError = "";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const int MaxFrame = 400 * 1024 * 1024;

    public event Action? StatusChanged;
    public bool Connected => !_peers.IsEmpty;
    public string Status => !_s.EnableSync ? "sync off" : _peers.IsEmpty ? "no peer connected" : "synced with " + string.Join(", ", _peers.Keys);
    public IReadOnlyList<string> PeerNames => _peers.Keys.ToList();

    public SyncService(ClipService svc)
    {
        _svc = svc;
        _s = svc.Settings;
        _crypto = new FrameCrypto(_s.SharedKey);
        _svc.LocalItemAdded += it => { try { Broadcast(FullItemMsg(it)); } catch (Exception ex) { Log.Write("sync add: " + ex.Message); } };
        _svc.LocalMetaChanged += it => Broadcast(new Msg { T = "meta", Item = it.CloneMeta() });
    }

    public void Start()
    {
        if (!_s.EnableSync) return;
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(BeaconLoop);
        _ = Task.Run(DiscoveryLoop);
        _ = Task.Run(ManualPeersLoop);
    }

    // ---------------- connection establishment ----------------

    private async Task AcceptLoop()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _s.SyncPort);
            _listener.Start();
            Log.Write($"sync listening on {_s.SyncPort}");
        }
        catch (Exception ex) { Log.Write($"sync listen failed on {_s.SyncPort}: {ex.Message}"); return; }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandlePeer(c, false));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Write("accept: " + ex.Message); await Task.Delay(500); }
        }
    }

    private IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        IEnumerable<IPAddress> directed;
        try
        {
            directed = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                .Select(u =>
                {
                    var ip = u.Address.GetAddressBytes(); var mask = u.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    return new IPAddress(b);
                }).Distinct().ToList();
        }
        catch { yield break; }
        foreach (var d in directed) yield return d;
    }

    private async Task BeaconLoop()
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var payload = Encoding.UTF8.GetBytes($"CLIPBRIDGE|{_s.MachineName}|{_s.SyncPort}|{_crypto.Fingerprint}");
                foreach (var addr in BroadcastAddresses())
                {
                    try { await udp.SendAsync(payload, payload.Length, new IPEndPoint(addr, _s.DiscoveryPort)); } catch { }
                }
            }
            catch (Exception ex) { Log.Write("beacon: " + ex.Message); }
            try { await Task.Delay(3000, _cts.Token); } catch { break; }
        }
    }

    private async Task DiscoveryLoop()
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _s.DiscoveryPort));
        }
        catch (Exception ex) { Log.Write("discovery bind failed: " + ex.Message); return; }

        using (udp)
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var r = await udp.ReceiveAsync(_cts.Token);
                    var parts = Encoding.UTF8.GetString(r.Buffer).Split('|');
                    if (parts.Length < 4 || parts[0] != "CLIPBRIDGE") continue;
                    if (string.Equals(parts[1], _s.MachineName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (parts[3] != _crypto.Fingerprint) { Log.Write($"peer {parts[1]} announced with a different shared key; ignoring"); continue; }
                    if (_peers.ContainsKey(parts[1])) continue;
                    _ = TryConnect(r.RemoteEndPoint.Address.ToString(), int.Parse(parts[2]));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Write("discovery: " + ex.Message); await Task.Delay(500); }
            }
        }
    }

    private async Task ManualPeersLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!Connected)
            {
                foreach (var host in _s.Peers.Where(h => !string.IsNullOrWhiteSpace(h)))
                {
                    var h = host.Trim(); var port = _s.SyncPort;
                    var idx = h.LastIndexOf(':');
                    if (idx > 0 && int.TryParse(h[(idx + 1)..], out var p)) { port = p; h = h[..idx]; }
                    _ = TryConnect(h, port);
                }
            }
            try { await Task.Delay(5000, _cts.Token); } catch { break; }
        }
    }

    private async Task TryConnect(string host, int port)
    {
        var key = $"{host}:{port}";
        var now = DateTime.UtcNow;
        if (_lastAttempt.TryGetValue(key, out var last) && (now - last).TotalSeconds < 4) return;
        _lastAttempt[key] = now;
        var c = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(4000);
            await c.ConnectAsync(host, port, timeout.Token);
            await HandlePeer(c, true);
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastConnectError) { _lastConnectError = ex.Message; Log.Write($"connect {key}: {ex.Message}"); }
            c.Dispose();
        }
    }

    private async Task HandlePeer(TcpClient client, bool initiated)
    {
        var peer = new Peer { Client = client, Initiated = initiated, Endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?" };
        try
        {
            client.NoDelay = true;
            peer.Stream = client.GetStream();
            await SendRaw(peer.Stream, new Msg { T = "hello", Machine = _s.MachineName }, peer.Cts.Token);
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(peer.Cts.Token, _cts.Token);
            helloTimeout.CancelAfter(10000);
            var hello = await ReadMsg(peer.Stream, helloTimeout.Token);
            if (hello?.T != "hello" || string.IsNullOrEmpty(hello.Machine)) { Log.Write($"bad hello from {peer.Endpoint}"); client.Close(); return; }
            peer.Name = hello.Machine;
            if (string.Equals(peer.Name, _s.MachineName, StringComparison.OrdinalIgnoreCase)) { client.Close(); return; }

            // Both sides may connect to each other at once (and discovery + manual peers can each open a link).
            // Rules, evaluated identically on both ends so they agree:
            //   - first link to a peer is used;
            //   - a second link in the same direction is dropped, the existing one stays;
            //   - links in opposite directions: keep the one initiated by the alphabetically-first machine.
            var iAmInitiator = string.Compare(_s.MachineName, peer.Name, StringComparison.OrdinalIgnoreCase) < 0;
            bool registered;
            lock (_regGate)
            {
                if (!_alive.TryGetValue(peer.Name, out var list)) _alive[peer.Name] = list = new List<Peer>();
                list.Add(peer);
                if (!_peers.TryGetValue(peer.Name, out var existing)) { _peers[peer.Name] = peer; registered = true; }
                else if (ReferenceEquals(existing, peer)) registered = true;
                else if (existing.Initiated == initiated) registered = false;
                else
                {
                    registered = initiated == iAmInitiator;
                    if (registered) { _peers[peer.Name] = peer; existing.Cts.Cancel(); try { existing.Client.Close(); } catch { } }
                }
            }
            if (!registered)
            {
                Log.Write($"duplicate link to {peer.Name} ({peer.Endpoint}, {(initiated ? "outbound" : "inbound")}) dropped");
                client.Close();
                return;
            }
            Log.Write($"sync connected to {peer.Name} ({peer.Endpoint}, {(initiated ? "outbound" : "inbound")})");
            StatusChanged?.Invoke();

            peer.Outbox.Writer.TryWrite(ManifestMsg());
            _ = SendLoop(peer);
            _ = PingLoop(peer);
            await ReceiveLoop(peer);
        }
        catch (Exception ex) { if (!peer.Cts.IsCancellationRequested) Log.Write($"peer {peer.Name}: {ex.Message}"); }
        finally
        {
            peer.Cts.Cancel();
            try { client.Close(); } catch { }
            if (!string.IsNullOrEmpty(peer.Name))
            {
                lock (_regGate)
                {
                    if (_alive.TryGetValue(peer.Name, out var list)) { list.Remove(peer); if (list.Count == 0) _alive.Remove(peer.Name); }
                    if (_peers.TryGetValue(peer.Name, out var cur) && ReferenceEquals(cur, peer))
                    {
                        _peers.TryRemove(peer.Name, out _);
                        var next = list?.FirstOrDefault(p => !p.Cts.IsCancellationRequested);
                        if (next != null)
                        {
                            _peers[peer.Name] = next;
                            next.Outbox.Writer.TryWrite(ManifestMsg());
                            Log.Write($"sync link to {peer.Name} switched to {next.Endpoint}");
                        }
                        else Log.Write($"sync disconnected from {peer.Name}");
                    }
                }
            }
            StatusChanged?.Invoke();
        }
    }

    // ---------------- loops ----------------

    private async Task SendLoop(Peer peer)
    {
        try
        {
            await foreach (var msg in peer.Outbox.Reader.ReadAllAsync(peer.Cts.Token))
                await SendRaw(peer.Stream, msg, peer.Cts.Token);
        }
        catch (Exception ex) { if (!peer.Cts.IsCancellationRequested) Log.Write($"send {peer.Name}: {ex.Message}"); peer.Cts.Cancel(); }
    }

    private async Task PingLoop(Peer peer)
    {
        try
        {
            while (!peer.Cts.IsCancellationRequested)
            {
                await Task.Delay(20000, peer.Cts.Token);
                peer.Outbox.Writer.TryWrite(new Msg { T = "ping" });
            }
        }
        catch { }
    }

    private async Task ReceiveLoop(Peer peer)
    {
        while (!peer.Cts.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(peer.Cts.Token, _cts.Token);
            idle.CancelAfter(90000);
            var msg = await ReadMsg(peer.Stream, idle.Token);
            if (msg == null) break;
            try { Handle(peer, msg); }
            catch (Exception ex) { Log.Write($"handle {msg.T} from {peer.Name}: {ex}"); }
        }
    }

    private void Handle(Peer peer, Msg msg)
    {
        switch (msg.T)
        {
            case "ping": break;
            case "manifest": OnManifest(peer, msg.Manifest ?? new()); break;
            case "need":
                foreach (var id in msg.Ids ?? new())
                {
                    var it = _svc.Store.Get(id);
                    if (it != null) peer.Outbox.Writer.TryWrite(FullItemMsg(it));
                }
                break;
            case "item":
                if (msg.Item == null) break;
                ReceiveFiles(msg.Item);
                var stored = _svc.ApplyRemoteItem(msg.Item);
                Log.Write($"item {msg.Item.Id[..8]} ({msg.Item.Kind}) from {peer.Name}: {(stored ? "stored" : "already current")}");
                break;
            case "meta":
                if (msg.Item == null) break;
                if (!_svc.ApplyRemoteMeta(msg.Item)) peer.Outbox.Writer.TryWrite(new Msg { T = "need", Ids = new() { msg.Item.Id } });
                break;
        }
    }

    private void OnManifest(Peer peer, List<ManifestEntry> theirs)
    {
        var theirMap = theirs.ToDictionary(e => e.Id, e => e);
        var mine = _svc.Store.All();
        var need = new List<string>();
        foreach (var it in mine)
        {
            if (!theirMap.TryGetValue(it.Id, out var t))
            {
                if (!it.Deleted) peer.Outbox.Writer.TryWrite(FullItemMsg(it));
            }
            else if (it.Version > t.V)
            {
                peer.Outbox.Writer.TryWrite(new Msg { T = "meta", Item = it.CloneMeta() });
            }
        }
        var mineMap = mine.ToDictionary(i => i.Id, i => i);
        foreach (var t in theirs)
        {
            if (t.D) continue;
            if (!mineMap.TryGetValue(t.Id, out var local) || t.V > local.Version) need.Add(t.Id);
        }
        if (need.Count > 0) peer.Outbox.Writer.TryWrite(new Msg { T = "need", Ids = need });
        Log.Write($"manifest from {peer.Name}: they have {theirs.Count}, we requested {need.Count}");
    }

    private Msg ManifestMsg() => new()
    {
        T = "manifest",
        Manifest = _svc.Store.All().Select(i => new ManifestEntry { Id = i.Id, V = i.Version, D = i.Deleted }).ToList(),
    };

    private Msg FullItemMsg(ClipItem it)
    {
        var m = it.CloneMeta();
        m.Html = it.Html; m.Rtf = it.Rtf; m.Thumb = it.Thumb;
        if (it.Kind == ClipKind.Image && !it.Deleted) m.ImagePng = it.ImagePng ?? _svc.Store.GetImage(it.Id);
        var cap = (long)_s.MaxFileTransferMB * 1024 * 1024;
        var total = it.Files.Sum(f => Math.Max(0, f.Size));
        var sendData = it.Kind == ClipKind.Files && !it.Deleted && total <= cap && it.Files.All(f => f.Size >= 0);
        foreach (var f in it.Files)
        {
            var cf = new ClipFile { Name = f.Name, Size = f.Size, LocalPath = "" };
            if (sendData)
            {
                try { if (File.Exists(f.LocalPath)) cf.Data = File.ReadAllBytes(f.LocalPath); }
                catch (Exception ex) { Log.Write($"read file for sync: {ex.Message}"); }
            }
            m.Files.Add(cf);
        }
        return new Msg { T = "item", Item = m };
    }

    private static void ReceiveFiles(ClipItem item)
    {
        if (item.Kind != ClipKind.Files) return;
        foreach (var f in item.Files)
        {
            f.LocalPath = "";
            if (f.Data == null) continue;
            try
            {
                var dir = Path.Combine(Settings.ReceivedDir, item.Id);
                Directory.CreateDirectory(dir);
                var name = string.Concat(f.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                if (string.IsNullOrWhiteSpace(name)) name = "file";
                var path = Path.Combine(dir, name);
                File.WriteAllBytes(path, f.Data);
                f.LocalPath = path;
            }
            catch (Exception ex) { Log.Write("save received file: " + ex.Message); }
            finally { f.Data = null; }
        }
    }

    // ---------------- framing ----------------

    private void Broadcast(Msg msg)
    {
        foreach (var p in _peers.Values) p.Outbox.Writer.TryWrite(msg);
    }

    private async Task SendRaw(NetworkStream s, Msg msg, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(msg, Json);
        var frame = _crypto.Seal(json);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, frame.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    private async Task<Msg?> ReadMsg(NetworkStream s, CancellationToken ct)
    {
        var len = new byte[4];
        try { await s.ReadExactlyAsync(len, ct); }
        catch (EndOfStreamException) { return null; }
        var n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n <= 0 || n > MaxFrame) throw new InvalidDataException($"bad frame length {n}");
        var frame = new byte[n];
        await s.ReadExactlyAsync(frame, ct);
        var plain = _crypto.Open(frame) ?? throw new InvalidDataException("could not decrypt frame (shared key mismatch?)");
        return JsonSerializer.Deserialize<Msg>(plain, Json);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var p in _peers.Values) { p.Cts.Cancel(); try { p.Client.Close(); } catch { } }
        _peers.Clear();
    }
}
