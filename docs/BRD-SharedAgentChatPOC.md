# Business Requirements Document — Shared Agent Chat POC

**Workspace:** AgenticChat (project container)
**Document type:** BRD (Proof of Concept)
**Date:** 2026-09-08
**Author:** Saud Sajid Ansari

> **Scope note:** This POC is unrelated to the "AgenticChat" SaaS concept (project-scoped team chat with auto-generated living docs feeding AI coding agents) tracked elsewhere. It only shares this project workspace with that concept — it does not implement or depend on it. See the "Shared Agent Chat POC" naming below for the actual scope: two human users and one AI agent in a single shared chat transcript.

---

## 1. Purpose

Define the requirements for a proof-of-concept multi-user chat session in which two human users, each authenticated with their own AI provider account, converse with a single shared AI agent inside one shared conversation transcript.

The POC exists to validate the core mechanic — two humans and one agent, sharing one transcript, in real time — before any investment in a broader product.

## 2. Background

The idea originated from a broader concept of humans and AI agents collaborating inside one session, potentially across different AI providers (Claude, Cursor). Through discussion, the scope was deliberately narrowed for this first POC to the simplest version that still proves the mechanic: **two humans, one AI agent, one shared transcript, chat only.**

## 3. Objectives

- Prove that two independently-authenticated humans can hold a real-time conversation with one shared AI agent in a single transcript.
- Establish a clean separation between the agent integration and the chat mechanics, so the AI provider can be swapped later without reworking the application.
- Keep the build small enough to stand up quickly: no database, no formal authentication, no file/code editing capability.

## 4. Scope

### In scope

- One shared chat session with one ordered, shared transcript.
- Two human participants, each in their own browser tab, each identified by a display name they enter once (no formal login).
- One AI agent participant, backed by Cursor's Composer 2.5 model, replying into the same shared transcript.
- Real-time delivery of every message (human or agent) to both connected participants.
- A "typing…" indicator so each human can see when the other is composing a message.
- An "agent is responding…" indicator while an agent call is in progress.
- Each human's messages authenticated to the AI provider using that human's own Cursor API key (pass-through credential model).

### Out of scope (for this POC)

- File or repository access for the agent (chat only — no code editing, no repo context).
- Running two AI agents simultaneously in the same session.
- A database of any kind (SQL, SQLite, Redis, etc.). Transcript state lives in memory and may optionally be snapshotted to a local JSON file.
- A real authentication/login system (two browser tabs stand in for two users).
- Production-grade security hardening (secrets management, key rotation, audit logging).
- Support for mixed AI providers within a single agent slot (e.g., one human's messages routed to Claude, another's to Cursor).

## 5. Stakeholders

| Role | Description |
|---|---|
| Human participant 1 | Tester, own Cursor account/API key |
| Human participant 2 | Tester, own Cursor account/API key |
| AI agent | Cursor Composer 2.5, via Cursor's Cloud Agents API, no-repo (chat) mode |

## 6. Functional Requirements

| ID | Requirement |
|---|---|
| FR1 | The system shall maintain one shared, ordered message transcript per session, visible identically to both human participants. |
| FR2 | Both humans shall be able to send messages into the shared transcript; every message (from either human, or from the agent) shall be broadcast to both connected participants in real time. |
| FR3 | The single AI agent shall receive the shared transcript and generate a reply, which is appended to the transcript and broadcast to both humans. |
| FR4 | Message processing shall be serialized per session (a queue of depth one) so that two near-simultaneous human messages cannot trigger two overlapping or out-of-order agent calls. |
| FR5 | The system shall show a "typing…" indicator to one human when the other is actively composing a message. This is an ephemeral presence signal only — it is never written to the transcript and never sent to the agent. |
| FR6 | The system shall show an "agent is responding…" indicator to both humans while an agent call is in flight. |
| FR7 | Each human's messages shall be authenticated to the AI provider using that human's own Cursor API key; the credential used for a given agent call is resolved from whichever human's message triggered it. |
| FR8 | Every message in the transcript shall be attributed to its sender (human display name, or "Agent"). |
| FR9 | The agent shall run in "no-repo" mode — no file or repository context is created or attached to any agent call. |

## 7. Non-Functional Requirements

- **Latency expectations:** Composer 2.5, accessed via Cursor's Cloud Agents API, is a task-oriented run rather than an instant token stream — replies may take noticeably longer than a typical chat reply. The UI must communicate this (FR6) rather than appear frozen.
- **Frontend weight:** the client shall remain light and performant — plain HTML/CSS/JavaScript with the SignalR JS client, no frontend framework.
- **Persistence:** no database. Transcript state lives in process memory. Optionally write/read the message list from a local JSON file so a restart can restore the chat. API keys and presence signals are never written to that file.
- **Credential handling:** API keys are held only in browser memory for the duration of the session (never written to localStorage, never logged server-side) and are re-entered on refresh.

