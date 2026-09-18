# Technical Design Specification — Shared Agent Chat POC

**Workspace:** AgenticChat (project container)
**Document type:** TDS (Technical Design)
**Date:** 2026-09-17
**Author:** Saud Sajid Ansari
**Related document:** `claude/BRD-SharedAgentChatPOC.md`

> **Scope note:** Same as the BRD this document accompanies — this describes the Shared Agent Chat POC only (2 humans, 1 AI agent, one shared transcript). It is unrelated to the "AgenticChat" SaaS concept (living docs / git automation) tracked elsewhere in this workspace.

---

## 1. Purpose

Capture the concrete technical design for the POC: how the system is structured, how the two humans end up in the same session, how messages and presence signals propagate in real time, and how the single AI agent (Cursor Composer 2.5) is integrated. This document translates the BRD's functional requirements (FR1–FR9) into components, contracts, and flows.

## 2. Architecture Overview

C#, ASP.NET Core, Onion Architecture. Dependencies point inward only; the AI provider is a plug-in behind a port, not a dependency of the core.

| Layer | Contents | Depends on |
|---|---|---|
| Domain | `ChatSession`, `Message`, `Participant`, `TypingEvent` | Nothing |
| Application | Use cases, ports (`IAgentAdapter`, `ISessionRepository`, `IRealtimeNotifier`) | Domain |
| Infrastructure | `ComposerAgentAdapter`, in-memory `SessionRepository`, optional JSON-file `SessionRepository` | Application |
| Presentation | ASP.NET Core host, SignalR hub | Application, Infrastructure (composition root only) |

## 3. Data Model (Domain)

- **`ChatSession`** — `Id` (string, the well-known session identifier), `Participants` (list of `Participant`), `Transcript` (ordered list of `Message`).
- **`Participant`** — `ConnectionId` (current SignalR connection, may change across reconnects), `DisplayName`, `ProviderCredential` (held only for the lifetime of the connection; never persisted to the repository).
- **`Message`** — `Id`, `SenderDisplayName` (or `"Agent"`), `Text`, `Timestamp`.
- **`TypingEvent`** *(transient, not stored)* — `SessionId`, `DisplayName`, `IsTyping`.

## 4. Session & Connection Lifecycle

1. Exactly one `ChatSession` exists for the POC's lifetime, keyed by a well-known constant (e.g. `"poc-session"`). No session creation flow.
2. Both humans open the same URL, e.g. `/chat?session=poc-session`.
3. On SignalR connect, the client calls `JoinSession(sessionId, displayName)`.
4. Server: adds the connection to a SignalR **group** named by `sessionId` (`Groups.AddToGroupAsync`), registers/updates the `Participant` in the session, and returns the **full existing transcript** to the caller so a late-joining or reconnecting tab is backfilled rather than starting blank.
5. Reconnects are handled the same way as a fresh join — no special reconnection state is tracked; replaying the full transcript on `JoinSession` is sufficient at this scale.

## 5. Real-Time Contract (SignalR Hub)

**Client → Server**

| Method | Payload | Effect |
|---|---|---|
| `JoinSession` | `sessionId, displayName` | Adds connection to group; returns transcript so far |
| `SendMessage` | `sessionId, text, credential` | Appends message, broadcasts it, enqueues the agent call |
| `Typing` | `sessionId, displayName` | Broadcast only, no persistence |
| `StoppedTyping` | `sessionId, displayName` | Broadcast only, no persistence |

**Server → Clients** (all scoped via `Clients.Group(sessionId)`, never `Clients.All`)

| Event | Payload | Trigger |
|---|---|---|
| `MessageReceived` | `Message` | A human or the agent added a message |
| `TypingStarted` / `TypingStopped` | `displayName` | Relayed from a `Typing`/`StoppedTyping` call |
| `AgentResponding` / `AgentIdle` | — | Agent call started / finished |

Scoping every broadcast to a group (rather than all connections on the hub) costs nothing extra for a single-session POC, but means adding a second concurrent session later is a config change, not a rewrite.

## 6. Message Processing Flow

