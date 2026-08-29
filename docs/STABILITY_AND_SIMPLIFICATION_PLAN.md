# Hydra stability and simplification programme

Status: active engineering plan for this fork

Last reviewed: 2026-08-29

This plan is intentionally smaller than the generic audit that produced it. Hydra is a compact, cross-platform, latency-sensitive KVM. Simplification is valuable only when it improves reliability or makes behavior easier to verify without weakening input pairing, relay compatibility, native ownership, recovery, or platform fallbacks.

Hydra has no separate web configuration application. `hydra tui` is the sole interactive configuration editor.

## Non-negotiable invariants

Every change must preserve the relevant invariants:

1. Key-down/key-up, modifier, button-down/button-up, repeat, and disconnect cleanup remain paired.
2. Capture and resolution on the master remain compatible with injection and execution on every supported slave platform.
3. Unicode and keyboard-layout-aware routing remains the default; physical-key routing is reserved for deliberate shortcut cases.
4. Input and control traffic remains non-blocking and prioritized over bulk work; reliable release events are never silently dropped.
5. Styx/Hydra framing, casing, ordering, limits, authentication, and compatible defaults remain stable unless explicitly versioned.
6. Local management IPC remains local-only. Remote management retains separate credentials, redaction, freshness/replay checks, revision checks, connectivity guards, confirmation, and rollback.
7. Missing native/private facilities use existing bounded fallbacks instead of terminating Hydra.
8. Native handles, delegates, callbacks, buffers, CoreFoundation/IOKit objects, and COM objects retain explicit lifetime ownership.
9. TUI edits are accepted by the canonical parser and validator and preserve unknown, advanced, secret, and topology fields not exposed by the form.
10. Build and publish artifacts are not treated as installed-runtime evidence. Deployment preserves signing, config, service ownership, and a recovery path.

## Completed foundation

- Removed the independent web configuration application, its Node/container surface, and its dedicated workflow.
- Made the TUI the only interactive configuration editor while retaining complete JSON editing.
- Added one process-wide Linux Xlib initialization gate before every direct display open. Focused tests pass; real Xorg validation remains a named gap.
- Established local and paired remote management with bounded framing, redaction, revision-aware apply, and automatic rollback.
- Made output draining fault-isolated and observable, with backlog/fault telemetry and bounded shutdown that leaves native ownership with a blocked worker.
- Made file-transfer send cleanup operation-owned so a cancelled worker cannot dispose a replacement transfer.
- Added relative-mouse relay coalescing plus queue depth, age, peak, and send-latency diagnostics.
- Bounded local management to 16 tracked request handlers and made error replies cancellation-bound.

Completed work stays protected by regression tests and the invariants above; it should not remain in the active backlog.

## Active priorities

### P1 — Runtime failure paths

#### Make input-capture startup truthful

`ILocalEventTap.StartEventTap()` cannot report typed success or failure even though macOS and Windows distinguish failed setup internally and Xorg ignores some registration results. Add failed-start seams, a typed result, retry/fallback policy, and management health that cannot report Hydra healthy while input capture is absent.

#### Give screen-detector readiness a terminal policy

`ScreenDetector.Get()` can wait indefinitely when the first native detection throws before readiness is signalled. Characterize the hosting behavior, then add cancellation/deadline and an explicit retry or terminal failure result. Do not fabricate a valid screen snapshot.

#### Guarantee Windows mouse-setting recovery

Relative injection temporarily changes global Windows mouse settings. Add checked restoration, telemetry, failure seams, and a real-Windows recovery test for normal stop, initialization failure, and recoverable faults. Do not equate cursor restoration with mouse-setting restoration.

### P2 — Source-of-truth and lifecycle hardening

#### Harden canonical configuration validation

Decide and test empty profile lists, multiple relay definitions, empty SSIDs, missing profile names, and mode-mismatched fields. `Program` must not index a profile unless validation proves one exists. Add no-op and targeted TUI edit fixtures containing unknown fields, mirrors, ranges, root diagnostics, secrets, and profile overrides.

#### Make embedded Styx readiness explicit

The embedded server exposes readiness, but composition currently relies on hosted-service registration and relay reconnect. Add an initial-connection contract test and decide whether reconnect is intentional or startup must await the listener.

#### Clear or mark stale relay latency

Disconnect currently clears peers and pending probes while historical latency state can remain visible. Add disconnect/reconnect tests and choose either clearing or an explicit stale marker.

#### Centralize private-file replacement only after policy tests

