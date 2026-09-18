# Frontend Implementation Plan — Shared Agent Chat POC

**Workspace:** AgenticChat (project container)
**Document type:** Implementation Plan (Frontend)
**Date:** 2026-09-17
**Sources:** `docs/BRD-SharedAgentChatPOC.md`, `docs/TDS-SharedAgentChatPOC.md`, `docs/implementation-plans/backend.md`
**Scope:** Shared Agent Chat POC only (2 humans, 1 AI agent, one shared transcript). Unrelated to the AgenticChat SaaS concept.

The client is a **single static page**: HTML, CSS, and vanilla JavaScript plus the SignalR JS client. **No frontend framework.**

Backend contract this plan assumes (from the TDS and backend plan):

- Page origin: same host as ASP.NET Core (`/`)
- Hub: `/hubs/chat`
- Well-known session: `poc-session`
- Client methods: `JoinSession`, `SendMessage`, `Typing`, `StoppedTyping`
- Server events: `MessageReceived`, `TypingStarted`, `TypingStopped`, `AgentResponding`, `AgentIdle`

Start this plan after backend **step 5** (hub + stub agent). Composer is not required for UI work.

---

## 0. Target outcome

Two browser tabs on `/chat?session=poc-session` (or `/` with that query) where each human:

1. Enters a display name and their own Cursor API key once.
2. Sees the same ordered transcript, attributed by sender.
3. Sees the other human's typing indicator.
4. Sees “Agent is responding…” while a call is in flight.
5. Receives agent replies as they are broadcast.

Maps to **FR1–FR8** on the client (FR9 is server-side). Keys stay in a JS variable for the tab lifetime only. The browser does not persist the transcript; if the backend uses file mode, a server restart can still backfill the list via `JoinSession`.

---

## 1. Static assets and page shell

**Goal:** Light page served from `SharedAgentChat.Web/wwwroot`.

Files:

```
wwwroot/
  index.html
  css/chat.css
  js/chat.js
```

`index.html`:

- Semantic structure only; no inline business logic beyond a script tag.
- Load SignalR from a local `lib` copy **or** the official CDN (`@microsoft/signalr`). Prefer a local copy under `wwwroot/lib` so the demo works offline.
- Read `session` from the query string; default to `poc-session` if missing.

Layout regions (TDS §8):

1. **Join panel** — display name, API key, Join button (shown first).
2. **Chat panel** — hidden until join succeeds:
   - Header (session id, own display name — not the API key)
   - Message list
   - Presence line (typing / agent responding)
   - Composer: textarea + Send

Keep the join panel in the DOM but hide it after join; do not leave the API key in a visible input after join if you can move it to a JS variable and clear the field.

**Verify:** `/` loads the shell with join fields; no console errors if the hub is down yet (connection happens after Join).

---

## 2. Join flow and in-memory identity

**Goal:** One-time identity per tab. No login system.

On Join:

1. Trim display name; reject empty.
2. Trim API key; reject empty. Do **not** validate `crsr_` format strictly (keys may change); a non-empty check is enough.
3. Store:

   ```js
   state = {
     sessionId,
     displayName,
     credential,      // memory only
     messages: [],    // keyed by message.id
     typingNames: new Set(),
     agentResponding: false,
     connection: null
   }
   ```

4. Never write `credential` to `localStorage`, `sessionStorage`, cookies, or `URL` query.
5. Clear the password-type API key input (`input.value = ""`) after copying to `state.credential`.
6. Use `type="password"` (or `autocomplete="off"`) for the key field.

Reconnect/refresh: user must re-enter name and key (BRD NFR). That is intended.

**Verify:** After join, DevTools Application tab shows no stored key. Refresh shows the join panel again.

---

## 3. SignalR connection and `JoinSession`

**Goal:** Late join / refresh backfill (TDS §4, FR1).

In `js/chat.js`:

1. `new signalR.HubConnectionBuilder().withUrl("/hubs/chat").withAutomaticReconnect().build()`
2. Register server event handlers **before** `start()` (so replayed events after reconnect are not missed).
3. `await connection.start()`
4. `const transcript = await connection.invoke("JoinSession", sessionId, displayName)`
5. Render the returned transcript into the message list **from scratch** (replace, do not append blindly).
6. Show the chat panel.

