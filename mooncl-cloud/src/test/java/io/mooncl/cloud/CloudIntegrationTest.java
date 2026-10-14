package io.mooncl.cloud;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.ByteArrayInputStream;
import java.net.URI;
import java.net.http.*;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.Map;
import java.util.zip.GZIPInputStream;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;
import static org.assertj.core.api.Assertions.*;

@SpringBootTest(webEnvironment = SpringBootTest.WebEnvironment.RANDOM_PORT,
        properties = {"mooncl.admin-port=0", "mooncl.admin-token=integration_test_token_0123456789abcdef"})
class CloudIntegrationTest {
    private static final String TOKEN = "Bearer integration_test_token_0123456789abcdef";
    private static final Path DATA = temporary();
    private static Path temporary() {
        try {
            String scratch = System.getenv("PI_SCRATCH_DIR");
            return Files.createTempDirectory(Path.of(scratch == null ? System.getProperty("java.io.tmpdir") : scratch), "mooncl-integration-");
        } catch (Exception exception) { throw new IllegalStateException(exception); }
    }
    @DynamicPropertySource
    static void configuration(DynamicPropertyRegistry registry) { registry.add("mooncl.data-dir", DATA::toString); }
    @Autowired Ports ports;
    @Autowired ObjectMapper mapper;
    private final HttpClient client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build();

    private HttpResponse<byte[]> request(int port, String path, String method, String body, Map<String, String> headers) throws Exception {
        HttpRequest.Builder request = HttpRequest.newBuilder(URI.create("http://127.0.0.1:" + port + path)).timeout(Duration.ofSeconds(10));
        headers.forEach(request::header);
        request.method(method, body == null ? HttpRequest.BodyPublishers.noBody() : HttpRequest.BodyPublishers.ofString(body));
        return client.send(request.build(), HttpResponse.BodyHandlers.ofByteArray());
    }
    private Map<String, String> auth(String revision) {
        return Map.of("Authorization", TOKEN, "If-Match", revision, "Content-Type", "application/json");
    }
    private String payload(String id, boolean enabled) throws Exception {
        return mapper.writeValueAsString(Map.ofEntries(
                Map.entry("id", id), Map.entry("name", "月光整合包"), Map.entry("version", "1.0.0"),
                Map.entry("description", "网络测试简介".repeat(100)), Map.entry("minecraft", "1.21.1"),
                Map.entry("coverUrl", "https://cdn.example.com/cover.webp"), Map.entry("downloadUrl", "https://cdn.example.com/pack.mrpack"),
                Map.entry("backupUrls", java.util.List.of("https://backup.example.com/pack.mrpack")),
                Map.entry("fileName", "月光.mrpack"), Map.entry("enabled", enabled), Map.entry("sortOrder", 0)));
    }
    private String etag(HttpResponse<?> response) { return response.headers().firstValue("ETag").orElseThrow(); }
    private JsonNode json(HttpResponse<byte[]> response) throws Exception { return mapper.readTree(response.body()); }

    @Test
    void portIsolationAndAuthentication() throws Exception {
        assertThat(ports.publicPort()).isNotEqualTo(ports.adminPort());
        for (String path : java.util.List.of("/", "/app.js", "/api/admin/packs", "/api/admin/status")) {
            assertThat(request(ports.publicPort(), path, "GET", null, Map.of("Authorization", TOKEN,
                    "X-Forwarded-Port", String.valueOf(ports.adminPort()))).statusCode()).isEqualTo(404);
        }
        assertThat(request(ports.publicPort(), "/catalog.json", "POST", "{}", Map.of()).statusCode()).isEqualTo(405);
        assertThat(request(ports.adminPort(), "/api/admin/packs", "GET", null, Map.of()).statusCode()).isEqualTo(401);
        assertThat(request(ports.adminPort(), "/api/admin/packs", "GET", null, Map.of("Authorization", "Bearer wrong")).statusCode()).isEqualTo(401);
        assertThat(request(ports.adminPort(), "/api/admin/packs", "GET", null, Map.of("Authorization", TOKEN)).statusCode()).isEqualTo(200);
        assertThat(request(ports.adminPort(), "/catalog.json", "GET", null, Map.of()).statusCode()).isEqualTo(404);
        for (String path : java.util.List.of("/", "/app.js", "/app.css")) {
            var asset = request(ports.adminPort(), path, "GET", null, Map.of("Accept-Encoding", "gzip"));
            assertThat(asset.statusCode()).isEqualTo(200);
            assertThat(asset.headers().firstValue("Content-Security-Policy")).isPresent();
            assertThat(asset.headers().firstValue("Content-Encoding")).contains("gzip");
        }
    }

