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
        call(post(ROOT).contentType("application/json").content("x".repeat(32769))).andExpect(status().isPayloadTooLarge());
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

    private String route(String id, boolean enabled, String connection) {
        return "{\"id\":\"" + id + "\",\"name\":\"Route\",\"enabled\":" + enabled + ",\"connection\":" + connection + "}";
    }
    private ResultActions routes(JsonNode session, String body) throws Exception {
        return call(put(room(session) + "/routes").header("Authorization", auth(session)).contentType("application/json").content(body));
    }

    @Test void multiRoutesAreHostOnlyAtomicClearableAndLegacyCompatible() throws Exception {
        JsonNode host = create();
        assertThat(host.at("/room/routes").size()).isZero();
        JsonNode guest = json(call(post(room(host) + "/join").contentType("application/json").content("{\"playerName\":\"Guest\"}"))
                .andExpect(status().isOk()));
        String batch = "{\"routes\":[" + route("off", false, FRP) + "," + route("on", true, FRP) + "]}";
        call(put(room(host) + "/routes").contentType("application/json").content(batch)).andExpect(status().isUnauthorized());
        routes(guest, batch).andExpect(status().isForbidden());
        routes(host, batch).andExpect(status().isOk()).andExpect(jsonPath("$.routes.length()").value(2))
                .andExpect(jsonPath("$.connection.mode").value("frp"));
        for (String bad : new String[]{"{\"routes\":[" + route("same", true, FRP) + "," + route("same", true, FRP) + "]}",
                "{\"routes\":[" + route("good", true, FRP) + "," + route("bad", true, "{\"mode\":\"ipv6\"}") + "]}",
                "{\"routes\":null}", "{\"routes\":[null]}", "{\"routes\":[],\"unknown\":true}"}) {
            routes(host, bad).andExpect(status().isBadRequest());
            call(get(room(host)).header("Authorization", auth(host))).andExpect(status().isOk())
                    .andExpect(jsonPath("$.routes.length()").value(2)).andExpect(jsonPath("$.routes[0].id").value("off"));
        }
        routes(host, "{\"routes\":[]}").andExpect(status().isOk()).andExpect(jsonPath("$.routes").isEmpty())
                .andExpect(jsonPath("$.connection").isEmpty());
        call(put(room(host) + "/connection").header("Authorization", auth(host)).contentType("application/json").content(FRP))
                .andExpect(status().isOk()).andExpect(jsonPath("$.routes.length()").value(1))
                .andExpect(jsonPath("$.routes[0].id").value("legacy")).andExpect(jsonPath("$.routes[0].enabled").value(true));
        call(post(ROOT).contentType("application/json").content(CREATE.replace("null", FRP)))
                .andExpect(status().isOk()).andExpect(jsonPath("$.room.routes[0].id").value("legacy"));
    }

    @Test void profileCreateJoinAndOwnHeartbeatStayAuthenticatedAndPreserveNoBody() throws Exception {
        String avatar = RoomStoreTest.png(1, 1, 68);
        JsonNode host = json(call(post(ROOT).contentType("application/json").content(CREATE.replace("null}",
                "null,\"avatar\":\"" + avatar + "\",\"game\":{\"name\":\"Minecraft\",\"version\":\"1.21\",\"state\":\"launching\"}}")))
                .andExpect(status().isOk()).andExpect(jsonPath("$.room.members[0].avatar").value(avatar)));
        JsonNode guest = json(call(post(room(host) + "/join").contentType("application/json").content(
                "{\"playerName\":\"Guest\",\"avatar\":\"" + avatar + "\",\"game\":{\"state\":\"running\"}}"))
                .andExpect(status().isOk()));
        String update = "{\"avatar\":\"\",\"game\":{\"name\":\"Game\",\"version\":\"v2\",\"state\":\"failed\"}}";
        call(post(member(host) + "/heartbeat").header("Authorization", auth(guest)).contentType("application/json").content(update))
                .andExpect(status().isForbidden());
        call(post(member(guest) + "/heartbeat").header("Authorization", auth(guest)).contentType("application/json").content(update))
                .andExpect(status().isOk()).andExpect(jsonPath("$.members[0].game.state").value("launching"))
                .andExpect(jsonPath("$.members[1].game.state").value("failed")).andExpect(jsonPath("$.members[1].name").value("Guest"));
        call(post(member(guest) + "/heartbeat").header("Authorization", auth(guest))).andExpect(status().isOk())
                .andExpect(jsonPath("$.members[1].game.state").value("failed"));
        call(get(room(host))).andExpect(status().isUnauthorized()).andExpect(jsonPath("$.members").doesNotExist());
        call(post(member(host) + "/heartbeat").header("Authorization", auth(host)).contentType("application/json")
                .content("{\"playerName\":\"Renamed\"}")).andExpect(status().isBadRequest());
    }

    @Test void invalidPresenceDoesNotRenewAndRejectsAvatarAndGameBounds() throws Exception {
        JsonNode host = create();
        assertThat(host.at("/room/members/0/avatar").asText()).isEmpty();
        assertThat(host.at("/room/members/0/game/state").asText()).isEqualTo("stopped");
        clock.advance(74);
        for (String bad : new String[]{"{\"avatar\":\"https://example.com/a.png\"}", "{\"avatar\":\"broken!\"}",
                "{\"avatar\":\"" + RoomStoreTest.png(65, 1, 68) + "\"}", "{\"game\":{\"state\":\"starting\"}}",
                "{\"game\":{\"name\":\"" + "x".repeat(81) + "\"}}", "{\"game\":{\"version\":\"bad\\n\"}}"}) {
            call(post(member(host) + "/heartbeat").header("Authorization", auth(host)).contentType("application/json").content(bad))
                    .andExpect(status().isBadRequest());
        }
        call(get(room(host)).header("Authorization", auth(host))).andExpect(status().isOk())
                .andExpect(jsonPath("$.members[0].game.state").value("stopped"));
        clock.advance(1);
        call(get(room(host)).header("Authorization", auth(host))).andExpect(status().isNotFound());
    }
    @Test void gamePackIsSharedToMembersAndInvalidPackDoesNotReplacePresence() throws Exception {
        String pack = "\"pack\":{\"id\":\"starrail-harpy-express\",\"name\":\"SRE\",\"version\":\"4.4.0\",\"minecraft\":\"1.21.1\",\"fileName\":\"pack.mrpack\",\"downloadUrl\":\"https://example.com/pack.mrpack\",\"sha256\":\"" + "a".repeat(64) + "\",\"sharePath\":\"folder/pack.mrpack\"}";
        JsonNode host = json(call(post(ROOT).contentType("application/json").content(
                "{\"name\":\"Room\",\"playerName\":\"Host\",\"connection\":null,\"game\":{\"state\":\"running\"," + pack + "}}"))
                .andExpect(status().isOk()));
        assertThat(host.at("/room/members/0/game/pack/downloadUrl").asText()).isEqualTo("https://example.com/pack.mrpack");
        JsonNode guest = json(call(post(room(host) + "/join").contentType("application/json").content("{\"playerName\":\"Guest\"}"))
                .andExpect(status().isOk()));
        call(get(room(host)).header("Authorization", auth(guest))).andExpect(status().isOk())
                .andExpect(jsonPath("$.members[0].game.pack.fileName").value("pack.mrpack"));
        call(post(member(host) + "/heartbeat").header("Authorization", auth(host)).contentType("application/json")
                .content("{\"game\":{\"state\":\"running\",\"pack\":{\"id\":\"bad\",\"name\":\"x\",\"version\":\"1\",\"fileName\":\"pack.exe\",\"downloadUrl\":\"file:///C:/secret.zip\"}}}"))
                .andExpect(status().isBadRequest());
        call(get(room(host)).header("Authorization", auth(host))).andExpect(status().isOk())
                .andExpect(jsonPath("$.members[0].game.pack.id").value("starrail-harpy-express"));
        call(post(member(host) + "/heartbeat").header("Authorization", auth(host)).contentType("application/json")
                .content("{\"game\":{\"state\":\"stopped\"}}")).andExpect(status().isOk())
                .andExpect(jsonPath("$.members[0].game.pack").doesNotExist());
    }

    @Test void overlayDescriptorsWorkThroughRoutesAndRejectIrrelevantFields() throws Exception {
        JsonNode host = create();
        String zt = "{\"mode\":\"zerotier\",\"address\":\"10.0.0.1\",\"port\":25565,\"networkId\":\"0123456789abcdef\"}";
        String et = "{\"mode\":\"easytier\",\"address\":\"fd00::1\",\"port\":25565,\"networkName\":\"net\",\"secretKey\":\"room-only\",\"peers\":[\"tcp://example.com:11010\",\"wss://[fd00::2]:443/ws\"]}";
        routes(host, "{\"routes\":[" + route("zt", true, zt) + "," + route("et", true, et) + "]}")
                .andExpect(status().isOk()).andExpect(jsonPath("$.routes[0].connection.networkId").value("0123456789abcdef"))
                .andExpect(jsonPath("$.routes[1].connection.peers.length()").value(2));
        for (String bad : new String[]{zt.replace("abcdef", "abcdeg"), zt.replace("25565,", "25565,\"peers\":[],"),
                et.replace("net", "x".repeat(65)), et.replace("tcp://example.com:11010", "tcp://user@example.com:11010"),
                FRP.replace("25565}", "25565,\"networkName\":\"net\"}")})
            routes(host, "{\"routes\":[" + route("bad", true, bad) + "]}").andExpect(status().isBadRequest());
    }

    @Test void largerBodiesAreBoundedOnlyForRoutesAndProfilesIncludingUnknownLength() throws Exception {
        JsonNode host = create();
        String padded = " ".repeat(5000);
        routes(host, padded + "{\"routes\":[]}").andExpect(status().isOk());
        call(post(member(host) + "/heartbeat").header("Authorization", auth(host)).contentType("application/json")
                .content(padded + "{\"game\":{\"state\":\"running\"}}" )).andExpect(status().isOk());
        call(post(ROOT).contentType("application/json").content(padded + CREATE)).andExpect(status().isOk());
        call(post(room(host) + "/join").contentType("application/json").content(padded + "{\"playerName\":\"Guest\"}"))
                .andExpect(status().isOk());
        call(put(room(host) + "/connection").header("Authorization", auth(host)).contentType("application/json").content(padded + FRP))
                .andExpect(status().isPayloadTooLarge());
        mvc.perform(context -> {
            var request = new org.springframework.mock.web.MockHttpServletRequest(context) {
                @Override public int getContentLength() { return -1; }
                @Override public long getContentLengthLong() { return -1; }
            };
            request.setMethod("PUT");
            request.setRequestURI(room(host) + "/routes");
            request.setContentType("application/json");
            request.addHeader("Authorization", auth(host));
            request.addHeader("Transfer-Encoding", "chunked");
            request.setContent(" ".repeat(32769).getBytes(java.nio.charset.StandardCharsets.UTF_8));
            return request;
        }).andExpect(status().isPayloadTooLarge()).andExpect(header().string("Cache-Control", "no-store"));
        call(post(member(host) + "/heartbeat").header("Authorization", auth(host)).contentType("application/json").content(" ".repeat(32769)))
                .andExpect(status().isPayloadTooLarge());
    }
}