Reconnect:

- On `connection.onreconnected`, call `JoinSession` again and **replace** the message list with the returned transcript.
- This addresses TDS open risk #3 (duplicate-render glitches). Use message `id` as the DOM key; rebuild the list from the server snapshot rather than appending on top of stale nodes.

Disconnect UX:

- If `start()` fails, show an error on the join panel; stay on join.
- If the connection drops later, disable Send and show “Reconnecting…” in the presence line. Do not wipe the visible transcript until a successful re-join replaces it.

**Verify:** Tab B joins after Tab A has sent messages; B’s list matches A’s immediately from the invoke return value, without waiting for a new send.

---

## 4. Message list (attribution and order)

**Goal:** FR1, FR2, FR8.

`MessageReceived` payload:

```ts
{ id, senderDisplayName, text, timestamp }
```

Render rules:

- Append if `id` is not already in `state.messages`; skip duplicates (reconnect + live event can race).
- Sort/display in arrival/transcript order (server is source of truth; do not re-sort by local clock).
- Attribution:
  - Own messages: visually distinct (e.g. right-aligned or different background) when `senderDisplayName === state.displayName`
  - Other human: default bubble
  - Agent: `senderDisplayName === "Agent"` — third style so the agent is obvious
- Show sender name and a readable local time from `timestamp`.
- Preserve whitespace in `text` (`white-space: pre-wrap`) so multi-line messages stay intact.
- Auto-scroll the list to the bottom **only if** the user was already near the bottom; do not yank scroll if they are reading history.
- Escape HTML. Treat `text` and `displayName` as text nodes or use `textContent` — never `innerHTML` with server/user strings.

**Verify:** Two tabs; A sends “hello”; both lists show `Alice` + text. Agent stub reply shows as `Agent`. XSS payload in a message renders as literal text.

---

## 5. Send message

**Goal:** FR2, FR7 (credential pass-through).

Send control:

- Enabled only when connected, joined, textarea non-empty, and preferably not waiting on the invoke (disable the button during the send round-trip to prevent double-submit).
- Enter sends; Shift+Enter inserts a newline.
- Call `connection.invoke("SendMessage", sessionId, text, state.credential)`.
- **Do not** optimistically insert the message. Wait for `MessageReceived` so both tabs stay identical and ids match the server.
- On success: clear the textarea; fire `StoppedTyping`.
- On failure: re-enable Send, keep the text, show an inline error (invalid key, hub error). Never print the credential in that error.

Do not send typing events to the agent; that is server-side. The client must not call any extra “ask agent” API — sending a human message is enough.

**Verify:** Network tab: SignalR invocation includes the key in the hub payload (expected for this POC) but no REST POST of the transcript to Cursor from the browser. Cursor is called only from the backend.

---

## 6. Typing indicator

**Goal:** FR5 — ephemeral, other human only.

Behavior:

- On textarea `input`, invoke `Typing` (debounced: at most every 300–500ms while keys are pressed).
- After 2s of inactivity, invoke `StoppedTyping` (timer reset on each key).
- On Send / blur / disconnect, invoke `StoppedTyping`.
- Ignore `TypingStarted` / `TypingStopped` for `displayName === state.displayName` if the server still echoes to the sender.

Presence line:

- One or more names: `Alice is typing…` / `Alice, Bob are typing…`
- Multiple typers: keep a `Set` of names; add on start, delete on stop.
- This line must **not** be inserted into the message list or transcript array.

**Verify:** A types, B sees the line, A stops, line clears. Transcript length unchanged. Agent does not mention “typing” unless a human typed that word as a message.

---

## 7. Agent responding indicator

**Goal:** FR6 — Composer latency is visible, UI is not frozen.

- On `AgentResponding`: `state.agentResponding = true` → show `Agent is responding…` (both tabs).
- On `AgentIdle`: hide it.
- Sending must remain possible while the agent is busy (TDS: human messages are not queued). Keep Send enabled; the depth-one queue is server-side.
- Optional: disable nothing except perhaps a subtle note that the agent is still working on an earlier turn.
- The presence region can show typing and agent status together, e.g. `Alice is typing…` on one line and `Agent is responding…` on the next.

