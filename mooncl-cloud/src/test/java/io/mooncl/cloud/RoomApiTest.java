package io.mooncl.cloud;

import com.fasterxml.jackson.databind.*;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.http.converter.json.MappingJackson2HttpMessageConverter;
import org.springframework.test.web.servlet.*;
import org.springframework.test.web.servlet.request.MockHttpServletRequestBuilder;
import org.springframework.test.web.servlet.setup.MockMvcBuilders;
import static org.assertj.core.api.Assertions.*;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.*;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.*;

class RoomApiTest {
    private final ObjectMapper mapper = new ObjectMapper().findAndRegisterModules().disable(SerializationFeature.WRITE_DATES_AS_TIMESTAMPS);
    private MockMvc mvc;
    private RoomStoreTest.MutableClock clock;
    private static final String ROOT = "/api/v1/rooms";
    private static final String CREATE = "{\"name\":\"Room\",\"playerName\":\"Host\",\"connection\":null}";
    private static final String FRP = "{\"mode\":\"frp\",\"address\":\"game.example.com\",\"port\":25565}";

    @BeforeEach void setup() {
        clock = new RoomStoreTest.MutableClock();
        RoomStore store = new RoomStore(clock);
        Ports ports = new Ports() { @Override boolean isAdmin(int port) { return port == 9091; } };
        var properties = new CloudProperties(9091, "127.0.0.1", "integration_test_token_0123456789abcdef", null);
        mvc = MockMvcBuilders.standaloneSetup(new RoomController(store)).setControllerAdvice(new ApiErrors())
                .setMessageConverters(new MappingJackson2HttpMessageConverter(mapper))
                .addFilters(new PortGuard(ports, properties, mapper, store)).build();
    }

    private ResultActions call(MockHttpServletRequestBuilder request) throws Exception {
        return mvc.perform(request).andExpect(header().string("Cache-Control", "no-store"));
    }
    private JsonNode json(ResultActions result) throws Exception { return mapper.readTree(result.andReturn().getResponse().getContentAsByteArray()); }
    private JsonNode create() throws Exception { return json(call(post(ROOT).contentType("application/json").content(CREATE)).andExpect(status().isOk())); }
    private String auth(JsonNode session) { return "Bearer " + session.path("token").asText(); }
    private String room(JsonNode session) { return ROOT + "/" + session.at("/room/code").asText(); }
    private String member(JsonNode session) { return room(session) + "/members/" + session.path("memberId").asText(); }

    @Test void lifecycleAuthenticationAndNoSensitiveSnapshotFields() throws Exception {
        JsonNode host = create();
        assertThat(host.at("/room/connection").isNull()).isTrue();
        assertThat(host.at("/room/expiresAt").asText()).isEqualTo("2026-01-01T00:01:15Z");
        assertThat(host.at("/room/members/0/isHost").asBoolean()).isTrue();
        call(get(room(host))).andExpect(status().isUnauthorized());
        call(get(room(host)).header("Authorization", "Bearer wrong")).andExpect(status().isUnauthorized());
        JsonNode guest = json(call(post(room(host) + "/join").contentType("application/json").content("{\"playerName\":\"Guest\"}"))
                .andExpect(status().isOk()));
        String snapshot = call(get(room(host)).header("Authorization", auth(guest))).andExpect(status().isOk())
                .andExpect(jsonPath("$.members.length()").value(2)).andReturn().getResponse().getContentAsString();
        assertThat(snapshot).doesNotContain("token", "password", "salt", host.path("token").asText(), guest.path("token").asText());
        call(post(member(host) + "/heartbeat").header("Authorization", auth(guest))).andExpect(status().isForbidden());
        call(post(member(guest) + "/heartbeat").header("Authorization", auth(guest))).andExpect(status().isOk());
        call(put(room(host) + "/connection").header("Authorization", auth(guest)).contentType("application/json").content(FRP)).andExpect(status().isForbidden());
        call(put(room(host) + "/connection").header("Authorization", auth(host)).contentType("application/json").content(FRP))
                .andExpect(status().isOk()).andExpect(jsonPath("$.connection.mode").value("frp"));
        call(delete(member(guest)).header("Authorization", auth(host))).andExpect(status().isForbidden());
        call(delete(member(guest)).header("Authorization", auth(guest))).andExpect(status().isNoContent()).andExpect(content().string(""));
        call(get(room(host)).header("Authorization", auth(guest))).andExpect(status().isUnauthorized());
        call(delete(member(host)).header("Authorization", auth(host))).andExpect(status().isNoContent());
        call(get(room(host)).header("Authorization", auth(host))).andExpect(status().isNotFound());
    }

