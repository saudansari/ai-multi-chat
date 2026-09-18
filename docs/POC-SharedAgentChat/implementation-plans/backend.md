# Backend Implementation Plan — Shared Agent Chat POC

**Workspace:** AgenticChat (project container)
**Document type:** Implementation Plan (Backend)
**Date:** 2026-09-17
**Sources:** `docs/BRD-SharedAgentChatPOC.md`, `docs/TDS-SharedAgentChatPOC.md`
**Scope:** Shared Agent Chat POC only (2 humans, 1 AI agent, one shared transcript). Unrelated to the AgenticChat SaaS concept.

This plan is ordered so each step can be built and verified before the next. Do not start the Cursor adapter until the chat/realtime path works with a stub agent.

---

## 0. Target outcome

A C# ASP.NET Core host that:

- Holds exactly one `ChatSession` (`poc-session`) in process memory, optionally snapshotted to a local JSON file. **No database.**
- Exposes a SignalR hub for join, send, typing, and agent-status events.
- Serializes agent work with a per-session queue of depth one.
- Calls Cursor Composer 2.5 in no-repo mode using the sending human's API key.
- Serves the static frontend from the same host.

Maps to **FR1–FR9**. Login, mixed providers, repo context, and any DB (SQL, EF, Redis, etc.) are out of scope.

---

## 1. Solution scaffold

**Goal:** Onion layout with inward-only dependencies.

Create a solution at the repo root:

```
src/
  SharedAgentChat.Domain/
  SharedAgentChat.Application/
  SharedAgentChat.Infrastructure/
  SharedAgentChat.Web/          # ASP.NET Core host (Presentation + composition root)
tests/
  SharedAgentChat.Tests/
```

Project rules:

| Project | References | Packages |
|---|---|---|
| Domain | none | none |
| Application | Domain | none (or only abstractions) |
| Infrastructure | Application, Domain | `HttpClient` / `Microsoft.Extensions.Http` |
| Web | Application, Infrastructure | `Microsoft.AspNetCore.SignalR`, static files |
| Tests | all of the above | `xunit`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.AspNetCore.SignalR.Client` |

Web project settings:

- Target current LTS (`net8.0` or `net9.0`).
- Enable static files.
- CORS not required if the page is served from the same origin. If the frontend is opened as a file URL during early UI work, allow the local origin temporarily, then remove it.

**Verify:** Solution builds; Web project starts and returns 200 for a placeholder `wwwroot/index.html`.

---

## 2. Domain model

**Goal:** Entities with no infrastructure types.

Implement TDS §3:

- `ChatSession`
  - `Id` (string)
  - `Participants` (`List<Participant>`)
  - `Transcript` (`List<Message>`) — append-only, ordered
  - Methods: `AddOrUpdateParticipant(...)`, `AppendMessage(...)`, `GetTranscriptCopy()` (return a snapshot, do not leak the live list)
- `Participant`
  - `ConnectionId`
  - `DisplayName`
  - `ProviderCredential` — **never** copied into messages, logs, or repository snapshots that might be serialized to clients
- `Message`
  - `Id` (GUID string)
  - `SenderDisplayName` (human name or `"Agent"`)
  - `Text`
  - `Timestamp` (UTC)
- `TypingEvent` (value object, not stored)
  - `SessionId`, `DisplayName`, `IsTyping`

Constants:

- `ChatSession.WellKnownId = "poc-session"`
- `Message.AgentSenderName = "Agent"`

Concurrency:

- Protect `ChatSession` mutations with a lock or treat the repository as the single-threaded gate. Prefer a lock on the session object now; the depth-one agent queue (step 6) handles agent serialization separately.

**Verify:** Unit tests for append order, participant upsert by connection id, and that credentials are not present on `Message`.

---

## 3. Application ports

**Goal:** Use-case boundaries the host and adapters implement.

Create:

```csharp
public interface ISessionRepository
{
    ChatSession GetOrCreate(string sessionId);
}

public interface IAgentAdapter
{
    Task<string> GetReplyAsync(
        IReadOnlyList<Message> transcript,
        string credential,
        CancellationToken cancellationToken);
}

public interface IRealtimeNotifier
{
    Task MessageReceived(string sessionId, Message message);
    Task TypingStarted(string sessionId, string displayName);
    Task TypingStopped(string sessionId, string displayName);
    Task AgentResponding(string sessionId);
    Task AgentIdle(string sessionId);
}

public interface IAgentWorkQueue
{
    void Enqueue(AgentWorkItem item);
}

public sealed record AgentWorkItem(
    string SessionId,
    string TriggeringCredential);
