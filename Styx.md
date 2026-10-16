# Styx

Styx is an authenticated message relay with peer discovery. Clients connect over a WebSocket, prove
membership of a *network*, announce a name, and from then on exchange opaque byte payloads with other
named members of the same network. The relay routes bytes; it does not interpret them.

This document specifies the protocol as observed on the wire, and is sufficient to implement an
interoperable client or server without reference to any existing implementation.

## 1. Conventions

The key words MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are to be interpreted as requirements on
implementations.

| Term | Meaning |
|---|---|
| **relay** | The server. |
| **client** | Anything that connects to the relay. |
| **network** | A routing namespace identified by a UUID. Membership is proved by an authorization token. |
| **path** | A sub-network within a network, named by a v2 token's `sub` caveats (§4.2). Empty for the network itself. |
| **peer** | An authenticated client, identified within its network by a **hostname**. |
| **payload** | An opaque byte string carried between peers. |

## 2. Model

A network is a tree of namespaces: the network itself, and any sub-network paths below it that v2 tokens
name (§4.2). Every peer in a namespace can address every other peer in that namespace by hostname, and
receives notification whenever its membership changes. Namespaces are mutually invisible: two peers in
different networks, or on different paths of one network, cannot address, discover, or detect one another,
even when connected to the same relay and using the same hostname.

Peer identity is a single string. Hostnames are compared ordinally, ignoring case, and canonicalised to
lowercase by the relay; a client that authenticates as `ALPHA-Box` is reported to its peers, and addressed
by them, as `alpha-box`. Hostnames are otherwise uninterpreted unless the token restricts them (§4.2): any
non-empty string is acceptable, and there is no requirement that it resemble a DNS name.

Identity is bound to the connection, not to the credential. One connection carries exactly one identity
at a time. A hostname is unique within a namespace: a second connection claiming a hostname already in use
**always** displaces the first (§6.2), whatever tokens either holds. Where holders are not trusted with each
other's names, the remedy is hostname-restricted tokens (§10), not a different displacement rule.

The relay holds no state beyond the set of live connections. Nothing is persisted, queued, or replayed.
A payload addressed to a peer that is not connected at that instant is discarded.

## 3. Transport

Styx is a SignalR hub. The hub is mounted at **`/relay`** relative to the server root, and the same hub again
at **`/relay/parallel`**. The two differ only in how many of one client's invocations run at once (§3.4), and
so in whether its `Send`s keep their order (§5.2). Peers on either path are one set of peers: they see each
other in `Peers`, `Send` to each other, and displace each other by hostname. Everything below that names
`/relay` applies to `/relay/parallel` as well, its negotiate endpoint being `/relay/parallel/negotiate`.

### 3.1 Establishing the connection

Clients MAY perform the standard SignalR negotiate exchange:

```
POST /relay/negotiate?negotiateVersion=1
→ 200 {"negotiateVersion":1,"connectionId":"…","connectionToken":"…",
       "availableTransports":[{"transport":"WebSockets","transferFormats":["Text","Binary"]},
                              {"transport":"ServerSentEvents", …},{"transport":"LongPolling", …}]}
```

and then open a WebSocket to `/relay?id=<connectionToken>`.

Clients MAY also **skip negotiation entirely** and open a WebSocket directly to `/relay` with no query
string. This is the recommended path for new implementations: it removes an HTTP round-trip and the
connection-token bookkeeping, and behaves identically thereafter. Server-sent events and long polling are
advertised but offer no advantage here.

Use `wss://` against any relay reachable over an untrusted network (§10).

### 3.2 Handshake

Immediately after the socket opens, the client MUST send a handshake frame selecting a hub protocol:

```
{"protocol":"json","version":1}<0x1E>
```

The relay replies `{}` followed by `0x1E` on success, or an object with an `error` member on failure. All
subsequent frames are records terminated by the **record separator byte `0x1E`**; a single WebSocket
message MAY contain several records.

### 3.3 Hub protocols

Two protocols are supported, and either is a complete implementation target:

| | `json` | `messagepack` |
|---|---|---|
| Frame body | UTF-8 JSON, `0x1E`-terminated | varint length prefix + MessagePack array |
| Byte payloads | base64 string (+33% size) | native binary, no expansion |
| Argument member names | case-insensitive | case-insensitive |
| Returned member names | camelCase (`authenticated`) | PascalCase (`Authenticated`) |

Argument member names are matched case-insensitively under both protocols, so `hostName`, `HostName` and
`HOSTNAME` are equally acceptable and a client may use whichever casing its language favours. This
document writes the camelCase form throughout.

Members returned by the relay are **not** normalised between the protocols: a `json` client reads
`authenticated`, a `messagepack` client reads `Authenticated`. Clients that support both SHOULD read
response members case-insensitively too.

Message framing, invocation records (`type: 1`), completion records (`type: 3`), ping records
(`type: 6`) and close records (`type: 7`) follow the SignalR Hub Protocol and are not restated here.
§11 gives a complete worked exchange.

### 3.4 Limits and liveness

| Property | Value |
|---|---|
| Maximum invocation size | 32 MiB (33 554 432 bytes) |
| Concurrent invocations per client | 1 on `/relay`; on `/relay/parallel`, `RELAY_PARALLEL_INVOCATIONS` (§13), default 4 |
| Relay keep-alive ping interval | 5 s |
| Configured client timeout | 15 s |
| Deadline for a client to take a message | 30 s |

On `/relay` the relay runs a client's invocations **one at a time, in the order it received them**: the next
starts only once the last has finished. So a slow invocation holds up everything behind it from that client,
whether that is a `Send` waiting on a target that is not reading or a login waiting out its second (§5.1). On
`/relay/parallel` up to the configured number run at once, so a slow one holds up nothing until that many are
running, and in exchange nothing orders them. While a client's invocation waits its turn the relay is not reading
from that client, and does not count the silence against its client timeout; at any other time it does, so a
client MUST keep sending its own ping records while an invocation of its own is running.

A client that does not take a message the relay is sending it (`Receive`, `Peers` or `Kicked`) within the
deadline is disconnected, however lively its own ping records: a client that keeps sending while it has
stopped reading would otherwise hold every sender to it, and every membership change, indefinitely. A message
counts as taken once all but the transport's buffers' worth of it has gone, so a message that needs longer than
the deadline on a slow target's downlink costs that target its connection, and is one more reason to fragment.

Exceeding the size limit is fatal to the connection, not to the invocation: the relay sends a close
record naming the limit and drops the connection. Clients MUST fragment large transfers at the
application layer.

The relay emits a ping record roughly every 5 s on an otherwise idle connection. Clients SHOULD treat a
prolonged absence of relay traffic as a dead link, and SHOULD send their own ping records so that the
relay can do the same. A silent client is not promptly disconnected in practice, so a client MUST NOT
rely on the relay to garbage-collect its stale registrations; conversely, half-open links are detected
only as fast as one side probes.

## 4. Authorization tokens

A token is both the proof of network membership and the statement of *which* network is being joined. There
are two formats, told apart by prefix: a token beginning **`styx2.`** is v2 (§4.2), anything else is v1
(§4.1).

**A network is v1 or v2, never both.** Networks created for v2 have version-8 UUIDs (RFC 9562, random apart
from the version and variant bits). The relay accepts a v2 token only for a version-8 network and a v1
token only for any other. There is no upgrade path: a client moves to v2 by moving to a new network.

A client that only connects treats a token as an opaque string (§9). Issuing and narrowing tokens is
specified in §14.

### 4.1 v1 tokens

A v1 token is an encrypted network UUID; the relay learns the network identity only by decrypting it.

Construction, given the relay password `P` and the network UUID `N`:

1. `salt` ← 64 random bytes.
2. `key` ← PBKDF2-HMAC-SHA256(`P`, `salt`, iterations = 100 000, length = 32).
3. `nonce` ← 12 bytes, unique per encryption under `key`.
4. `plaintext` ← UTF-8 of `N` rendered as a **JSON string**, i.e. including the surrounding quotes:
   `"1e2d3c4b-…"`, 38 bytes.
5. `ciphertext`, `tag` ← AES-256-GCM(`key`, `nonce`, `plaintext`), 16-byte tag.
6. Token ← base64 of the concatenation:

```
┌──────────────┬───────────────┬─────────────┬────────────────┐
│ salt 64 B    │ nonce 12 B    │ tag 16 B    │ ciphertext …   │
└──────────────┴───────────────┴─────────────┴────────────────┘
  92-byte header, then the ciphertext (130 bytes total for a UUID)
```

The relay reverses this: read the salt, derive the key, verify the tag, parse the UUID. Any failure is
reported as a generic authentication failure. A relay accepts v1 tokens only while it is configured with a
v1 password (§13), and never for a version-8 network. Nothing issues new v1 tokens.

Properties an implementer should be aware of:

- The token is a **bearer credential**, replayable and long-lived. It carries no expiry, no binding to
  a hostname, and no binding to a client. Two clients MAY authenticate with the same token.
- Every v1 token is an **offline verifier of the password**: its salt, tag and known plaintext shape let
  anyone holding one test password guesses without the relay. Anyone holding the password can mint a v1
  token for any legacy network whose UUID they know.
- There is no revocation mechanism for an individual token. Rotating the relay password invalidates
  every v1 token on that relay.
- Key derivation is deliberately expensive (100 000 iterations, ~50–100 ms). Derive once, cache, reuse.

