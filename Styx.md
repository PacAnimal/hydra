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
| **peer** | An authenticated client, identified within its network by a **hostname**. |
| **payload** | An opaque byte string carried between peers. |

## 2. Model

A network is a flat namespace. Every peer in a network can address every other peer in that network by
hostname, and receives notification whenever the membership changes. Networks are mutually invisible: two
peers in different networks cannot address, discover, or detect one another, even when connected to the
same relay and using the same hostname.

Peer identity is a single string. Hostnames are compared case-insensitively and canonicalised to
lowercase by the relay; a client that authenticates as `ALPHA-Box` is reported to its peers, and
addressed by them, as `alpha-box`. Hostnames are otherwise uninterpreted: any non-empty string is
acceptable, and there is no requirement that it resemble a DNS name.

Identity is bound to the connection, not to the credential. One connection carries exactly one identity
at a time. A hostname is unique within a network: a second connection claiming a hostname already in use
displaces the first (§6.2).

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

A token is an encrypted network UUID. It is both the proof of network membership and the statement of
*which* network is being joined — the relay learns the network identity only by decrypting it.

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
reported as a generic authentication failure.

Properties an implementer should be aware of:

- The token is a **bearer credential**, replayable and long-lived. It carries no expiry, no binding to
  a hostname, and no binding to a client. Two clients MAY authenticate with the same token.
- Anyone holding the relay password can mint a token for **any** network UUID, and thereby join or
  create any network. The password is the only real secret; network UUIDs are not secrets.
- There is no revocation mechanism for an individual token. Rotating the relay password invalidates
  every token on that relay.
- Key derivation is deliberately expensive (100 000 iterations, ~50–100 ms). Derive once, cache, reuse.

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
login    { "authorization": "<base64 token>", "hostName": "<name>" }
response { "authenticated": true|false, "message": "<text>"|null }
```

`message` carries a short human-readable reason on failure — `"Invalid authorization"`,
`"Authorization and hostName are both required"`, `"Server misconfigured"`, `"Connection aborted"` — and
is null on success. Clients SHOULD surface it and MUST NOT parse it.

Both members are mandatory, but omitting one is answered rather than punished: a login missing either
member, under either hub protocol, returns `authenticated: false` with the reason above. A client never
has to distinguish a malformed request from a rejected credential by inspecting transport errors.

Every response, successful or not, is delayed to a **minimum of one second** to blunt password guessing.
Clients MUST tolerate this latency and SHOULD apply a timeout of several seconds to the invocation.

Authenticating on an already-authenticated connection is permitted and **replaces** the identity: the
connection is thereafter known by the new hostname, the old hostname vanishes from the network, and the
membership change is broadcast. A client MAY use this to rename itself in place.

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

Sent when the relay evicts the client's registration. The only reason currently issued is
`"duplicate hostname"`: another connection authenticated with the same hostname in the same network, and
the newcomer wins. Stale registrations from an unclean disconnect are evicted the same way, which is what
makes reconnection after a dropped link work. Every other member of the network sees the displaced hostname
leave and return, per §6.2.

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

### 8.1 `GET /api/status`

```
Authorization: Bearer <authorization token>
→ 200 {"peers":["alpha-box","beta"]}
→ 401 (invalid or absent token)
```

Membership of the token's network without joining it. Minimum response time 2 s.

### 8.2 `POST /api/network-config`

```
{"password":"<relay password>"}
→ 200 {"authorization":"<base64 token>"}
→ 401 (wrong password)
```

Mints a token for a **freshly generated random network UUID** — a self-service way to create an isolated
network on a relay whose password you know. It cannot produce a token for an existing network; that
requires either an existing token or the construction in §4. Minimum response time 5 s.

## 9. Client provisioning

A client needs a relay URL and a token. The conventional way to carry both as a single opaque string is
base64 of a JSON object:

```json
{
  "styxServer": "https://styx.example.org",
  "encryptionKey": "<application-level secret>",
  "authorization": "<base64 token>"
}
```

Member names are matched case-insensitively. `encryptionKey` is **never transmitted to the relay** and
has no meaning to it; it is carried here purely so that one blob provisions both relay access and
whatever end-to-end key the application layered on top (§10). A client with no such layer MAY omit it.

This blob is a convention, not part of the protocol. It is also, in full, the credential: treat it as a
secret.

## 10. Security considerations

**The relay sees every payload byte.** Styx authenticates network membership and routes; it provides no
confidentiality or integrity for payload contents against the relay operator. An application whose
payloads are sensitive MUST encrypt them end-to-end between peers, with keys the relay never receives.
Even then the relay observes hostnames, the peer graph, message sizes, and timing.

**Membership is the whole authorization model.** There is no per-peer permission, no read/write split,
and no restriction on who may address whom. Every peer in a network is fully trusted by every other peer:
any member can address any other, and one compromised token compromises the network. Partition mutually
untrusting parties into separate networks.

**Sender identity is relay-asserted, and only that.** `sourceHost` cannot be forged by a peer, but any
holder of the network's token can authenticate *as* any unclaimed hostname, and can displace a peer that
already holds one (§6.2). Repeatedly claiming a hostname is an effective denial of service against that
peer, and lets an attacker with a valid token impersonate it to the rest of the network. Applications
requiring genuine peer authentication MUST establish it themselves, above Styx.

**Password compromise is total.** The relay password mints tokens for every network, existing or new.
Tokens cannot be revoked individually; recovery means rotating the password and reprovisioning every
client. A relay serving distinct tenants from one password does not isolate them.

**Transport.** Tokens are bearer credentials and are replayable, so a relay reachable over an untrusted
network MUST be served over TLS. The 1 s authentication floor and 2 s / 5 s HTTP floors slow guessing but
are not a substitute for a strong password.

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
| `RELAY_PASSWORD` | Token-verification password. **Required**; the relay refuses to start without it. |
| `LOCAL_PORT` | Listening port. Default `5000`. |
| `LOCAL_ONLY` | `true` binds loopback only. |
| `DEBUG_MESSAGES` | `true` logs per-payload routing metadata (network, sender, targets, size — not contents). |

The relay honours forwarded headers for client-address determination behind a reverse proxy, and disables
Nagle's algorithm on accepted connections. It holds no database and no on-disk state; restarting it drops
every peer, which then reconnect and re-announce.
