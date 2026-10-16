package io.mooncl.cloud;

/** A client-selectable route; publishing never connects to its endpoint. */
record RoomRoute(String id, String name, RoomConnection connection, boolean enabled) {
    RoomRoute validated() {
        if (id == null || !id.matches("[A-Za-z0-9_-]{1,64}") || connection == null)
            throw new ApiException(400, "Invalid route");
        return new RoomRoute(id, RoomStore.text(name, 48, "route name"), connection.validated(), enabled);
    }

    static RoomRoute legacy(RoomConnection connection) {
        return new RoomRoute("legacy", "默认线路", connection, true);
    }
}
