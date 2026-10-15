package io.mooncl.cloud;

import java.net.*;

/** Only client connection information, never an frps administration/auth token. */
record RoomConnection(String mode, String address, Integer port, String serverHost,
                      Integer serverPort, String proxyName, String secretKey) {
    @Override public String toString() { return "RoomConnection[redacted]"; }

    RoomConnection validated() {
        if (mode == null) throw invalid();
        switch (mode) {
            case "ipv6" -> {
                if (!globalIpv6(address) || !validPort(port) || serverHost != null || serverPort != null
                        || proxyName != null || secretKey != null) throw invalid();
            }
            case "frp" -> {
                if (!host(address) || !validPort(port) || serverHost != null || serverPort != null
                        || proxyName != null || secretKey != null) throw invalid();
            }
            case "p2p" -> {
                if (!host(serverHost) || !validPort(serverPort) || address != null || port != null
                        || proxyName == null || !proxyName.matches("[A-Za-z0-9_-]{1,64}")
                        || secretKey == null || secretKey.isBlank() || secretKey.length() > 128
                        || secretKey.chars().anyMatch(c -> c < 33 || c > 126)) throw invalid();
            }
            default -> throw invalid();
        }
        return this;
    }

    private static boolean validPort(Integer value) { return value != null && value >= 1 && value <= 65535; }

    private static boolean host(String value) {
        if (value == null || value.length() > 253 || value.isEmpty()) return false;
        if (value.contains(":")) return ipv6(value) != null;
        if (value.matches("[0-9.]+")) {
            String[] parts = value.split("\\.", -1);
            if (parts.length != 4) return false;
            for (String part : parts) {
                if (!part.matches("0|[1-9][0-9]{0,2}") || Integer.parseInt(part) > 255) return false;
            }
            return true;
        }
        for (String label : value.split("\\.", -1))
            if (!label.matches("[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?")) return false;
        return true;
    }

    private static byte[] ipv6(String value) {
        if (value == null || value.length() > 45 || !value.contains(":") || !value.matches("[0-9A-Fa-f:]+")) return null;
        try {
            InetAddress parsed = InetAddress.getByName(value); // Hex/colon only: cannot trigger DNS.
            return parsed instanceof Inet6Address ? parsed.getAddress() : null;
        } catch (UnknownHostException exception) { return null; }
    }

    private static boolean globalIpv6(String value) {
        byte[] ip = ipv6(value);
        if (ip == null || (ip[0] & 0xe0) != 0x20) return false; // Global unicast 2000::/3 only.
        int first = (ip[0] & 255) * 256 + (ip[1] & 255);
        int second = (ip[2] & 255) * 256 + (ip[3] & 255);
        // Conservative exclusions: protocol assignments, documentation and transition networks.
        return !(first == 0x2001 && (second < 0x0200 || second == 0x0db8))
                && first != 0x2002 && !(first == 0x3fff && second < 0x1000);
    }

    private static ApiException invalid() { return new ApiException(400, "Invalid connection descriptor"); }
}
