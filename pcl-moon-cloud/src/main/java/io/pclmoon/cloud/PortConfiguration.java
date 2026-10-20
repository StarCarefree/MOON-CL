package io.pclmoon.cloud;

import java.net.InetAddress;
import java.net.UnknownHostException;
import org.apache.catalina.connector.Connector;
import org.apache.coyote.http11.Http11NioProtocol;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.boot.web.embedded.tomcat.TomcatServletWebServerFactory;
import org.springframework.boot.web.server.WebServerFactoryCustomizer;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.stereotype.Component;

@Configuration
class PortConfiguration {
    @Bean
    WebServerFactoryCustomizer<TomcatServletWebServerFactory> connectors(
            CloudProperties properties, Ports ports, @Value("${server.port}") int publicPort) {
        if (publicPort > 0 && publicPort == properties.adminPort()) {
            throw new IllegalArgumentException("获取端口和管理端口不能相同");
        }
        return factory -> {
            factory.addConnectorCustomizers(connector -> ports.publicConnector = connector);
            Connector admin = new Connector(Http11NioProtocol.class.getName());
            admin.setPort(properties.adminPort());
            admin.setMaxPostSize(131072);
            Http11NioProtocol protocol = (Http11NioProtocol) admin.getProtocolHandler();
            try {
                protocol.setAddress(InetAddress.getByName(properties.adminBind()));
            } catch (UnknownHostException exception) {
                throw new IllegalArgumentException("管理监听地址无效", exception);
            }
            protocol.setMaxThreads(16);
            protocol.setMinSpareThreads(2);
            protocol.setMaxConnections(128);
            protocol.setAcceptCount(32);
            protocol.setConnectionTimeout(10000);
            protocol.setKeepAliveTimeout(15000);
            protocol.setMaxKeepAliveRequests(100);
            protocol.setMaxHttpRequestHeaderSize(8192);
            protocol.setMaxSwallowSize(131072);
            ports.adminConnector = admin;
            factory.addAdditionalTomcatConnectors(admin);
        };
    }
}

@Component
class Ports {
    volatile Connector publicConnector;
    volatile Connector adminConnector;
    int publicPort() { return publicConnector.getLocalPort(); }
    int adminPort() { return adminConnector.getLocalPort(); }
    boolean isAdmin(int localPort) { return adminConnector != null && localPort == adminPort(); }
}
