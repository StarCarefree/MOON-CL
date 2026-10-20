package io.pclmoon.cloud;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.context.properties.EnableConfigurationProperties;

@SpringBootApplication
@EnableConfigurationProperties(CloudProperties.class)
public class PclMoonApplication {
    public static void main(String[] args) {
        SpringApplication.run(PclMoonApplication.class, args);
    }
}
