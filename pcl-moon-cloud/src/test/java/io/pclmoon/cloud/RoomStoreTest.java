package io.pclmoon.cloud;

import java.time.*;
import java.util.*;
import java.nio.ByteBuffer;
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
        new RoomConnection("ip", "192.0.2.10", 25566, null, null, null, null).validated();
        new RoomConnection("ip", "play.example.com", 1, null, null, null, null).validated();
        status(400, () -> new RoomConnection("ip", "https://example.com", 25565, null, null, null, null).validated());
        new RoomConnection("frp", "192.0.2.1", 65535, null, null, null, null).validated();
        new RoomConnection("frp", "game.example.com", 1, null, null, null, null).validated();
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

    private RoomConnection frp() { return new RoomConnection("frp", "game.example.com", 25565, null, null, null, null); }
    private RoomRoute route(String id, boolean enabled) { return new RoomRoute(id, "Route", frp(), enabled); }
    static String png(int width, int height, int bytes) {
        byte[] source = Base64.getDecoder().decode(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aO1sAAAAASUVORK5CYII=");
        byte[] png = Arrays.copyOf(source, bytes);
        ByteBuffer header = ByteBuffer.wrap(png);
        header.putInt(16, width).putInt(20, height);
        if (bytes >= 80) { // Add an inert ancillary chunk before IEND to exercise exact byte limits.
            header.putInt(56, bytes - 80).putInt(60, 0x70614464);
            System.arraycopy(source, 56, png, bytes - 12, 12);
        }
        for (int offset = 8; offset + 12 <= png.length;) {
            int length = header.getInt(offset);
            if (length < 0 || length > png.length - offset - 12) break;
            var crc = new java.util.zip.CRC32();
            crc.update(png, offset + 4, length + 4);
            header.putInt(offset + 8 + length, (int) crc.getValue());
            offset += length + 12;
        }
        return Base64.getEncoder().encodeToString(png);
    }

    @Test void routesAreHostOnlyAtomicBoundedAndLegacyCompatible() {
        var host = create();
        String code = host.room().code();
        var guest = store.join(code, new RoomStore.Join("Guest", null));
        assertThat(host.room().routes()).isEmpty();
        var batch = new RoomStore.Routes(List.of(new RoomRoute("off", "Off",
                new RoomConnection("frp", "disabled.example.com", 1, null, null, null, null), false),
                route("first", true), new RoomRoute("last", "Last",
                new RoomConnection("frp", "last.example.com", 2, null, null, null, null), true)));
        status(403, () -> store.publishRoutes(code, guest.token(), batch));
        var room = store.publishRoutes(code, host.token(), batch);
        assertThat(room.routes()).hasSize(3);
        assertThat(room.connection()).isEqualTo(frp());
        for (var invalid : List.of(
                new RoomStore.Routes(List.of(route("duplicate", true), route("duplicate", false))),
                new RoomStore.Routes(List.of(route("good", true), route("bad!", true))),
                new RoomStore.Routes(List.of(route("", true))),
                new RoomStore.Routes(List.of(route("x".repeat(65), true))),
                new RoomStore.Routes(List.of(route("线路", true))),
                new RoomStore.Routes(List.of(new RoomRoute("id", "x".repeat(49), frp(), true))),
                new RoomStore.Routes(List.of(new RoomRoute("id", "", frp(), true))),
                new RoomStore.Routes(List.of(new RoomRoute("id", "Route", null, true))),
                new RoomStore.Routes(Collections.nCopies(13, route("id", true))),
                new RoomStore.Routes(Arrays.asList(route("good", true), null)),
                new RoomStore.Routes(null))) {
            status(400, () -> store.publishRoutes(code, host.token(), invalid));
            assertThat(store.get(code, host.token()).routes()).isEqualTo(room.routes());
        }
        var disabled = store.publishRoutes(code, host.token(), new RoomStore.Routes(List.of(route("off", false))));
        assertThat(disabled.connection()).isNull();
        var cleared = store.publishRoutes(code, host.token(), new RoomStore.Routes(List.of()));
        assertThat(cleared.routes()).isEmpty();
        assertThat(cleared.connection()).isNull();
        var legacy = store.publish(code, host.token(), frp());
        assertThat(legacy.routes()).containsExactly(RoomRoute.legacy(frp()));
        assertThat(store.create(new RoomStore.Create("Room", "Host", null, frp())).room().routes())
                .containsExactly(RoomRoute.legacy(frp()));
        assertThat(room.routes()).hasSize(3);
        assertThatThrownBy(() -> room.routes().clear()).isInstanceOf(UnsupportedOperationException.class);
        var twelve = java.util.stream.IntStream.range(0, 12).mapToObj(i -> route("route_" + i, true)).toList();
        assertThat(store.publishRoutes(code, host.token(), new RoomStore.Routes(twelve)).routes()).hasSize(12);
    }

    @Test void presenceIsOwnOnlyAndNoBodyPreservesProfile() {
        var host = store.create(new RoomStore.Create("Room", "Host", null, null, png(64, 64, 68),
                new RoomStore.Game("Minecraft", "1.21", "launching")));
        String code = host.room().code();
        var guest = store.join(code, new RoomStore.Join("Guest", null, png(1, 1, 68), new RoomStore.Game("Game", "v", "running")));
        var update = new RoomStore.Presence("", new RoomStore.Game("", "", "failed"));
        status(403, () -> store.heartbeat(code, host.memberId(), guest.token(), update));
        var room = store.heartbeat(code, guest.memberId(), guest.token(), update);
        assertThat(room.members().get(0)).isEqualTo(host.room().members().get(0));
        assertThat(room.members().get(1).name()).isEqualTo("Guest");
        assertThat(room.members().get(1).avatar()).isEmpty();
        assertThat(room.members().get(1).game().state()).isEqualTo("failed");
        assertThat(store.heartbeat(code, guest.memberId(), guest.token()).members()).isEqualTo(room.members());
        assertThat(store.heartbeat(code, guest.memberId(), guest.token(), new RoomStore.Presence(null, null)).members()).isEqualTo(room.members());
        assertThat(guest.room().members().get(1).game().state()).isEqualTo("running");
        var defaultMember = create().room().members().get(0);
        assertThat(defaultMember.avatar()).isEmpty();
        assertThat(defaultMember.game()).isEqualTo(new RoomStore.Game("", "", "stopped"));
    }

    @Test void invalidProfileDoesNotRenewOrPartiallyMutate() {
        var host = create();
        String code = host.room().code();
        clock.advance(74);
        status(400, () -> store.heartbeat(code, host.memberId(), host.token(),
                new RoomStore.Presence(png(1, 1, 68), new RoomStore.Game("x".repeat(81), "", "running"))));
        assertThat(store.get(code, host.token()).members()).isEqualTo(host.room().members());
        assertThat(store.get(code, host.token()).expiresAt()).isEqualTo(host.room().expiresAt());
        clock.advance(1);
        status(404, () -> store.get(code, host.token()));
    }

    @Test void avatarAndGameBoundsAreValidatedAtAllEntryPoints() {
        var maximum = store.create(new RoomStore.Create("Room", "Host", null, null, png(64, 64, 6144), null));
        assertThat(maximum.room().members().get(0).avatar()).hasSize(8192);
        byte[] corrupt = Base64.getDecoder().decode(png(1, 1, 68));
        corrupt[29] ^= 1;
        status(400, () -> store.join(maximum.room().code(), new RoomStore.Join("Guest", null,
                Base64.getEncoder().encodeToString(corrupt), null)));
        for (String invalid : new String[]{"https://example.com/a.png", "data:image/png;base64," + png(1, 1, 68),
                "not base64!", "A".repeat(8193), png(0, 1, 68), png(1, 65, 68), png(65, 1, 68), png(1, 1, 33), png(1, 1, 6145),
                Base64.getEncoder().encodeToString(new byte[33]), Base64.getEncoder().encodeToString(new byte[6145])}) {
            status(400, () -> store.create(new RoomStore.Create("Room", "Host", null, null, invalid, null)));
        }
        var host = create();
        for (var invalid : List.of(new RoomStore.Game("x".repeat(81), "", "stopped"),
                new RoomStore.Game("", "x".repeat(81), "stopped"), new RoomStore.Game("a\nb", "", "stopped"),
                new RoomStore.Game("", "\u007f", "stopped"), new RoomStore.Game("", "", "starting"))) {
            status(400, () -> store.join(host.room().code(), new RoomStore.Join("Guest", null, null, invalid)));
        }
        for (String state : List.of("stopped", "launching", "running", "failed")) {
            var room = store.heartbeat(host.room().code(), host.memberId(), host.token(),
                    new RoomStore.Presence(null, new RoomStore.Game("x".repeat(80), "v".repeat(80), state)));
            assertThat(room.members().get(0).game().state()).isEqualTo(state);
        }
        assertThat(store.get(host.room().code(), host.token()).members()).hasSize(1);
    }

    private RoomConnection zt(String address, String id) {
        return new RoomConnection("zerotier", address, 25565, null, null, null, null, id, null, null);
    }
    private RoomConnection et(String address, String name, String key, List<String> peers) {
        return new RoomConnection("easytier", address, 25565, null, null, null, key, null, name, peers);
    }

    @Test void zerotierAndEasytierDescriptorsAreStrictAndImmutable() {
        zt("10.1.2.3", "0123456789aBcDeF").validated();
        zt("fd00::1", "0123456789abcdef").validated();
        zt("::ffff:192.0.2.1", "0123456789abcdef").validated();
        for (String id : List.of("", "0123456789abcde", "0123456789abcdefg", "0123456789abcdeg"))
            status(400, () -> zt("10.0.0.1", id).validated());
        for (String address : List.of("example.com", "999.1.1.1", "[fd00::1]", "fd00::1%eth0", "10.01.0.1")) {
            status(400, () -> zt(address, "0123456789abcdef").validated());
            status(400, () -> et(address, "net", "key", List.of("tcp://example.com:11010")).validated());
        }
        for (String scheme : List.of("tcp", "udp", "ws", "wss", "quic", "wg"))
            et("fd00::1", "net", "key", List.of(scheme + "://[fd00::2]:11010")).validated();
        for (String peer : List.of("example.com", "https://example.com:11010", "tcp://example.com", "tcp://example.com:0",
                "tcp://example.com:65536", "tcp://user@example.com:11010", "tcp://example.com:11010?q=x",
                "tcp://example.com:11010#x", "tcp://:11010", "tcp://example.com:11010/" + "x".repeat(256)))
            status(400, () -> et("10.0.0.1", "net", "key", List.of(peer)).validated());
        for (var peers : List.of(List.<String>of(), Collections.nCopies(9, "tcp://example.com:11010"), Arrays.asList((String) null)))
            status(400, () -> et("10.0.0.1", "net", "key", peers).validated());
        for (String name : List.of("", "x".repeat(65), "bad\n"))
            status(400, () -> et("10.0.0.1", name, "key", List.of("tcp://example.com:11010")).validated());
        for (String key : List.of("", "x".repeat(129), "bad\u007f"))
            status(400, () -> et("10.0.0.1", "net", key, List.of("tcp://example.com:11010")).validated());
        var mutable = new ArrayList<>(List.of("tcp://example.com:11010"));
        var descriptor = et("10.0.0.1", "x".repeat(64), "k".repeat(128), mutable).validated();
        mutable.clear();
        assertThat(descriptor.peers()).hasSize(1);
        status(400, () -> new RoomConnection("frp", "example.com", 1, null, null, null, null, "0123456789abcdef", null, null).validated());
        status(400, () -> new RoomConnection("p2p", null, null, "example.com", 1, "proxy", "key", null, "net", null).validated());
        status(400, () -> new RoomConnection("ipv6", "2606:4700::1111", 1, null, null, null, null, null, null, List.of()).validated());
        status(400, () -> new RoomConnection("zerotier", "10.0.0.1", 1, null, null, null, "key", "0123456789abcdef", null, null).validated());
        status(400, () -> new RoomConnection("easytier", "10.0.0.1", 1, null, null, null, "key", "0123456789abcdef", "net", List.of("tcp://example.com:1")).validated());
    }
}
