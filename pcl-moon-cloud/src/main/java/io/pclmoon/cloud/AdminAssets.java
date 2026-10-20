package io.pclmoon.cloud;

import jakarta.servlet.http.HttpServletRequest;
import java.io.IOException;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import org.springframework.core.io.ClassPathResource;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
final class AdminAssets {
    private final Map<String, Blob> assets = new ConcurrentHashMap<>();

    @GetMapping({"/", "/index.html", "/app.css", "/app.js"})
    ResponseEntity<byte[]> asset(HttpServletRequest request) throws IOException {
        String path = request.getRequestURI().equals("/") ? "/index.html" : request.getRequestURI();
        Blob blob = assets.get(path);
        if (blob == null) {
            synchronized (assets) {
                blob = assets.get(path);
                if (blob == null) {
                    MediaType type = MediaType.parseMediaType(switch (path) {
                        case "/app.css" -> "text/css;charset=UTF-8";
                        case "/app.js" -> "text/javascript;charset=UTF-8";
                        default -> "text/html;charset=UTF-8";
                    });
                    try (var input = new ClassPathResource("admin" + path).getInputStream()) {
                        blob = new Blob(input.readAllBytes(), type);
                    }
                    assets.put(path, blob);
                }
            }
        }
        return blob.respond(request, "private, no-cache");
    }
}