    @Test
    void fullLifecycleCachingAndDownloadMirrors() throws Exception {
        var initial = request(ports.adminPort(), "/api/admin/packs", "GET", null, Map.of("Authorization", TOKEN));
        String version = etag(initial);
        var created = request(ports.adminPort(), "/api/admin/packs", "POST", payload("moon-test", true), auth(version));
        assertThat(created.statusCode()).isEqualTo(200);
        String next = etag(created);
        assertThat(next).isNotEqualTo(version);
        assertThat(request(ports.adminPort(), "/api/admin/packs", "POST", payload("draft-test", false), auth(version)).statusCode()).isEqualTo(412);
        assertThat(request(ports.adminPort(), "/api/admin/packs", "POST", payload("draft-test", false),
                Map.of("Authorization", TOKEN, "Content-Type", "application/json")).statusCode()).isEqualTo(428);
        var draft = request(ports.adminPort(), "/api/admin/packs", "POST", payload("draft-test", false), auth(next));
        assertThat(draft.statusCode()).isEqualTo(200);
        next = etag(draft);
        var catalog = request(ports.publicPort(), "/catalog.json", "GET", null, Map.of());
        assertThat(json(catalog).get("packs")).hasSize(1);
        assertThat(json(catalog).at("/packs/0/icon").asText()).isEqualTo("https://cdn.example.com/cover.webp");
        var compressed = request(ports.publicPort(), "/catalog.json", "GET", null, Map.of("Accept-Encoding", "gzip"));
        assertThat(compressed.headers().firstValue("Content-Encoding")).contains("gzip");
        assertThat(compressed.body().length).isLessThan(catalog.body().length / 2);
        try (GZIPInputStream gzip = new GZIPInputStream(new ByteArrayInputStream(compressed.body()))) {
            assertThat(gzip.readAllBytes()).isEqualTo(catalog.body());
        }
        assertThat(etag(compressed)).isNotEqualTo(etag(catalog));
        var unchanged = request(ports.publicPort(), "/catalog.json", "GET", null, Map.of("If-None-Match", "W/" + etag(catalog)));
        assertThat(unchanged.statusCode()).isEqualTo(304);
        assertThat(unchanged.body()).isEmpty();
        var head = request(ports.publicPort(), "/catalog.json", "HEAD", null, Map.of());
        assertThat(head.body()).isEmpty();
        assertThat(head.headers().firstValueAsLong("Content-Length").orElseThrow()).isEqualTo(catalog.body().length);
        assertThat(request(ports.publicPort(), "/catalog.json", "GET", null, Map.of("Accept-Encoding", "gzip;q=0, *;q=1"))
                .headers().firstValue("Content-Encoding")).isEmpty();
        assertThat(request(ports.publicPort(), "/api/v1/packs/draft-test", "GET", null, Map.of()).statusCode()).isEqualTo(404);
        assertThat(request(ports.publicPort(), "/api/v1/packs/moon-test/download", "GET", null, Map.of()).headers().firstValue("Location"))
                .contains("https://cdn.example.com/pack.mrpack");
        var mirror = request(ports.publicPort(), "/api/v1/packs/moon-test/download?mirror=1", "GET", null, Map.of());
        assertThat(mirror.statusCode()).isEqualTo(302);
        assertThat(mirror.headers().firstValue("Location")).contains("https://backup.example.com/pack.mrpack");
        assertThat(request(ports.publicPort(), "/api/v1/packs/moon-test/download?mirror=2", "GET", null, Map.of()).statusCode()).isEqualTo(400);
        var updated = request(ports.adminPort(), "/api/admin/packs/moon-test", "PUT", payload("moon-test", false), auth(next));
        assertThat(updated.statusCode()).isEqualTo(200);
        assertThat(json(request(ports.publicPort(), "/catalog.json", "GET", null, Map.of())).get("packs")).isEmpty();
        assertThat(request(ports.publicPort(), "/api/v1/packs/moon-test/download", "GET", null, Map.of()).statusCode()).isEqualTo(404);
        var deleted = request(ports.adminPort(), "/api/admin/packs/moon-test", "DELETE", null, auth(etag(updated)));
        assertThat(deleted.statusCode()).isEqualTo(200);
        assertThat(Files.readString(DATA.resolve("catalog.json"))).doesNotContain("moon-test");
        assertThat(request(ports.adminPort(), "/api/admin/packs/draft-test", "DELETE", null, auth(etag(deleted))).statusCode()).isEqualTo(200);
    }

    @Test
    void rejectsUnsafeAndOversizedInput() throws Exception {
        String revision = etag(request(ports.adminPort(), "/api/admin/packs", "GET", null, Map.of("Authorization", TOKEN)));
        String valid = payload("bad-test", true);
        for (String invalid : java.util.List.of(valid.replace("https://cdn.example.com/pack.mrpack", "file:///etc/passwd"),
                valid.replace("月光.mrpack", "../pack.zip"), valid.replace("https://cdn.example.com/pack.mrpack", "https://user:pass@example.com/a"),
                valid.replace("bad-test", "../bad"), valid.replace("1.0.0", ""), valid.replace("\"enabled\":true", "\"enabled\":true,\"command\":\"bad\""))) {
            assertThat(request(ports.adminPort(), "/api/admin/packs", "POST", invalid, auth(revision)).statusCode()).isEqualTo(400);
        }
        assertThat(request(ports.adminPort(), "/api/admin/packs", "POST", "x".repeat(131073), auth(revision)).statusCode()).isEqualTo(413);
        // Unknown-length/chunked requests must be bounded too, not only Content-Length requests.
        var chunked = HttpRequest.newBuilder(URI.create("http://127.0.0.1:" + ports.adminPort() + "/api/admin/packs"))
                .header("Authorization", TOKEN).header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofInputStream(() -> new ByteArrayInputStream(new byte[131073]))).build();
        assertThat(client.send(chunked, HttpResponse.BodyHandlers.discarding()).statusCode()).isEqualTo(413);
    }
}
