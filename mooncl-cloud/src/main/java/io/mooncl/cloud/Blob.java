package io.mooncl.cloud;

import jakarta.servlet.http.HttpServletRequest;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HexFormat;
import java.util.Locale;
import java.util.zip.GZIPOutputStream;
import org.springframework.http.HttpHeaders;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;

/** Immutable wire representations; serialization, compression and hashing happen only on changes. */
final class Blob {
    private final byte[] plain;
    private final byte[] gzip;
    private final String plainTag;
    private final String gzipTag;
    private final MediaType type;

    Blob(byte[] plain, MediaType type) throws IOException {
        this.plain = plain;
        this.type = type;
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        try (GZIPOutputStream compressor = new GZIPOutputStream(output)) { compressor.write(plain); }
        gzip = output.toByteArray();
        plainTag = tag(plain);
        gzipTag = tag(gzip);
    }

    private static String tag(byte[] bytes) {
        try {
            return "\"" + HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes)) + "\"";
        } catch (NoSuchAlgorithmException exception) { throw new IllegalStateException(exception); }
    }

    ResponseEntity<byte[]> respond(HttpServletRequest request, String cachePolicy) {
        boolean compressed = gzip.length < plain.length && acceptsGzip(request.getHeader("Accept-Encoding"));
        byte[] bytes = compressed ? gzip : plain;
        String etag = compressed ? gzipTag : plainTag;
        HttpHeaders headers = new HttpHeaders();
        headers.setContentType(type);
        headers.setCacheControl(cachePolicy);
        headers.setETag(etag);
        headers.setVary(java.util.List.of("Accept-Encoding"));
        if (compressed) headers.set("Content-Encoding", "gzip");
        if (matches(request.getHeader("If-None-Match"), etag)) return new ResponseEntity<>(null, headers, 304);
        headers.setContentLength(bytes.length);
        return new ResponseEntity<>("HEAD".equals(request.getMethod()) ? null : bytes, headers, 200);
    }

    static boolean matches(String value, String tag) {
        if (value == null) return false;
        for (String candidate : value.split(",")) {
            candidate = candidate.trim();
            if (candidate.equals("*") || candidate.equals(tag) || candidate.equals("W/" + tag)) return true;
        }
        return false;
    }

    static boolean acceptsGzip(String value) {
        if (value == null) return false;
        Double gzipQuality = null, wildcardQuality = null;
        for (String entry : value.toLowerCase(Locale.ROOT).split(",")) {
            String[] parts = entry.trim().split(";");
            double quality = 1;
            for (int index = 1; index < parts.length; index++) {
                String parameter = parts[index].trim();
                if (parameter.startsWith("q=")) {
                    try { quality = Double.parseDouble(parameter.substring(2)); }
                    catch (NumberFormatException exception) { quality = 0; }
                }
            }
            if (!Double.isFinite(quality) || quality < 0 || quality > 1) quality = 0;
            if (parts[0].trim().equals("gzip")) gzipQuality = quality;
            if (parts[0].trim().equals("*")) wildcardQuality = quality;
        }
        return (gzipQuality != null ? gzipQuality : wildcardQuality != null ? wildcardQuality : 0) > 0;
    }
}
