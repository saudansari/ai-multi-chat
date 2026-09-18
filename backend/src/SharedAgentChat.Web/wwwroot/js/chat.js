(() => {
  const DEFAULT_SESSION = "poc-session";
  const AGENT_NAME = "Agent";
  const TYPING_DEBOUNCE_MS = 400;
  const STOPPED_TYPING_MS = 2000;

  const els = {
    joinPanel: document.getElementById("join-panel"),
    chatPanel: document.getElementById("chat-panel"),
    joinForm: document.getElementById("join-form"),
    displayName: document.getElementById("display-name"),
    apiKey: document.getElementById("api-key"),
    joinError: document.getElementById("join-error"),
    joinButton: document.getElementById("join-button"),
    agentMode: document.getElementById("agent-mode"),
    sessionId: document.getElementById("session-id"),
    ownName: document.getElementById("own-name"),
    headerAgentMode: document.getElementById("header-agent-mode"),
    messageList: document.getElementById("message-list"),
    typingLine: document.getElementById("typing-line"),
    agentLine: document.getElementById("agent-line"),
    connectionLine: document.getElementById("connection-line"),
    composer: document.getElementById("composer"),
    messageInput: document.getElementById("message-input"),
    sendButton: document.getElementById("send-button"),
    sendError: document.getElementById("send-error"),
  };

  const state = {
    sessionId: readSessionId(),
    displayName: "",
    credential: "",
    messages: [],
    typingNames: new Set(),
    agentInFlight: 0,
    connection: null,
    joined: false,
    sending: false,
    reconnecting: false,
  };

  let typingDebounce = null;
  let stoppedTypingTimer = null;
  let lastTypingInvoke = 0;

  function readSessionId() {
    const value = new URLSearchParams(window.location.search).get("session");
    return (value && value.trim()) || DEFAULT_SESSION;
  }

  function showError(el, message) {
    el.textContent = message || "";
    el.hidden = !message;
  }

  function hubErrorMessage(error) {
    const raw = error && (error.message || String(error));
    return raw.replace(/^An unexpected error occurred invoking '.*'\. /, "") || "Something went wrong.";
  }

  function setSendEnabled() {
    const connected = state.connection && state.connection.state === signalR.HubConnectionState.Connected;
    const hasText = els.messageInput.value.trim().length > 0;
    els.sendButton.disabled = !state.joined || !connected || state.sending || state.reconnecting || !hasText;
    els.messageInput.disabled = !state.joined || !connected || state.reconnecting;
  }

  function renderPresence() {
    const names = [...state.typingNames].filter((name) => name !== state.displayName);
    if (names.length === 0) {
      els.typingLine.hidden = true;
      els.typingLine.textContent = "";
    } else if (names.length === 1) {
      els.typingLine.hidden = false;
      els.typingLine.textContent = `${names[0]} is typing…`;
    } else {
      els.typingLine.hidden = false;
      els.typingLine.textContent = `${names.join(", ")} are typing…`;
    }

    els.agentLine.hidden = state.agentInFlight <= 0;
    els.connectionLine.hidden = !state.reconnecting;
  }

  function wasNearBottom() {
    const list = els.messageList;
    return list.scrollHeight - list.scrollTop - list.clientHeight < 80;
  }

  function normalizeMessage(message) {
    if (!message || typeof message !== "object") {
      return null;
    }

    const id = message.id || message.Id;
    if (!id) {
      return null;
    }

    return {
      id,
      senderDisplayName: message.senderDisplayName || message.SenderDisplayName || "",
      text: message.text ?? message.Text ?? "",
      timestamp: message.timestamp || message.Timestamp,
    };
  }

  function createMessageNode(message) {
    const article = document.createElement("article");
    article.className = "message";
    article.dataset.id = message.id;

    if (message.senderDisplayName === AGENT_NAME) {
      article.classList.add("agent");
    } else if (message.senderDisplayName === state.displayName) {
      article.classList.add("own");
    }

    const who = document.createElement("div");
    who.className = "who";

    const name = document.createElement("span");
    name.textContent = message.senderDisplayName;

    const time = document.createElement("time");
    const stamp = new Date(message.timestamp);
    if (!Number.isNaN(stamp.getTime())) {
      time.dateTime = stamp.toISOString();
      time.textContent = stamp.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
    }

    who.append(name, time);

    const text = document.createElement("div");
    text.className = "text";
    text.textContent = message.text ?? "";

    article.append(who, text);
    return article;
  }

  function renderTranscript(messages) {
    const shouldStick = wasNearBottom();
    state.messages = Array.isArray(messages)
      ? messages.map(normalizeMessage).filter(Boolean)
      : [];
    const fragment = document.createDocumentFragment();
    for (const message of state.messages) {
      fragment.append(createMessageNode(message));
    }
    els.messageList.replaceChildren(fragment);
    if (shouldStick) {
      els.messageList.scrollTop = els.messageList.scrollHeight;
    }
  }

    function appendMessage(raw) {
    const message = normalizeMessage(raw);
    // #region agent log
    fetch('http://127.0.0.1:7288/ingest/bd4d9a33-93fb-4a69-95ad-ef53426eebe3',{method:'POST',headers:{'Content-Type':'application/json','X-Debug-Session-Id':'17d39d'},body:JSON.stringify({sessionId:'17d39d',location:'chat.js:appendMessage',message:'MessageReceived',data:{rawKeys: raw && typeof raw === 'object' ? Object.keys(raw) : [], id: message && message.id, sender: message && message.senderDisplayName, textChars: message && message.text ? message.text.length : 0, accepted: !!message},timestamp:Date.now(),hypothesisId:'H-ui'})}).catch(()=>{});
    // #endregion
    if (!message) {
      return;
    }
    if (state.messages.some((item) => item.id === message.id)) {
      return;
    }
    const shouldStick = wasNearBottom();
    state.messages.push(message);
    els.messageList.append(createMessageNode(message));
    if (shouldStick) {
      els.messageList.scrollTop = els.messageList.scrollHeight;
    }
  }

  function registerHandlers(connection) {
    connection.on("MessageReceived", appendMessage);

    connection.on("TypingStarted", (displayName) => {
      if (!displayName || displayName === state.displayName) {
        return;
      }
      state.typingNames.add(displayName);
      renderPresence();
    });

    connection.on("TypingStopped", (displayName) => {
      if (!displayName) {
        return;
      }
      state.typingNames.delete(displayName);
      renderPresence();
    });

    connection.on("AgentResponding", () => {
      state.agentInFlight += 1;
      renderPresence();
    });

    connection.on("AgentIdle", () => {
      state.agentInFlight = Math.max(0, state.agentInFlight - 1);
      renderPresence();
    });

    connection.onreconnecting(() => {
      state.reconnecting = true;
      renderPresence();
      setSendEnabled();
      void invokeStoppedTyping();
    });

    connection.onreconnected(async () => {
      state.reconnecting = false;
      renderPresence();
      try {
        await joinSession();
      } catch (error) {
        showError(els.sendError, hubErrorMessage(error));
      }
      setSendEnabled();
    });

    connection.onclose(() => {
      state.reconnecting = true;
      renderPresence();
      setSendEnabled();
    });
  }

  async function joinSession() {
    const transcript = await state.connection.invoke("JoinSession", state.sessionId, state.displayName);
    state.joined = true;
    renderTranscript(transcript);
    renderPresence();
    setSendEnabled();
  }

  async function connectAndJoin() {
    if (state.connection) {
      try {
        await state.connection.stop();
      } catch {
        // A failed prior connection can be discarded.
      }
    }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl("/hubs/chat")
      .withAutomaticReconnect()
      .build();

    registerHandlers(connection);
    state.connection = connection;
    await connection.start();
    await joinSession();
  }

  async function invokeStoppedTyping() {
    if (!state.connection || state.connection.state !== signalR.HubConnectionState.Connected || !state.joined) {
      return;
    }
    try {
      await state.connection.invoke("StoppedTyping", state.sessionId, state.displayName);
    } catch {
      // Presence is best-effort.
    }
  }

  async function invokeTyping() {
    if (!state.connection || state.connection.state !== signalR.HubConnectionState.Connected || !state.joined) {
      return;
    }
    try {
      await state.connection.invoke("Typing", state.sessionId, state.displayName);
    } catch {
      // Presence is best-effort.
    }
  }

  function scheduleTyping() {
    const now = Date.now();
    if (now - lastTypingInvoke >= TYPING_DEBOUNCE_MS) {
      lastTypingInvoke = now;
      void invokeTyping();
    } else if (!typingDebounce) {
      typingDebounce = setTimeout(() => {
        typingDebounce = null;
        lastTypingInvoke = Date.now();
        void invokeTyping();
      }, TYPING_DEBOUNCE_MS);
    }

    clearTimeout(stoppedTypingTimer);
    stoppedTypingTimer = setTimeout(() => {
      void invokeStoppedTyping();
    }, STOPPED_TYPING_MS);
  }

  function showChat() {
    els.joinPanel.hidden = true;
    els.joinPanel.setAttribute("aria-hidden", "true");
    els.chatPanel.hidden = false;
    els.chatPanel.removeAttribute("aria-hidden");
    els.sessionId.textContent = state.sessionId;
    els.ownName.textContent = state.displayName;
    els.messageInput.focus();
  }

  async function loadAgentMode() {
    try {
      const response = await fetch("/api/agent");
      if (!response.ok) {
        throw new Error("agent config unavailable");
      }
      const data = await response.json();
      const provider = (data.provider || "Stub").trim();
      const isComposer = provider.toLowerCase() === "composer";
      els.agentMode.textContent = isComposer
        ? "Agent mode: Composer. Your Cursor API key authenticates each agent turn."
        : "Agent mode: Stub. Replies are canned echoes. Set Agent:Provider to Composer to call Cursor.";
      els.headerAgentMode.textContent = isComposer ? "Composer" : "Stub";
    } catch {
      els.agentMode.textContent =
        "Could not read agent mode. If replies look like “Stub reply to: …”, the host is still on Stub.";
      els.headerAgentMode.textContent = "unknown";
    }
  }

  els.joinForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    showError(els.joinError, "");

    const displayName = els.displayName.value.trim();
    const credential = els.apiKey.value.trim();
    if (!displayName) {
      showError(els.joinError, "Enter a display name.");
      return;
    }
    if (!credential) {
      showError(els.joinError, "Enter your Cursor API key.");
      return;
    }

    state.displayName = displayName;
    state.credential = credential;
    els.apiKey.value = "";
    els.joinButton.disabled = true;

    try {
      await connectAndJoin();
      showChat();
    } catch (error) {
      state.joined = false;
      state.credential = "";
      showError(els.joinError, hubErrorMessage(error));
    } finally {
      els.joinButton.disabled = false;
    }
  });

  els.composer.addEventListener("submit", async (event) => {
    event.preventDefault();
    await sendMessage();
  });

  els.messageInput.addEventListener("input", () => {
    showError(els.sendError, "");
    setSendEnabled();
    if (els.messageInput.value.trim().length > 0) {
      scheduleTyping();
    }
  });

  els.messageInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      void sendMessage();
    }
  });

  els.messageInput.addEventListener("blur", () => {
    void invokeStoppedTyping();
  });

  async function sendMessage() {
    const text = els.messageInput.value.trim();
    if (!text || state.sending) {
      return;
    }

    state.sending = true;
    setSendEnabled();
    showError(els.sendError, "");

    try {
      await state.connection.invoke("SendMessage", state.sessionId, text, state.credential);
      els.messageInput.value = "";
      void invokeStoppedTyping();
    } catch (error) {
      showError(els.sendError, hubErrorMessage(error));
    } finally {
      state.sending = false;
      setSendEnabled();
      els.messageInput.focus();
    }
  }

  setSendEnabled();
  void loadAgentMode();
})();
