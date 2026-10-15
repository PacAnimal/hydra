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

Styx is a SignalR hub. The hub is mounted at **`/relay`** relative to the server root.

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
| Concurrent invocations per client | 4 |
| Relay keep-alive ping interval | 5 s |
| Configured client timeout | 15 s |

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
**relay key**: 32 random bytes held by the relay alone.

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
- It reveals nothing to guess against: the relay key is random, and no password is involved.
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
It blunts password guessing against v1 tokens, and it caps each connection at one login a second, which
bounds the membership churn a token holder can cause by re-authenticating in a loop. Clients MUST tolerate
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
- Ordering is preserved between a given pair of peers, being the ordering of a single WebSocket. No
  ordering is implied across different senders.

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

Alongside the hub, the relay serves static files from its root and two JSON endpoints. Both respond no
faster than a fixed floor, for the same reason as §5.1. JSON member names are camelCase.

Wrong v1 tokens that fail to decrypt, and wrong admin secrets, are counted per client address (an IPv6
address by its /64). Past 20 in a minute, that address's further attempts **on those two paths only** are
refused without being checked until the minute passes. v2 tokens are never refused by address.

### 8.1 `GET /api/status`

```
Authorization: Bearer <authorization token>
→ 200 {"peers":["alpha-box","beta"]}
→ 401 (invalid or absent token)
```

Membership of the token's network and path without joining it. Either token format. Minimum response
time 2 s.

### 8.2 `POST /api/network-config`

```
{"password":"<admin secret>"}
→ 200 {"authorization":"<styx2. token>"}
→ 401 (wrong secret)
→ 404 (the relay has no v2 key or no admin secret, so creates no networks)
```

Mints a v2 root token for a **freshly generated version-8 network** — a self-service way to create an
isolated network on a relay whose admin secret you know. It cannot produce a token for an existing network.
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
legacy network whose UUID is known, never for a v2 network; recover by rotating it, which invalidates every
v1 token at once. Every v1 token is an offline verifier of the password (§4.1), so a relay that keeps v1
needs a long random password.

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
| `RELAY_KEY` | The v2 key: base64 of 32 random bytes. During a rotation, a comma-separated list whose first entry mints and whose others only verify. |
| `RELAY_PASSWORD` | The v1 password. Set, v1 tokens are accepted; unset, they are not. |
| `RELAY_ADMIN_PASSWORD` | The secret `POST /api/network-config` checks. Defaults to `RELAY_PASSWORD`. |
| `RELAY_REVOKED_NETWORKS` | Comma-separated network UUIDs whose tokens are refused, v1 and v2. |
| `RELAY_REVOKED_IDS` | Comma-separated v2 `id` caveat values whose tokens are refused (compared ordinally). |
| `LOCAL_PORT` | Listening port. Default `5000`. |
| `LOCAL_ONLY` | `true` binds loopback only. |
| `DEBUG_MESSAGES` | `true` logs per-payload routing metadata (network, sender, targets, size — not contents). |

The relay refuses to start unless it can verify something (`RELAY_KEY` or `RELAY_PASSWORD`), and refuses a
malformed key, the same key twice, two keys sharing a `kid`, or the published test key of §14.6. Every
change to these is a restart, which drops every peer; that is also what makes a revocation complete.

**Rotating the v2 key:** put a new key first in `RELAY_KEY`, with the old one after it; reissue tokens;
then remove the old key, which ends every token it signed.

The relay honours forwarded headers for client-address determination behind a reverse proxy, and disables
Nagle's algorithm on accepted connections. It holds no database and no on-disk state; restarting it drops
every peer, which then reconnect and re-announce.

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

Complete implementations of reading, narrowing and blob writing. Both validate everything before signing
anything, and both are checked against the vectors below on every build of this repository.

**Python** (standard library only):

<!-- styx-test:python -->
```python
import base64, hashlib, hmac, json, re

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
```

