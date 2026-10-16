# Ephemeral room coordination (v2, compatible `/api/v1` paths)

Rooms run on the **public connector** (default 6664), under `/api/v1/rooms`.
The admin connector (8081) rejects these routes, even with an admin token.
No database, game relay, frpc/frps process, DNS lookup or connectivity probe is involved.
Restarting the service loses every room/session. Deploy one instance; do not load-balance rooms across independent instances.

## Wire contract

JSON field names are case-sensitive. Use `Content-Type: application/json` for bodies.
Unknown JSON fields are rejected, including any frps authentication field.
All room responses, including errors/204, have `Cache-Control: no-store`.

| Method/path (relative to `/api/v1/rooms`) | Body | Authorization | Success |
| --- | --- | --- | --- |
| POST root | `{name,playerName,password,connection,avatar,game}` | none | 200 Session |
| POST `/{code}/join` | `{playerName,password,avatar,game}` | none | 200 Session |
| GET `/{code}` | none | any current member bearer | 200 Room |
| POST `/{code}/members/{id}/heartbeat` | optional Presence `{avatar,game}` | that member's bearer | 200 Room |
| PUT `/{code}/connection` | Connection (legacy compatibility) | host bearer only | 200 Room |
| PUT `/{code}/routes` | `{routes:[{id,name,connection,enabled},...]}` | host bearer only | 200 Room |
| DELETE `/{code}/members/{id}` | none | that member's bearer | 204, empty body |

`Authorization: Bearer <token>` is required on authenticated routes, never query parameters.
The host cannot evict another member with DELETE. Host departure closes the entire room.
GET is authenticated; there is no room listing or unauthenticated metadata endpoint.
Creation/join issue a fresh member each time (no reconnect/deduplication or idempotency key).

```json
{
  "room": {
    "code": "ABCDEFGH",
    "name": "Evening game",
    "hostId": "UUID",
    "members": [{"id": "UUID", "name": "Host", "isHost": true, "avatar": "", "game": {"name": "", "version": "", "state": "stopped"}}],
    "connection": null,
    "expiresAt": "2026-01-01T00:01:15Z",
    "routes": []
  },
  "memberId": "UUID",
  "token": "43-character-base64url-token"
}
```

Room is exactly the nested `room` shape above. Tokens are 32 random bytes, unpadded
base64url (43 characters), returned only by create/join. Only SHA-256 token digests
are retained. Invitation codes are 8 cryptographically random characters from
`ABCDEFGHJKLMNPQRSTUVWXYZ23456789` (40 bits), uppercase and case-sensitive.
Snapshots contain no member tokens, password hash or salt. Keep tokens/code private;
the code is an invitation, not a substitute for the optional room password.

`name` is required, at most 80 UTF-16 characters; `playerName` required, at most 32.
Both are trimmed, nonblank and reject control characters. Password is optional:
null/omitted/empty means unlocked; otherwise at most 128 UTF-16 characters, no
controls, **not trimmed**. Passwords use per-room 16-byte random salt,
PBKDF2-HMAC-SHA256 (120,000 iterations, 256-bit output) and constant-time comparison.

## Member presence

Create/join accept optional `avatar` and `game`. The member's identity `name` is
still set only by `playerName` on create/join; heartbeat cannot rename a member.
An omitted/null avatar defaults to `""`; an omitted/null game defaults to
`{"name":"","version":"","state":"stopped"}`. Game name/version each allow
0–80 UTF-16 characters without controls, untrimmed. Missing/null name/version become
`""`; missing/null state becomes `stopped`. States are case-sensitive and exactly
`stopped`, `launching`, `running`, `failed` (not `starting`).

Optional `game.pack` is a public download descriptor, not a file upload:
`{id,name,version,minecraft,fileName,downloadUrl,sha256,shareUrl,sharePath}`.
`id` matches `[a-z0-9][a-z0-9_-]{0,63}`; name and version are 1–80 characters;
minecraft is 0–40; fileName is a single `.zip` or `.mrpack` name; sha256 is empty
or 64 hex characters; sharePath is 0–512 characters without controls, `..`, or
backslashes. downloadUrl and shareUrl are credential-free HTTP(S) URLs of at most
2048 characters; at least one must be present. Omitted or null pack means no share.
A supplied game object replaces the previous pack, so sending game without pack
clears it. Invalid pack data rejects the whole presence update.