### 4.2 v2 tokens

A v2 token is an HMAC-SHA256 chain over a network UUID and a list of **caveats**, each a restriction, under a
**relay key**: 64 bytes held by the relay alone, either random or derived from a long passphrase (§13.1).

String form: `styx2.` followed by the **canonical** base64url encoding (RFC 4648 §5, no padding) of the
binary below. Canonical means decoding and re-encoding gives the identical string: padding, whitespace and
non-zero trailing bits are all invalid, so a token has exactly one spelling.

```
┌──────┬─────┬──────────────┬───────┬────────────────────────────────────┬──────────┐
│ 0x02 │ kid │ network 16 B │ count │ count × ( len 1 B │ "key=value" )   │ sig 32 B │
└──────┴─────┴──────────────┴───────┴────────────────────────────────────┴──────────┘
```

| Field | Meaning |
|---|---|
| `0x02` | Format version. |
| `kid` | Which relay key signed the root: the first byte of `SHA-256("styx-v2/kid\0" ‖ key)`. |
| `network` | The network UUID in RFC 9562 byte order (as written, big-endian). Always version 8. Not secret. |
| `count` | Number of caveats, at most 16. |
| caveats | Each a length byte (3–200) and that many bytes of ASCII `key=value`. |
| `sig` | The final signature of the chain below. |

The whole binary is at most 1024 bytes. A relay checks every bound and the grammar before computing any
MAC, so a login costs at most 17 HMACs.

**The chain**, where `‖` is concatenation and `\0` a zero byte:

```
sig₀ = HMAC-SHA256(key,    "styx-v2/root\0"   ‖ 0x02 ‖ kid ‖ network)
sigᵢ = HMAC-SHA256(sigᵢ₋₁, "styx-v2/caveat\0" ‖ len ‖ caveatᵢ)
token.sig = sigₙ
```

A token carries only its final signature. Appending a caveat needs only that signature, so **any holder
can narrow a token** (§14); removing, changing or reordering one needs an earlier signature that is never
disclosed. A relay verifies by recomputing the chain from its key and comparing in constant time.

**Caveats.** Every caveat must hold. A key the relay does not know **rejects the token**, so a relay never
silently ignores a restriction it predates. Keys are matched case-sensitively.

| Caveat | Value | Several | Effect |
|---|---|---|---|
| `sub` | `[A-Za-z0-9_-]{1,64}` | appended in order, at most 8 | A sub-network path below the network. |
| `host` | hostname | must all be equal (ignoring case) | The only hostname the token may log in as. |
| `hostPrefix` | hostname | all must hold | The hostname must start with each (ignoring case). |
| `hostNot` | hostname | all must hold | The hostname must not be this one (ignoring case). |
| `exp` | decimal Unix seconds, ≤ 253402300799 | earliest wins | Refused at and after this instant; a live registration is evicted then (§6.3). |
| `id` | `[A-Za-z0-9._-]{1,72}` | all apply | A label the relay operator can revoke by (§13). Narrowed tokens inherit it. |

A *hostname* value matches `[A-Za-z0-9._-]{1,63}`. A *decimal* value matches `0|[1-9][0-9]{0,18}`: no
sign, no whitespace, no leading zero; out of range is invalid, never clamped. Contradictory `host` caveats
make a token invalid.

**Paths.** A token's path is the ordered list of its `sub` values; none is a path. Each network-and-path
is a namespace of its own, as isolated from its parent and siblings as two networks are (§2). Paths compare
element by element and case-sensitively.

**Hostnames.** Hostname rules compare ordinally, ignoring case, exactly as the relay compares hostnames
everywhere (§2). When a token carries any hostname caveat, the login's `hostName` must itself match the
hostname grammar, so no lookalike, padding or format character can slip past a rule. **The relay never
renames a peer**: a name the rules do not allow is refused (§5.1).

**Verification**, in order: the prefix; the bounds, encoding and grammar; a `kid` the relay holds; a
version-8 network; the chain; revocations (§13); then `exp`. Every failure but the last is the generic
`"Invalid authorization"`. An expired token is refused with `"Authorization expired"`, which only a valid
signature can reach.

Properties:

- A v2 token is still a **bearer credential**: it needs TLS and careful storage.
- It reveals nothing worth guessing against when the relay key is 64 random bytes. A key derived from a
  passphrase (§13.1) can be guessed at offline, through a deliberately slow derivation, so it is only as
  strong as the passphrase is unpredictable.
- It lives until its `exp`, its `id` or network is revoked, or its relay key is retired.
- Narrowing never widens: a holder of a narrowed token can add restrictions, including deeper `sub`
  paths below its own, but can reach nothing its token did not already allow.

## 5. Methods invoked on the relay

| Method | Requires authentication | Returns |
|---|---|---|
| `Ping` | no | `true` |
| `Authenticate` | no | login response |
| `GetMyIp` | yes | string |
| `Send` | yes | nothing |

Invoking a method that requires authentication before authenticating is fatal: the relay **aborts the
connection without sending a completion record**. A client waiting on that completion waits forever, so
implementations SHOULD treat connection closure as failure of every outstanding invocation.

### 5.1 `Authenticate(login) → response`

```
login    { "authorization": "<token>", "hostName": "<name>" }
response { "authenticated": true|false, "message": "<text>"|null }
```

`message` carries a short human-readable reason on failure — `"Invalid authorization"`,
`"Authorization expired"`, `"Authorization does not allow this hostname"`,
`"Authorization and hostName are both required"`, `"Connection aborted"` — and is null on success. Clients
SHOULD surface it and MUST NOT parse it.

The relay never renames a peer. A `hostName` that a v2 token's hostname caveats do not allow is refused, so
a client is always known by exactly the name it chose.

Both members are mandatory, but omitting one is answered rather than punished: a login missing either
member, under either hub protocol, returns `authenticated: false` with the reason above. A client never
has to distinguish a malformed request from a rejected credential by inspecting transport errors.

Every response, successful or not and for either token format, is delayed to a **minimum of one second**.
It blunts password guessing against v1 tokens. A connection logs in once at a time on either path: a second
`Authenticate` on the same connection waits until the first has answered. Together they cap each connection at
one login a second, which bounds the membership churn a token holder can cause by re-authenticating in a loop. Clients MUST tolerate
this latency and SHOULD apply a timeout of several seconds to the invocation.

Authenticating on an already-authenticated connection is permitted and **replaces** the identity: the
connection is thereafter known by the new hostname, the old hostname vanishes from the network, and the
membership change is broadcast. A client MAY use this to rename itself in place, or to move to another
network or path; the namespace it left is told it has gone. A refused re-authentication leaves the existing
identity as it was.

### 5.2 `Send(targetHosts, payload)`

`targetHosts` is an array of hostnames; `payload` is a byte string. The relay resolves each target
within the sender's network and delivers `Receive` (§6.1) to each resolved connection. There is no
completion value and no acknowledgement.

Delivery semantics:

- **Fan-out, not broadcast.** There is no wildcard target; a sender that wants every peer enumerates
  them from the most recent `Peers` notification.
- **Unknown targets are silently dropped.** A hostname not currently connected in the sender's network
  produces no error, and a partial delivery is indistinguishable from a complete one.
- **No de-duplication.** A hostname listed twice is delivered twice.
- **Loopback is permitted.** A sender MAY address its own hostname and will receive its own payload.
- An empty `targetHosts` array, and empty strings within it, are ignored.
- On `/relay`, ordering is preserved between a given pair of peers, whether or not the sender waits for
  each `Send` to complete: the relay takes a client's invocations one at a time, in the order it received
  them (§3.4), and writes each delivery to the target before it starts the next. A sender MAY therefore send
  `Send` as a non-blocking invocation (no `invocationId`) and send the next at once.
- On `/relay/parallel`, no ordering is implied even between one pair of peers: two `Send`s from one sender
  may arrive in either order. A sender that waits for each `Send`'s completion record before sending the
  next gets them in order there too.
- No ordering is implied across different senders.

### 5.3 `Ping() → true`

Liveness check callable before authentication. Distinct from transport-level ping records, and not
required for keep-alive.

### 5.4 `GetMyIp() → string`

Returns the relay's view of the caller's remote address, as a string. Behind a reverse proxy this is the
forwarded client address. Informational only.

## 6. Methods the relay invokes on the client

A client MUST accept these three invocations. They are sent as SignalR invocations with no invocation
identifier, so no completion is expected; a client MAY simply ignore any it does not need. Handlers
SHOULD return promptly, since the relay dispatches them sequentially per connection.

### 6.1 `Receive(sourceHost, sourceIp, payload)`

Delivers a payload sent by another peer.

`sourceHost` is asserted by the **relay**, from the sending connection's registered identity, and not by
the sender. A peer therefore cannot forge its identity in the eyes of another peer, though see §10 on
what this does and does not buy. `sourceIp` is the sender's remote address, informational.

### 6.2 `Peers(hostNames)`

Reports the current membership of the client's network, **excluding the recipient**, ordered
lexicographically. Sent to every member of a network whenever any member authenticates, is displaced, or
disconnects. It is a complete snapshot, not a delta; a client SHOULD replace its view rather than merge.

A newly authenticated peer receives its own snapshot, which is empty if it is alone in the network.

