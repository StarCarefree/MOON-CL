using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PCL.Core.Multiplayer;

/// <summary>Serializes small API mutations, never holds the gate while a transport is being probed.</summary>
public sealed class RoomSession(RoomApiClient api) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private RoomTicket? _ticket;
    public RoomTicket? Ticket { get { lock (_state) return _ticket; } }
    public RoomSnapshot? Room => Ticket?.Room;
    public bool IsHost => Ticket is { } t && t.MemberId == t.Room.HostId;
    public bool IsFresh => Room is { } room && room.ExpiresAt > DateTimeOffset.UtcNow;

    public Task CreateAsync(CreateRoomRequest request, CancellationToken ct) => RunAsync(async token =>
    {
        EnsureEmpty(); request.Connection?.Validate(); new RoomPresence(request.Avatar, request.Game).Validate();
        SetTicket(await api.CreateAsync(request, token).ConfigureAwait(false));
    }, ct);
    public Task JoinAsync(string code, JoinRoomRequest request, CancellationToken ct) => RunAsync(async token =>
    {
        EnsureEmpty(); new RoomPresence(request.Avatar, request.Game).Validate();
        SetTicket(await api.JoinAsync(code, request, token).ConfigureAwait(false));
    }, ct);
    public Task RefreshAsync(CancellationToken ct) => RefreshAsync(null, ct);
    public Task RefreshAsync(RoomPresence? presence, CancellationToken ct) => RunAsync(async token =>
    {
        if (Ticket is not { } ticket) return;
        try
        {
            var room = presence is null ? await api.HeartbeatAsync(ticket, token).ConfigureAwait(false)
                : await api.HeartbeatAsync(ticket, presence, token).ConfigureAwait(false);
            SetTicket(ticket with { Room = room });
        }
        catch (RoomApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        { SetTicket(null); throw; }
    }, ct);
    public Task PublishConnectionAsync(RoomConnection connection, CancellationToken ct) => RunAsync(async token =>
    {
        var ticket = RequireHost();
        SetTicket(ticket with { Room = await api.PublishAsync(ticket, connection, token).ConfigureAwait(false) });
    }, ct);
    public Task PublishRoutesAsync(RoomRoute[] routes, CancellationToken ct) => RunAsync(async token =>
    {
        var ticket = RequireHost(); RoomRoute.ValidateAll(routes);
        SetTicket(ticket with { Room = await api.PublishRoutesAsync(ticket, routes, token).ConfigureAwait(false) });
    }, ct);
    public Task LeaveAsync(CancellationToken ct) => RunAsync(async token =>
    {
        var ticket = Ticket; SetTicket(null);
        if (ticket is not null) await api.LeaveAsync(ticket, token).ConfigureAwait(false);
    }, ct);
    private RoomTicket RequireHost() => Ticket is { } ticket && ticket.MemberId == ticket.Room.HostId
        ? ticket : throw new InvalidOperationException("只有房主能修改房间线路。");
    private void EnsureEmpty()
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        if (Ticket is not null) throw new InvalidOperationException("请先退出当前房间。");
    }
    private void SetTicket(RoomTicket? ticket)
    {
        lock (_state) { _lifetime.Token.ThrowIfCancellationRequested(); _ticket = ticket; }
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await action(linked.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        lock (_state) { if (_lifetime.IsCancellationRequested) return; _lifetime.Cancel(); _ticket = null; }
        api.Dispose();
    }
}
