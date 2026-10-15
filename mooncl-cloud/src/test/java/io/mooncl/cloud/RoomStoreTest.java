package io.mooncl.cloud;

import java.time.*;
import java.util.Base64;
import org.junit.jupiter.api.Test;
import static org.assertj.core.api.Assertions.*;

class RoomStoreTest {
    static final class MutableClock extends Clock {
        Instant now = Instant.parse("2026-01-01T00:00:00Z");
        void advance(long seconds) { now = now.plusSeconds(seconds); }
        @Override public ZoneId getZone() { return ZoneOffset.UTC; }
        @Override public Clock withZone(ZoneId zone) { return this; }
        @Override public Instant instant() { return now; }
    }
    private final MutableClock clock = new MutableClock();
    private final RoomStore store = new RoomStore(clock);
    private RoomStore.Session create() { return store.create(new RoomStore.Create("Room", "Host", null, null)); }
    private void status(int status, Runnable action) {
        assertThatThrownBy(action::run).isInstanceOfSatisfying(ApiException.class, e -> assertThat(e.status).isEqualTo(status));
    }

    @Test void hostDeadlineClosesRoomEvenWithLiveGuest() {
        var host = create();
        String code = host.room().code();
        assertThat(code).matches("[A-HJ-NP-Z2-9]{8}");
        assertThat(Base64.getUrlDecoder().decode(host.token())).hasSize(32);
        assertThat(host.room().expiresAt()).isEqualTo(clock.instant().plusSeconds(75));
        var guest = store.join(code, new RoomStore.Join("Guest", null));
        clock.advance(74);
        store.heartbeat(code, guest.memberId(), guest.token());
        store.get(code, host.token()); // Reads do not extend the owner's lease.
        clock.advance(1);
        status(404, () -> store.get(code, guest.token()));
        status(404, () -> store.heartbeat(code, host.memberId(), host.token()));
    }

    @Test void hostHeartbeatExtendsLeaseAndGuestIsEvictedAtExactDeadline() {
        var host = create();
        String code = host.room().code();
        var guest = store.join(code, new RoomStore.Join("Guest", null));
        clock.advance(50);
        assertThat(store.heartbeat(code, host.memberId(), host.token()).expiresAt()).isEqualTo(clock.instant().plusSeconds(75));
        clock.advance(25);
        assertThat(store.get(code, host.token()).members()).hasSize(1);
        status(401, () -> store.get(code, guest.token()));
        assertThat(guest.room().members()).hasSize(2); // Immutable snapshot, no internal state escape.
        clock.advance(50);
        status(404, () -> store.get(code, host.token()));
    }

    @Test void memberIsolationLeaveAndPassword() {
        var host = store.create(new RoomStore.Create("Room", "Host", "password", null));
        String code = host.room().code();
        status(403, () -> store.join(code, new RoomStore.Join("Guest", "wrong")));
        status(403, () -> store.join(code, new RoomStore.Join("Guest", null)));
        var guest = store.join(code, new RoomStore.Join("Guest", "password"));
        status(401, () -> store.get(code, "a".repeat(43)));
        status(403, () -> store.heartbeat(code, host.memberId(), guest.token()));
        status(403, () -> store.leave(code, guest.memberId(), host.token()));
        status(403, () -> store.publish(code, guest.token(), new RoomConnection("frp", "game.example.com", 25565, null, null, null, null)));
        store.leave(code, guest.memberId(), guest.token());
        status(401, () -> store.get(code, guest.token()));
        var next = store.join(code, new RoomStore.Join("Guest2", "password"));
        store.leave(code, host.memberId(), host.token());
        status(404, () -> store.get(code, next.token()));
    }

    @Test void boundedRoomsAndMembersAndReclaimsCapacity() {
        var host = create();
        String code = host.room().code();
        for (int i = 0; i < 15; i++) store.join(code, new RoomStore.Join("Guest" + i, null));
        status(409, () -> store.join(code, new RoomStore.Join("Overflow", null)));
        for (int i = 1; i < 1000; i++) create();
        status(503, this::create);
        clock.advance(75);
        assertThat(create().room().members()).hasSize(1);
    }

    @Test void rateLimitIsBoundedAndResets() {
        for (int i = 0; i < 20; i++) store.rateLimit("127.0.0.1");
        status(429, () -> store.rateLimit("127.0.0.1"));
        for (int i = 0; i < 100; i++) store.rateLimit("peer" + i);
        status(429, () -> store.rateLimit("new-peer"));
        clock.advance(60);
        store.rateLimit("127.0.0.1");
    }

    @Test void validatesStringAndDescriptorBounds() {
        status(400, () -> store.create(new RoomStore.Create("x".repeat(81), "Host", null, null)));
        status(400, () -> store.create(new RoomStore.Create("Room", "x".repeat(33), null, null)));
        status(400, () -> store.create(new RoomStore.Create("Room", "Host", "x".repeat(129), null)));
        for (String address : new String[]{"::1", "::", "fe80::1", "fd00::1", "ff02::1", "2001:db8::1", "2002::1", "3fff::1", "2001::1", "example.com", "[2606:4700::1111]", "2606:4700::1111%eth0", "127.0.0.1"}) {
            status(400, () -> new RoomConnection("ipv6", address, 25565, null, null, null, null).validated());
        }
        new RoomConnection("ipv6", "2606:4700:4700::1111", 25565, null, null, null, null).validated();
        new RoomConnection("frp", "game.example.com", 1, null, null, null, null).validated();
        new RoomConnection("frp", "192.0.2.1", 65535, null, null, null, null).validated();
        new RoomConnection("p2p", null, null, "frps.example.com", 7000, "moon-room", "visitor-secret").validated();
        for (String address : new String[]{"https://example.com", "a/b", "a:25565", "999.1.1.1", "-bad.example", "a".repeat(254)})
            status(400, () -> new RoomConnection("frp", address, 25565, null, null, null, null).validated());
        for (int port : new int[]{0, -1, 65536})
            status(400, () -> new RoomConnection("frp", "example.com", port, null, null, null, null).validated());
        status(400, () -> new RoomConnection("p2p", null, null, "example.com", 7000, "x".repeat(65), "key").validated());
        status(400, () -> new RoomConnection("p2p", null, null, "example.com", 7000, "room", "x".repeat(129)).validated());
        status(400, () -> new RoomConnection("frp", "example.com", 25565, null, null, null, "unexpected").validated());
    }

    @Test void diagnosticStringsRedactCredentials() {
        var request = new RoomStore.Create("Room", "Host", "room-password", null);
        var host = store.create(request);
        var connection = new RoomConnection("p2p", null, null, "example.com", 7000, "room", "visitor-secret");
        assertThat(request.toString()).doesNotContain("room-password");
        assertThat(new RoomStore.Join("Guest", "room-password").toString()).doesNotContain("room-password");
        assertThat(host.toString()).doesNotContain(host.token());
        assertThat(connection.toString()).doesNotContain("visitor-secret");
    }
}