When an authentication displaces an existing registration for the same hostname (§6.3), the other members of
the network receive **two** snapshots in this order: one without the hostname, then one with it. Peer identity
on the wire is the hostname, so a displacement is otherwise indistinguishable from no change at all, and a
client tracking membership by name alone would never learn that the peer behind it restarted. The relay
guarantees the pair is delivered in that order; it does not guarantee that no other membership change is
interleaved with it. Clients that reset per-peer state on departure get re-announcement for free; clients that
do not SHOULD treat the removal as a genuine departure of that peer.

Note the ordering hazard: `Peers` **may arrive before the completion record for the client's own
`Authenticate` invocation**. Clients MUST register their callbacks before invoking `Authenticate`, or
they will miss the first membership snapshot and any payload a peer sends in reaction to it.

### 6.3 `Kicked(reason)`

Sent when the relay evicts the client's registration. Two reasons are issued:

- `"duplicate hostname"`: another connection authenticated with the same hostname in the same namespace,
  and the newcomer wins. Stale registrations from an unclean disconnect are evicted the same way, which is
  what makes reconnection after a dropped link work. Every other member of the namespace sees the displaced
  hostname leave and return, per §6.2.
- `"authorization expired"`: the v2 token the registration was made with reached its `exp`. The other
  members receive one membership snapshot without the hostname. A later re-authentication on the same
  connection, with another token, replaces the expiry along with the identity.

A kicked client is deregistered but **its connection stays open**. Anonymous methods still work, so the
client MAY re-authenticate on the same connection. The first authenticated method invoked while
deregistered aborts the connection, per §5. Clients SHOULD treat `Kicked` as a request to stop rather
than as a hint to reconnect immediately: a client that reconnects and reclaims its hostname unconditionally
will ping-pong with the peer that displaced it indefinitely.

## 7. Session lifecycle

Records sent by the client are left-aligned below, records sent by the relay right-aligned. Record
separators are omitted for legibility; see §3.2.

```
  client                                          relay
  │                                              │
  │ (optional) POST /relay/negotiate             │
  │─────────────────────────────────────────────▶│
  │ WebSocket /relay                             │
  │─────────────────────────────────────────────▶│
  │ handshake {"protocol":"json","version":1}    │
  │─────────────────────────────────────────────▶│
  │                                           {} │
  │◀─────────────────────────────────────────────│
  │ register Receive/Peers/Kicked handlers       │  ← before Authenticate, not after
  │ invoke Authenticate                          │
  │─────────────────────────────────────────────▶│
  │                                    Peers […] │  ← may precede the completion below
  │◀─────────────────────────────────────────────│
  │                   completion {authenticated} │  ← at least 1 s after the invocation
  │◀─────────────────────────────────────────────│
  │ Send / Receive …                             │
  │◀────────────────────────────────────────────▶│
```

On `authenticated: false` the connection remains usable for anonymous methods; the client MAY retry with
a different token. On disconnection the relay deregisters the peer and broadcasts the resulting
membership to the rest of the network.

Clients SHOULD reconnect automatically, with a delay of some seconds, and SHOULD apply random jitter to
that delay — a relay restart otherwise brings every peer back in lockstep, repeatedly.

## 8. HTTP interface

Alongside the hub, the relay serves static files from its root and two JSON endpoints. JSON member names are
camelCase. `/api/network-config` holds every answer to a fixed floor, for the same reason as §5.1.
`/api/status` holds only its refusals, and a client can give up on one sooner, so what limits guessing there
is the per-address count below and the strength of v2 keys.

Wrong v1 tokens that fail to decrypt, and wrong relay passwords, are counted per client address (an IPv6
address by its /64). Past 20 in a minute, that address's further attempts **on those two paths only** are
refused without being checked until the minute passes. v2 tokens are never refused by address.

### 8.1 `GET /api/status`

```
Authorization: Bearer <authorization token>
→ 200 {"peers":["alpha-box","beta"]}
→ 401 (invalid or absent token)
```

Membership of the token's network and path without joining it. Either token format. A valid token is
answered at once; a refusal takes at least 2 s.

### 8.2 `POST /api/network-config`

```
{"password":"<relay password>"}
→ 200 {"authorization":"<styx2. token>"}
→ 401 (wrong password)
→ 404 (the relay has no v2 key or no password, so creates no networks)
```

Mints a v2 root token for a **freshly generated version-8 network** — a self-service way to create an
isolated network on a relay whose password (`RELAY_PASSWORD`) you know. It cannot produce a token for an existing network.
It never issues a v1 token. Minimum response time 5 s.

## 9. Client provisioning

A client needs a relay URL and a token. The conventional way to carry both as a single opaque string is
base64 of a JSON object:

```json
{"StyxServer":"https://styx.example.org","EncryptionKey":"<application-level secret>","Authorization":"<token>"}
```

Readers SHOULD match member names case-insensitively. Writers SHOULD emit exactly the form above (§14.3):
some clients in the field parse it by hand and accept nothing else. `authorization` is opaque to a client
that only connects: it MUST NOT parse it, and a v2 token goes where a v1 token went. `encryptionKey` is **never transmitted to the relay** and
has no meaning to it; it is carried here purely so that one blob provisions both relay access and
whatever end-to-end key the application layered on top (§10). A client with no such layer MAY omit it.

This blob is a convention, not part of the protocol. It is also, in full, the credential: treat it as a
secret.

## 10. Security considerations

**The relay sees every payload byte.** Styx authenticates network membership and routes; it provides no
confidentiality or integrity for payload contents against the relay operator. An application whose
payloads are sensitive MUST encrypt them end-to-end between peers, with keys the relay never receives.

Hydra encrypts these opaque payloads end-to-end. Its optional remote-management messages additionally
require a separately paired controller credential, signed requests, freshness checks, and replay protection;
possessing the ordinary Styx/Hydra network configuration does not itself grant remote-management authority.
Even then the relay observes hostnames, the peer graph, message sizes, and timing.

**Membership is the whole authorization model.** There is no per-peer permission, no read/write split,
and no restriction on who may address whom. Every peer in a network is fully trusted by every other peer:
any member can address any other, and one compromised token compromises the network. Partition mutually
untrusting parties into separate networks.

**Sender identity is relay-asserted, and only that.** `sourceHost` cannot be forged by a peer, but a token
without hostname caveats can authenticate *as* any unclaimed hostname, and can displace a peer that already
holds one (§6.2). Repeatedly claiming a hostname is an effective denial of service against that peer, and
lets an attacker with a valid token impersonate it to the rest of the network. Applications requiring
genuine peer authentication MUST establish it themselves, above Styx.

**Give potentially hostile holders hostname-restricted tokens.** Duplicate hostnames are always resolved by
displacing the old holder; there is no other policy. Where a holder must not take some names, issue it a v2
token that says so: allow names with `host` or `hostPrefix`, or deny them with `hostNot`, as the situation
demands (§14.4). Allowlists are the stronger: a `hostNot` excludes only what it lists.

**Key and password compromise.** A v2 relay key mints tokens for every v2 network signed under it; recover
by adding a new key, reissuing, and retiring the old one (§13). The v1 password mints v1 tokens for any
legacy network whose UUID is known, never for a v2 network, and creates new v2 networks through §8.2;
recover by rotating it, which invalidates every v1 token at once. Every v1 token is an offline verifier of
the password (§4.1), so a relay that keeps v1 needs a long random password.

**Availability of v1.** A v1 login that is not already cached costs a PBKDF2, and only a few run at once
with a bounded wait. Junk from many addresses can therefore starve v1 logins (after a restart especially,
when nothing is cached), though never the relay's CPU and never a v2 login.

**Revocation.** v2 tokens are revoked by `id` or by network (§13), or end at their `exp`. v1 tokens are
revoked only by network or by rotating the password.

**Transport.** Tokens are bearer credentials and are replayable, so a relay reachable over an untrusted
network MUST be served over TLS. The relay trusts forwarded headers from private address ranges to learn
client addresses, so it MUST NOT be reachable from private networks except through its reverse proxy.

**Resource exposure.** An authenticated peer can fan a 32 MiB payload out to every member of its
network, and nothing rate-limits `Send`. Applications SHOULD bound their own throughput.

## 11. Worked exchange

Verified against a live relay, `json` protocol, `␞` denoting `0x1E`. Two clients join network
`1337b007-…` as `alpha-box` and `beta`; `beta` sends four bytes to `alpha-box`.

```
alpha → {"protocol":"json","version":1}␞
alpha ← {}␞
alpha → {"type":1,"invocationId":"1","target":"Authenticate",
          "arguments":[{"authorization":"<token>","hostName":"ALPHA-Box"}]}␞
alpha ← {"type":1,"target":"Peers","arguments":[[]]}␞
alpha ← {"type":3,"invocationId":"1","result":{"authenticated":true,"message":null}}␞

  (beta performs the same handshake and authenticates as "beta")

alpha ← {"type":1,"target":"Peers","arguments":[["beta"]]}␞
beta  ← {"type":1,"target":"Peers","arguments":[["alpha-box"]]}␞

beta  → {"type":1,"target":"Send","arguments":[["ALPHA-box"],"AAEC/w=="]}␞
alpha ← {"type":1,"target":"Receive","arguments":["beta","203.0.113.7","AAEC/w=="]}␞

  (a third connection authenticates as "beta")

beta  ← {"type":1,"target":"Kicked","arguments":["duplicate hostname"]}␞
alpha ← {"type":1,"target":"Peers","arguments":[[]]}␞
alpha ← {"type":1,"target":"Peers","arguments":[["beta"]]}␞
```