**JavaScript** (WebCrypto; runs in a browser and in Node):

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
```

**Test vectors.** The key below is **TEST ONLY — a relay refuses to start with it**, and the network is a
made-up version-8 UUID. `root` is the root token for that network under that key. Each step appends one
caveat to the previous token, giving the signature and token after it. Every `rejected` token must be
refused by a relay holding the key. `blob` is a blob around `root`, the same blob as a reader may meet it in the field
(`camelCase`: lowercase-first members, whitespace, an escaped `+`), and the blob narrowed, which narrowing
either form must produce; every `refusedBlobValues` entry must be refused as an `EncryptionKey`.

<!-- styx-test:vectors -->
```json
{
  "key": "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
  "kid": 109,
  "network": "00000000-0000-8000-8000-000000000001",
  "root": "styx2.Am0AAAAAAACAAIAAAAAAAAABANl1jz_ZWK1yEx4--HYgrK8AlAgEpQCLFMJkD3q3ytuH",
  "steps": [
    {
      "caveat": "sub=game",
      "signature": "209546b8b23ba1f8aead09d28be5232d58c8ef48a82123874ef76ed5da392403",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABAQhzdWI9Z2FtZSCVRriyO6H4rq0J0ovlIy1YyO9IqCEjh073btXaOSQD"
    },
    {
      "caveat": "sub=room-1",
      "signature": "2e513303d6779250348238c73a0a20b55ad2d02233c83b110f27ab2357c73901",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABAghzdWI9Z2FtZQpzdWI9cm9vbS0xLlEzA9Z3klA0gjjHOgogtVrS0CIzyDsRDyerI1fHOQE"
    },
    {
      "caveat": "hostPrefix=player-",
      "signature": "ab7fa1277b917cc292505d7b2e544f263caa2573192c540bc026be911b0e04e8",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABAwhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLat_oSd7kXzCklBdey5UTyY8qiVzGSxUC8AmvpEbDgTo"
    },
    {
      "caveat": "hostNot=player-0",
      "signature": "afaae1a8fcdcceb71d50d504ba43f9a1d2e918a40b8d6d5549d42fc01f24fb63",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABBAhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wr6rhqPzczrcdUNUEukP5odLpGKQLjW1VSdQvwB8k-2M"
    },
    {
      "caveat": "host=player-1",
      "signature": "5cede9309335a4c0e8bf6b9a434e60c13bfcddc9edbaa75805960431e5cc3822",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABBQhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTFc7ekwkzWkwOi_a5pDTmDBO_zdye26p1gFlgQx5cw4Ig"
    },
    {
      "caveat": "exp=4102444800",
      "signature": "62ce352a13e8699c70f71591bb8edbc6b2b736f2d2476c1fd0bcdd9e3bb5c44f",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABBghzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDBizjUqE-hpnHD3FZG7jtvGsrc28tJHbB_QvN2eO7XETw"
    },
    {
      "caveat": "id=room-1",
      "signature": "36e594549354df787c3d8378244f7d25161358fe485654f1f773fa8319c811b9",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABBwhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDAJaWQ9cm9vbS0xNuWUVJNU33h8PYN4JE99JRYTWP5IVlTx93P6gxnIEbk"
    }
  ],
  "rejected": [
    {
      "why": "a signature bit flipped",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABBwhzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDAJaWQ9cm9vbS0xN-WUVJNU33h8PYN4JE99JRYTWP5IVlTx93P6gxnIEbk"
    },
    {
      "why": "the last caveat dropped, keeping the final signature",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABBghzdWI9Z2FtZQpzdWI9cm9vbS0xEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDA25ZRUk1TfeHw9g3gkT30lFhNY_khWVPH3c_qDGcgRuQ"
    },
    {
      "why": "two caveats swapped",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABBwpzdWI9cm9vbS0xCHN1Yj1nYW1lEmhvc3RQcmVmaXg9cGxheWVyLRBob3N0Tm90PXBsYXllci0wDWhvc3Q9cGxheWVyLTEOZXhwPTQxMDI0NDQ4MDAJaWQ9cm9vbS0xNuWUVJNU33h8PYN4JE99JRYTWP5IVlTx93P6gxnIEbk"
    },
    {
      "why": "padding added",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABANl1jz_ZWK1yEx4--HYgrK8AlAgEpQCLFMJkD3q3ytuH="
    },
    {
      "why": "whitespace added",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABANl1jz_ZWK1yEx4--HYgrK8AlAgEpQCLFMJkD3q3ytuH "
    },
    {
      "why": "a caveat key the relay does not know, correctly signed",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABAQVtYXg9MVCRt1-rUVs7CngDDXuznqlFAddRj7HidfiM0dn0mHq-"
    },
    {
      "why": "a caveat key in the wrong case, correctly signed",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABAQhTdWI9Z2FtZaWXg8l2MhRiggznYcSLW8xJOT7ZGolQTGlT47q2EeYW"
    },
    {
      "why": "contradictory host caveats, correctly signed",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABAgZob3N0PWEGaG9zdD1imGoRww6ET0Pc1VE9CeNBUWD0SoXG5TsrrLy_3_SMVaY"
    },
    {
      "why": "exp with a leading zero, correctly signed",
      "token": "styx2.Am0AAAAAAACAAIAAAAAAAAABAQZleHA9MDGukX1cnYVX_7RYSZUTMosQyQ0CxCd0qS94oQpe625-Vg"
    },
    {
      "why": "a version-4 network id, correctly signed",
      "token": "styx2.Am0AAAAAAABAAIAAAAAAAAABAPkCl529AQwbko1Uih488QHfPJ2-vP59tMnDS3FR0K9h"
    },
    {
      "why": "signed by a key the relay does not hold",
      "token": "styx2.AgoAAAAAAACAAIAAAAAAAAABAMq3MO0qj5_Q-VSuAHxDHaVLi7HQ8jtYKZexmz4ERAtG"
    }
  ],
  "blob": {
    "styxServer": "https://styx.example.org",
    "encryptionKey": "k+y/test=key",
    "before": "eyJTdHl4U2VydmVyIjoiaHR0cHM6Ly9zdHl4LmV4YW1wbGUub3JnIiwiRW5jcnlwdGlvbktleSI6ImsreS90ZXN0PWtleSIsIkF1dGhvcml6YXRpb24iOiJzdHl4Mi5BbTBBQUFBQUFBQ0FBSUFBQUFBQUFBQUJBTmwxanpfWldLMXlFeDQtLUhZZ3JLOEFsQWdFcFFDTEZNSmtEM3EzeXR1SCJ9",
    "camelCase": "eyAic3R5eFNlcnZlciI6ICJodHRwczovL3N0eXguZXhhbXBsZS5vcmciLCAiZW5jcnlwdGlvbktleSI6ICJrXHUwMDJCeS90ZXN0PWtleSIsICJhdXRob3JpemF0aW9uIjogInN0eXgyLkFtMEFBQUFBQUFDQUFJQUFBQUFBQUFBQkFObDFqel9aV0sxeUV4NC0tSFlncks4QWxBZ0VwUUNMRk1Ka0QzcTN5dHVIIiB9",
    "narrowedWith": [
      "hostNot=admin",
      "id=page-1"
    ],
    "maxTokenLength": 255,
    "maxBlobBytes": 511,
    "after": "eyJTdHl4U2VydmVyIjoiaHR0cHM6Ly9zdHl4LmV4YW1wbGUub3JnIiwiRW5jcnlwdGlvbktleSI6ImsreS90ZXN0PWtleSIsIkF1dGhvcml6YXRpb24iOiJzdHl4Mi5BbTBBQUFBQUFBQ0FBSUFBQUFBQUFBQUJBZzFvYjNOMFRtOTBQV0ZrYldsdUNXbGtQWEJoWjJVdE1hc3pmcGItUVpySHZRd1R4aEVWZXVGTWs5cEJJdDNoQTFEOWVPXzFVRGJZIn0="
  },
  "refusedBlobValues": [
    "k\"ey",
    "k\\ey",
    "k\u00e9y"
  ]
}
```