1. Human A types → `Typing` → relayed to Human B's tab as `TypingStarted`.
2. Human A sends → `SendMessage` → server appends to `ChatSession.Transcript`, broadcasts `MessageReceived` to the group (both tabs update immediately).
3. The message enters a **per-session queue of depth one**. If Human B sends a message while A's is still being processed, B's message is appended and broadcast the same way, but its agent call waits until A's completes — this is what prevents two overlapping or out-of-order agent calls from a near-simultaneous send.
4. Queue processor calls `IAgentAdapter.GetReplyAsync(transcript, credential)`, broadcasting `AgentResponding` to the group first.
5. On completion, the reply is appended to the transcript, broadcast as `MessageReceived` (sender = `"Agent"`), and `AgentIdle` is broadcast.
6. Queue is now free for the next pending message, if any.

This flow is what delivers the "updates instantly for all users" behavior: every state change on the server (steps 2 and 5 above) is pushed to the group the instant it happens, over an already-open connection — not polled. The one thing this doesn't shorten is Composer's own generation time between steps 4 and 5; both tabs simply observe the same "agent responding" state for that whole interval, then see the finished reply land simultaneously.

## 7. Agent Integration (Infrastructure — `ComposerAgentAdapter`)

- **Endpoint:** Cursor Cloud Agents API — create-agent/run call.
- **Model:** `{ id: "composer-2.5" }`. Mode (Standard vs. Fast) is a request-level choice; **Fast** is the intended default for this POC since a human is actively waiting on each turn — Standard's latency/cost tradeoff only pays off for unattended background work.
- **Repo context:** `repos` and `env` both omitted — the agent runs in no-repo (pure chat) mode per FR9.
- **Auth:** `Authorization: Bearer <credential>`, where `<credential>` is the Cursor API key belonging to whichever human's message triggered this specific call (resolved per-call from `Participant.ProviderCredential`, per FR7) — not a single shared service credential.
- **Latency handling:** this is a task-run API, not a token stream; the adapter's `GetReplyAsync` is a single awaited call that completes when the run finishes, which is why the `AgentResponding`/`AgentIdle` broadcast pair (Section 5) exists — the UI must not appear frozen during this call.

## 8. Frontend Design

Single static page (HTML/CSS/vanilla JS) plus the SignalR JS client — no framework, per the "light and performant" requirement.

- One-time fields on load: display name, Cursor API key (held in a JS variable for the tab's lifetime only — never `localStorage`, never sent anywhere except as the `credential` argument to `SendMessage`).
- Message list rendered from `MessageReceived` events, attributed by sender.
- A "*{name} is typing…*" line driven by `TypingStarted`/`TypingStopped`.
- An "*Agent is responding…*" line driven by `AgentResponding`/`AgentIdle`.

## 9. Non-Functional Notes

- **Persistence:** No database. The live `ChatSession` is always in process memory. Optionally the transcript (messages only) is written to a local JSON file and reloaded on startup so a host restart does not wipe the chat. Credentials, SignalR connection ids, and typing/agent-responding signals are never written to disk. `SessionStore:Mode` is `Memory` or `File`.
- **Credential handling:** never logged server-side, never written to disk; held only for the duration of a connection/tab.
- **Concurrency guarantee:** the depth-one queue is per-session, not global — irrelevant at one session, but the correct unit if this ever generalizes.

## 10. Open Technical Risks

Carried over from the BRD, plus one added during this design pass:

| # | Item | Status |
|---|---|---|
| 1 | Does a Cloud Agents API call bill against the same subscription pool as the IDE, at the same rate? | Unconfirmed in Cursor's docs — verify via usage dashboard after first real calls. |
| 2 | Composer's task-based latency in practice, with two humans conversing | To be observed once running. |
| 3 | Reconnect behavior: a dropped/refreshed tab currently just calls `JoinSession` again and gets the full transcript replayed — acceptable for POC scale, but worth confirming it doesn't produce visible duplicate-render glitches in the frontend's message list. | To be verified during build. |

## 11. Related Documents

- `claude/BRD-SharedAgentChatPOC.md` — requirements, scope, cost model, assumptions.