Note `ALPHA-Box` announced, `alpha-box` reported; `ALPHA-box` accepted as a target; and the payload
carried through unaltered.

## 12. Implementation checklist

A minimal client:

1. Open a WebSocket to `<server>/relay`. Negotiation is optional (§3.1).
2. Send the handshake; await `{}`.
3. Register `Receive`, `Peers` and `Kicked` handlers **before** step 4.
4. Invoke `Authenticate` with the token and a hostname. Allow for the ≥ 1 s floor.
5. Maintain the peer set from `Peers` snapshots, replacing wholesale. A hostname that leaves and returns
   across two snapshots is a peer that reconnected; discard whatever you cached about it.
6. `Send` to explicit hostname lists; expect no acknowledgement and tolerate silent loss.
7. Answer relay ping records, emit your own, and reconnect with jittered backoff on loss.
8. Treat `Kicked` as a stop, not a retry.

Anything beyond that — reliability, ordering across peers, confidentiality, authenticity of peers,
message structure — is the application's to build. The relay carries bytes between names.

## 13. Relay configuration

| Variable | Effect |
|---|---|
| `RELAY_SIGNING_KEY` | The v2 key, which signs tokens: base64 of 64 random bytes. Not a blob's `EncryptionKey` (§9), which the relay never sees. During a rotation, a comma-separated list whose first entry mints and whose others only verify. |
| `RELAY_SIGNING_PASSPHRASE` | A v2 key as a passphrase instead (§13.1). Set, its key comes first, so it mints, and `RELAY_SIGNING_KEY`'s keys only verify. |
| `RELAY_PASSWORD` | The relay password. Set, v1 tokens are accepted, and `POST /api/network-config` creates networks for whoever knows it (given a v2 key); unset, neither. |
| `RELAY_REVOKED_NETWORKS` | Comma-separated network UUIDs whose tokens are refused, v1 and v2. |
| `RELAY_REVOKED_IDS` | Comma-separated v2 `id` caveat values whose tokens are refused (compared ordinally). |
| `RELAY_PARALLEL_INVOCATIONS` | How many of one client's invocations `/relay/parallel` runs at once (§3.4): a whole number from 1 to 64, default 4. Each invocation running can hold a whole message, so one client can hold this many times 32 MiB; above 8 the relay says how much when it starts. At 1 the path is not parallel, which the relay also says when it starts: a way to tell whether a fault needs invocations to overlap without changing the client. |
| `LOCAL_PORT` | Listening port. Default `5000`. |
| `LOCAL_ONLY` | `true` binds loopback only. |
| `DEBUG_MESSAGES` | `true` logs per-payload routing metadata (network, sender, targets, size — not contents). |

The relay refuses to start unless it can verify something (`RELAY_SIGNING_KEY`, `RELAY_SIGNING_PASSPHRASE` or
`RELAY_PASSWORD`), and refuses a malformed key, a passphrase outside the rules of §13.1, the same key twice,
two keys sharing a `kid`, the published test key or passphrase of §14.6, a `RELAY_PARALLEL_INVOCATIONS` that is
not a whole number from 1 to 64, or a retired setting (`RELAY_KEY`,
`RELAY_KEY_PASSPHRASE`, `RELAY_ADMIN_PASSWORD`), naming what replaced it. Every
change to these is a restart, which drops every peer; that is also what makes a revocation complete.

**Rotating the v2 key:** the first key mints and the rest only verify, and a passphrase's key comes first.
Put the new key first (as `RELAY_SIGNING_PASSPHRASE`, or first in `RELAY_SIGNING_KEY` with no passphrase set) and the old
one after it in `RELAY_SIGNING_KEY`; reissue tokens; then remove the old key, which ends every token it signed. To
keep an old passphrase's key verifying, put the key it derives in `RELAY_SIGNING_KEY` and unset or replace
`RELAY_SIGNING_PASSPHRASE`; `Styx derive` prints that key, reading the passphrase from standard input (`docker run
--rm -i`).

The relay honours forwarded headers for client-address determination behind a reverse proxy, and disables
Nagle's algorithm on accepted connections. It holds no database and no on-disk state; restarting it drops
every peer, which then reconnect and re-announce.

### 13.1 Keys from a passphrase

Every client holds a token, and a token lets anyone test guesses at the key offline, as fast as their
hardware allows. A random key leaves nothing to guess. A passphrase is exactly as strong as it is
unpredictable, so make it **long and unique**: a long sentence nobody has ever written down, a long stretch
of random hammering on the keyboard, or best of all a password manager's generator. Never a quotation, a
lyric, a name or anything else that appears in print: the salt below is the same for every relay, so a
passphrase anyone could think of falls to a dictionary that is computed once and then tried against every
relay at the same time. The rules below set a floor, not a target.

The key is derived as follows, and any implementation that follows it gets the same key:

1. Refuse it if it is not valid Unicode, then normalise it to Unicode NFC.
2. Refuse it if it has fewer than 64 characters or fewer than 16 different ones
   (counting Unicode scalar values, not bytes or UTF-16 units); holds a line break (U+000A or U+000D)
   anywhere; or starts or ends with a space, U+0009 to U+000D, U+0085, U+00A0 or U+FEFF, which a copy, a
   file or a shell adds and drops without anyone noticing.
3. `key = PBKDF2-HMAC-SHA512(password = UTF-8 of the passphrase, salt = "styx-v2/passphrase" (ASCII, no
   terminator), iterations = 1 000 000, length = 64 bytes)`.

The derivation costs the relay about a third of a second, once, when it starts, and costs an attacker the
same for every guess.

## 14. Issuing tokens

This section is for anyone who hands tokens out: a server giving each of its clients its own token, a page
served to a browser, a tool provisioning devices. It needs no relay key and no library; everything is here,
including complete reference code (§14.6). A client that only connects needs none of it.

What an issuer can do without a key is **read** a token and **narrow** it. Minting a root token for a new
network needs the relay key, so it is done by the relay (`POST /api/network-config`, §8.2) or by its
operator. A v1 token cannot be narrowed at all; a network that needs narrowed tokens is a v2 network.

### 14.1 Reading a token

1. Check the token starts with `styx2.`, and strip that.
2. Decode the rest as base64url without padding, and re-encode it: a token whose re-encoding differs is
   invalid.
3. Read the fields of §4.2: version (must be 2), `kid`, the 16-byte network UUID (version 8: the high nibble
   of its seventh byte is 8), `count` (at most 16), then `count` caveats each as a length byte (3–200) and
   that many ASCII bytes; the last 32 bytes are the signature, and nothing may lie between the caveats and
   the signature. The whole binary is 51 to 1024 bytes.

Reading tells you what a token claims. It proves nothing: a token received from someone else may be forged
or stale, and only a relay, holding the key, can tell. Never base an authorization decision on decoding a
token.

### 14.2 Narrowing a token

To append caveats `c₁ … cₖ` to a token:

1. Read the token (§14.1).
2. Check every new caveat against the grammar of §4.2: a known key, matched case-sensitively, and a value
   matching that key's pattern; for `exp`, at most 253402300799.
3. Check the result stays in bounds: at most 16 caveats in all, at most 8 `sub`, no two `host` values that
   differ (ignoring case), at most 1024 bytes, and any length limit of the client it is for.
4. Starting from the token's own signature, for each new caveat in order compute
   `sig = HMAC-SHA256(sig, "styx-v2/caveat\0" ‖ len ‖ caveat)`, where `len` is one byte, the caveat's length.
5. Write the binary again with `count` increased, the new caveats appended after the old ones (each as its
   length byte and its bytes), and the final `sig` in place of the old signature.
6. Encode canonically: base64url, no padding, prefixed with `styx2.`.

Do every check before computing anything, so a mistake is caught at the issuer rather than as a refused
login at a client that may not be able to say why. Pitfalls: the length byte is part of each MAC input; the
encoding has no padding; caveat keys are case-sensitive (`Sub=x` is invalid); hostname rules are compared by
the relay ignoring case, so `hostNot=Admin` does exclude `admin`; `sub` values cannot contain `/` or
`.`.

### 14.3 Network config blobs

A blob (§9) is written in exactly one form, because some readers in the field parse it by hand and accept
nothing else:

- exactly the members `StyxServer`, `EncryptionKey`, `Authorization`, in that order, with no others;
- compact JSON, no whitespace, UTF-8;
- every value printable ASCII (0x20–0x7E) **excluding `"` and `\`**, so that no JSON escape ever applies;
- standard base64 with padding around it.

A writer refuses a value outside that set; it never escapes one. Escaping is not portable here: one
reader copies a value raw up to the next `"`, and common JSON encoders escape differently (some escape `+`).
A client with little storage may limit the decoded blob's size; check it before handing one out. To narrow a blob, read it leniently (any member
casing, ordinary JSON), narrow its `Authorization` (§14.2), and write it again in this form.

### 14.4 Choosing caveats

| To | Give | It protects | It does not protect |
|---|---|---|---|
| Hand one token to many holders that must not take the owner's name (a public page, a device fleet) | `hostNot` for **every** privileged name on the network, and an `id` | The listed names | Any name not listed, including every other holder's |
| Bind each holder to its own names (one token per client) | `host`, or `hostPrefix`, plus an `id` | Every name outside the holder's own | Names that share the prefix: give prefixes no other holder's names can start with |
| Separate groups (a room per game) | `sub` | Everything outside the path | Peers within the same path from each other |
| Limit a token's life | `exp` | Use after the instant | A device that cannot refresh its config goes dark at it |