```

Do **not** put SignalR types on these interfaces.

**Verify:** Application project still has no ASP.NET or HTTP packages.

---

## 4. Session store (memory, optional JSON file — never a database)

**Goal:** FR1 — one shared transcript. Storage is **in-process memory**, with an optional **local JSON file** so a process restart can reload the transcript. Do not add Entity Framework, SQL, SQLite, Redis, or any other database.

Live chat still requires memory: SignalR connections, typing, the agent queue, and `Participant.ProviderCredential` exist only in the running process.

### 4.1 What is stored where

| Data | Memory | JSON file (if enabled) |
|---|---|---|
| Transcript (`Message` list) | Yes (source of truth while running) | Yes — written after each append, read on startup |
| `Participant.DisplayName` + `ConnectionId` | Yes | No — connections die with the process |
| `Participant.ProviderCredential` | Yes, connection lifetime only | **Never** |
| Typing / agent-responding | Yes (ephemeral) | **Never** |

File contents are messages only, for example `data/poc-session.json`:

```json
{
  "sessionId": "poc-session",
  "messages": [
    {
      "id": "...",
      "senderDisplayName": "Alice",
      "text": "hello",
      "timestamp": "2026-09-17T14:00:00Z"
    }
  ]
}
```

Add `data/` to `.gitignore` if the file will contain real conversation text.

### 4.2 `InMemorySessionRepository`

- Concurrent dictionary keyed by session id.
- `GetOrCreate("poc-session")` returns the same instance for the process lifetime.
- Reject any other session id so the POC cannot silently create extra sessions.

### 4.3 `FileSessionRepository` (optional wrapper)

Implement as a decorator over the in-memory session, still behind `ISessionRepository`:

1. On first `GetOrCreate`, if the JSON file exists, deserialize messages and seed the transcript. If the file is missing, start empty and create the file on first write.
2. After every successful `AppendMessage`, write the full transcript atomically: serialize to `poc-session.json.tmp`, then replace `poc-session.json` (avoids a truncated file on crash).
3. Use a lock (or serialize writes on the session lock) so two appends cannot interleave file writes.
4. If deserialize fails, log (no secrets) and start a new empty session rather than crashing the host — or fail fast; pick one and test it. Prefer fail-fast in development so a corrupt file is obvious.
5. Path from config, default `data/poc-session.json`, relative to the content root.

Keep file I/O in Infrastructure. Application handlers must not open files.

### 4.4 Config switch

```json
"SessionStore": {
  "Mode": "File",
  "FilePath": "data/poc-session.json"
}
```

- `Memory` — transcript dies on restart (fastest to stand up).
- `File` — recommended for the POC demo so a restart does not wipe the chat. Still not a database.

Default to `File` once the in-memory repository works. Build memory first, then wrap it.

**Verify:**

- Memory mode: two `GetOrCreate` calls return the same object; restart empties the transcript.
- File mode: send two messages, stop the host, start again, `JoinSession` returns those two messages; the JSON file contains no `credential` / `apiKey` fields.

---

## 5. Application use cases

**Goal:** Orchestration lives in Application, not in the hub.

Implement:

### 5.1 `JoinSessionHandler`

Input: `sessionId`, `displayName`, `connectionId`.

Steps:

1. Load session.
2. Add/update `Participant` (`ConnectionId` + `DisplayName`). Do **not** require credential on join (credential is sent only on `SendMessage`, per TDS §5 / FR7).
3. Return a copy of the full transcript.

### 5.2 `SendMessageHandler`

Input: `sessionId`, `text`, `credential`, `connectionId` (or display name resolved from the participant).

Steps:

1. Reject empty text and missing credential.
2. Resolve display name from the participant matching this connection (do not trust a client-supplied sender name for attribution).
3. Store/update `ProviderCredential` on that participant for this connection lifetime only.
4. Append human `Message` to the transcript.
5. Notify `MessageReceived`.
6. Enqueue agent work with **this** message's credential (FR7) and a snapshot is **not** required yet — the queue processor reads the live transcript when it runs.

### 5.3 `TypingHandler` / `StoppedTypingHandler`

- Broadcast only. No repository writes (FR5).
- Prefer `Clients.OthersInGroup` at the notifier implementation so a sender does not see their own typing line. The Application handler can still call `TypingStarted(sessionId, displayName)` and let the notifier exclude the caller via connection id if you pass it through.

### 5.4 Disconnect (optional but useful)

On hub disconnect, drop `ConnectionId` from the participant (or remove the participant). Do not wipe the transcript.

**Verify:** Handler unit tests with fake repository + fake notifier: send appends one message and enqueues once; typing never touches the transcript.

---

## 6. Per-session agent queue (depth one)

**Goal:** FR4 — no overlapping or out-of-order agent calls.

Implement `SessionAgentWorkQueue` as a hosted service or a singleton keyed by session id:

- One worker loop per session (for the POC, a single loop for `poc-session` is enough, but key by session id so a second session later is a config change).
- Channel / `SemaphoreSlim(1,1)` + queue.
- Processing for one item:

  1. `AgentResponding`
  2. Snapshot transcript
  3. `GetReplyAsync(snapshot, credential)`
  4. Append `Message` with sender `"Agent"`
  5. `MessageReceived`
  6. `AgentIdle`
  7. Dequeue next item if any

Error path:

- If the adapter throws, still broadcast `AgentIdle`.
- Optionally append nothing and notify a future error event. **Do not invent an error SignalR event unless you also update the TDS/frontend.** For the POC, log the failure (without the credential) and idle the agent so the UI is not stuck on “responding”.

Rules:

- Human messages are **never** delayed by the queue. They are appended and broadcast in `SendMessageHandler` immediately (TDS §6 step 2).
- Only the **agent call** waits.
- The credential on the work item is the triggering human's key, even if later humans have also sent.

**Verify:** Test with a slow fake adapter (e.g. 200ms delay): send A then B quickly; assert two agent calls ran sequentially; transcript order is `A, B, Agent(for A), Agent(for B)` **or** `A, Agent(A), B, Agent(B)` depending on timing.

Document the intended order in the test: because human messages are appended immediately, the likely order is:

`HumanA, HumanB, AgentReplyToTranscriptIncludingA, AgentReplyToTranscriptIncludingAandB`

That is correct per TDS: B is visible before A's agent reply if B arrived during A's agent call.

---

## 7. SignalR hub and realtime notifier

**Goal:** FR2, FR5, FR6 — group-scoped realtime contract from TDS §5.

Hub name: `/hubs/chat` (or `/chatHub`). Keep it stable; the frontend plan depends on it.

### Client → server

| Method | Args | Behavior |
|---|---|---|
| `JoinSession` | `sessionId`, `displayName` | `Groups.AddToGroupAsync(Context.ConnectionId, sessionId)`; run join handler; **return transcript** |
| `SendMessage` | `sessionId`, `text`, `credential` | run send handler |
| `Typing` | `sessionId`, `displayName` | relay |
| `StoppedTyping` | `sessionId`, `displayName` | relay |

Validate `sessionId == "poc-session"` (or the well-known constant). Reject empty display names.

On connect: nothing until `JoinSession`.
On reconnect: client calls `JoinSession` again (TDS §4). No extra reconnect state.

### Server → clients

Implement `SignalRRealtimeNotifier` with `IHubContext<ChatHub>`:

Always `Clients.Group(sessionId)` — **never** `Clients.All`.

| Event | Payload |
|---|---|
| `MessageReceived` | `{ id, senderDisplayName, text, timestamp }` |
| `TypingStarted` | `displayName` |
| `TypingStopped` | `displayName` |
| `AgentResponding` | none |
| `AgentIdle` | none |

JSON: camelCase so the vanilla JS client can use `message.senderDisplayName` without surprises. Configure SignalR JSON protocol accordingly.

Hub should be thin: parse args, call handlers, map exceptions to hub errors. No transcript mutation in the hub.

**Verify:** Integration test with two `HubConnection` clients in the same group:

1. Client A joins, gets empty transcript.
2. Client A sends; both receive `MessageReceived`.
3. Client B joins later; return value includes A's message (late join backfill).
4. Typing from A is seen by B.
5. A third connection in a fake group name receives nothing.

---

## 8. Stub agent adapter (before Cursor)

**Goal:** Unblock the full loop without spending API quota.

`StubAgentAdapter`: return a fixed string such as `"Stub reply to: {lastHumanText}"` after a short delay (500–1500ms) so FR6 can be tested.

Register it behind `IAgentAdapter`. Keep a compile-time or config switch (`Agent:Provider = Stub | Composer`).

**Verify:** Two SignalR clients see: human message → `AgentResponding` → agent `MessageReceived` (sender `Agent`) → `AgentIdle`. Two rapid sends produce two sequential stub replies.

---

## 9. Composer agent adapter

**Goal:** FR3, FR7, FR9.

Implement `ComposerAgentAdapter`:

- HTTP client to Cursor Cloud Agents API (create-agent / run).
- Request:
  - `Authorization: Bearer {credential}` from the work item, **not** from configuration.
  - `model: { id: "composer-2.5" }`
  - Fast mode as the default (TDS §7).
  - Omit `repos` and `env` (no-repo / chat-only).
- Body: send the full transcript as the prompt/context (both humans + prior agent replies). Format as a readable transcript, e.g.

  ```
  Alice: ...
  Bob: ...
  Agent: ...
  Alice: ...
  ```

- `GetReplyAsync` is a single await until the run completes (task-run, not token stream).
- Parse the completed run into a plain string. If the API returns structured content blocks, concatenate text only.
- Timeouts: use a generous `HttpClient` timeout (minutes, not seconds). Cancellation should idle the agent (step 6 error path).
- Never log the bearer token, never write it to disk, never include it in exception messages.

Config allowed: base URL only. No shared service API key in `appsettings`.

**Verify:**

1. Adapter unit test with `HttpMessageHandler` fake: asserts `repos`/`env` absent, model id present, `Authorization` header equals the passed credential, transcript included.
2. Manual call with a real key (optional, after stub path is solid). Check Cursor usage dashboard (open risk #1 in the TDS).

If the Cloud Agents HTTP contract differs from current Cursor docs at implementation time, update this adapter only — Application code must stay on `IAgentAdapter`.

---

## 10. Composition root (Web)

In `Program.cs`:

- `AddSignalR()`
- Register `ISessionRepository`: `InMemorySessionRepository` when `SessionStore:Mode` is `Memory`; `FileSessionRepository` wrapping the in-memory session when `File`
- `AddSingleton<IAgentWorkQueue, SessionAgentWorkQueue>()` (and hosted service if needed)
- `AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>()`
- `AddHttpClient<IAgentAdapter, ComposerAgentAdapter>()` **or** stub, from config
- Map hub
- `UseDefaultFiles` + `UseStaticFiles` for `wwwroot`
- Do not enable request logging of hub payloads (credentials travel on `SendMessage`)

`appsettings.json`:

```json
{
  "SessionStore": {
    "Mode": "File",
    "FilePath": "data/poc-session.json"
  },
  "Agent": {
    "Provider": "Stub",
    "Composer": {
      "BaseUrl": "https://api.cursor.com",
      "ModelId": "composer-2.5",
      "Mode": "Fast"
    }
  }
}
```

Default agent `Stub` so a fresh clone runs without keys. Default session store `File` so a restart keeps the transcript. Switch agent to `Composer` for the real POC demo. Switch store to `Memory` only if you want a throwaway process.

**Verify:** App starts; `/hubs/chat` negotiate succeeds; static index is served at `/`.

---

## 11. Credential and logging hardening (POC-level)

Not production security, but required by the BRD NFR:

- Never persist `ProviderCredential` outside the in-memory `Participant` (not in the JSON file, not in logs).
- Hub method `SendMessage` should not be logged by default middleware.
- Exception handlers must not interpolate the credential.
- Transcript DTOs sent to clients must omit credentials.
- `JoinSession` return type is `IReadOnlyList<MessageDto>`, not `ChatSession`.

**Verify:** Search the codebase for log/serialize of `credential` / `ProviderCredential`. Only the adapter HTTP header and the in-memory participant field should remain.

---

## 12. Backend test matrix (done when all pass)

| # | Case | FR |
|---|---|---|
| 1 | One session, ordered transcript | FR1 |
| 2 | Two hub clients receive the same human message | FR2 |
| 3 | Stub/Composer reply appended as `"Agent"` | FR3, FR8 |
| 4 | Overlapping sends → sequential agent calls | FR4 |
| 5 | Typing is not in transcript | FR5 |
| 6 | `AgentResponding` then `AgentIdle` around adapter call | FR6 |
| 7 | Adapter Authorization uses sender credential | FR7 |
| 8 | HTTP body has no `repos` / `env` | FR9 |
| 9 | Late `JoinSession` returns full transcript | TDS §4 |
| 10 | Broadcasts are group-scoped | TDS §5 |
| 11 | File mode: restart reloads transcript; JSON has messages only | store NFR |
| 12 | No EF/SQL/SQLite/Redis packages in the solution | store NFR |

---

## 13. Suggested implementation order (checklist)

1. Scaffold solution and empty Web host
2. Domain + unit tests
3. Ports + in-memory repository
4. File-backed repository wrapper + restart test
5. Join/send/typing handlers + fakes
6. SignalR hub + notifier + two-client test
7. Depth-one queue + stub adapter
8. End-to-end stub loop
9. Composer adapter + HTTP fake tests
10. Config switch Stub → Composer
11. Credential/logging pass (confirm JSON file has no keys)
12. Hand off to frontend plan (same origin `/` + `/hubs/chat`)

Frontend can start after step 6 using the stub adapter, in parallel with steps 9–11.

---

## 14. Out of scope (do not build)

- Any database (SQL Server, PostgreSQL, SQLite, Cosmos, Redis, EF Core)
- ASP.NET Identity / OAuth
- Multiple concurrent well-known sessions (design allows it; do not productize it)
- `ClaudeAgentAdapter`
- Streaming tokens from Composer
- File/repo context for the agent
- Production secrets management

---

## 15. Related documents

- `docs/BRD-SharedAgentChatPOC.md` — requirements FR1–FR9
- `docs/TDS-SharedAgentChatPOC.md` — architecture, hub contract, flows
- `docs/implementation-plans/frontend.md` — client steps that depend on this hub
