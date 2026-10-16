package io.mooncl.cloud;
package io.mooncl.cloud;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.time.*;
import java.util.Locale;
import java.util.*;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.PBEKeySpec;
import org.springframework.stereotype.Component;

/** Ephemeral coordination only. All mutations and snapshots share one bounded-state lock. */
@Component
final class RoomStore {
    static final Duration TTL = Duration.ofSeconds(75);
    private static final String ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private final Clock clock;
    private final SecureRandom random = new SecureRandom();
    private final Map<String, State> rooms = new HashMap<>();
    private final Map<String, Window> attempts = new HashMap<>();
    private Window global = new Window(Instant.MIN);
    private Instant nextCleanup = Instant.MIN;

    RoomStore() { this(Clock.systemUTC()); }
    RoomStore(Clock clock) { this.clock = clock; }

    record Create(String name, String playerName, String password, RoomConnection connection, String avatar, Game game) {
        Create(String name, String playerName, String password, RoomConnection connection) {
            this(name, playerName, password, connection, null, null);
        }
        @Override public String toString() { return "Create[redacted]"; }
    }
    record Join(String playerName, String password, String avatar, Game game) {
        Join(String playerName, String password) { this(playerName, password, null, null); }
        @Override public String toString() { return "Join[redacted]"; }
    }
    record Game(String name, String version, String state, RoomPackShare pack, String server) {
        Game validated() {
            String n = profileText(name, "game name"), v = profileText(version, "game version");
            String s = state == null ? "stopped" : state;
            if (!Set.of("stopped", "launching", "running", "failed").contains(s))
                throw new ApiException(400, "Invalid game state");
            return new Game(n, v, s, pack == null ? null : pack.validated(), serverAddress(server));
        }
    }
    record Presence(String avatar, Game game) {
        @Override public String toString() { return "Presence[redacted]"; }
    }
    static String serverAddress(String value) {
        if (value == null || value.isEmpty()) return "";
        if (value.length() > 253 || value.chars().anyMatch(ch -> Character.isISOControl(ch) || Character.isWhitespace(ch)))
            throw new ApiException(400, "Invalid game server");
        String host = value;
        int port = 25565;
        int split = value.lastIndexOf(':');
        if (split > 0 && value.indexOf(':') == split) {
            host = value.substring(0, split);
            try { port = Integer.parseInt(value.substring(split + 1)); }
            catch (NumberFormatException exception) { throw new ApiException(400, "Invalid game server"); }
        }
        if (host.isEmpty() || host.length() > 253 || port < 1 || port > 65535
                || !(host.matches("[A-Za-z0-9.-]+") || host.contains(":")))
            throw new ApiException(400, "Invalid game server");
        return value;
    }
    /** Public download descriptor only. Never a local path, token, or uploaded archive. */
    record RoomPackShare(String id, String name, String version, String minecraft, String fileName,
                         String downloadUrl, String sha256, String shareUrl, String sharePath) {
        RoomPackShare validated() {
            String packId = id == null ? "" : id;
            if (!packId.matches("[a-z0-9][a-z0-9_-]{0,63}")) throw new ApiException(400, "Invalid pack id");
            String packName = shareText(name, 80, "pack name");
            String packVersion = shareText(version, 80, "pack version");
            String mc = minecraft == null ? "" : minecraft;
            if (mc.length() > 40 || mc.chars().anyMatch(Character::isISOControl))
                throw new ApiException(400, "Invalid pack minecraft");
            String file = fileName == null ? "" : fileName;
            if (!file.matches("[^\\\\/:*?\\\"<>|\\p{Cntrl}]+\\.(?i:zip|mrpack)") || file.endsWith(" ") || file.startsWith("."))
                throw new ApiException(400, "Invalid pack file name");
            String download = httpUrl(downloadUrl, "pack download");
            String hash = sha256 == null ? "" : sha256;
            if (!hash.matches("(?:[a-fA-F0-9]{64})?")) throw new ApiException(400, "Invalid pack sha256");
            String share = httpUrl(shareUrl, "pack share");
            String path = sharePath == null ? "" : sharePath;
            if (path.length() > 512 || path.chars().anyMatch(Character::isISOControl) || path.contains("..") || path.contains("\\"))
                throw new ApiException(400, "Invalid pack share path");
            if (download.isEmpty() && share.isEmpty()) throw new ApiException(400, "Pack needs a public download or share URL");
            return new RoomPackShare(packId, packName, packVersion, mc, file, download, hash.toLowerCase(Locale.ROOT), share, path);
        }
        private static String shareText(String value, int max, String field) {
            if (value == null || value.isBlank() || value.length() > max || value.chars().anyMatch(Character::isISOControl))
                throw new ApiException(400, "Invalid " + field);
            return value;
        }
        private static String httpUrl(String value, String field) {
            if (value == null || value.isEmpty()) return "";
            if (value.length() > 2048) throw new ApiException(400, "Invalid " + field);
            try {
                java.net.URI uri = java.net.URI.create(value);
                if (!("https".equalsIgnoreCase(uri.getScheme()) || "http".equalsIgnoreCase(uri.getScheme()))
                        || uri.getHost() == null || uri.getRawUserInfo() != null || uri.getRawFragment() != null
                        || uri.getPort() == 0 || uri.getPort() > 65535)
                    throw new IllegalArgumentException();
                return uri.toString();
            } catch (IllegalArgumentException exception) {
                throw new ApiException(400, "Invalid " + field);
            }
        }
    }
    record Routes(List<RoomRoute> routes) {}
    record Member(String id, String name, boolean isHost, String avatar, Game game) {}
    record Room(String code, String name, String hostId, List<Member> members,
                RoomConnection connection, Instant expiresAt, List<RoomRoute> routes) {}
    record Session(Room room, String memberId, String token) {
        @Override public String toString() { return "Session[redacted]"; }
    }