A token can carry several of these at once.

### 14.5 Security for issuers

- **The root token is ownership.** Keep it as a secret on the issuing server: never in a browser, a client
  config, a log or a repository. Narrow on the server and hand out only the result; never narrow from the
  root in page script.
- **Every narrowed token is still a bearer credential.** Keep it out of logs, URLs and error messages.
- **Give every token you hand out a unique `id`**, and record which went to whom: it is the only way to
  revoke one token without the rest. Never reuse an `id` after revoking it.
- **Prefer allowlists.** `host` and `hostPrefix` bind a holder to its own names; `hostNot` excludes only
  what it lists, so it must list every privileged name, and a name added later means reissuing every token
  that should exclude it.
- **Use `exp` wherever the client can refresh its config.** Leave it off only where a refused token strands
  the device, knowing that `id` and network revocation are then the only ways to end the token.
- **Holders can always narrow further**, including opening paths below their own with `sub`. That reaches
  nothing they could not already reach, but it means a `sub` caveat caps nothing.
- **Check the result before handing it out**: grammar, bounds, and the length limits of the client it is
  for. A token the relay rejects fails closed, but only once it reaches the client.
- The reference code below never verifies a token and needs no constant-time comparison. Do not build a
  verifier from it, or from the vectors, to gate access: verification belongs to the relay.

### 14.6 Reference implementations and test vectors

Complete implementations of reading, narrowing, blob writing and deriving a key from a passphrase, in Python,
JavaScript, C# and Rust. Each validates everything before signing anything, and each is run against the
vectors below, and its tokens against a real relay, on every build of this repository.

**Python** (standard library only):

<!-- styx-test:python -->
```python
import base64, hashlib, hmac, json, re, unicodedata

PREFIX = "styx2."
HOSTNAME = re.compile(r"[A-Za-z0-9._-]{1,63}")
GRAMMAR = {"sub": re.compile(r"[A-Za-z0-9_-]{1,64}"), "host": HOSTNAME, "hostPrefix": HOSTNAME,
           "hostNot": HOSTNAME, "id": re.compile(r"[A-Za-z0-9._-]{1,72}"), "exp": re.compile(r"0|[1-9][0-9]{0,18}")}

def _encode(raw):
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("ascii")

def _decode(text):
    raw = base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))
    if _encode(raw) != text:
        raise ValueError("not canonical base64url")
    return raw

def check_caveat(caveat):
    key, sep, value = caveat.partition("=")
    if not sep or key not in GRAMMAR or not GRAMMAR[key].fullmatch(value):
        raise ValueError(f"caveat outside the grammar: {caveat!r}")
    if key == "exp" and int(value) > 253402300799:
        raise ValueError("exp beyond 9999-12-31T23:59:59Z")

def read(token):
    """kid, network, caveats and signature. Informational only: it proves nothing about validity."""
    if not token.startswith(PREFIX):
        raise ValueError("not a v2 token; a v1 token cannot be narrowed")
    raw = _decode(token[len(PREFIX):])
    if not 51 <= len(raw) <= 1024 or raw[0] != 2 or raw[18] > 16 or raw[8] >> 4 != 8:
        raise ValueError("malformed token")
    caveats, offset = [], 19
    for _ in range(raw[18]):
        length = raw[offset] if offset < len(raw) - 32 else 0
        if not 3 <= length <= 200 or offset + 1 + length > len(raw) - 32:
            raise ValueError("malformed token")
        caveats.append(raw[offset + 1:offset + 1 + length].decode("ascii"))
        offset += 1 + length
    if offset != len(raw) - 32:
        raise ValueError("malformed token")
    return {"kid": raw[1], "network": raw[2:18], "caveats": caveats, "signature": raw[-32:]}

def narrow(token, caveats, max_length=None):
    """The token with caveats appended. Needs no key; validates everything before signing anything."""
    old = read(token)
    for caveat in caveats:
        check_caveat(caveat)
    every = old["caveats"] + list(caveats)
    hosts = {c[len("host="):].lower() for c in every if c.startswith("host=")}
    if len(every) > 16 or len(hosts) > 1 or sum(c.startswith("sub=") for c in every) > 8:
        raise ValueError("too many caveats, contradictory hosts, or a path deeper than 8")
    size = 51 + sum(1 + len(c) for c in every)
    if size > 1024 or (max_length is not None and len(PREFIX) + (size * 4 + 2) // 3 > max_length):
        raise ValueError("token too long")
    signature = old["signature"]
    for caveat in caveats:
        data = caveat.encode("ascii")
        signature = hmac.new(signature, b"styx-v2/caveat\0" + bytes([len(data)]) + data, hashlib.sha256).digest()
    body = bytes([2, old["kid"]]) + old["network"] + bytes([len(every)])
    body += b"".join(bytes([len(c)]) + c.encode("ascii") for c in every) + signature
    return PREFIX + _encode(body)

def make_blob(styx_server, encryption_key, authorization):
    """The network config blob, in the one form every client reads."""
    for value in (styx_server, encryption_key, authorization):
        if any(not " " <= ch <= "~" or ch in '"\\' for ch in value):
            raise ValueError("blob values must be printable ASCII without '\"' or '\\'")
    text = f'{{"StyxServer":"{styx_server}","EncryptionKey":"{encryption_key}","Authorization":"{authorization}"}}'
    return base64.b64encode(text.encode("ascii")).decode("ascii")

def narrow_blob(blob, caveats, max_token_length=None, max_blob_bytes=None):
    fields = {k.lower(): v for k, v in json.loads(base64.b64decode(blob, validate=True)).items()}
    token = narrow(fields["authorization"], caveats, max_token_length)
    result = make_blob(fields["styxserver"], fields["encryptionkey"], token)
    if max_blob_bytes is not None and len(base64.b64decode(result)) > max_blob_bytes:
        raise ValueError("blob too large")
    return result

EDGES = " \t\n\x0b\x0c\r\x85\xa0\ufeff"

def key_from_passphrase(passphrase):
    """The relay key for a passphrase (§13.1). Only an operator, who holds the passphrase, needs this."""
    text = unicodedata.normalize("NFC", passphrase)
    if (len(text) < 64 or len(set(text)) < 16 or "\n" in text or "\r" in text
            or text[0] in EDGES or text[-1] in EDGES or any(0xD800 <= ord(ch) <= 0xDFFF for ch in text)):
        raise ValueError("a passphrase is valid Unicode, 64 characters and 16 different ones, on one line, "
                         "with no whitespace or byte order mark at either end")
    return hashlib.pbkdf2_hmac("sha512", text.encode("utf-8"), b"styx-v2/passphrase", 1_000_000, 64)
```

**JavaScript** (WebCrypto; Node 20 or later, or a browser from 2023 on):