`TransactionalConfigStore`, `RemoteApplyStore`, and `RemoteManagementStore` share temporary-write, flush, permission, replace, and cleanup mechanics. Extract a primitive only after tests cover symlinks, private modes, concurrent writers, crashes, and each store's intentionally different replacement policy.

#### Align CI with supported runtime claims

Keep Linux/X11 and Windows tests, add Mac-native test execution rather than publish-only coverage, use documented Release gates, and state which hardware-only input/media/brightness paths remain manual. Compilation and cross-target publishing are never runtime validation.

### P3 — Structural simplification after characterization

#### Add a deterministic TUI interaction seam

`HydraTui.cs` owns view construction, polling, formatting, configuration binding, lifecycle controls, and remote apply coordination. Add a supported fake/test driver before extracting cohesive internal controllers. Keep the management contracts unchanged.

#### Characterize `InputRouter` before splitting it

Map its serialized command queue, held-state ownership, screen transitions, mouse throttling, file-transfer hotkeys, locks, and disconnect cleanup. Add missing sequence tests first. Do not split the state machine across independently synchronized services.

#### Reduce composition-root coupling behind graph tests

Extract platform or mode registration modules from `Program.cs` only if tests prove selected implementations, singleton aliasing, hosted-service order, screen readiness dependencies, recovery bootstrap, Windows session-child behavior, and Mac shield initialization.

#### Replace the lazy relay/activity edge only if ownership improves

The composition root enables `Lazy<T>` to break the ActivityTracker/relay dependency. Map callback timing and ownership before replacing it. Do not move work onto the input hot path for architectural neatness.

#### Improve state ownership behind immutable snapshots

`WorldState` mixes peer topology, role state, encryption keys, and logger state. First add concurrency, departure, and snapshot-immutability tests. Introduce smaller owners only if the tests or recurring defects justify the migration.

#### Repair wire/domain leakage through compatibility adapters

Platform abstractions currently consume some relay message records directly. Introduce neutral commands only after byte-value, JSON/MessagePack, ordering, and older-payload fixtures exist. This is boundary debt, not permission to redesign the wire protocol.

## Delivery order

1. Land one failure-path repair at a time with focused characterization and the affected platform lane.
2. Harden canonical config/TUI preservation and readiness contracts.
3. Improve CI evidence and small lifecycle owners.
4. Add deterministic TUI and InputRouter seams.
5. Attempt deeper boundary changes only when defect history or measured latency/reliability evidence justifies them.

High-risk candidates require an independent review. Keep changes reversible and avoid combining platform behavior, protocol changes, and structural refactors in one patch.

## Leave-alone list without new evidence

- Platform-native implementations and fallback ladders that merely look duplicated.
- `NativeMethods` declaration blocks, key maps, and protocol tables when ownership is cohesive.
- macOS media, audio, brightness, BetterDisplay, DisplayServices, DDC, and legacy fallbacks without live path-specific evidence.
- Relay ordering, control-message reliability, and the unbounded ordered control queue without measured outage/backpressure evidence.
- Message kinds, MessagePack/JSON casing, crypto framing, sidecars, and remote-apply markers without a versioned migration plan.
- Local management transport identity and permissions.
- `Program.cs` registration order and the single-reader `InputRouter` queue until characterization tests make extraction safe.
- Installed binaries, LaunchAgents/services, private configs, signing identity, and remote machines during ordinary refactor validation.

## Verification matrix

All changes require focused tests, a Release solution build, `git diff --check`, and explicit working-tree review.

- Config/TUI: canonical parser and validation tests, guided-edit preservation, secret masking, transactional save, docs/examples, and deterministic interaction tests where UI behavior changes.
- Input/routing/shared DTOs: sequence and compatibility tests plus `./run-tests.sh mac`; run Linux/X11 and Windows lanes when affected.
- Linux/X11: `./run-tests.sh linux` and name the real-Xorg gap unless it was actually exercised.
- Windows native/service: real Windows validation when global settings, hooks, injection, or service-child behavior changes.
- macOS native/package: focused Mac tests, Release build, self-contained `osx-arm64` publish, and explicit live hardware evidence for changed side effects.
- Styx/protocol: wire-format and integration tests, `Styx.md` review, and a local Styx container build when Docker is available.
- Release/dependency work: restore, tests, build, relevant container/publish lanes, and advisory review without blanket forced upgrades.

Record skipped and unavailable lanes. A green lane must never be generalized to a platform it did not execute on.
