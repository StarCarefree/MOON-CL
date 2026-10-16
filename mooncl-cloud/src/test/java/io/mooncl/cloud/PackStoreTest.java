package io.mooncl.cloud;

import com.fasterxml.jackson.databind.ObjectMapper;
import jakarta.validation.Validation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.concurrent.*;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;
import static org.assertj.core.api.Assertions.*;

class PackStoreTest {
    @TempDir Path directory;
    private CloudProperties properties() { return new CloudProperties(8081, "127.0.0.1", "test_token_012345678901234567890123456789", directory); }
    private Pack pack(String id) {
        return new Pack(id, "测试整合包", "说明", "1.0", "1.21.1", "", "", "", "https://cdn.example.com/a.zip",
                List.of(), "", "a.zip", "", "", true, 0);
    }

    @Test
    void persistenceRestartAndExclusiveDirectoryLock() throws Exception {
        try (var factory = Validation.buildDefaultValidatorFactory()) {
            var mapper = new ObjectMapper();
            PackStore first = new PackStore(properties(), mapper, factory.getValidator());
            try {
                first.create(pack("persistent"), "\"0\"");
                assertThatThrownBy(() -> new PackStore(properties(), mapper, factory.getValidator()))
                        .isInstanceOf(java.nio.channels.OverlappingFileLockException.class);
            } finally { first.close(); }
            PackStore restarted = new PackStore(properties(), mapper, factory.getValidator());
            try {
                assertThat(restarted.current().admin().revision()).isEqualTo(1);
                assertThat(restarted.current().published()).containsKey("persistent");
                assertThat(restarted.current().admin().packs()).hasSize(1);
            } finally { restarted.close(); }
        }
    }

    @Test
    void simultaneousWritersCannotOverwriteEachOther() throws Exception {
        var executor = Executors.newFixedThreadPool(2);
        try (var factory = Validation.buildDefaultValidatorFactory()) {
            PackStore store = new PackStore(properties(), new ObjectMapper(), factory.getValidator());
            try {
                Callable<Integer> one = () -> { try { store.create(pack("one"), "\"0\""); return 200; } catch (ApiException exception) { return exception.status; } };
                Callable<Integer> two = () -> { try { store.create(pack("two"), "\"0\""); return 200; } catch (ApiException exception) { return exception.status; } };
                var results = executor.invokeAll(List.of(one, two));
                assertThat(List.of(results.get(0).get(), results.get(1).get())).containsExactlyInAnyOrder(200, 412);
                assertThat(store.current().admin().packs()).hasSize(1);
            } finally { store.close(); }
        } finally {
            executor.shutdownNow();
            assertThat(executor.awaitTermination(5, TimeUnit.SECONDS)).isTrue();
        }
    }

    @Test
    void failedAtomicWriteKeepsExistingSnapshot() throws Exception {
        try (var factory = Validation.buildDefaultValidatorFactory()) {
            PackStore store = new PackStore(properties(), new ObjectMapper(), factory.getValidator());
            try {
                store.create(pack("old"), "\"0\"");
                // Make replacement impossible on every platform: destination is a nonempty directory.
                Files.delete(directory.resolve("catalog.json"));
                Files.createDirectory(directory.resolve("catalog.json"));
                Files.writeString(directory.resolve("catalog.json/blocker"), "x");
                assertThatThrownBy(() -> store.create(pack("new"), "\"1\"")).isInstanceOf(java.io.IOException.class);
                assertThat(store.current().published()).containsOnlyKeys("old");
                assertThat(store.current().admin().revision()).isEqualTo(1);
                try (var files = Files.list(directory)) { assertThat(files.filter(p -> p.toString().endsWith(".tmp")).count()).isZero(); }
            } finally { store.close(); }
        }
    }

    @Test
    void corruptedFileDoesNotResetDataAndReleasesLock() throws Exception {
        Files.writeString(directory.resolve("catalog.json"), "{not-json");
        try (var factory = Validation.buildDefaultValidatorFactory()) {
            assertThatThrownBy(() -> new PackStore(properties(), new ObjectMapper(), factory.getValidator())).isInstanceOf(java.io.IOException.class);
            assertThat(Files.readString(directory.resolve("catalog.json"))).isEqualTo("{not-json");
            Files.delete(directory.resolve("catalog.json"));
            PackStore recovered = new PackStore(properties(), new ObjectMapper(), factory.getValidator());
            recovered.close();
        }
    }

    @Test
    void rejectsCatalogThatWouldExceedRestartReadLimit() throws Exception {
        String url = "https://cdn.example.com/" + "月".repeat(2000);
        int accepted = 0;
        try (var factory = Validation.buildDefaultValidatorFactory()) {
            var mapper = new ObjectMapper();
            PackStore store = new PackStore(properties(), mapper, factory.getValidator());
            try {
                for (int index = 0; index < 100; index++) {
                    Pack large = new Pack("large-" + index, "名称", "月".repeat(4000), "1.0", "1.21.1",
                            url, url, url, url, java.util.Collections.nCopies(8, url), "", "pack.zip", url, "", false, 0);
                    try {
                        store.create(large, "\"" + accepted + "\"");
                        accepted++;
                    } catch (ApiException exception) {
                        assertThat(exception.status).isEqualTo(409);
                        assertThat(exception.getMessage()).contains("8 MiB");
                        break;
                    }
                }
                assertThat(accepted).isBetween(1, 99);
                assertThat(Files.size(directory.resolve("catalog.json"))).isLessThanOrEqualTo(PackStore.MAX_FILE_BYTES);
                assertThat(store.current().admin().revision()).isEqualTo(accepted);
            } finally { store.close(); }
            PackStore restarted = new PackStore(properties(), mapper, factory.getValidator());
            try { assertThat(restarted.current().admin().packs()).hasSize(accepted); }
            finally { restarted.close(); }
        }
    }

    @Test
    void refusesWeakStartupToken() {
        assertThatThrownBy(() -> new CloudProperties(8081, "127.0.0.1", "password", directory)).isInstanceOf(IllegalArgumentException.class);
    }
}