<!-- styx-test:javascript -->
```javascript
const PREFIX = "styx2.";
const HOSTNAME = /^[A-Za-z0-9._-]{1,63}$/;
const GRAMMAR = { sub: /^[A-Za-z0-9_-]{1,64}$/, host: HOSTNAME, hostPrefix: HOSTNAME, hostNot: HOSTNAME,
                  id: /^[A-Za-z0-9._-]{1,72}$/, exp: /^(0|[1-9][0-9]{0,18})$/ };
const ascii = text => Uint8Array.from(text, ch => ch.charCodeAt(0));

function encode(bytes) {
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function decode(text) {
  if (!/^[A-Za-z0-9_-]*$/.test(text)) throw new Error("not base64url");
  const binary = atob(text.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - text.length % 4) % 4));
  const bytes = ascii(binary);
  if (encode(bytes) !== text) throw new Error("not canonical base64url");
  return bytes;
}

function checkCaveat(caveat) {
  const at = caveat.indexOf("=");
  const key = caveat.slice(0, at), value = caveat.slice(at + 1);
  if (at <= 0 || !Object.hasOwn(GRAMMAR, key) || !GRAMMAR[key].test(value)) throw new Error(`caveat outside the grammar: ${caveat}`);
  if (key === "exp" && BigInt(value) > 253402300799n) throw new Error("exp beyond 9999-12-31T23:59:59Z");
}

// kid, network, caveats and signature. Informational only: it proves nothing about validity.
function read(token) {
  if (!token.startsWith(PREFIX)) throw new Error("not a v2 token; a v1 token cannot be narrowed");
  const raw = decode(token.slice(PREFIX.length));
  if (raw.length < 51 || raw.length > 1024 || raw[0] !== 2 || raw[18] > 16 || raw[8] >> 4 !== 8) throw new Error("malformed token");
  const caveats = [];
  let offset = 19;
  for (let i = 0; i < raw[18]; i++) {
    const length = offset < raw.length - 32 ? raw[offset] : 0;
    if (length < 3 || length > 200 || offset + 1 + length > raw.length - 32) throw new Error("malformed token");
    caveats.push(String.fromCharCode(...raw.slice(offset + 1, offset + 1 + length)));
    offset += 1 + length;
  }
  if (offset !== raw.length - 32) throw new Error("malformed token");
  return { kid: raw[1], network: raw.slice(2, 18), caveats, signature: raw.slice(raw.length - 32) };
}

// The token with caveats appended. Needs no key; validates everything before signing anything.
async function narrow(token, caveats, maxLength = Infinity) {
  const old = read(token);
  caveats.forEach(checkCaveat);
  const every = [...old.caveats, ...caveats];
  const hosts = new Set(every.filter(c => c.startsWith("host=")).map(c => c.slice(5).toLowerCase()));
  if (every.length > 16 || hosts.size > 1 || every.filter(c => c.startsWith("sub=")).length > 8)
    throw new Error("too many caveats, contradictory hosts, or a path deeper than 8");
  const size = 51 + every.reduce((sum, c) => sum + 1 + c.length, 0);
  if (size > 1024 || PREFIX.length + Math.floor((size * 4 + 2) / 3) > maxLength) throw new Error("token too long");
  let signature = old.signature;
  for (const caveat of caveats) {
    const key = await crypto.subtle.importKey("raw", signature, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
    const data = ascii(caveat);
    signature = new Uint8Array(await crypto.subtle.sign("HMAC", key, new Uint8Array([...ascii("styx-v2/caveat\0"), data.length, ...data])));
  }
  const body = new Uint8Array([2, old.kid, ...old.network, every.length,
    ...every.flatMap(c => [c.length, ...ascii(c)]), ...signature]);
  return PREFIX + encode(body);
}

// The network config blob, in the one form every client reads.
function makeBlob(styxServer, encryptionKey, authorization) {
  for (const value of [styxServer, encryptionKey, authorization])
    if (!/^[ -~]*$/.test(value) || /["\\]/.test(value)) throw new Error("blob values must be printable ASCII without '\"' or '\\'");
  return btoa(`{"StyxServer":"${styxServer}","EncryptionKey":"${encryptionKey}","Authorization":"${authorization}"}`);
}

async function narrowBlob(blob, caveats, maxTokenLength = Infinity, maxBlobBytes = Infinity) {
  const fields = Object.fromEntries(Object.entries(JSON.parse(atob(blob))).map(([k, v]) => [k.toLowerCase(), v]));
  const result = makeBlob(fields.styxserver, fields.encryptionkey, await narrow(fields.authorization, caveats, maxTokenLength));
  if (atob(result).length > maxBlobBytes) throw new Error("blob too large");
  return result;
}

// The relay key for a passphrase (§13.1). Only an operator, who holds the passphrase, needs this.
const EDGES = " \t\n\v\f\r\u0085\u00a0\ufeff";

async function keyFromPassphrase(passphrase) {
  const text = passphrase.normalize("NFC");
  const chars = [...text];
  if (!text.isWellFormed() || chars.length < 64 || new Set(chars).size < 16 || /[\n\r]/.test(text)
      || EDGES.includes(chars[0]) || EDGES.includes(chars.at(-1)))
    throw new Error("a passphrase is valid Unicode, 64 characters and 16 different ones, on one line, " +
      "with no whitespace or byte order mark at either end");
  const material = await crypto.subtle.importKey("raw", new TextEncoder().encode(text), "PBKDF2", false, ["deriveBits"]);
  const params = { name: "PBKDF2", hash: "SHA-512", salt: ascii("styx-v2/passphrase"), iterations: 1000000 };
  return new Uint8Array(await crypto.subtle.deriveBits(params, material, 512));
}
```

**C#** (.NET 8 or later, no packages):

<!-- styx-test:csharp -->
```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record StyxTokenParts(byte KeyId, byte[] Network, List<string> Caveats, byte[] Signature);

public static class StyxIssue
{
    private const string Prefix = "styx2.";
    private const string HostName = @"^[A-Za-z0-9._-]{1,63}\z";
    private static readonly Dictionary<string, Regex> Grammar = new()
    {
        ["sub"] = new Regex(@"^[A-Za-z0-9_-]{1,64}\z"),
        ["host"] = new Regex(HostName),
        ["hostPrefix"] = new Regex(HostName),
        ["hostNot"] = new Regex(HostName),
        ["id"] = new Regex(@"^[A-Za-z0-9._-]{1,72}\z"),
        ["exp"] = new Regex(@"^(0|[1-9][0-9]{0,18})\z"),
    };

    private static string Encode(byte[] raw) => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string text)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4)); }
        catch (FormatException) { throw new FormatException("not base64url"); }
        if (Encode(raw) != text) throw new FormatException("not canonical base64url");
        return raw;
    }

    public static void CheckCaveat(string caveat)
    {
        var at = caveat.IndexOf('=');
        if (at < 0 || !Grammar.TryGetValue(caveat[..at], out var pattern) || !pattern.IsMatch(caveat[(at + 1)..]))
            throw new FormatException($"caveat outside the grammar: {caveat}");
        if (caveat[..at] == "exp" && ulong.Parse(caveat[(at + 1)..], CultureInfo.InvariantCulture) > 253402300799)
            throw new FormatException("exp beyond 9999-12-31T23:59:59Z");
    }

    /// <summary>Key id, network, caveats and signature. Informational only: it proves nothing about validity.</summary>
    public static StyxTokenParts Read(string token)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("not a v2 token; a v1 token cannot be narrowed");
        var raw = Decode(token[Prefix.Length..]);
        if (raw.Length is < 51 or > 1024 || raw[0] != 2 || raw[18] > 16 || raw[8] >> 4 != 8) throw new FormatException("malformed token");
        var (caveats, offset, end) = (new List<string>(), 19, raw.Length - 32);
        for (var i = 0; i < raw[18]; i++)
        {
            var length = offset < end ? raw[offset] : 0;
            if (length is < 3 or > 200 || offset + 1 + length > end || raw.AsSpan(offset + 1, length).ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
                throw new FormatException("malformed token");
            caveats.Add(Encoding.ASCII.GetString(raw, offset + 1, length));
            offset += 1 + length;
        }
        if (offset != end) throw new FormatException("malformed token");
        return new StyxTokenParts(raw[1], raw[2..18], caveats, raw[end..]);
    }

    /// <summary>The token with caveats appended. Needs no key; validates everything before signing anything.</summary>
    public static string Narrow(string token, IReadOnlyList<string> caveats, int maxLength = int.MaxValue)
    {
        var old = Read(token);
        foreach (var caveat in caveats) CheckCaveat(caveat);
        var every = old.Caveats.Concat(caveats).ToList();
        var hosts = every.Where(c => c.StartsWith("host=", StringComparison.Ordinal)).Select(c => c[5..].ToLowerInvariant()).Distinct().Count();
        if (every.Count > 16 || hosts > 1 || every.Count(c => c.StartsWith("sub=", StringComparison.Ordinal)) > 8)
            throw new FormatException("too many caveats, contradictory hosts, or a path deeper than 8");
        var size = 51 + every.Sum(c => 1 + c.Length);
        if (size > 1024 || Prefix.Length + (size * 4 + 2) / 3 > maxLength) throw new FormatException("token too long");
        var signature = old.Signature;
        foreach (var caveat in caveats)
        {
            var data = Encoding.ASCII.GetBytes(caveat);
            byte[] message = [.. "styx-v2/caveat\0"u8, (byte)data.Length, .. data];
            signature = HMACSHA256.HashData(signature, message);
        }
        var body = new List<byte> { 2, old.KeyId };
        body.AddRange(old.Network);
        body.Add((byte)every.Count);
        foreach (var caveat in every)
        {
            body.Add((byte)caveat.Length);
            body.AddRange(Encoding.ASCII.GetBytes(caveat));
        }
        body.AddRange(signature);
        return Prefix + Encode([.. body]);
    }

    /// <summary>The network config blob, in the one form every client reads.</summary>
    public static string MakeBlob(string styxServer, string encryptionKey, string authorization)
    {
        foreach (var value in new[] { styxServer, encryptionKey, authorization })
            if (value.Any(ch => ch is < ' ' or > '~' or '"' or '\\')) throw new FormatException("blob values must be printable ASCII without '\"' or '\\'");
        var json = $$"""{"StyxServer":"{{styxServer}}","EncryptionKey":"{{encryptionKey}}","Authorization":"{{authorization}}"}""";
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(json));
    }

    public static string NarrowBlob(string blob, IReadOnlyList<string> caveats, int maxTokenLength = int.MaxValue, int maxBlobBytes = int.MaxValue)
    {
        using var json = JsonDocument.Parse(Convert.FromBase64String(blob));
        var fields = new Dictionary<string, string>();
        foreach (var member in json.RootElement.EnumerateObject())
            if (member.Value.ValueKind == JsonValueKind.String) fields[member.Name.ToLowerInvariant()] = member.Value.GetString()!;
        string Field(string name) => fields.TryGetValue(name, out var value) ? value : throw new FormatException($"the blob has no {name}");
        var result = MakeBlob(Field("styxserver"), Field("encryptionkey"), Narrow(Field("authorization"), caveats, maxTokenLength));
        if (Convert.FromBase64String(result).Length > maxBlobBytes) throw new FormatException("blob too large");
        return result;
    }

    /// <summary>The relay key for a passphrase (§13.1). Only an operator, who holds the passphrase, needs this.</summary>
    public static byte[] KeyFromPassphrase(string passphrase)
    {
        const string edges = " \t\n\v\f\r\u0085\u00a0\ufeff";
        string text;
        try { text = passphrase.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new FormatException("a passphrase is valid Unicode"); }
        var runes = text.EnumerateRunes().ToList();
        if (runes.Count < 64 || runes.Distinct().Count() < 16 || text.Contains('\n') || text.Contains('\r') || edges.Contains(text[0]) || edges.Contains(text[^1]))
            throw new FormatException("a passphrase is 64 characters and 16 different ones, on one line, with no whitespace or byte order mark at either end");
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(text), "styx-v2/passphrase"u8.ToArray(), 1_000_000, HashAlgorithmName.SHA512, 64);
    }
}
```