## 8. Technical Architecture Summary

- **Backend:** C#, Onion Architecture.
  - **Domain** — `ChatSession`, `Message`, `Participant`, typing-event value objects. No external dependencies.
  - **Application** — use cases and ports: message-send orchestration, `IAgentAdapter.GetReplyAsync(transcript, credential)`, `ISessionRepository`, `IRealtimeNotifier`.
  - **Infrastructure** — concrete adapters: `ComposerAgentAdapter` (Cursor Cloud Agents API, no-repo mode), in-memory `SessionRepository`, optional JSON-file session snapshot.
  - **Presentation** — ASP.NET Core Web API host with a **SignalR** hub (`SendMessage`, `Typing`/`StoppedTyping` methods) as the real-time transport.
- **Frontend:** single lightweight page, opened in two browser tabs; SignalR JS client; message list with sender attribution, typing indicator, agent-responding indicator, a one-time display-name field, and a one-time field to paste each human's own Cursor API key.
- **Agent integration:** Cursor Cloud Agents API, `model: { id: "composer-2.5" }`, `repos` and `env` omitted (no-repo/chat mode), authenticated per call with the sending human's API key (`Authorization: Bearer <key>`).

## 9. Credentials & Cost Model

### Obtaining a Cursor API key (per human)

1. Sign in at `cursor.com/dashboard`.
2. Go to **Dashboard → API Keys** (`cursor.com/dashboard/api`).
3. Click **New API Key**, name it, and create it.
4. Copy the key immediately (format `crsr_...`) — it is shown once.

### Cursor pricing (for reference)

| Plan | Price | Notes |
|---|---|---|
| Hobby (Free) | $0 | Limited completions/agent requests |
| Pro | $20/mo | ~$20 of pooled model usage |
| Pro+ | $60/mo | ~$60 of usage, ~3x Pro's agent limits |
| Ultra | $200/mo | ~$400 of usage, ~20x Pro's agent capacity |

Composer 2.5 token rates (apply once a plan's included allowance is exceeded):

| Mode | Input | Cache read | Output |
|---|---|---|---|
| Standard | $0.50/M tokens | $0.20/M tokens | $2.50/M tokens |
| Fast | $3/M tokens | $0.50/M tokens | $15/M tokens |

Composer 2.5 usage is included in Pro/Pro+/Ultra plans up to the plan's allowance; the API key is generated from the same dashboard/account as the subscription (unlike Anthropic's model — see below).

### Anthropic Claude (kept for reference — not used in this POC's first build)

- Requires a separate Anthropic Console account and API key; a claude.ai Pro/Max subscription cannot be used to authenticate a third-party application (Anthropic explicitly disallows this).
- Claude Sonnet 5 API pricing: $2/M input tokens, $10/M output tokens (standard); cache hits $0.20/M; batch API $1/$5 per M.
- The adapter interface (`IAgentAdapter`) is designed so a `ClaudeAgentAdapter` could be added later without reworking the application.

## 10. Assumptions

*(Per project preference, assumptions are kept explicitly separate from confirmed requirements above.)*

- Both humans will use the same provider (Cursor) for this phase of the POC; a mixed-provider setup (one human's turns via Claude, another's via Cursor) is not being built now.
- "Own logged-in account" means each human supplies their own Cursor API key, not a shared service credential.
- Two browser tabs with a typed display name is sufficient identity for this POC; no password-based or OAuth login is being built.
- The shared transcript is a single context — the agent sees the full conversation from both humans, not a private thread per participant.

## 11. Open Questions / Risks

| # | Item | Status |
|---|---|---|
| 1 | Does a Cloud Agents API call draw down the same subscription usage pool as the Cursor IDE, or is it billed separately regardless of plan? | Not confirmed in Cursor's documentation — verify empirically via the usage dashboard after initial test calls. |
| 2 | How will Composer's task-based latency (vs. instant chat) feel in practice for two humans conversing? | To be observed once the POC is running; may need to tune the "agent is responding…" UX further. |

## 12. Future Considerations (Not in POC Scope)

- Reintroducing a second AI agent (e.g., Claude) into the same session, with an explicit addressing mechanism (e.g., `@mention`) to avoid the cross-agent feedback-loop risk identified during design discussion.
- Persistent transcript storage in a database (a local JSON snapshot is already in POC scope).
- A real authentication system.
- Granting the agent file/repository access for coding tasks beyond plain chat.
