package io.pclmoon.cloud;

import jakarta.servlet.http.HttpServletRequest;
import java.io.IOException;
import java.net.URI;
import java.util.Map;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

@RestController
final class CloudController {
    private static final String PUBLIC_CACHE = "public, max-age=60, stale-while-revalidate=300";
    private final PackStore store;
    private final Ports ports;

    CloudController(PackStore store, Ports ports) { this.store = store; this.ports = ports; }

    @GetMapping({"/index.json", "/catalog.json", "/api/v1/packs"})
    ResponseEntity<byte[]> catalog(HttpServletRequest request) {
        return store.current().catalog().respond(request, PUBLIC_CACHE);
    }

    @GetMapping("/api/v1/packs/{id}")
    ResponseEntity<byte[]> detail(@PathVariable String id, HttpServletRequest request) {
        Blob blob = store.current().details().get(id);
        if (blob == null) throw new ApiException(404, "整合包不存在或未发布");
        return blob.respond(request, PUBLIC_CACHE);
    }

    @GetMapping("/api/v1/packs/{id}/download")
    ResponseEntity<Void> download(@PathVariable String id, @RequestParam(defaultValue = "0") int mirror) {
        Pack pack = store.current().published().get(id);
        if (pack == null) throw new ApiException(404, "整合包不存在或未发布");
        if (mirror < 0 || mirror > pack.backupUrls().size()) throw new ApiException(400, "备用地址序号无效");
        String url = mirror == 0 ? pack.downloadUrl() : pack.backupUrls().get(mirror - 1);
        return ResponseEntity.status(302).header("Cache-Control", "no-store").location(URI.create(url)).build();
    }

    @GetMapping("/api/admin/packs")
    ResponseEntity<PackStore.AdminData> adminCatalog() { return adminResponse(store.current().admin()); }

    @PostMapping("/api/admin/packs")
    ResponseEntity<PackStore.AdminData> create(@RequestBody Pack pack, @RequestHeader(value = "If-Match", required = false) String revision)
            throws IOException { return adminResponse(store.create(pack, revision)); }

    @PutMapping("/api/admin/packs/{id}")
    ResponseEntity<PackStore.AdminData> update(@PathVariable String id, @RequestBody Pack pack,
            @RequestHeader(value = "If-Match", required = false) String revision) throws IOException {
        return adminResponse(store.update(id, pack, revision));
    }

    @DeleteMapping("/api/admin/packs/{id}")
    ResponseEntity<PackStore.AdminData> delete(@PathVariable String id,
            @RequestHeader(value = "If-Match", required = false) String revision) throws IOException {
        return adminResponse(store.delete(id, revision));
    }

    private ResponseEntity<PackStore.AdminData> adminResponse(PackStore.AdminData data) {
        return ResponseEntity.ok().header("Cache-Control", "no-store").eTag("\"" + data.revision() + "\"").body(data);
    }

    @GetMapping("/api/admin/status")
    Map<String, Object> status() {
        PackStore.Snapshot snapshot = store.current();
        return Map.of("publicPort", ports.publicPort(), "adminPort", ports.adminPort(),
                "packCount", snapshot.admin().packs().size(), "publishedCount", snapshot.published().size(),
                "revision", snapshot.admin().revision());
    }

    @GetMapping("/healthz")
    ResponseEntity<Map<String, String>> health() {
        return ResponseEntity.ok().header("Cache-Control", "no-store").body(Map.of("status", "ok"));
    }
}
