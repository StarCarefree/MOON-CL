# Ephemeral room coordination

Rooms run on the **public connector** (default 8080), under `/api/v1/rooms`.
The admin connector (8081) rejects these routes, even with an admin token.
No database, game relay, frpc/frps process, DNS lookup or connectivity probe is involved.
Restarting the service loses every room/session. Deploy one instance; do not load-balance rooms across independent instances.

## Wire contract

JSON field names are case-sensitive. Use `Content-Type: application/json` for bodies.
Unknown JSON fields are rejected, including any frps authentication field.
All room responses, including errors/204, have `Cache-Control: no-store`.

| Method/path (relative to `/api/v1/rooms`) | Body | Authorization | Success |
| --- | --- | --- | --- |
| POST root | `{name,playerName,password,connection}` | none | 200 Session |
| POST `/{code}/join` | `{playerName,password}` | none | 200 Session |
| GET `/{code}` | none | any current member bearer | 200 Room |
| POST `/{code}/members/{id}/heartbeat` | none | that member's bearer | 200 Room |
| PUT `/{code}/connection` | Connection | host bearer only | 200 Room |
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
    "members": [{"id": "UUID", "name": "Host", "isHost": true}],
    "connection": null,
    "expiresAt": "2026-01-01T00:01:15Z"
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

## Connection descriptors

Create with `connection: null` (or omit it) while preparing a future game launch.
Once ready, publish a **complete** descriptor via host PUT. A mode-only partial
object is invalid; PUT cannot clear a published connection to null. Create can
also accept a complete descriptor. Fields irrelevant to a mode must be null/omitted.
No server-side outbound connection is made.

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
evicted and their tokens stop authenticating. GET/join/connection PUT do not renew
leases. Expired sessions cannot be revived; create/join again.

Cleanup is request-driven: target room expiry checked on every operation; global
cleanup at most every 5 seconds on reads/mutations, and on create to reclaim capacity.
No timer or high-frequency background scan; idle stale state remains bounded until
next request. State is synchronized and snapshots immutable. Limits: 1,000 rooms,
16 members per room **including host**, 4 KiB write request body including chunked bodies.

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
    client_max_body_size 4k;
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
or enable request/response debug logging for rooms. The p2p visitor secret is deliberately
returned to authenticated members. Apply edge IP request/connection limits and short body
timeouts as well; application bounds are basic abuse resistance, not a DDoS guarantee.
The application deliberately ignores `X-Forwarded-For`/`Forwarded` for room limiting;
with a reverse proxy all callers share its IP budget. Do not enable arbitrary forwarded
header rewriting to work around this: use trusted edge enforcement/design before scaling.
Keep the public backend firewalled to your proxy and preserve the existing socket-port
admin isolation. No new environment variables or dependencies are required.

Tests: `./gradlew test` (Windows: `.\gradlew.bat test`) with JDK 21. Room unit/MockMvc
tests use a controllable Clock, no sleeps, game server, database or external network.
