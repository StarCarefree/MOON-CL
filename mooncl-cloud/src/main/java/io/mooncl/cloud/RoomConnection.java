package io.mooncl.cloud;

import java.net.*;
import java.util.List;

/** Only client connection information, never an frps administration/auth token. */
record RoomConnection(String mode, String address, Integer port, String serverHost,
                      Integer serverPort, String proxyName, String secretKey,
                      String networkId, String networkName, List<String> peers) {
    RoomConnection(String mode, String address, Integer port, String serverHost,
                   Integer serverPort, String proxyName, String secretKey) {
        this(mode, address, port, serverHost, serverPort, proxyName, secretKey, null, null, null);
    }
    @Override public String toString() { return "RoomConnection[redacted]"; }

    RoomConnection validated() {
        if (mode == null) throw invalid();
        if (!mode.equals("zerotier") && !mode.equals("easytier")
                && (networkId != null || networkName != null || peers != null)) throw invalid();
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
            case "zerotier" -> {
                if (!ipLiteral(address) || !validPort(port) || networkId == null
                        || !networkId.matches("[A-Fa-f0-9]{16}") || serverHost != null || serverPort != null
                        || proxyName != null || secretKey != null || networkName != null || peers != null) throw invalid();
            }
            case "easytier" -> {
                if (!ipLiteral(address) || !validPort(port) || !boundedText(networkName, 64)
                        || !boundedText(secretKey, 128) || serverHost != null || serverPort != null
                        || proxyName != null || networkId != null || peers == null || peers.isEmpty()
                        || peers.size() > 8 || peers.stream().anyMatch(p -> !peer(p))) throw invalid();
            }
            default -> throw invalid();
        }
        return peers == null ? this : new RoomConnection(mode, address, port, serverHost,
                serverPort, proxyName, secretKey, networkId, networkName, List.copyOf(peers));
    }

    private static boolean boundedText(String value, int max) {
        return value != null && !value.isEmpty() && value.length() <= max
                && value.chars().noneMatch(Character::isISOControl);
    }

    private static boolean ipLiteral(String value) {
        if (value == null) return false;
        if (!value.contains(":")) return value.matches("[0-9.]+") && host(value);
        if (value.length() > 45 || !value.matches("[0-9A-Fa-f:.]+")) return false;
        if (value.contains(".") && !host(value.substring(value.lastIndexOf(':') + 1))) return false;
        try {
            InetAddress.getByName(value); // Literal-only characters and a colon: never DNS.
            return true;
        } catch (UnknownHostException exception) { return false; }
    }

    private static boolean peer(String value) {
        if (value == null || value.length() > 256) return false;
        try {
            URI uri = new URI(value);
            String hostname = uri.getHost();
            if (hostname != null && hostname.startsWith("[") && hostname.endsWith("]"))
                hostname = hostname.substring(1, hostname.length() - 1);
            return uri.getScheme() != null && List.of("tcp", "udp", "ws", "wss", "quic", "wg").contains(uri.getScheme())
                    && (hostname != null && hostname.contains(":") ? ipLiteral(hostname) : host(hostname))
                    && validPort(uri.getPort()) && uri.getRawUserInfo() == null
                    && uri.getRawQuery() == null && uri.getRawFragment() == null;
        } catch (URISyntaxException exception) { return false; }
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
