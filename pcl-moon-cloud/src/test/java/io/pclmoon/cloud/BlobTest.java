package io.pclmoon.cloud;

import org.junit.jupiter.api.Test;
import static org.assertj.core.api.Assertions.*;

class BlobTest {
    @Test void respectsEncodingQualityAndWildcardOverrides() {
        assertThat(Blob.acceptsGzip(null)).isFalse();
        assertThat(Blob.acceptsGzip("gzip")).isTrue();
        assertThat(Blob.acceptsGzip("br, GZIP;q=0.5")).isTrue();
        assertThat(Blob.acceptsGzip("*;q=1")).isTrue();
        assertThat(Blob.acceptsGzip("gzip;q=0, *;q=1")).isFalse();
        assertThat(Blob.acceptsGzip("gzip;q=NaN")).isFalse();
        assertThat(Blob.acceptsGzip("gzip;q=wrong")).isFalse();
    }
    @Test void supportsWeakValidatorsAndLists() {
        assertThat(Blob.matches("W/\"abc\"", "\"abc\"")).isTrue();
        assertThat(Blob.matches("\"other\", \"abc\"", "\"abc\"")).isTrue();
        assertThat(Blob.matches("*", "\"abc\"")).isTrue();
        assertThat(Blob.matches("\"different\"", "\"abc\"")).isFalse();
    }
}
