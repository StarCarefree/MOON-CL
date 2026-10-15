using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

/// <summary>Caller serializes operations. Poll every 15s while in a room, including when its page is hidden.</summary>
public sealed class RoomSession(RoomApiClient api) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    public RoomTicket? Ticket { get; private set; }
    public RoomSnapshot? Room => Ticket?.Room;
    public bool IsHost => Ticket is { } t && t.MemberId == t.Room.HostId;
    public bool IsFresh => Room is { } room && room.ExpiresAt > DateTimeOffset.UtcNow;

    public async Task CreateAsync(CreateRoomRequest request, CancellationToken ct)
    {
        EnsureEmpty(); request.Connection?.Validate();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var ticket = await api.CreateAsync(request, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        Ticket = ticket;
    }
    public async Task JoinAsync(string code, JoinRoomRequest request, CancellationToken ct)
    {
        EnsureEmpty();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var ticket = await api.JoinAsync(code, request, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        Ticket = ticket;
    }
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (Ticket is not { } ticket) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            var room = await api.HeartbeatAsync(ticket, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            Ticket = ticket with { Room = room };
        }
        catch (RoomApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        { Ticket = null; throw; }
    }
    public async Task PublishConnectionAsync(RoomConnection connection, CancellationToken ct)
    {
        if (Ticket is not { } ticket || !IsHost) throw new InvalidOperationException("只有房主能发布游戏连接信息。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var room = await api.PublishAsync(ticket, connection, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        Ticket = ticket with { Room = room };
    }
    public async Task LeaveAsync(CancellationToken ct)
    {
        var ticket = Ticket; Ticket = null;
        if (ticket is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await api.LeaveAsync(ticket, linked.Token).ConfigureAwait(false);
    }
    private void EnsureEmpty()
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (Ticket is not null) throw new InvalidOperationException("请先退出当前房间。");
    }
    public void Dispose() { _lifetime.Cancel(); Ticket = null; api.Dispose(); }
}
