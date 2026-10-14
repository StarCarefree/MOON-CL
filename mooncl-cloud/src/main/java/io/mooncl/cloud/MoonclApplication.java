package io.mooncl.cloud;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.context.properties.EnableConfigurationProperties;

@SpringBootApplication
@EnableConfigurationProperties(CloudProperties.class)
public class MoonclApplication {
    public static void main(String[] args) {
        SpringApplication.run(MoonclApplication.class, args);
    }
}