    /** Socket IP only: forwarded headers are intentionally not trusted. Called before JSON parsing. */
    synchronized void rateLimit(String ip) {
        Instant now = clock.instant();
        attempts.entrySet().removeIf(e -> !now.isBefore(e.getValue().start.plusSeconds(60)));
        if (!now.isBefore(global.start.plusSeconds(60))) global = new Window(now);
        Window window = attempts.get(ip);
        if (global.count >= 120 || (window != null && window.count >= 20)
                || (window == null && attempts.size() >= 4096))
            throw new ApiException(429, "Too many room attempts; retry in 60 seconds");
        if (window == null) { window = new Window(now); attempts.put(ip, window); }
        window.count++;
        global.count++;
    }

    synchronized Session create(Create request) {
        if (request == null) throw new ApiException(400, "Room body required");
        String name = text(request.name(), 80, "name");
        String player = text(request.playerName(), 32, "playerName");
        String password = password(request.password());
        RoomConnection connection = request.connection() == null ? null : request.connection().validated();
        String avatar = avatar(request.avatar());
        Game game = game(request.game());
        Instant now = clock.instant();
        cleanup(now, true);
        if (rooms.size() >= 1000) throw new ApiException(503, "Room capacity reached");
        String code;
        do {
            StringBuilder value = new StringBuilder(8);
            for (int i = 0; i < 8; i++) value.append(ALPHABET.charAt(random.nextInt(ALPHABET.length())));
            code = value.toString();
        } while (rooms.containsKey(code));
        String token = randomText(32);
        Participant host = new Participant(player, digest(token), now, avatar, game);
        State state = new State(code, name, host.id, connection);
        if (password != null) {
            state.salt = new byte[16]; random.nextBytes(state.salt);
            state.passwordHash = hash(password, state.salt);
        }
        state.members.put(host.id, host);
        rooms.put(code, state);
        return new Session(snapshot(state), host.id, token);
    }

    synchronized Session join(String code, Join request) {
        if (request == null) throw new ApiException(400, "Join body required");
        String player = text(request.playerName(), 32, "playerName");
        String password = password(request.password());
        String avatar = avatar(request.avatar());
        Game game = game(request.game());
        State state = find(code);
        if (state.passwordHash != null && !MessageDigest.isEqual(state.passwordHash,
                hash(password == null ? "" : password, state.salt)))
            throw new ApiException(403, "Incorrect room password");
        if (state.members.size() >= 16) throw new ApiException(409, "Room is full");
        String token = randomText(32);
        Participant member = new Participant(player, digest(token), clock.instant(), avatar, game);
        state.members.put(member.id, member);
        return new Session(snapshot(state), member.id, token);
    }

    synchronized Room get(String code, String token) {
        State state = find(code);
        authenticate(state, token);
        return snapshot(state);
    }

    synchronized Room heartbeat(String code, String id, String token) {
        return heartbeat(code, id, token, null);
    }

