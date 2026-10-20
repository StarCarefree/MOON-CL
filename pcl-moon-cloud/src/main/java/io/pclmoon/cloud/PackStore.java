package io.pclmoon.cloud;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;
import jakarta.annotation.PreDestroy;
import jakarta.validation.Validator;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.file.*;
import java.util.*;
import org.springframework.http.MediaType;
import org.springframework.stereotype.Service;

@Service
final class PackStore {
    static final int MAX_PACKS = 100;
    static final int MAX_FILE_BYTES = 8 * 1024 * 1024;
    private final ObjectMapper mapper;
    private final Validator validator;
    private final Path directory;
    private final Path file;
    private final FileChannel lockChannel;
    private final FileLock lock;
    private volatile Snapshot snapshot;

    record AdminData(long revision, List<Pack> packs) {}
    record Snapshot(AdminData admin, Blob catalog, Map<String, Blob> details, Map<String, Pack> published) {}

    PackStore(CloudProperties properties, ObjectMapper mapper, Validator validator) throws IOException {
        this.mapper = mapper;
        this.validator = validator;
        directory = properties.dataDir().toAbsolutePath().normalize();
        Files.createDirectories(directory);
        file = directory.resolve("catalog.json");
        lockChannel = FileChannel.open(directory.resolve("catalog.lock"), StandardOpenOption.CREATE, StandardOpenOption.WRITE);
        FileLock acquired;
        try { acquired = lockChannel.tryLock(); }
        catch (RuntimeException | IOException exception) { lockChannel.close(); throw exception; }
        if (acquired == null) {
            lockChannel.close();
            throw new IOException("数据目录已被另一服务占用；本服务仅支持单实例写入");
        }
        lock = acquired;
        try {
            AdminData initial = new AdminData(0, List.of());
            if (Files.exists(file)) {
                if (Files.size(file) > MAX_FILE_BYTES) throw new IOException("目录文件超过 8 MiB");
                initial = mapper.readValue(file.toFile(), AdminData.class);
                if (initial.revision() < 0 || initial.packs() == null || initial.packs().size() > MAX_PACKS)
                    throw new IOException("目录文件格式无效");
                Set<String> ids = new HashSet<>();
                for (Pack pack : initial.packs()) {
                    validate(pack);
                    if (!ids.add(pack.id())) throw new IOException("目录文件存在重复 ID");
                }
            }
            snapshot = build(initial.revision(), initial.packs());
        } catch (RuntimeException | IOException exception) { close(); throw exception; }
    }

    Snapshot current() { return snapshot; }

    private void validate(Pack pack) {
        if (pack == null) throw new ApiException(400, "整合包不能为空");
        var errors = validator.validate(pack);
        if (!errors.isEmpty()) {
            var error = errors.stream().sorted(Comparator.comparing(v -> v.getPropertyPath().toString())).findFirst().orElseThrow();
            throw new ApiException(400, error.getPropertyPath() + ": " + error.getMessage());
        }
        pack.validateLinks();
    }

    synchronized AdminData create(Pack pack, String expected) throws IOException {
        checkRevision(expected);
        validate(pack);
        if (snapshot.admin().packs().size() >= MAX_PACKS) throw new ApiException(409, "最多支持 100 个整合包");
        if (snapshot.admin().packs().stream().anyMatch(p -> p.id().equals(pack.id())))
            throw new ApiException(409, "整合包 ID 已存在");
        List<Pack> packs = new ArrayList<>(snapshot.admin().packs());
        packs.add(pack);
        return persist(packs);
    }

    synchronized AdminData update(String id, Pack pack, String expected) throws IOException {
        checkRevision(expected);
        validate(pack);
        if (!id.equals(pack.id())) throw new ApiException(400, "不允许修改整合包 ID");
        List<Pack> packs = new ArrayList<>(snapshot.admin().packs());
        int index = indexOf(packs, id);
        packs.set(index, pack);
        return persist(packs);
    }

    synchronized AdminData delete(String id, String expected) throws IOException {
        checkRevision(expected);
        List<Pack> packs = new ArrayList<>(snapshot.admin().packs());
        packs.remove(indexOf(packs, id));
        return persist(packs);
    }

    private int indexOf(List<Pack> packs, String id) {
        for (int index = 0; index < packs.size(); index++) if (packs.get(index).id().equals(id)) return index;
        throw new ApiException(404, "整合包不存在");
    }

    private void checkRevision(String expected) {
        if (expected == null) throw new ApiException(428, "写入必须提供 If-Match 版本，请先刷新目录");
        if (!expected.equals("\"" + snapshot.admin().revision() + "\""))
            throw new ApiException(412, "目录已被修改，请刷新后重新确认编辑");
    }

    private AdminData persist(List<Pack> packs) throws IOException {
        Snapshot next = build(Math.addExact(snapshot.admin().revision(), 1), packs);
        byte[] bytes = mapper.writerWithDefaultPrettyPrinter().writeValueAsBytes(next.admin());
        if (bytes.length > MAX_FILE_BYTES) throw new ApiException(409, "目录总大小超过 8 MiB，请缩短说明或地址");
        Path temporary = Files.createTempFile(directory, "catalog-", ".tmp");
        try {
            try (FileChannel channel = FileChannel.open(temporary, StandardOpenOption.WRITE)) {
                ByteBuffer buffer = ByteBuffer.wrap(bytes);
                while (buffer.hasRemaining()) channel.write(buffer);
                channel.force(true);
            }
            // No non-atomic fallback: on unsupported filesystems a failed write leaves the old snapshot intact.
            Files.move(temporary, file, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
            snapshot = next;
            return next.admin();
        } finally { Files.deleteIfExists(temporary); }
    }

    private Snapshot build(long revision, List<Pack> packs) throws IOException {
        List<Pack> sorted = packs.stream().sorted(Comparator.comparingInt(Pack::sortOrder).thenComparing(Pack::id)).toList();
        List<Map<String, Object>> publicPacks = new ArrayList<>();
        Map<String, Blob> details = new HashMap<>();
        Map<String, Pack> published = new HashMap<>();
        for (Pack pack : sorted) {
            if (!pack.enabled()) continue;
            Map<String, Object> view = mapper.convertValue(pack, new TypeReference<LinkedHashMap<String, Object>>() {});
            // Existing launcher uses icon/background. coverUrl also remains available to new clients.
            if (pack.icon().isBlank()) view.put("icon", pack.coverUrl());
            if (pack.background().isBlank()) view.put("background", pack.coverUrl());
            publicPacks.add(view);
            details.put(pack.id(), new Blob(mapper.writeValueAsBytes(view), MediaType.APPLICATION_JSON));
            published.put(pack.id(), pack);
        }
        Blob catalog = new Blob(mapper.writeValueAsBytes(Map.of("packs", publicPacks)), MediaType.APPLICATION_JSON);
        return new Snapshot(new AdminData(revision, sorted), catalog, Map.copyOf(details), Map.copyOf(published));
    }

    @PreDestroy
    void close() throws IOException {
        if (lock.isValid()) lock.release();
        if (lockChannel.isOpen()) lockChannel.close();
    }
}
