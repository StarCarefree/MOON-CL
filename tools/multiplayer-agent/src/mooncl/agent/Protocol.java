package mooncl.agent;

import java.io.*;
import java.net.*;
import java.nio.*;
import java.nio.charset.*;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;

final class Protocol {
    static final int MAX_LINE = 16 * 1024;
    static final long JOB_TIMEOUT_MS = 15000;
    static final class State {
        final String state, address, message;
        State(String state, String address, String message) { this.state = state; this.address = address; this.message = message; }
    }
    static String b64(String s) { return Base64.getEncoder().encodeToString(s.getBytes(StandardCharsets.UTF_8)); }
    static String utf8(byte[] bytes) throws CharacterCodingException {
        return StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
    }
    static String decode(String s) throws IOException {
        try { return utf8(Base64.getDecoder().decode(s)); }
        catch (IllegalArgumentException e) { throw new IOException("Invalid base64", e); }
    }
    static int port(String s) {
        if (s == null || !s.matches("[0-9]{1,5}")) throw new IllegalArgumentException("Invalid link port");
        int n = Integer.parseInt(s); if (n < 1 || n > 65535) throw new IllegalArgumentException("Invalid port range"); return n;
    }
    static String address(String value) {
        if (value == null || value.isEmpty() || value.length() > 1024 || !value.equals(value.trim()))
            throw new IllegalArgumentException("Empty/oversize address or surrounding whitespace");
        for (int i = 0; i < value.length(); i++) if (Character.isWhitespace(value.charAt(i)) || Character.isISOControl(value.charAt(i)))
            throw new IllegalArgumentException("Whitespace/control character in address");
        String host = value, p = null;
        if (value.startsWith("[")) {
            int close = value.indexOf(']'); if (close < 0) throw new IllegalArgumentException("Invalid IPv6 brackets");
            host = value.substring(1, close);
            String tail = value.substring(close + 1);
            if (!tail.isEmpty()) { if (!tail.startsWith(":")) throw new IllegalArgumentException("Invalid IPv6 suffix"); p = tail.substring(1); }
            ipv6(host);
        } else {
            int colon = value.indexOf(':');
            if (colon >= 0 && colon != value.lastIndexOf(':')) { ipv6(value); return value; }
            if (colon >= 0) { host = value.substring(0, colon); p = value.substring(colon + 1); }
            if (host.matches("[0-9.]+")) {
                String[] pieces = host.split("\\.", -1);
                if (pieces.length != 4) throw new IllegalArgumentException("Invalid IPv4");
                for (String piece : pieces) if (!piece.matches("[0-9]{1,3}") || Integer.parseInt(piece) > 255)
                    throw new IllegalArgumentException("Invalid IPv4");
            } else {
                String ascii = IDN.toASCII(host, IDN.USE_STD3_ASCII_RULES);
                if (ascii.endsWith(".")) ascii = ascii.substring(0, ascii.length() - 1);
                if (ascii.isEmpty() || ascii.length() > 253) throw new IllegalArgumentException("Invalid host");
                for (String label : ascii.split("\\.", -1)) if (!label.matches("[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?"))
                    throw new IllegalArgumentException("Invalid host label");
            }
        }
        if (p != null) port(p);
        return value;
    }
    static void ipv6(String host) {
        if (!host.contains(":") || !host.matches("[0-9a-fA-F:.]+")) throw new IllegalArgumentException("Invalid IPv6 literal");
        try { InetAddress.getByName(host); } // Colon + numeric-only alphabet: no DNS lookup.
        catch (UnknownHostException e) { throw new IllegalArgumentException("Invalid IPv6 literal"); }
    }
    /** A timeout preserves a partial line; a partial line has its own hard 5-second deadline. */
    static final class Lines {
        final InputStream in; final ByteArrayOutputStream partial = new ByteArrayOutputStream(); long since;
        Lines(InputStream in) { this.in = in; }
        String read() throws IOException {
            long slice = now();
            while (true) {
                if (since != 0 && now() - since > 5000) throw new IOException("Partial line timeout");
                if (now() - slice >= 100) throw new SocketTimeoutException("Read timeslice; partial line preserved");
                int b = in.read();
                if (b < 0) throw new EOFException("Link closed");
                if (b == '\n') {
                    byte[] bytes = partial.toByteArray(); partial.reset(); since = 0;
                    int len = bytes.length; if (len > 0 && bytes[len - 1] == '\r') len--;
                    return utf8(Arrays.copyOf(bytes, len));
                }
                if (since == 0) since = now();
                if (partial.size() >= MAX_LINE) throw new IOException("Line exceeds 16 KiB");
                partial.write(b);
            }
        }
    }
    static long now() { return System.nanoTime() / 1000000L; }
    static String message(Throwable t) {
        while (t instanceof java.lang.reflect.InvocationTargetException && t.getCause() != null) t = t.getCause();
        return t.getClass().getSimpleName() + ": " + String.valueOf(t.getMessage());
    }
    static State failure(Throwable t) {
        return new State(t instanceof MinecraftBridge.Unsupported ? "unsupported" : "error", "", message(t));
    }
    final int port; final String token; final MinecraftBridge.Provider provider; final long timeout;
    volatile boolean closed; volatile long writingSince; Socket socket; OutputStream output;
    MinecraftBridge bridge; volatile Job active; Command queued; long nextPoll;
    final long discoveryStarted = now(); long nextStalledReport;
    final Set<String> ids = new LinkedHashSet<String>();
    Protocol(int port, String token, MinecraftBridge.Provider provider) { this(port, token, provider, JOB_TIMEOUT_MS); }
    Protocol(int port, String token, MinecraftBridge.Provider provider, long timeout) {
        if (token == null || !token.matches("[0-9a-fA-F]{64}")) throw new IllegalArgumentException("Link token must be 64 hex characters");
        if (port < 1 || port > 65535) throw new IllegalArgumentException("Invalid port");
        this.port = port; this.token = token; this.provider = provider; this.timeout = timeout;
    }
    void run() {
        try (Socket s = new Socket()) {
            socket = s; s.connect(new InetSocketAddress(InetAddress.getByAddress(new byte[] {127, 0, 0, 1}), port), 5000);
            s.setTcpNoDelay(true); s.setSoTimeout(100); output = s.getOutputStream();
            Thread watchdog = new Thread(new Runnable() { public void run() {
                while (!closed) {
                    if (writingSince != 0 && now() - writingSince > 5000) { close(); return; }
                    try { Thread.sleep(100); } catch (InterruptedException e) { return; }
                }
            } }, "MoonCL-link-watchdog"); watchdog.setDaemon(true); watchdog.start();
            write("HELLO\t1\t" + token);
            Lines lines = new Lines(s.getInputStream()); String reply = null; long deadline = now() + 5000;
            while (reply == null && now() < deadline) try { reply = lines.read(); } catch (SocketTimeoutException ignored) { }
            if (!"OK".equals(reply)) throw new IOException("Launcher authentication rejected/timed out");
            state("", new State("waiting", "", "Waiting for initialized Minecraft singleton"));
            while (!closed) {
                tick();
                try { command(lines.read()); } catch (SocketTimeoutException ignored) { }
            }
        } catch (Throwable t) {
            // No token, environment dump, or game-start failure propagation.
            System.err.println("[MoonCL agent] Link stopped: " + message(t));
        } finally { close(); }
    }
    void write(String line) throws IOException {
        byte[] bytes = line.getBytes(StandardCharsets.UTF_8);
        if (bytes.length > MAX_LINE) throw new IOException("Outbound line exceeds 16 KiB");
        writingSince = now();
        try { output.write(bytes); output.write('\n'); output.flush(); } finally { writingSince = 0; }
    }
    void state(String id, State s) throws IOException {
        String ip = s.address.length() > 1024 ? "" : s.address;
        String msg = s.message.length() > 1024 ? s.message.substring(0, 1024) : s.message;
        write("STATE\t" + id + "\t" + s.state + "\t" + b64(ip) + "\t" + b64(msg));
    }
    static final class Command {
        final String id, address; final long created = now();
        Command(String id, String address) { this.id = id; this.address = address; }
    }
    void command(String line) throws IOException {
        String[] p = line.split("\t", -1); String id = p.length > 1 ? p[1] : "";
        if (!id.matches("[A-Za-z0-9_.:-]{1,128}")) { state("", new State("error", "", "Invalid requestId")); return; }
        try {
            Command c;
            if (p.length == 3 && p[0].equals("CONNECT")) c = new Command(id, address(decode(p[2])));
            else if (p.length == 2 && p[0].equals("DISCONNECT")) c = new Command(id, null);
            else throw new IllegalArgumentException("Unknown command or field count");
            if (ids.contains(id)) throw new IllegalArgumentException("Duplicate requestId (recent 256)");
            ids.add(id); if (ids.size() > 256) ids.remove(ids.iterator().next());
            if (queued != null || (active != null && (active.command != null || active.reported))) throw new IllegalArgumentException("Agent busy; retry with a new id after completion");
            queued = c;
        } catch (Exception e) { state(id, new State("error", "", message(e))); }
    }
    static final class Job implements Runnable {
        final MinecraftBridge bridge; final Command command; final long started = now();
        final AtomicInteger phase = new AtomicInteger(); // 0 queued, 1 running, 2 completed, 3 cancelled
        volatile State result; boolean reported;
        Job(MinecraftBridge bridge, Command command) { this.bridge = bridge; this.command = command; }
        public void run() {
            if (!phase.compareAndSet(0, 1)) { phase.compareAndSet(3, 2); return; }
            try { result = command == null ? bridge.observe() : command.address == null ? bridge.disconnect() : bridge.connect(command.address); }
            catch (Throwable t) { result = failure(t); }
            finally { phase.set(2); }
        }
    }
    void tick() throws IOException {
        long n = now();
        if (active != null) {
            int phase = active.phase.get();
            if (phase == 2) {
                if (!active.reported) state(active.command == null ? "" : active.command.id, active.result);
                active = null;
            } else if (!active.reported && n - active.started >= timeout) {
                active.phase.compareAndSet(0, 3); // A timed-out queued runnable must never execute later.
                state(active.command == null ? "" : active.command.id,
                        new State("error", "", "Minecraft main-thread task timed out; running tasks cannot be interrupted"));
                active.reported = true;
            }
        }
        if (queued != null && n - queued.created >= timeout) {
            state(queued.id, new State("error", "", "Command queue timeout")); queued = null;
        }
        if (active != null) {
            // Keep even cancelled runnables until drained: a stalled executor must not accumulate jobs forever.
            if (active.reported && n >= nextStalledReport) {
                state("", new State("error", "", "Minecraft executor still stalled; no further game tasks submitted"));
                nextStalledReport = n + 1000;
            }
            return;
        }
        if (queued == null && n < nextPoll) return;
        if (bridge == null) try { bridge = provider.discover(); }
        catch (Throwable t) {
            state(queued == null ? "" : queued.id, failure(t)); queued = null; nextPoll = n + 1000; return;
        }
        if (bridge == null) {
            String status = queued != null ? "error" : n - discoveryStarted >= 60000 ? "unsupported" : "waiting";
            state(queued == null ? "" : queued.id, new State(status, "", "Known initialized Minecraft singleton not found; check mappings/version. Command not retained"));
            queued = null; nextPoll = n + 1000; return;
        }
        // Due polls take priority; a stream of commands must not postpone observation forever.
        Command command = queued;
        if (n >= nextPoll) { command = null; nextPoll = n + 1000; }
        else queued = null;
        active = new Job(bridge, command);
        try { bridge.executor.execute(active); }
        catch (Throwable t) { active.result = failure(t); active.phase.set(2); }
    }
    void close() {
        closed = true;
        Job job = active;
        if (job != null) job.phase.compareAndSet(0, 3);
        try { if (socket != null) socket.close(); } catch (IOException ignored) { }
    }
}