Optional `game.server` is the remote Minecraft address the member is currently
playing, `host` or `host:port`. It is at most 253 characters, without spaces or
controls. Empty or omitted means not connected to a remote server. A supplied
game object replaces it. Invalid server text rejects the whole presence update.

Avatar is raw standard base64 PNG bytes, **not a URL or data URI**. Empty string
clears it. Maximum 8,192 encoded characters and 6,144 decoded bytes. Validation
checks base64, PNG signature, first IHDR chunk (13-byte payload), legal header
fields and dimensions 1–64 inclusive in both axes. Bounded chunk lengths/checksums
and IDAT/IEND presence are checked. No outbound fetch, decompression or image processing
is performed; clients must treat avatar data as untrusted and safely decode it.

Heartbeat without a body preserves the complete profile and only renews the lease.
With Presence, omitted/null avatar/game preserves that field; a supplied game
replaces the whole game object using the defaults above. All supplied fields are
validated before changing either the profile or deadline, so invalid requests do
neither. Only the token's own member ID may be updated; even the host cannot update
a guest. Snapshots expose profiles only to successfully created/joined or already
authenticated members, never via a public listing.

## Connection descriptors

Create/join the hall first with no connection; creation returns `routes: []` and
`connection: null`. Network choices are published inside the room afterward.
There is no `create.routes` field. Host PUT `/routes` replaces the **entire list
atomically**, preserving order, after validating every entry (including disabled
ones). Maximum 12 entries; IDs are case-sensitive `[A-Za-z0-9_-]{1,64}` and must
be unique. Names are trimmed nonblank text, 1–48 UTF-16 characters, no controls.
Each route contains a complete nonnull Connection and an `enabled` boolean.
`{"routes":[]}` clears all routes. Null/missing routes, duplicate IDs or any invalid
entry reject the whole batch without changing existing routes.

Legacy `Room.connection` is derived as the **first enabled route's connection**,
or null if none are enabled. Legacy host PUT `/connection` replaces all routes
with `[{"id":"legacy","name":"默认线路","connection":<descriptor>,"enabled":true}]`.
Legacy create with a nonnull `connection` produces the same single route.
Legacy PUT cannot accept null; use `/routes` to clear. Fields irrelevant to a mode
must be null/omitted, including the new `networkId`, `networkName`, `peers` on
ipv6/frp/p2p. No server-side outbound connection is made.

- IPv6: `{"mode":"ipv6","address":"2606:4700:4700::1111","port":25565}`.
  Address must be an unbracketed, zone-free hexadecimal IPv6 literal, global
  unicast `2000::/3`. Special protocol assignments `2001::/23`, documentation
  `2001:db8::/32` and `3fff::/20`, and 6to4 `2002::/16` are conservatively excluded.
  Private, link-local, loopback, multicast, mapped IPv4 and hostnames are rejected.
  Global syntax does not prove reachability; host must configure firewall/game listener.
- FRP TCP: `{"mode":"frp","address":"game.example.com","port":25565}`.
  Address is a hostname or IP literal, no scheme/path/brackets/embedded port.
- P2P: `{"mode":"p2p","serverHost":"frps.example.com","serverPort":7000,"proxyName":"moon-room","secretKey":"visitor-only-key"}`.
  `proxyName` is 1–64 ASCII letters/digits/underscore/hyphen. `secretKey` is
  1–128 printable, non-space ASCII characters. This is the **room-specific visitor
  key**, shared with room members; it is NEVER the frps server authentication token.
  frps authentication stays local to the launcher/deployment, outside this API.
- ZeroTier: `{"mode":"zerotier","address":"10.0.0.1","port":25565,"networkId":"0123456789abcdef"}`.
  Address is an IP literal (private/global allowed), networkId exactly 16 hex
  characters. `serverHost`, `serverPort`, `proxyName`, `secretKey`, `networkName`,
  `peers` must be null/omitted.
