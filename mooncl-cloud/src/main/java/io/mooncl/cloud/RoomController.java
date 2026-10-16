package io.mooncl.cloud;

import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

@RestController
@RequestMapping("/api/v1/rooms")
final class RoomController {
    private final RoomStore rooms;
    RoomController(RoomStore rooms) { this.rooms = rooms; }

    @PostMapping
    RoomStore.Session create(@RequestBody RoomStore.Create request) { return rooms.create(request); }

    @PostMapping("/{code}/join")
    RoomStore.Session join(@PathVariable String code, @RequestBody RoomStore.Join request) { return rooms.join(code, request); }

    @GetMapping("/{code}")
    RoomStore.Room get(@PathVariable String code, @RequestHeader(value = "Authorization", required = false) String auth) {
        return rooms.get(code, bearer(auth));
    }

    @PostMapping("/{code}/members/{id}/heartbeat")
    RoomStore.Room heartbeat(@PathVariable String code, @PathVariable String id,
                            @RequestHeader(value = "Authorization", required = false) String auth,
                            @RequestBody(required = false) RoomStore.Presence presence) {
        return rooms.heartbeat(code, id, bearer(auth), presence);
    }

    @PutMapping("/{code}/connection")
    RoomStore.Room publish(@PathVariable String code, @RequestHeader(value = "Authorization", required = false) String auth,
                          @RequestBody RoomConnection connection) {
        return rooms.publish(code, bearer(auth), connection);
    }

    @PutMapping("/{code}/routes")
    RoomStore.Room routes(@PathVariable String code, @RequestHeader(value = "Authorization", required = false) String auth,
                          @RequestBody RoomStore.Routes request) {
        return rooms.publishRoutes(code, bearer(auth), request);
    }

    @DeleteMapping("/{code}/members/{id}")
    ResponseEntity<Void> leave(@PathVariable String code, @PathVariable String id,
                               @RequestHeader(value = "Authorization", required = false) String auth) {
        rooms.leave(code, id, bearer(auth));
        return ResponseEntity.noContent().build();
    }

    private static String bearer(String auth) {
        if (auth == null || !auth.regionMatches(true, 0, "Bearer ", 0, 7) || auth.length() != 50)
            throw new ApiException(401, "Invalid or missing room bearer token");
        return auth.substring(7);
    }
}