    synchronized Room heartbeat(String code, String id, String token, Presence presence) {
        State state = find(code);
        Participant member = authenticate(state, token);
        if (!member.id.equals(id)) throw new ApiException(403, "Member token does not match");
        // Validate the whole profile before either changing it or renewing the lease.
        String avatar = presence == null || presence.avatar() == null ? member.avatar : avatar(presence.avatar());
        Game game = presence == null || presence.game() == null ? member.game : game(presence.game());
        member.avatar = avatar;
        member.game = game;
        member.lastSeen = clock.instant();
        return snapshot(state);
    }

    synchronized Room publish(String code, String token, RoomConnection connection) {
        State state = find(code);
        if (!authenticate(state, token).id.equals(state.hostId)) throw new ApiException(403, "Host token required");
        if (connection == null) throw new ApiException(400, "Connection required");
        state.routes = List.of(RoomRoute.legacy(connection.validated()));
        return snapshot(state);
    }

    synchronized Room publishRoutes(String code, String token, Routes request) {
        State state = find(code);
        if (!authenticate(state, token).id.equals(state.hostId)) throw new ApiException(403, "Host token required");
        if (request == null || request.routes() == null || request.routes().size() > 12)
            throw new ApiException(400, "Invalid routes (maximum 12)");
        List<RoomRoute> validated = new ArrayList<>();
        Set<String> ids = new HashSet<>();
        for (RoomRoute route : request.routes()) {
            if (route == null || !ids.add(route.id())) throw new ApiException(400, "Invalid or duplicate route");
            validated.add(route.validated());
        }
        state.routes = List.copyOf(validated);
        return snapshot(state);
    }

    synchronized void leave(String code, String id, String token) {
        State state = find(code);
        Participant member = authenticate(state, token);
        if (!member.id.equals(id)) throw new ApiException(403, "Member token does not match");
        if (id.equals(state.hostId)) rooms.remove(state.code);
        else state.members.remove(id);
    }

    private State find(String code) {
        Instant now = clock.instant();
        cleanup(now, false);
        State state = code != null && code.matches("[A-HJ-NP-Z2-9]{8}") ? rooms.get(code) : null;
        if (state != null && !expire(state, now)) { rooms.remove(code); state = null; }
        if (state == null) throw new ApiException(404, "Room not found or expired");
        return state;
    }

    private void cleanup(Instant now, boolean force) {
        if (!force && now.isBefore(nextCleanup)) return;
        rooms.values().removeIf(state -> !expire(state, now));
        nextCleanup = now.plusSeconds(5);
    }

    private boolean expire(State state, Instant now) {
        if (!now.isBefore(state.members.get(state.hostId).lastSeen.plus(TTL))) return false;
        state.members.values().removeIf(member -> !member.id.equals(state.hostId) && !now.isBefore(member.lastSeen.plus(TTL)));
        return true;
    }

    private Participant authenticate(State state, String token) {
        if (token != null && token.matches("[A-Za-z0-9_-]{43}")) {
            byte[] hash = digest(token);
            for (Participant member : state.members.values())
                if (MessageDigest.isEqual(member.tokenHash, hash)) return member;
        }
        throw new ApiException(401, "Invalid or missing room bearer token");
    }

    private Room snapshot(State state) {
        return new Room(state.code, state.name, state.hostId,
                state.members.values().stream().map(m -> new Member(m.id, m.name, m.id.equals(state.hostId), m.avatar, m.game)).toList(),
                state.routes.stream().filter(RoomRoute::enabled).map(RoomRoute::connection).findFirst().orElse(null),
                state.members.get(state.hostId).lastSeen.plus(TTL), state.routes);
    }

    static String text(String value, int max, String field) {
        if (value == null || value.isBlank() || value.length() > max || value.chars().anyMatch(Character::isISOControl))
            throw new ApiException(400, "Invalid " + field);
        return value.strip();
    }

    private static String profileText(String value, String field) {
        if (value == null) return "";
        if (value.length() > 80 || value.chars().anyMatch(Character::isISOControl))
            throw new ApiException(400, "Invalid " + field);
        return value;
    }

    private static Game game(Game value) {
        return (value == null ? new Game("", "", "stopped") : value).validated();
    }

