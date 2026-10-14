package io.mooncl.cloud;

import jakarta.validation.constraints.*;
import java.net.URI;
import java.util.List;

public record Pack(
        @NotBlank @Pattern(regexp = "[a-z0-9][a-z0-9_-]{0,63}") String id,
        @NotBlank @Size(max = 120) String name,
        @Size(max = 4000) String description,
        @NotBlank @Size(max = 80) String version,
        @Size(max = 40) String minecraft,
        @Size(max = 2048) String icon,
        @Size(max = 2048) String coverUrl,
        @Size(max = 2048) String background,
        @NotBlank @Size(max = 2048) String downloadUrl,
        @NotNull @Size(max = 8) List<@NotBlank @Size(max = 2048) String> backupUrls,
        @Pattern(regexp = "(?:[a-fA-F0-9]{64})?") String sha256,
        @NotBlank @Size(max = 160) String fileName,
        @Size(max = 2048) String shareUrl,
        @Size(max = 512) String sharePath,
        boolean enabled,
        @Min(-100000) @Max(100000) int sortOrder) {
    public Pack {
        description = empty(description);
        minecraft = empty(minecraft);
        icon = empty(icon);
        coverUrl = empty(coverUrl);
        background = empty(background);
        sha256 = empty(sha256);
        shareUrl = empty(shareUrl);
        sharePath = empty(sharePath);
        backupUrls = backupUrls == null ? List.of() : List.copyOf(backupUrls);
    }

    private static String empty(String value) { return value == null ? "" : value; }

    void validateLinks() {
        for (String link : List.of(icon, coverUrl, background, downloadUrl == null ? "" : downloadUrl, shareUrl)) {
            if (!link.isEmpty()) validateUrl(link);
        }
        backupUrls.forEach(Pack::validateUrl);
        if (fileName == null || !fileName.matches("[^\\\\/:*?\"<>|\\p{Cntrl}]+\\.(?i:zip|mrpack)")
                || fileName.endsWith(" ") || fileName.startsWith(".")) {
            throw new ApiException(400, "文件名必须是合法的 .zip 或 .mrpack 文件名，不得含路径");
        }
    }

    private static void validateUrl(String link) {
        try {
            URI uri = URI.create(link);
            if (!("https".equalsIgnoreCase(uri.getScheme()) || "http".equalsIgnoreCase(uri.getScheme()))
                    || uri.getHost() == null || uri.getRawUserInfo() != null || uri.getRawFragment() != null
                    || uri.getPort() > 65535 || uri.getPort() == 0) {
                throw new IllegalArgumentException();
            }
        } catch (IllegalArgumentException exception) {
            throw new ApiException(400, "URL 必须为不含凭据或片段的完整 HTTP(S) 地址");
        }
    }
}