**Verify:** With stub delay, both tabs show the agent line between the human message and the agent message, then it disappears. Rapid sends from both humans: agent line stays on until the queue drains (`AgentIdle` after each item, then `AgentResponding` again — UI should tolerate that flicker; if it is noisy, keep a counter instead of a boolean:

```js
agentInFlight += 1  // on AgentResponding
agentInFlight -= 1  // on AgentIdle
show = agentInFlight > 0
```

Prefer the counter so overlapping idle/responding events cannot get stuck.

---

## 8. CSS and “light and performant”

**Goal:** BRD frontend-weight NFR.

- One CSS file, no UI kit.
- Readable default: system font stack, sufficient contrast, message bubbles, sticky composer at the bottom, scrollable transcript.
- Join panel centered; chat panel full viewport height.
- Do not add animations beyond a simple fade on the presence line.
- Viewport: usable at ~1280px desktop (two testers side by side). A basic mobile-width layout is nice-to-have, not required for the POC.

**Verify:** Two windows side by side, each ~50% of a 1080p screen, can read messages and type without horizontal scroll.

---

## 9. Duplicate and reconnect hardening

**Goal:** TDS open risk #3.

Checklist in `renderTranscript(messages)`:

1. Build a new fragment from the array.
2. Replace `messageList.replaceChildren(fragment)` (or `innerHTML = ""` then append text nodes).
3. Index by `message.id`.
4. Live `MessageReceived` during rebuild: buffer events or ignore ids already drawn.

Also:

- Query `?session=poc-session` documented in a short comment at the top of `index.html`.
- Opening `/` without a query still joins `poc-session` so testers cannot miss the param.

**Verify:** Refresh Tab B mid-conversation: re-join with name/key, list matches Tab A with no duplicated bubbles. Kill network briefly (DevTools offline) then back online: after reconnect + JoinSession, still no dupes.

---

## 10. Manual two-tab acceptance script

Run against the stub agent first, then Composer.

| Step | Action | Expected |
|---|---|---|
| 1 | Open `/` in Tab A and Tab B | Join panel |
| 2 | A: name `Alice`, paste key, Join | Chat panel, empty list |
| 3 | B: name `Bob`, paste **Bob's** key, Join | Chat panel, empty (or Alice’s messages if A already sent) |
| 4 | A types without sending | B sees `Alice is typing…`; A does not |
| 5 | A sends `hello` | Both lists: `Alice: hello` immediately |
| 6 | Wait | Both see `Agent is responding…`, then `Agent: …`, then idle |
| 7 | B sends `follow up` while agent still running on A’s turn | B’s message appears at once on both tabs; second agent reply comes after the first |
| 8 | Refresh B, rejoin as `Bob` | Full transcript backfilled, no dupes |
| 8b | (File store) Restart the backend, both tabs rejoin | Same transcript as before the restart; still no keys in browser storage |
| 9 | Inspect Application storage | No API key persisted |
| 10 | Switch backend to Composer and repeat 5–7 | Same UX; replies from Composer 2.5; check usage dashboard |

Do not declare the frontend done after a single screenshot. Complete this script.

---

## 11. Suggested implementation order (checklist)

1. `index.html` + `css/chat.css` shell (join + chat regions)
2. State object and join validation (memory-only credential)
3. SignalR client, `JoinSession`, initial transcript render
4. `MessageReceived` list + HTML escaping + attribution styles
5. `SendMessage` (no optimistic insert)
6. Typing debounce + presence line
7. Agent responding counter + presence line
8. Reconnect / replace-on-join (anti-dupe)
9. Two-tab acceptance script on stub
10. Repeat on Composer when backend provider is switched

---

## 12. Out of scope (do not build)

- React, Vue, Angular, or bundlers (unless a bundler is required later; the POC should not need one)
- `localStorage` “remember my key”
- Markdown rendering of agent output (plain text is enough)
- Avatars, reactions, message edit/delete
- File upload / repo picker (FR9)
- Second agent / `@mention` routing
- Auth screens

---

## 13. Related documents

- `docs/BRD-SharedAgentChatPOC.md` — requirements and NFRs
- `docs/TDS-SharedAgentChatPOC.md` — §5 hub contract, §8 frontend design
- `docs/implementation-plans/backend.md` — hub path, event names, stub-first order
