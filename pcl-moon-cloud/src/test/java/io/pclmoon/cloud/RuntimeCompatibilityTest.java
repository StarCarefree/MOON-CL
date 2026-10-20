package io.pclmoon.cloud;

import static org.assertj.core.api.Assertions.assertThat;

import java.io.DataInputStream;
import java.io.IOException;
import org.junit.jupiter.api.Test;
import org.springframework.boot.env.YamlPropertySourceLoader;
import org.springframework.core.io.ClassPathResource;
import org.springframework.mock.env.MockEnvironment;

final class RuntimeCompatibilityTest {
    @Test
    void applicationBytecodeTargetsJava17() throws IOException {
        try (var stream = new DataInputStream(PclMoonApplication.class.getResourceAsStream("PclMoonApplication.class"))) {
            assertThat(stream.readInt()).isEqualTo(0xCAFEBABE);
            stream.readUnsignedShort();
            assertThat(stream.readUnsignedShort()).as("Java 17 class-file major version").isEqualTo(61);
        }
    }

    @Test
    void defaultPublicPortIs6664AndEnvironmentOverrideStillWorks() throws IOException {
        var environment = new MockEnvironment();
        var defaults = new YamlPropertySourceLoader().load("defaults", new ClassPathResource("application.yml"));
        defaults.forEach(source -> environment.getPropertySources().addLast(source));
        assertThat(environment.getProperty("server.port", Integer.class)).isEqualTo(6664);
        environment.withProperty("PCL_MOON_PUBLIC_PORT", "7777");
        assertThat(environment.getProperty("server.port", Integer.class)).isEqualTo(7777);
    }
}