- EasyTier: `{"mode":"easytier","address":"fd00::1","port":25565,"networkName":"room-net","secretKey":"room-only-key","peers":["tcp://peer.example.com:11010"]}`.
  Address is an IP literal (private/global allowed); networkName 1–64 characters,
  secretKey 1–128 characters, neither allowing controls and neither trimmed.
  Peers is a required list of 1–8 URL strings, each at most 256 characters.
  Schemes: `tcp`, `udp`, `ws`, `wss`, `quic`, `wg`; valid host and explicit port
  required, no userinfo/query/fragment. URL paths are permitted (e.g. WebSocket).
  `serverHost`, `serverPort`, `proxyName`, `networkId` must be null/omitted.

All ports are integers 1–65535. Hostnames/IP addresses are at most 253 characters;
DNS labels at most 63 ASCII characters (use punycode for IDNs), no trailing dot.
IPv6 literals at most 45 characters. IPv4 is strict dotted decimal, no octal shorthand.
FRP/P2P may name private endpoints; clients decide reachability. Do not use descriptors
as trusted commands/config fragments. Never interpolate their values into a shell.

## Expiry, bounds and errors

Each member has a **75-second** lease, renewed **only by its own heartbeat**.
Send heartbeat about every 20–25 seconds, including while preparing the game.
`expiresAt` is the host's deadline, an ISO-8601 UTC instant. At the exact deadline,
the host's absence closes the room even if guests are active. Expired guests are
evicted and their tokens stop authenticating. GET/join/connection PUT/routes PUT
do not renew leases. Expired sessions cannot be revived; create/join again.

Cleanup is request-driven: target room expiry checked on every operation; global
cleanup at most every 5 seconds on reads/mutations, and on create to reclaim capacity.
No timer or high-frequency background scan; idle stale state remains bounded until
next request. State is synchronized and snapshots immutable. Limits: 1,000 rooms,
16 members per room **including host**. Create/join, Presence heartbeat and routes
PUT allow at most **32 KiB** per write body, including unknown-length/chunked
bodies. Other room writes (including legacy connection PUT) remain at **4 KiB**.

Create/join share a fixed-window budget of **20 attempts per socket IP per 60 seconds**,
plus **120 attempts globally per 60 seconds**, checked before body parsing/password
hashing. Invalid JSON, unknown codes and wrong passwords also count. IP tracking is
bounded to 4,096 entries (expired buckets are removed); saturation fails closed.
429 includes `Retry-After: 60`. NAT/proxy users share the socket-IP budget.

Errors use existing `{"error":"description"}` format:
400 invalid fields/body/descriptor; 401 missing/invalid/expired member token;
403 wrong room password, wrong member ID or nonhost publication;
404 missing/expired room or wrong connector; 409 full room; 413 oversized body;
429 attempts exceeded; 503 room capacity reached. Authenticated requests with an
unknown code may receive 404 before token verification; no room metadata is returned.

## Deployment / proxy

The existing `deploy/nginx.conf.example` is **catalog-only** and strips Authorization
in its catalog location. It will return 404 for rooms until explicitly extended.
Add a separate room location inside its TLS server; do not modify/admin-proxy 8081:

```nginx
location ~ ^/api/v1/rooms(?:/|$) {
    client_max_body_size 32k;
    limit_req zone=mooncl_rate burst=20 nodelay;
    limit_conn mooncl_connections 20;
    proxy_pass http://mooncl_public;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header Authorization $http_authorization;
    proxy_connect_timeout 3s;
    proxy_read_timeout 15s;
    proxy_send_timeout 15s;
    proxy_cache off;
    add_header Cache-Control "no-store" always;
}
```

Use HTTPS only. Do not cache, buffer into diagnostic logs, log bodies/Authorization,
or enable request/response debug logging for rooms. P2P/EasyTier room secrets are
deliberately returned to authenticated members. Apply edge IP request/connection limits and short body
timeouts as well; application bounds are basic abuse resistance, not a DDoS guarantee.
The application deliberately ignores `X-Forwarded-For`/`Forwarded` for room limiting;
with a reverse proxy all callers share its IP budget. Do not enable arbitrary forwarded
header rewriting to work around this: use trusted edge enforcement/design before scaling.
Keep the public backend firewalled to your proxy and preserve the existing socket-port
admin isolation. No new environment variables or dependencies are required.

Tests: `./gradlew test` (Windows: `.\gradlew.bat test`) with JDK 17 or later (compiled with `--release 17`, runnable on Java 17+). Room unit/MockMvc
tests use a controllable Clock, no sleeps, game server, database or external network.