**Rust**, with these dependencies:

<!-- styx-test:cargo -->
```toml
[dependencies]
base64 = "=0.22.1"
hmac = "=0.12.1"
pbkdf2 = { version = "=0.12.2", features = ["hmac"] }
serde_json = { version = "=1.0.154", features = ["preserve_order"] }
sha2 = "=0.10.9"
unicode-normalization = "=0.1.25"
```

<!-- styx-test:rust -->
```rust
use base64::engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD};
use base64::Engine;
use hmac::{Hmac, Mac};
use serde_json::{Map, Value};
use sha2::{Sha256, Sha512};
use unicode_normalization::UnicodeNormalization;

const PREFIX: &str = "styx2.";

pub struct Token {
    pub kid: u8,
    pub network: [u8; 16],
    pub caveats: Vec<String>,
    pub signature: [u8; 32],
}

fn decode(text: &str) -> Result<Vec<u8>, String> {
    let raw = URL_SAFE_NO_PAD.decode(text).map_err(|_| "not base64url")?;
    if URL_SAFE_NO_PAD.encode(&raw) != text {
        return Err("not canonical base64url".into());
    }
    Ok(raw)
}

fn charset(value: &str, max: usize, extra: &str) -> bool {
    (1..=max).contains(&value.len()) && value.chars().all(|c| c.is_ascii_alphanumeric() || extra.contains(c))
}

pub fn check_caveat(caveat: &str) -> Result<(), String> {
    let refused = || format!("caveat outside the grammar: {caveat}");
    let (key, value) = caveat.split_once('=').ok_or_else(refused)?;
    let valid = match key {
        "sub" => charset(value, 64, "_-"),
        "host" | "hostPrefix" | "hostNot" => charset(value, 63, "._-"),
        "id" => charset(value, 72, "._-"),
        "exp" => value == "0" || (charset(value, 19, "") && value.bytes().all(|b| b.is_ascii_digit()) && !value.starts_with('0')),
        _ => false,
    };
    if !valid {
        return Err(refused());
    }
    if key == "exp" && value.parse::<u64>().map_err(|e| e.to_string())? > 253_402_300_799 {
        return Err("exp beyond 9999-12-31T23:59:59Z".into());
    }
    Ok(())
}

/// Key id, network, caveats and signature. Informational only: it proves nothing about validity.
pub fn read(token: &str) -> Result<Token, String> {
    let raw = decode(token.strip_prefix(PREFIX).ok_or("not a v2 token; a v1 token cannot be narrowed")?)?;
    if raw.len() < 51 || raw.len() > 1024 || raw[0] != 2 || raw[18] > 16 || raw[8] >> 4 != 8 {
        return Err("malformed token".into());
    }
    let end = raw.len() - 32;
    let (mut caveats, mut offset) = (Vec::new(), 19);
    for _ in 0..raw[18] {
        let length = if offset < end { raw[offset] as usize } else { 0 };
        if !(3..=200).contains(&length) || offset + 1 + length > end {
            return Err("malformed token".into());
        }
        let caveat = &raw[offset + 1..offset + 1 + length];
        if !caveat.iter().all(|b| (0x20..=0x7e).contains(b)) {
            return Err("malformed token".into());
        }
        caveats.push(String::from_utf8_lossy(caveat).into_owned());
        offset += 1 + length;
    }
    if offset != end {
        return Err("malformed token".into());
    }
    let mut token = Token { kid: raw[1], network: [0; 16], caveats, signature: [0; 32] };
    token.network.copy_from_slice(&raw[2..18]);
    token.signature.copy_from_slice(&raw[end..]);
    Ok(token)
}

/// The token with caveats appended. Needs no key; validates everything before signing anything.
pub fn narrow(token: &str, caveats: &[&str], max_length: Option<usize>) -> Result<String, String> {
    let old = read(token)?;
    for caveat in caveats {
        check_caveat(caveat)?;
    }
    let every: Vec<String> = old.caveats.iter().cloned().chain(caveats.iter().map(|c| c.to_string())).collect();
    let mut hosts: Vec<String> = every.iter().filter_map(|c| c.strip_prefix("host=")).map(str::to_ascii_lowercase).collect();
    hosts.sort();
    hosts.dedup();
    if every.len() > 16 || hosts.len() > 1 || every.iter().filter(|c| c.starts_with("sub=")).count() > 8 {
        return Err("too many caveats, contradictory hosts, or a path deeper than 8".into());
    }
    let size = 51 + every.iter().map(|c| 1 + c.len()).sum::<usize>();
    if size > 1024 || max_length.is_some_and(|max| PREFIX.len() + (size * 4 + 2) / 3 > max) {
        return Err("token too long".into());
    }
    let mut signature = old.signature;
    for caveat in caveats {
        let mut mac = Hmac::<Sha256>::new_from_slice(&signature).expect("HMAC takes any key length");
        mac.update(b"styx-v2/caveat\0");
        mac.update(&[caveat.len() as u8]);
        mac.update(caveat.as_bytes());
        signature.copy_from_slice(&mac.finalize().into_bytes());
    }
    let mut body = vec![2, old.kid];
    body.extend_from_slice(&old.network);
    body.push(every.len() as u8);
    for caveat in &every {
        body.push(caveat.len() as u8);
        body.extend_from_slice(caveat.as_bytes());
    }
    body.extend_from_slice(&signature);
    Ok(format!("{PREFIX}{}", URL_SAFE_NO_PAD.encode(body)))
}

/// The network config blob, in the one form every client reads.
pub fn make_blob(styx_server: &str, encryption_key: &str, authorization: &str) -> Result<String, String> {
    for value in [styx_server, encryption_key, authorization] {
        if value.chars().any(|c| !(' '..='~').contains(&c) || c == '"' || c == '\\') {
            return Err("blob values must be printable ASCII without '\"' or '\\'".into());
        }
    }
    let json = format!(r#"{{"StyxServer":"{styx_server}","EncryptionKey":"{encryption_key}","Authorization":"{authorization}"}}"#);
    Ok(STANDARD.encode(json))
}

// the last of any members that differ only in case, as the other implementations take it
fn field<'a>(fields: &'a Map<String, Value>, name: &str) -> Result<&'a str, String> {
    let value = fields.iter().rev().find(|(key, _)| key.to_lowercase() == name).and_then(|(_, value)| value.as_str());
    value.ok_or_else(|| format!("the blob has no {name}"))
}

pub fn narrow_blob(blob: &str, caveats: &[&str], max_token_length: Option<usize>, max_blob_bytes: Option<usize>) -> Result<String, String> {
    let json = STANDARD.decode(blob).map_err(|_| "not base64")?;
    let fields: Map<String, Value> = serde_json::from_slice(&json).map_err(|e| e.to_string())?;
    let token = narrow(field(&fields, "authorization")?, caveats, max_token_length)?;
    let result = make_blob(field(&fields, "styxserver")?, field(&fields, "encryptionkey")?, &token)?;
    if max_blob_bytes.is_some_and(|max| STANDARD.decode(&result).map_or(0, |raw| raw.len()) > max) {
        return Err("blob too large".into());
    }
    Ok(result)
}

/// The relay key for a passphrase (§13.1). Only an operator, who holds the passphrase, needs this.
pub fn key_from_passphrase(passphrase: &str) -> Result<[u8; 64], String> {
    const EDGES: &[char] = &[' ', '\t', '\n', '\u{b}', '\u{c}', '\r', '\u{85}', '\u{a0}', '\u{feff}'];
    let text: String = passphrase.nfc().collect();
    let distinct: std::collections::HashSet<char> = text.chars().collect();
    if text.chars().count() < 64 || distinct.len() < 16 || text.contains(['\n', '\r']) || text.starts_with(EDGES) || text.ends_with(EDGES) {
        return Err("a passphrase is 64 characters and 16 different ones, on one line, with no whitespace or byte order mark at either end".into());
    }
    let mut key = [0u8; 64];
    pbkdf2::pbkdf2_hmac::<Sha512>(text.as_bytes(), b"styx-v2/passphrase", 1_000_000, &mut key);
    Ok(key)
}
```

**Test vectors.** The key below is **TEST ONLY — a relay refuses to start with it**, and so is the
passphrase, whose derived key (§13.1) is `passphraseKey`; each of `passphraseRules` must be accepted or
refused as it says; the network is a made-up version-8 UUID. `root` is the root token for that network under that key. Each step appends one
caveat to the previous token, giving the signature and token after it. Every `rejected` token must be
refused by a relay holding the key. `blob` is a blob around `root`, the same blob as a reader may meet it in the field
(`camelCase`: lowercase-first members, whitespace, an escaped `+`), and the blob narrowed, which narrowing
either form must produce; every `refusedBlobValues` entry must be refused as an `EncryptionKey`.

