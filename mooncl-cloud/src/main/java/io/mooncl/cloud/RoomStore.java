package io.mooncl.cloud;

import java.nio.charset.StandardCharsets;
import java.security.*;
import java.time.*;
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

    record Create(String name, String playerName, String password, RoomConnection connection) {
        @Override public String toString() { return "Create[redacted]"; }
    }
    record Join(String playerName, String password) {
        @Override public String toString() { return "Join[redacted]"; }
    }
    record Member(String id, String name, boolean isHost) {}
    record Room(String code, String name, String hostId, List<Member> members,
                RoomConnection connection, Instant expiresAt) {}
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
        Participant host = new Participant(player, digest(token), now);
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
        State state = find(code);
        if (state.passwordHash != null && !MessageDigest.isEqual(state.passwordHash,
                hash(password == null ? "" : password, state.salt)))
            throw new ApiException(403, "Incorrect room password");
        if (state.members.size() >= 16) throw new ApiException(409, "Room is full");
        String token = randomText(32);
        Participant member = new Participant(player, digest(token), clock.instant());
        state.members.put(member.id, member);
        return new Session(snapshot(state), member.id, token);
    }

    synchronized Room get(String code, String token) {
        State state = find(code);
        authenticate(state, token);
        return snapshot(state);
    }

    synchronized Room heartbeat(String code, String id, String token) {
        State state = find(code);
        Participant member = authenticate(state, token);
        if (!member.id.equals(id)) throw new ApiException(403, "Member token does not match");
        member.lastSeen = clock.instant();
        return snapshot(state);
    }

    synchronized Room publish(String code, String token, RoomConnection connection) {
        State state = find(code);
        if (!authenticate(state, token).id.equals(state.hostId)) throw new ApiException(403, "Host token required");
        if (connection == null) throw new ApiException(400, "Connection required");
        state.connection = connection.validated();
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
                state.members.values().stream().map(m -> new Member(m.id, m.name, m.id.equals(state.hostId))).toList(),
                state.connection, state.members.get(state.hostId).lastSeen.plus(TTL));
    }

    static String text(String value, int max, String field) {
        if (value == null || value.isBlank() || value.length() > max || value.chars().anyMatch(Character::isISOControl))
            throw new ApiException(400, "Invalid " + field);
        return value.strip();
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
        Participant(String name, byte[] tokenHash, Instant now) { this.name = name; this.tokenHash = tokenHash; lastSeen = now; }
    }
    private static final class State {
        final String code, name, hostId;
        final Map<String, Participant> members = new LinkedHashMap<>();
        RoomConnection connection;
        byte[] salt, passwordHash;
        State(String code, String name, String hostId, RoomConnection connection) {
            this.code = code; this.name = name; this.hostId = hostId; this.connection = connection;
        }
    }
    private static final class Window {
        final Instant start;
        int count;
        Window(Instant start) { this.start = start; }
    }
}