    @Test void passwordAndFullRoom() throws Exception {
        JsonNode host = json(call(post(ROOT).contentType("application/json").content("{\"name\":\"Room\",\"playerName\":\"Host\",\"password\":\"pass\"}"))
                .andExpect(status().isOk()));
        call(post(room(host) + "/join").contentType("application/json").content("{\"playerName\":\"Guest\",\"password\":\"bad\"}"))
                .andExpect(status().isForbidden());
        for (int i = 0; i < 15; i++)
            call(post(room(host) + "/join").contentType("application/json").content("{\"playerName\":\"Guest\",\"password\":\"pass\"}"))
                    .andExpect(status().isOk());
        call(post(room(host) + "/join").contentType("application/json").content("{\"playerName\":\"Overflow\",\"password\":\"pass\"}"))
                .andExpect(status().isConflict());
    }

    @Test void invalidDescriptorsAndUnknownFrpsCredentialsAreRejected() throws Exception {
        JsonNode host = create();
        for (String invalid : new String[]{"{\"mode\":\"ipv6\",\"address\":\"::1\",\"port\":25565}",
                "{\"mode\":\"frp\",\"address\":\"example.com\",\"port\":65536}",
                "{\"mode\":\"p2p\",\"serverHost\":\"frps.example.com\",\"serverPort\":7000,\"proxyName\":\"room\"}",
                FRP.replace("25565}", "25565,\"authToken\":\"must-not-be-shared\"}"), "{\"mode\":\"other\"}", "null"}) {
            call(put(room(host) + "/connection").header("Authorization", auth(host)).contentType("application/json").content(invalid))
                    .andExpect(status().isBadRequest());
        }
        String p2p = "{\"mode\":\"p2p\",\"serverHost\":\"frps.example.com\",\"serverPort\":7000,\"proxyName\":\"room\",\"secretKey\":\"visitor-only-key\"}";
        call(put(room(host) + "/connection").header("Authorization", auth(host)).contentType("application/json").content(p2p))
                .andExpect(status().isOk()).andExpect(jsonPath("$.connection.secretKey").value("visitor-only-key"));
        call(post(ROOT).contentType("application/json").content("{\"name\":\"Room\",\"playerName\":\"Host\",\"connection\":{\"mode\":\"ipv6\"}}"))
                .andExpect(status().isBadRequest());
    }

    @Test void expiryWorksThroughHttp() throws Exception {
        JsonNode host = create();
        clock.advance(75);
        call(post(member(host) + "/heartbeat").header("Authorization", auth(host))).andExpect(status().isNotFound());
    }

    @Test void bodyRateAndPortLimitsPreserveAdminSeparation() throws Exception {
        call(post(ROOT).contentType("application/json").content("x".repeat(4097))).andExpect(status().isPayloadTooLarge());
        call(post(ROOT).contentType("application/json").content("{}").with(r -> { r.setLocalPort(9091); return r; }))
                .andExpect(status().isNotFound());
        mvc.perform(get("/api/admin/packs").header("Authorization", "Bearer integration_test_token_0123456789abcdef"))
                .andExpect(status().isNotFound());
        for (int i = 0; i < 19; i++) call(post(ROOT).contentType("application/json").content("{}"))
                .andExpect(status().isBadRequest());
        call(post(ROOT).contentType("application/json").content(CREATE).header("X-Forwarded-For", "198.51.100.10"))
                .andExpect(status().isTooManyRequests()).andExpect(header().string("Retry-After", "60"));
        clock.advance(60);
        create();
    }

    @Test void heartbeatBypassesAttemptBudgetButMatrixJoinCannot() throws Exception {
        JsonNode host = create();
        for (int i = 0; i < 19; i++) call(post(ROOT).contentType("application/json").content("{}"))
                .andExpect(status().isBadRequest());
        call(post(member(host) + "/heartbeat").header("Authorization", auth(host))).andExpect(status().isOk());
        call(post(room(host) + "/join;guess=1").contentType("application/json").content("{\"playerName\":\"Guest\"}"))
                .andExpect(status().isTooManyRequests());
    }
}
