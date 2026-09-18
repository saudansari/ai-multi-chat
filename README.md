# Shared Agent Chat POC

Two **human testers**, each in their own browser tab, each with **their own Cursor API key**, talking to **one shared Composer 2.5 agent** in a single transcript.

This is not two AI models in two tabs. Mixed providers (for example Cursor in one tab and Claude in the other) and two agents in the same session are out of scope. What you are proving is: two independently authenticated humans, one shared agent, one live transcript.

Full requirements: `docs/BRD-SharedAgentChatPOC.md`. Design: `docs/TDS-SharedAgentChatPOC.md`. Build order: `docs/implementation-plans/`.

---

## What you need

| Item | Notes |
|---|---|
| .NET SDK | LTS (`net8` or `net9`), matching the Web project |
| Two Cursor API keys | One per tester / tab — do not share a key |
| Two browser tabs | Same machine is fine; two windows side by side is easier |

### Create each Cursor API key

Each tester, on **their own** Cursor account:

1. Sign in at [cursor.com/dashboard](https://cursor.com/dashboard).
2. Open **Dashboard → API Keys** ([cursor.com/dashboard/api](https://cursor.com/dashboard/api)).
3. **New API Key**, name it (for example `poc-alice` / `poc-bob`), create it.
4. Copy it immediately (`crsr_...`). It is shown once.

Keys live only in that tab’s memory after you paste them. They are not saved to `localStorage` or to the JSON transcript file. Refreshing the tab means you paste the key again.

Composer usage bills against **whichever tester’s key** triggered that agent turn.

---

## Run the app

From the repo root (after the backend is built):

```bash
dotnet run --project backend/src/SharedAgentChat.Web
```

Open:

```
http://localhost:5xxx/?session=poc-session
```

Use the port printed in the console. `/` without a query still joins `poc-session`.

### Config that matters for this test

In `appsettings.json` (or environment):

| Setting | For this two-tab test |
|---|---|
| `Agent:Provider` | `Stub` in `appsettings.json` (canned echoes, no Cursor spend). Local `dotnet run` uses Development, which is `Composer`. |
| `SessionStore:Mode` | `File` recommended (`data/poc-session.json`). `Memory` wipes chat on restart. |

There is no database.

---

## Two-tab test (follow this)

Place two windows side by side. Treat them as two people, not two copies of the same user.

### 1. Join as two different testers

**Tab A**

1. Open the app URL.
2. Display name: `Alice`.
3. Paste **Alice’s** Cursor API key.
4. Join.

**Tab B**

1. Open the **same** URL (new tab or window).
2. Display name: `Bob`.
3. Paste **Bob’s** Cursor API key (a different key).
4. Join.

You should see the chat panel, not the join form. The API key field must not reappear with the secret still visible.

If B joins after A already sent messages, B’s list must already show those messages (backfill).

### 2. Shared transcript

1. In Tab A, send `hello from Alice`.
2. Both tabs show `Alice: hello from Alice` immediately, same order.
3. In Tab B, send `hello from Bob`.
4. Both tabs show Bob’s line under Alice’s.

Attribution: human names on human lines; agent lines say `Agent`, not Alice or Bob.

### 3. Typing indicator

1. In Tab A, type without sending.
2. Tab B shows `Alice is typing…`.
3. Tab A does **not** show its own typing line.
4. Stop typing / send. The line on B clears.
5. Reverse: Bob types, Alice sees it.

Typing must not appear as a message in the transcript.

### 4. Shared agent reply (one agent, billed per sender)

With `Agent:Provider` = `Stub`, replies are fake and delayed on purpose. With `Composer`, wait longer — this is a task run, not a token stream.

1. Alice sends a question.
2. **Both** tabs show `Agent is responding…`.
3. When it finishes, **both** tabs get the same `Agent: …` line, then the indicator clears.
4. Bob sends a follow-up. Same pattern. The agent sees the **full** shared transcript (Alice + Bob + prior Agent lines), not a private thread.

The credential used for that Cursor call is the key of the human who **just sent** the message (Alice’s key for Alice’s turn, Bob’s for Bob’s). You will not see the keys in the UI. To confirm billing, each tester checks their own Cursor usage dashboard after Composer mode.

### 5. Overlapping sends (queue of depth one)

1. Alice sends a message.
2. While `Agent is responding…` is still showing, Bob sends another message.
3. Bob’s **human** message appears on both tabs immediately.
4. The second agent reply arrives **after** the first agent reply finishes — not two agent replies at once, and not out of order.

Human messages are never held back by the agent queue.

### 6. Refresh and reconnect

1. Refresh Tab B.
2. Join again as `Bob` with Bob’s key (keys are not remembered).
3. Transcript matches Tab A. No duplicated bubbles.

### 7. File store (optional)

If `SessionStore:Mode` is `File`:

1. Send a few messages.
2. Stop the backend.
3. Start it again.
4. Both tabs rejoin.
5. The same transcript is back.
6. Open `data/poc-session.json`: messages only — **no API keys**.

`Memory` mode: restart clears the chat. That is expected.

### 8. Keys must not stick around

In each tab: DevTools → Application → Local Storage / Session Storage. The Cursor key must not be stored. After refresh, you paste it again.

---

## Stub vs Composer

| Mode | When to use | What you should see |
|---|---|---|
| `Stub` | First pass, no Cursor spend | Same two-tab UX; replies are canned |
| `Composer` | Real POC | Same UX; replies from Composer 2.5; each turn uses the sending tester’s key |

Do not skip the stub pass. If two tabs already fail on stub, Composer will not fix it.

---

## Pass / fail

**Pass** when all of this is true:

- Two tabs, two display names, two different API keys.
- One ordered transcript, identical on both tabs.
- Typing only as a presence line, only for the other person.
- One agent reply stream, visible to both, with `Agent is responding…` while a call is in flight.
- Overlapping human sends do not overlap agent calls.
- Refresh backfills; file mode survives a process restart; keys never hit disk or browser storage.

**Not a pass:**

- Same API key pasted in both tabs (you did not test FR7).
- Optimistic local-only messages that the other tab never gets.
- Two different AI products in the two tabs (not this POC).

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Second tab is blank after join | Hub not connected, or not the same `poc-session` URL |
| Only one tab updates | Broadcast not scoped to the session group, or one tab failed `JoinSession` |
| UI stuck on “Agent is responding…” | Adapter error; check server logs (they must not print the API key) |
| Composer 401 / failed reply | Wrong or revoked key on **that** tab; the other tab’s key is unrelated |
| Duplicate messages after refresh | Client appending instead of replacing the transcript from `JoinSession` |
| Transcript gone after restart | `SessionStore:Mode` is `Memory`, or the JSON file was deleted |

---

## Related docs

- `docs/BRD-SharedAgentChatPOC.md` — FR1–FR9, cost model
- `docs/TDS-SharedAgentChatPOC.md` — SignalR contract and flows
- `docs/implementation-plans/backend.md`
- `docs/implementation-plans/frontend.md`
