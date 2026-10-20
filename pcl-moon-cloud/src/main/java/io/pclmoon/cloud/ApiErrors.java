package io.pclmoon.cloud;

import java.io.IOException;
import java.util.Map;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.ResponseEntity;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.web.bind.annotation.*;
import org.springframework.web.method.annotation.MethodArgumentTypeMismatchException;

@RestControllerAdvice
final class ApiErrors {
    private static final Logger LOG = LoggerFactory.getLogger(ApiErrors.class);

    @ExceptionHandler(ApiException.class)
    ResponseEntity<Map<String, String>> api(ApiException exception) { return error(exception.status, exception.getMessage()); }

    @ExceptionHandler({HttpMessageNotReadableException.class, MethodArgumentTypeMismatchException.class})
    ResponseEntity<Map<String, String>> malformed(Exception exception) { return error(400, "JSON 或参数格式错误，请检查字段与类型"); }

    @ExceptionHandler(IOException.class)
    ResponseEntity<Map<String, String>> disk(IOException exception) {
        LOG.error("目录持久化失败", exception);
        return error(503, "保存失败，旧目录保持不变；请检查磁盘权限与空间");
    }

    private ResponseEntity<Map<String, String>> error(int status, String message) {
        return ResponseEntity.status(status).header("Cache-Control", "no-store").body(Map.of("error", message));
    }
}
