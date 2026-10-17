using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Minecraft.Launcher;

public sealed record GameLinkState(string SessionId, string Instance, int? ProcessId, string State, string Address, string Message, string RequestId);

/// <summary>One authenticated, loopback-only control channel per launched game. Never carries account credentials.</summary>
public sealed class GameLinkSession : IDisposable
{
    private static readonly ConcurrentDictionary<string, GameLinkSession> Active = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private volatile StreamWriter? _writer;
    private TcpClient? _client;
    private int _disposed;
    private GameLinkState _state;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public GameLinkState State => Volatile.Read(ref _state);
    public static GameLinkState[] Snapshot() => Active.Values.Select(s => s.State).OrderBy(s => s.Instance).ToArray();

    public GameLinkSession(string instance)
    {
        _state = new(Id, instance, null, "waiting", "", "等待游戏 Agent 连接", "");
        _listener.Start();
        Active[Id] = this;
        _ = ListenAsync();
    }

    public void Configure(ProcessStartInfo info, string agentJar, string? mappings = null, string? runtimeMappings = null)
    {
        if (!File.Exists(agentJar)) throw new FileNotFoundException("联机 Agent 文件不存在", agentJar);
        var existing = info.Arguments ?? "";
        if (!existing.Contains("-javaagent:\"" + agentJar + "\"", StringComparison.OrdinalIgnoreCase))
            info.Arguments = "-javaagent:\"" + agentJar + "\" " + existing;
        info.Environment["MOONCL_LINK_PORT"] = ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["MOONCL_LINK_TOKEN"] = _token;
        if (!string.IsNullOrEmpty(mappings)) info.Environment["MOONCL_LINK_MAPPINGS"] = mappings;
        else info.Environment.Remove("MOONCL_LINK_MAPPINGS");
        if (!string.IsNullOrEmpty(runtimeMappings)) info.Environment["MOONCL_LINK_RUNTIME_MAPPINGS"] = runtimeMappings;
        else info.Environment.Remove("MOONCL_LINK_RUNTIME_MAPPINGS");
    }

    public void Attach(Process process)
    {
        Volatile.Write(ref _state, State with { ProcessId = process.Id });
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Dispose();
        if (process.HasExited) Dispose();
    }

    public static async Task<string> SendAsync(string sessionId, string? address)
    {
        if (!Active.TryGetValue(sessionId, out var session)) throw new InvalidOperationException("游戏会话已结束，请重新选择游戏实例");
        if (address != null) ValidateAddress(address);
        var id = Guid.NewGuid().ToString("N");
        await session._writeLock.WaitAsync(session._stop.Token).ConfigureAwait(false);
        try
        {
            var writer = session._writer ?? throw new InvalidOperationException("游戏 Agent 尚未就绪；请等待游戏加载完成");
            var line = address == null ? $"DISCONNECT\t{id}" : $"CONNECT\t{id}\t{Encode(address)}";
            await writer.WriteLineAsync(line.AsMemory(), session._stop.Token).ConfigureAwait(false);
            await writer.FlushAsync(session._stop.Token).ConfigureAwait(false);
            return id; // Delivery only. The Agent reports actual game state separately.
        }
        finally { session._writeLock.Release(); }
    }

    public static void ValidateAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 255 || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '"' or '?' or '#' or '@'))
            throw new ArgumentException("请输入服务器域名或 IP，可附带端口");
        if (!Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port is < -1 or 0 or > 65535 || uri.AbsolutePath != "/")
            throw new ArgumentException("服务器地址或端口无效；IPv6 地址请使用 [地址]:端口");
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var stream = client.GetStream();
                    if (await ReadLineAsync(stream, handshake.Token).ConfigureAwait(false) != "HELLO\t1\t" + _token)
                    { client.Dispose(); continue; }
                    var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { NewLine = "\n" };
                    await writer.WriteLineAsync("OK".AsMemory(), handshake.Token).ConfigureAwait(false);
                    await writer.FlushAsync(handshake.Token).ConfigureAwait(false);
                    _client = client;
                    _writer = writer;
                    while (!_stop.IsCancellationRequested)
                    {
                        var line = await ReadLineAsync(stream, _stop.Token).ConfigureAwait(false);
                        if (line == null) break;
                        ApplyState(line);
                    }
                    _writer = null;
                    client.Dispose();
                    if (!_stop.IsCancellationRequested)
                        Volatile.Write(ref _state, State with { State = "error", Message = "游戏 Agent 连接已关闭；运行时控制不可用" });
                    return;
                }
                catch (Exception) when (!_stop.IsCancellationRequested && _client == null) { client.Dispose(); }
                finally { if (_stop.IsCancellationRequested) client.Dispose(); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException or FormatException)
        {
            if (!_stop.IsCancellationRequested) Volatile.Write(ref _state, State with { State = "error", Message = "游戏 Agent 通信失败" });
        }
        finally
        {
            _writer = null;
            _client?.Dispose();
        }
    }

    private void ApplyState(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length != 5 || parts[0] != "STATE") throw new IOException("Invalid game state frame");
        if (!new[] { "waiting", "ready", "connecting", "connected", "disconnected", "unsupported", "error" }.Contains(parts[2])) throw new IOException("Invalid game state");
        Volatile.Write(ref _state, State with { RequestId = parts[1], State = parts[2], Address = Decode(parts[3]), Message = Decode(parts[4]) });
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new byte[16384];
        var one = new byte[1];
        var count = 0;
        while (await stream.ReadAsync(one.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n') return Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\r');
            if (count == bytes.Length) throw new IOException("Game frame too long");
            bytes[count++] = one[0];
        }
        if (count != 0) throw new IOException("Truncated game frame");
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Active.TryRemove(Id, out _);
        _stop.Cancel();
        _listener.Stop();
        _writer = null;
        _client?.Dispose();
    }
}
