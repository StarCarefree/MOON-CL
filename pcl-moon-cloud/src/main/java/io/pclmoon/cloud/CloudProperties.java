package io.pclmoon.cloud;

import java.nio.file.Path;
import org.springframework.boot.context.properties.ConfigurationProperties;

@ConfigurationProperties("pclmoon")
public record CloudProperties(int adminPort, String adminBind, String adminToken, Path dataDir) {
    public CloudProperties {
        if (adminPort < 0 || adminPort > 65535) throw new IllegalArgumentException("管理端口无效");
        if (adminToken == null || adminToken.length() < 32 || adminToken.length() > 256
                || !adminToken.matches("[A-Za-z0-9_-]+")) {
            throw new IllegalArgumentException("PCL_MOON_ADMIN_TOKEN 必须是 32–256 位随机字母、数字、下划线或连字符");
        }
    }
}