<!-- styx-test:vectors -->
```json
{
  "key": "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f",
  "kid": 108,
  "network": "00000000-0000-8000-8000-000000000001",
  "root": "styx2.AmwAAAAAAACAAIAAAAAAAAABAPE5nVw2q4NZacG7w01pFgaNVURkodZunnIQgulLXocU",
  "steps": [
    {
      "caveat": "sub=game",
      "signature": "662ba227cc129477471e87af5f5c889ea077be1b2e38ad402ec86ab56b15bcb0",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAQhzdWI9Z2FtZWYroifMEpR3Rx6Hr19ciJ6gd74bLjitQC7IarVrFbyw"
    },
    {
      "caveat": "sub=room-1",
      "signature": "171b239843d10f94c31c226cac1030a3daa08e02fb495bfd40de20bf81538bf7",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAghzdWI9Z2FtZQpzdWI9cm9vbS0xFxsjmEPRD5TDHCJsrBAwo9qgjgL7SVv9QN4gv4FTi_c"
    },
    {
      "caveat": "hostPrefix=player-",
      "signature": "43a7124ac9cab2736d8fe08e3757739587d10e4b4bed9fd77dfb4f3b63ae529c",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAwhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLUOnEkrJyrJzbY_gjjdXc5WH0Q5LS-2f1337TztjrlKc"
    },
    {
      "caveat": "hostNot=player-0",
      "signature": "96b37a109110211953ce76fbe513f9e522a54d562157ce11e6ec765c1f1bbe42",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABBAhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wlrN6EJEQIRlTznb75RP55SKlTVYhV84R5ux2XB8bvkI"
    },
    {
      "caveat": "host=player-1",
      "signature": "7485938cfb018ad4799ef957618df340c48b693ec7afdf3c575e79a9cd0b70aa",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABBQhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTF0hZOM-wGK1Hme-VdhjfNAxItpPsev3zxXXnmpzQtwqg"
    },
    {
      "caveat": "exp=4102444800",
      "signature": "8301a879ac1afdda15624e73258cec14b646d0e8dcd1330e087d02f748932584",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABBghzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDCDAah5rBr92hViTnMljOwUtkbQ6NzRMw4IfQL3SJMlhA"
    },
    {
      "caveat": "id=room-1",
      "signature": "0d84e2f470d24497304de48456b89ddb168501dc61166d8315d6db4fd0fb2294",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABBwhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDAJaWQ9cm9vbS0xDYTi9HDSRJcwTeSEVrid2xaFAdxhFm2DFdbbT9D7IpQ"
    }
  ],
  "rejected": [
    {
      "why": "a signature bit flipped",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABBwhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDAJaWQ9cm9vbS0xDITi9HDSRJcwTeSEVrid2xaFAdxhFm2DFdbbT9D7IpQ"
    },
    {
      "why": "the last caveat dropped, keeping the final signature",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABBghzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDANhOL0cNJElzBN5IRWuJ3bFoUB3GEWbYMV1ttP0PsilA"
    },
    {
      "why": "two caveats swapped",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABBwpzdWI9cm9vbS0xCHN1Yj1nYW1lEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDAJaWQ9cm9vbS0xDYTi9HDSRJcwTeSEVrid2xaFAdxhFm2DFdbbT9D7IpQ"
    },
    {
      "why": "padding added",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAPE5nVw2q4NZacG7w01pFgaNVURkodZunnIQgulLXocU="
    },
    {
      "why": "whitespace added",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAPE5nVw2q4NZacG7w01pFgaNVURkodZunnIQgulLXocU "
    },
    {
      "why": "a caveat key the relay does not know, correctly signed",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAQVtYXg9Mb8-0zG7TAhkgOJEC0EEJlynjxVw2RHRDhy_SBVLQ9F4"
    },
    {
      "why": "a caveat key in the wrong case, correctly signed",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAQhTdWI9Z2FtZbgeVB87QDHRhG7UXh897J50bXnA2X2swkKRm3Yieaof"
    },
    {
      "why": "contradictory host caveats, correctly signed",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAgZob3N0PWEGaG9zdD1i4vPTdO3NcBcthM2pvXqJxizOLN9KkOcgvYbS5QE-Nw0"
    },
    {
      "why": "exp with a leading zero, correctly signed",
      "token": "styx2.AmwAAAAAAACAAIAAAAAAAAABAQZleHA9MDH9nYcUyh_eIaRRfvyMv3ZD13BKVvVawtU4R3djkRbvxg"
    },
    {
      "why": "a version-4 network id, correctly signed",
      "token": "styx2.AmwAAAAAAABAAIAAAAAAAAABABETSgDfF5_D7m5GIPhXe75NkQzzs5pvukU1frpkO8A-"
    },
    {
      "why": "signed by a key the relay does not hold",
      "token": "styx2.AksAAAAAAACAAIAAAAAAAAABAHDkxigCOcrwpDKFKvhJsGhq0yleD4mfS2hmwdoUMkc7"
    }
  ],
  "blob": {
    "styxServer": "https://styx.example.org",
    "encryptionKey": "k+y/test=key",
    "before": "eyJTdHl4U2VydmVyIjoiaHR0cHM6Ly9zdHl4LmV4YW1wbGUub3JnIiwiRW5jcnlwdGlvbktleSI6ImsreS90ZXN0PWtleSIsIkF1dGhvcml6YXRpb24iOiJzdHl4Mi5BbXdBQUFBQUFBQ0FBSUFBQUFBQUFBQUJBUEU1blZ3MnE0TlphY0c3dzAxcEZnYU5WVVJrb2RadW5uSVFndWxMWG9jVSJ9",
    "camelCase": "eyAic3R5eFNlcnZlciI6ICJodHRwczovL3N0eXguZXhhbXBsZS5vcmciLCAiZW5jcnlwdGlvbktleSI6ICJrXHUwMDJCeS90ZXN0PWtleSIsICJhdXRob3JpemF0aW9uIjogInN0eXgyLkFtd0FBQUFBQUFDQUFJQUFBQUFBQUFBQkFQRTVuVncycTROWmFjRzd3MDFwRmdhTlZVUmtvZFp1bm5JUWd1bExYb2NVIiB9",
    "narrowedWith": [
      "hostNot=admin",
      "id=page-1"
    ],
    "maxTokenLength": 255,
    "maxBlobBytes": 511,
    "after": "eyJTdHl4U2VydmVyIjoiaHR0cHM6Ly9zdHl4LmV4YW1wbGUub3JnIiwiRW5jcnlwdGlvbktleSI6ImsreS90ZXN0PWtleSIsIkF1dGhvcml6YXRpb24iOiJzdHl4Mi5BbXdBQUFBQUFBQ0FBSUFBQUFBQUFBQUJBZzFvYjNOMFRtOTBQV0ZrYldsdUNXbGtQWEJoWjJVdE1mZDc1QU14dTFsbXZieVpvdVUtRWd1QU9NWU94Qm9KS0FiQU5YbWZ1Y3I1In0="
  },
  "passphrase": "TEST ONLY: cafe\u0301 au lait is published in Styx.md, so no relay will ever start with it",
  "passphraseKey": "a683e2aeec89bb7f672af11b17eb20cc33da43c8665891c6bca176843743114970d7d2effea6c8756d188ea8209cf3f14fd462cbd5b85390cbaa921a207a203a",
  "passphraseRules": [
    {
      "why": "64 characters, 16 different",
      "passphrase": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "accepted": true
    },
    {
      "why": "64 characters, 16 different, outside the BMP",
      "passphrase": "\ud83d\udc00\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f\ud83d\udc00\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f\ud83d\udc00\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f\ud83d\udc00\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f",
      "accepted": true
    },
    {
      "why": "63 characters",
      "passphrase": "123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "accepted": false
    },
    {
      "why": "63 characters, 126 UTF-16 units",
      "passphrase": "\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f\ud83d\udc00\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f\ud83d\udc00\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f\ud83d\udc00\ud83d\udc01\ud83d\udc02\ud83d\udc03\ud83d\udc04\ud83d\udc05\ud83d\udc06\ud83d\udc07\ud83d\udc08\ud83d\udc09\ud83d\udc0a\ud83d\udc0b\ud83d\udc0c\ud83d\udc0d\ud83d\udc0e\ud83d\udc0f",
      "accepted": false
    },
    {
      "why": "75 characters, only 15 different",
      "passphrase": "0123456789abcde0123456789abcde0123456789abcde0123456789abcde0123456789abcde",
      "accepted": false
    },
    {
      "why": "a leading space",
      "passphrase": " 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "accepted": false
    },
    {
      "why": "a leading byte order mark",
      "passphrase": "\ufeff0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "accepted": false
    },
    {
      "why": "a trailing line feed",
      "passphrase": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n",
      "accepted": false
    },
    {
      "why": "a trailing vertical tab",
      "passphrase": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\u000b",
      "accepted": false
    },
    {
      "why": "a trailing no-break space",
      "passphrase": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\u00a0",
      "accepted": false
    },
    {
      "why": "a trailing next line",
      "passphrase": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\u0085",
      "accepted": false
    },
    {
      "why": "a line feed inside",
      "passphrase": "0123456789abcdef0123456789abcdef\n0123456789abcdef0123456789abcdef",
      "accepted": false
    },
    {
      "why": "a carriage return inside",
      "passphrase": "0123456789abcdef0123456789abcdef\r0123456789abcdef0123456789abcdef",
      "accepted": false
    }
  ],
  "refusedBlobValues": [
    "k\"ey",
    "k\\ey",
    "k\u00e9y"
  ]
}
```