    private static String avatar(String value) {
        if (value == null || value.isEmpty()) return "";
        if (value.length() > 8192) throw new ApiException(400, "Invalid avatar");
        byte[] png;
        try { png = Base64.getDecoder().decode(value); }
        catch (IllegalArgumentException exception) { throw new ApiException(400, "Invalid avatar"); }
        byte[] signature = {(byte) 137, 80, 78, 71, 13, 10, 26, 10};
        if (png.length < 33 || png.length > 6144 || !Arrays.equals(signature, Arrays.copyOf(png, 8)))
            throw new ApiException(400, "Invalid avatar PNG");
        ByteBuffer header = ByteBuffer.wrap(png);
        if (header.getInt(8) != 13 || header.getInt(12) != 0x49484452
                || header.getInt(16) < 1 || header.getInt(16) > 64
                || header.getInt(20) < 1 || header.getInt(20) > 64)
            throw new ApiException(400, "Invalid avatar PNG dimensions");
        // Bounded structural validation only; never inflate IDAT or invoke an image decoder.
        int depth = png[24] & 255, color = png[25] & 255;
        boolean validDepth = switch (color) {
            case 0 -> Set.of(1, 2, 4, 8, 16).contains(depth);
            case 2, 4, 6 -> depth == 8 || depth == 16;
            case 3 -> Set.of(1, 2, 4, 8).contains(depth);
            default -> false;
        };
        if (!validDepth || png[26] != 0 || png[27] != 0 || (png[28] != 0 && png[28] != 1))
            throw new ApiException(400, "Invalid avatar PNG header");
        boolean imageData = false, end = false;
        for (int offset = 8; offset < png.length;) {
            if (png.length - offset < 12) throw new ApiException(400, "Truncated avatar PNG");
            int length = header.getInt(offset), type = header.getInt(offset + 4);
            if (length < 0 || length > png.length - offset - 12 || offset != 8 && type == 0x49484452)
                throw new ApiException(400, "Invalid avatar PNG chunk");
            for (int i = offset + 4; i < offset + 8; i++)
                if (!(png[i] >= 'A' && png[i] <= 'Z' || png[i] >= 'a' && png[i] <= 'z'))
                    throw new ApiException(400, "Invalid avatar PNG chunk");
            var crc = new java.util.zip.CRC32();
            crc.update(png, offset + 4, length + 4);
            if ((int) crc.getValue() != header.getInt(offset + 8 + length))
                throw new ApiException(400, "Invalid avatar PNG checksum");
            if (type == 0x49444154 && length > 0) imageData = true;
            offset += length + 12;
            if (type == 0x49454e44) {
                if (length != 0 || offset != png.length) throw new ApiException(400, "Invalid avatar PNG end");
                end = true;
            }
        }
        if (!imageData || !end) throw new ApiException(400, "Incomplete avatar PNG");
        return value;
    }

    private static String password(String value) {
        if (value != null && (value.length() > 128 || value.chars().anyMatch(Character::isISOControl)))
            throw new ApiException(400, "Invalid password (maximum 128 characters)");
        return value == null || value.isEmpty() ? null : value;
    }

    private String randomText(int bytes) {
        byte[] value = new byte[bytes]; random.nextBytes(value);
        return Base64.getUrlEncoder().withoutPadding().encodeToString(value);
    }

    private static byte[] digest(String value) {
        try { return MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.US_ASCII)); }
        catch (GeneralSecurityException exception) { throw new IllegalStateException("SHA-256 unavailable"); }
    }

    private static byte[] hash(String password, byte[] salt) {
        PBEKeySpec spec = new PBEKeySpec(password.toCharArray(), salt, 120_000, 256);
        try { return SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).getEncoded(); }
        catch (GeneralSecurityException exception) { throw new IllegalStateException("PBKDF2 unavailable"); }
        finally { spec.clearPassword(); }
    }

    private static final class Participant {
        final String id = UUID.randomUUID().toString();
        final String name;
        final byte[] tokenHash;
        Instant lastSeen;
        String avatar;
        Game game;
        Participant(String name, byte[] tokenHash, Instant now, String avatar, Game game) {
            this.name = name; this.tokenHash = tokenHash; lastSeen = now; this.avatar = avatar; this.game = game;
        }
    }
    private static final class State {
        final String code, name, hostId;
        final Map<String, Participant> members = new LinkedHashMap<>();
        List<RoomRoute> routes;
        byte[] salt, passwordHash;
        State(String code, String name, String hostId, RoomConnection connection) {
            this.code = code; this.name = name; this.hostId = hostId;
            this.routes = connection == null ? List.of() : List.of(RoomRoute.legacy(connection));
        }
    }
    private static final class Window {
        final Instant start;
        int count;
        Window(Instant start) { this.start = start; }
    }
}
