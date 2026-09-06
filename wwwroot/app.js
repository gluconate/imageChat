"use strict";

(() => {
  const DEFAULT_IMAGE_PROMPT = "Create a new variation of this image, preserving its main subject and style.";
  const MAX_IMAGE_BYTES = 20 * 1024 * 1024;
  const ACTIVE_CHAT_KEY = "frame.activeChatId";
  const $ = (id) => document.getElementById(id);
  const mobile = matchMedia("(max-width: 800px)");
  const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
  const state = {
    status: null, statusError: "", statusLoading: false,
    history: [], historyLoaded: false, chat: null, tab: "chat",
    busy: "loading", attachment: null, attachmentUrl: null, attachmentLoading: false,
    unresolved: null, composing: false, timer: null, sidebarOpen: false,
    confirmResolve: null
  };

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "icon");
    svg.setAttribute("aria-hidden", "true");
    const use = document.createElementNS("http://www.w3.org/2000/svg", "use");
    use.setAttribute("href", `#i-${name}`);
    svg.append(use);
    return svg;
  }

  function text(value, fallback = "") {
    return typeof value === "string" && value.trim() ? value : fallback;
  }

  function errorText(error) {
    return error instanceof Error ? error.message : "Something went wrong. Please try again.";
  }

  function showError(message) {
    $("app-error-text").textContent = message;
    $("app-error").hidden = !message;
  }

  function showNotice(message) {
    $("app-notice").textContent = message;
    $("app-notice").hidden = !message;
  }

  function draftError(message) {
    $("composer-error").textContent = message;
    $("composer-error").hidden = !message;
  }

  function storedChatId() {
    try { return localStorage.getItem(ACTIVE_CHAT_KEY); }
    catch { return null; }
  }

  function storeChatId(id) {
    try {
      if (id) localStorage.setItem(ACTIVE_CHAT_KEY, id);
      else localStorage.removeItem(ACTIVE_CHAT_KEY);
    } catch {
      // Local storage may be unavailable. Server-side chat history still works.
    }
  }

  // POST generation deliberately has no client timeout: the backend may take five minutes.
  // Never automatically retry a mutating request, including when fetch loses its response.
  async function api(path, options = {}) {
    const headers = new Headers(options.headers);
    headers.set("Accept", "application/json");
    if (options.method && options.method !== "GET") headers.set("X-ImageChat-Request", "1");
    let response;
    try {
      const readOnly = !options.method || options.method === "GET";
      response = await fetch(path, {
        ...options, headers, credentials: "same-origin", cache: "no-store",
        signal: readOnly ? AbortSignal.timeout(30000) : undefined
      });
    } catch (error) {
      throw new Error(error.name === "TimeoutError"
        ? "The backend took too long to respond. Please check its status and try again."
        : "The backend could not be reached. Check that it is running and that your network is available.");
    }
    if (response.status === 204 && response.ok) return null;
    let payload;
    try {
      payload = await response.json();
    } catch {
      const error = new Error(`The backend returned an unreadable response (HTTP ${response.status}).`);
      error.status = response.status;
      throw error;
    }
    if (!response.ok) {
      const error = new Error(text(payload?.error, `The request failed (HTTP ${response.status}).`));
      error.status = response.status;
      error.serverError = typeof payload?.error === "string";
      throw error;
    }
    return payload;
  }

  function validateChat(chat, expectedId) {
    if (!chat || typeof chat.id !== "string" || !chat.id || !Array.isArray(chat.messages) ||
        (expectedId && chat.id !== expectedId) || chat.messages.some((message) =>
          !message || typeof message.id !== "string" || !["user", "assistant"].includes(message.role))) {
      throw new Error("The backend returned an invalid chat. Reload the saved result before sending again.");
    }
    return chat;
  }

  const chatPath = (id) => `/api/chats/${encodeURIComponent(id)}`;
  const getChat = async (id) => validateChat(await api(chatPath(id)), id);

  // Reject executable/data URLs even if they are returned by the backend.
  function imageUrl(value) {
    if (typeof value !== "string" || !value.trim()) return null;
    try {
      const url = new URL(value, location.href);
      return ["http:", "https:"].includes(url.protocol) && url.origin === location.origin && !url.username && !url.password ? url.href : null;
    } catch { return null; }
  }

  function downloadUrl(url) {
    // Equivalent to imageUrl + '?download=true' for ordinary image paths;
    // preserve any existing query parameters as well.
    const result = new URL(url);
    result.searchParams.set("download", "true");
    return result.href;
  }

  function formatDate(value, includeTime = false) {
    const date = new Date(value);
    if (!value || Number.isNaN(date.getTime())) return "";
    return new Intl.DateTimeFormat(undefined, {
      month: "short", day: "numeric", ...(includeTime ? { hour: "numeric", minute: "2-digit" } : {})
    }).format(date);
  }

  function locked() {
    return Boolean(state.busy || state.attachmentLoading || state.unresolved);
  }

  function updateControls() {
    const disabled = locked();
    for (const id of ["new-chat", "refresh-history", "prompt", "quality", "size", "use-latest", "image-input", "attach-button", "attachment-remove"]) {
      $(id).disabled = disabled;
    }
    for (const button of document.querySelectorAll(".chat-select, .chat-delete, .suggestion")) button.disabled = disabled;
    $("brand-home").setAttribute("aria-disabled", String(disabled));
    const hasInput = Boolean($("prompt").value.trim() || state.attachment);
    $("send-button").disabled = disabled || !hasInput || state.status?.configured !== true;
    $("send-button").title = state.status?.configured !== true ? "Configure the backend in Settings before generating." : disabled ? "Wait for the current operation to finish." : "Generate one image";
    $("send-label").textContent = state.busy === "generating" ? "Creating…" : state.unresolved ? "Awaiting result" : "Generate";
    $("check-result").disabled = Boolean(state.busy);
    $("image-only-help").hidden = !state.attachment || Boolean($("prompt").value.trim());
    $("refresh-status").disabled = state.statusLoading;
    $("generation-progress").hidden = state.busy !== "generating";
    $("recovery-notice").hidden = !state.unresolved;
    $("navigation-progress").hidden = !state.busy || state.busy === "generating";
    $("conversation").setAttribute("aria-busy", String(Boolean(state.busy)));
  }

  function setBusy(kind, description = "Loading…") {
    state.busy = kind;
    $("navigation-progress-text").textContent = description;
    updateControls();
  }

  function renderStatus() {
    const configured = state.status?.configured === true;
    const unknown = !state.status;
    $("status-chip-text").textContent = state.statusLoading ? "Checking setup" : configured ? "Configured · not tested" : unknown ? "Setup unavailable" : "Setup needed";
    $("status-chip").classList.toggle("not-ready", !configured && !state.statusLoading);
    $("config-warning").hidden = configured || (state.statusLoading && !state.statusError);
    $("config-title").textContent = unknown ? "Backend readiness is unavailable" : "Image generation isn’t configured yet";
    $("config-message").textContent = unknown
      ? text(state.statusError, "Check the backend setup, then check readiness again in Settings.")
      : text(state.status.message, "Configure Microsoft Foundry on the backend, then check readiness again in Settings. Your draft can stay here.");
    $("settings-readiness").textContent = state.statusLoading ? "Checking backend configuration…" : configured ? "Configured — connectivity has not been tested" : unknown ? "Unable to check readiness" : "Not configured — generation is unavailable";
    $("settings-readiness").classList.toggle("not-ready", !configured);
    $("settings-deployment").textContent = text(state.status?.deployment, "Not reported");
    $("settings-auth").textContent = text(state.status?.authMode, "Not reported");
    $("settings-message").textContent = state.statusError || text(state.status?.message, "The backend did not provide an additional status message.");
    updateControls();
  }

  async function loadStatus() {
    if (state.statusLoading) return;
    state.statusLoading = true;
    renderStatus();
    try {
      const status = await api("/api/status");
      if (!status || typeof status.configured !== "boolean") throw new Error("The backend returned an invalid readiness response.");
      state.status = status;
      state.statusError = "";
    } catch (error) {
      state.status = null;
      state.statusError = errorText(error);
    } finally {
      state.statusLoading = false;
      renderStatus();
    }
  }

  async function loadHistory() {
    const chats = await api("/api/chats");
    if (!Array.isArray(chats) || chats.some((chat) => !chat || typeof chat.id !== "string" || !chat.id)) {
      throw new Error("The backend returned an invalid history list.");
    }
    state.history = chats;
    state.historyLoaded = true;
    renderHistory();
  }

  function upsertHistory(chat) {
    const previous = state.history.find((item) => item.id === chat.id);
    const lastImage = [...chat.messages].reverse().find((message) => imageUrl(message.imageUrl));
    const summary = {
      id: chat.id, title: text(chat.title, "Untitled chat"), createdAt: chat.createdAt,
      updatedAt: chat.updatedAt, messageCount: chat.messages.length,
      thumbnailUrl: lastImage?.imageUrl || previous?.thumbnailUrl || null
    };
    state.history = [summary, ...state.history.filter((item) => item.id !== chat.id)];
    renderHistory();
  }

  function renderHistory() {
    const query = $("history-search").value.trim().toLocaleLowerCase();
    let chats = [...state.history].sort((a, b) => (Date.parse(b.updatedAt) || 0) - (Date.parse(a.updatedAt) || 0));
    chats = chats.filter((chat) => text(chat.title, "Untitled chat").toLocaleLowerCase().includes(query));
    if (state.tab === "chat" && !query) chats = chats.slice(0, 8);
    $("history-heading").textContent = state.tab === "chat" ? "Recent chats" : `All chats · ${state.history.length}`;
    $("history-panel").setAttribute("aria-labelledby", state.tab === "chat" ? "chat-tab" : "history-tab");
    const fragment = document.createDocumentFragment();
    for (const chat of chats) {
      const active = state.chat?.id === chat.id;
      const title = text(chat.title, "Untitled chat");
      const row = element("li", `chat-row${active ? " active" : ""}`);
      const select = element("button", "chat-select");
      select.type = "button";
      select.disabled = locked();
      select.title = title;
      select.setAttribute("aria-label", `Open chat: ${title}`);
      if (active) select.setAttribute("aria-current", "true");
      const thumb = element("span", "chat-thumb");
      thumb.setAttribute("aria-hidden", "true");
      const url = imageUrl(chat.thumbnailUrl);
      if (url) {
        const img = element("img", "chat-thumb");
        img.src = url;
        img.alt = "";
        img.loading = "lazy";
        img.referrerPolicy = "no-referrer";
        img.addEventListener("error", () => { thumb.replaceChildren(icon("chat")); }, { once: true });
        thumb.append(img);
      } else thumb.append(icon("chat"));
      const summary = element("span", "chat-summary");
      const count = Number.isFinite(chat.messageCount) ? chat.messageCount : 0;
      summary.append(element("strong", "", title), element("small", "", [formatDate(chat.updatedAt), `${count} ${count === 1 ? "message" : "messages"}`].filter(Boolean).join(" · ")));
      select.append(thumb, summary);
      select.addEventListener("click", () => { void selectChat(chat.id); });
      const remove = element("button", "icon-button chat-delete");
      remove.type = "button";
      remove.disabled = locked();
      remove.title = "Delete chat";
      remove.setAttribute("aria-label", `Delete chat: ${title}`);
      remove.append(icon("trash"));
      remove.addEventListener("click", () => { void deleteChat(chat); });
      row.append(select, remove);
      fragment.append(row);
    }
    $("chat-list").replaceChildren(fragment);
    $("history-empty").hidden = chats.length > 0;
    $("history-empty").textContent = query ? "No chats match that title. Try another search." : state.historyLoaded ? "Your ideas will live here. Start a chat to make your first image." : state.busy === "loading" ? "Loading your conversations…" : "History is unavailable. Use Refresh to try again.";
  }

  function setTab(tab, focus = false) {
    state.tab = tab;
    for (const name of ["chat", "history"]) {
      const button = $(`${name}-tab`);
      button.setAttribute("aria-selected", String(name === tab));
      button.tabIndex = name === tab ? 0 : -1;
    }
    renderHistory();
    if (focus) $(`${tab}-tab`).focus();
  }

  function renderImage(message, container) {
    if (!message.imageUrl) return;
    const url = imageUrl(message.imageUrl);
    if (!url) {
      const error = element("p", "image-load-error", "This image URL is not a supported HTTP or HTTPS address.");
      error.setAttribute("role", "alert");
      container.append(error);
      return;
    }
    const figure = element("figure", "message-image");
    const open = element("button", "image-open");
    open.type = "button";
    open.setAttribute("aria-label", message.role === "user" ? "Open attached image in large preview" : "Open generated image in large preview");
    const img = element("img");
    img.alt = message.role === "user" ? "Image attached to your prompt" : `Generated image${message.size ? `, ${text(message.size)}` : ""}`;
    img.src = url;
    img.loading = "lazy";
    img.decoding = "async";
    img.referrerPolicy = "no-referrer";
    const badge = element("span", "image-open-badge", "View image");
    badge.prepend(icon("expand"));
    open.append(img, badge);
    open.addEventListener("click", () => openPreview(url, img.alt));
    const caption = element("figcaption", "image-caption");
    const meta = [text(message.size).replaceAll("x", " × "), text(message.quality) ? `${message.quality} quality` : ""].filter(Boolean).join(" · ");
    const download = element("a", "download-link", "Download");
    download.href = downloadUrl(url);
    download.download = "";
    download.rel = "noopener noreferrer";
    download.prepend(icon("download"));
    caption.append(element("span", "", meta), download);
    figure.append(open, caption);
    img.addEventListener("error", () => {
      img.hidden = true;
      open.hidden = true;
      const error = element("p", "image-load-error", "The saved image could not be loaded. Try the download link or reopen this chat.");
      error.setAttribute("role", "alert");
      figure.prepend(error);
    }, { once: true });
    container.append(figure);
  }

  function renderChat() {
    const chat = state.chat;
    $("welcome").hidden = Boolean(chat);
    $("conversation").hidden = !chat;
    $("chat-title").textContent = chat ? text(chat.title, "Untitled chat") : "A fresh perspective";
    $("conversation-title").textContent = chat ? text(chat.title, "Image conversation") : "Image conversation";
    document.title = chat ? `${text(chat.title, "Untitled chat")} — Frame` : "Frame / image studio";
    const fragment = document.createDocumentFragment();
    if (chat) {
      for (const message of chat.messages) {
        const article = element("article", `message message-${message.role}`);
        article.setAttribute("aria-label", `${message.role === "user" ? "You" : "Frame"}${message.status === "failed" ? ", failed" : ""}`);
        const header = element("div", "message-header");
        const avatar = element("span", "message-avatar", message.role === "user" ? "Y" : "✳");
        avatar.setAttribute("aria-hidden", "true");
        const time = element("time", "message-time", formatDate(message.createdAt, true));
        const date = new Date(message.createdAt);
        if (message.createdAt && !Number.isNaN(date.getTime())) time.dateTime = date.toISOString();
        header.append(avatar, element("span", "message-author", message.role === "user" ? "You" : "Frame"), time);
        const content = element("div", "message-content");
        // Render the actual saved user text, including any backend fallback, never an optimistic guess.
        if (text(message.text)) content.append(element("p", "message-text", message.text));
        renderImage(message, content);
        if (message.status === "pending") {
          const pending = element("p", "message-text", "This image is still being generated. Reopen this chat shortly to see the saved result. Avoid resubmitting the same request.");
          pending.setAttribute("role", "status");
          content.append(pending);
        }
        if (message.status === "failed") {
          const failure = element("p", "message-failure", `Generation failed. ${text(message.error, "The backend could not complete this request.")}`);
          failure.setAttribute("role", "alert");
          content.append(failure);
        }
        article.append(header, content);
        fragment.append(article);
      }
    }
    $("messages").replaceChildren(fragment);
    $("empty-chat").hidden = !chat || chat.messages.length > 0;
    updateControls();
  }

  function acceptChat(chat) {
    state.chat = chat;
    storeChatId(chat.id);
    upsertHistory(chat);
    renderChat();
  }

  function releaseAttachment() {
    if (state.attachmentUrl) URL.revokeObjectURL(state.attachmentUrl);
    state.attachment = null;
    state.attachmentUrl = null;
    $("attachment-image").removeAttribute("src");
    $("attachment-preview").hidden = true;
    $("image-input").value = "";
    updateControls();
  }

  function clearDraft() {
    $("prompt").value = "";
    $("prompt").style.height = "";
    releaseAttachment();
    draftError("");
    updateControls();
  }

  function hasDraft() {
    return Boolean($("prompt").value.trim() || state.attachment);
  }

  function confirmAction({ title, description, label, cancelLabel = "Cancel" }) {
    $("confirm-title").textContent = title;
    $("confirm-description").textContent = description;
    $("confirm-action").textContent = label;
    $("confirm-dialog").querySelector(".secondary-button").textContent = cancelLabel;
    $("confirm-dialog").returnValue = "";
    $("confirm-dialog").showModal();
    return new Promise((resolve) => { state.confirmResolve = resolve; });
  }

  async function mayDiscardDraft() {
    if (!hasDraft()) return true;
    return confirmAction({ title: "Leave this draft behind?", description: "The unsent prompt and attachment will be cleared. Saved conversations will not be affected.", label: "Discard draft", cancelLabel: "Keep editing" });
  }

  function focusConversation() {
    setSidebar(false, false);
    (state.chat ? $("conversation-title") : $("welcome-title")).focus({ preventScroll: true });
    window.scrollTo({ top: 0, behavior: "instant" });
  }

  async function selectChat(id) {
    if (locked()) return;
    if (state.chat?.id === id) { setSidebar(false); return; }
    if (!(await mayDiscardDraft()) || locked()) return;
    setBusy("loading", "Opening saved conversation…");
    showError("");
    showNotice("");
    try {
      acceptChat(await getChat(id));
      clearDraft();
      focusConversation();
    } catch (error) {
      showError(`Could not open this chat. ${errorText(error)}`);
    } finally { setBusy(null); }
  }

  async function createChat() {
    return validateChat(await api("/api/chats", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }));
  }

  async function newChat() {
    if (locked() || !(await mayDiscardDraft()) || locked()) return;
    setBusy("creating", "Creating a new conversation…");
    showError("");
    showNotice("");
    try {
      acceptChat(await createChat());
      clearDraft();
      focusConversation();
    } catch (error) {
      showError(`Could not create a chat. ${errorText(error)} Your draft has been kept.`);
    } finally {
      setBusy(null);
      if (state.chat && !hasDraft()) $("prompt").focus({ preventScroll: true });
    }
  }

  async function showWelcome() {
    if (locked() || !(await mayDiscardDraft()) || locked()) return;
    state.chat = null;
    storeChatId(null);
    clearDraft();
    showError("");
    showNotice("");
    renderChat();
    renderHistory();
    focusConversation();
  }

  async function deleteChat(chat) {
    if (locked()) return;
    const isActive = state.chat?.id === chat.id;
    const confirmed = await confirmAction({ title: "Delete this chat?", description: `“${text(chat.title, "Untitled chat") }” and its saved conversation will be deleted from local history. This cannot be undone.${isActive && hasDraft() ? " The unsent draft and attachment will also be cleared." : ""}`, label: "Delete chat", cancelLabel: "Keep chat" });
    if (!confirmed || locked()) return;
    setBusy("deleting", "Deleting conversation…");
    showError("");
    showNotice("");
    try {
      try { await api(chatPath(chat.id), { method: "DELETE" }); }
      catch (error) { if (error.status !== 404) throw error; }
      state.history = state.history.filter((item) => item.id !== chat.id);
      if (isActive) {
        state.chat = null;
        storeChatId(null);
        clearDraft();
        renderChat();
        focusConversation();
      }
      renderHistory();
      showNotice("Chat deleted from local history.");
    } catch (error) {
      showError(`Could not confirm deletion. ${errorText(error)} Refresh history to check whether the chat was removed.`);
    } finally {
      setBusy(null);
      if (!isActive) $("refresh-history").focus({ preventScroll: true });
    }
  }

  async function refreshHistory() {
    if (locked()) return;
    setBusy("loading", "Refreshing saved conversations…");
    showError("");
    try {
      await loadHistory();
      // A chat deleted by another client is not silently replaced, and a draft is never cleared.
      if (state.chat && !state.history.some((chat) => chat.id === state.chat.id)) {
        showError("This chat is no longer in saved history. Keep a copy of any draft, then start a new chat.");
      }
    } catch (error) { showError(`Could not load history. ${errorText(error)}`); }
    finally { setBusy(null); renderHistory(); }
  }

  async function addAttachment(files) {
    if (locked()) { draftError("Wait for the current operation before changing the attachment."); return; }
    if (files.length !== 1) { draftError("Attach exactly one PNG or JPEG image. Multiple files are not supported."); return; }
    const file = files[0];
    if (!["image/png", "image/jpeg"].includes(file.type)) { draftError("Choose a PNG or JPEG image. Other file types are not supported."); return; }
    if (file.size === 0 || file.size > MAX_IMAGE_BYTES) { draftError("Choose a non-empty image no larger than 20 MB."); return; }
    state.attachmentLoading = true;
    updateControls();
    draftError("");
    let url;
    try {
      const bytes = new Uint8Array(await file.slice(0, 8).arrayBuffer());
      const validPng = bytes.length === 8 && [137, 80, 78, 71, 13, 10, 26, 10].every((byte, index) => bytes[index] === byte);
      const validJpeg = bytes[0] === 255 && bytes[1] === 216 && bytes[2] === 255;
      if ((file.type === "image/png" && !validPng) || (file.type === "image/jpeg" && !validJpeg)) {
        throw new Error("The file contents do not match a PNG or JPEG image.");
      }
      url = URL.createObjectURL(file);
      const probe = new Image();
      probe.src = url;
      await probe.decode();
      const replacing = Boolean(state.attachment);
      releaseAttachment();
      state.attachment = file;
      state.attachmentUrl = url;
      url = null;
      $("attachment-image").src = state.attachmentUrl;
      $("attachment-name").textContent = file.name;
      $("attachment-size").textContent = `${(file.size / (1024 * 1024)).toFixed(2)} MB · ${file.type === "image/png" ? "PNG" : "JPEG"}`;
      $("attachment-preview").hidden = false;
      showNotice(replacing ? "Attachment replaced. One image is attached." : "Image attached. Add a prompt, or use the default variation request shown below.");
    } catch (error) {
      if (url) URL.revokeObjectURL(url);
      draftError(error instanceof DOMException ? "This image could not be decoded. Choose a valid PNG or JPEG." : errorText(error));
    } finally {
      state.attachmentLoading = false;
      $("image-input").value = "";
      updateControls();
    }
  }

  function startProgress() {
    const started = Date.now();
    $("generation-elapsed").textContent = "0:00";
    state.timer = window.setInterval(() => {
      const seconds = Math.floor((Date.now() - started) / 1000);
      $("generation-elapsed").textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
    }, 1000);
  }

  function stopProgress() {
    if (state.timer !== null) window.clearInterval(state.timer);
    state.timer = null;
  }

  function outcome(chat, context) {
    const newMessages = chat.messages.filter((message) => !context.beforeIds.has(message.id));
    const assistant = [...newMessages].reverse().find((message) => message.role === "assistant");
    if (assistant?.status === "completed") return "completed";
    if (newMessages.some((message) => message.status === "failed")) return "failed";
    return newMessages.length ? "pending" : "absent";
  }

  function finishConfirmed(chat, result, interrupted = false) {
    state.unresolved = null;
    acceptChat(chat);
    if (result === "completed") {
      clearDraft();
      showError("");
      showNotice(interrupted ? "The response was interrupted, but the completed turn was saved. Your draft was cleared to prevent a duplicate." : "Your image is ready. Keep exploring, or download it to make it yours.");
    } else {
      draftError("Generation failed. Your prompt and attachment have been kept for retry. The saved failure is shown in the conversation.");
      showNotice("");
    }
  }

  function holdUnconfirmed(context, detail) {
    state.unresolved = context;
    $("recovery-text").textContent = `${detail} Your draft and attachment are kept. Check the saved result before retrying to avoid a duplicate. Keep this tab open; navigation is paused.`;
    updateControls();
  }

  async function reconcileSubmission(context, requestError) {
    // Keep a definite backend rejection if the first recovery GET also fails.
    // A later successful read can then safely unlock the original draft for retry.
    if (requestError) context.requestError = requestError;
    const submissionError = context.requestError;
    try {
      const chat = await getChat(context.chatId);
      const result = outcome(chat, context);
      acceptChat(chat);
      if (result === "completed" || result === "failed") {
        finishConfirmed(chat, result, true);
        if (result === "failed" && submissionError) showError(errorText(submissionError));
      } else if (result === "absent" && submissionError?.serverError) {
        // An explicit backend error plus a successful read showing no new turn is retryable.
        state.unresolved = null;
        draftError(`${errorText(submissionError)} No new turn was saved. Your draft and attachment are kept for retry.`);
      } else {
        holdUnconfirmed(context, "No completed or failed turn is saved yet. The original request may still be running.");
      }
    } catch (error) {
      holdUnconfirmed(context, `The saved chat could not be checked. ${errorText(error)}`);
    }
  }

  async function sendMessage() {
    if (locked() || state.composing) return;
    draftError("");
    if (state.status?.configured !== true) { draftError("Image generation is unavailable until backend readiness is confirmed in Settings."); return; }
    if (!hasDraft()) { draftError("Describe an image or attach one PNG or JPEG to begin."); $("prompt").focus(); return; }
    const prompt = $("prompt").value.trim() || DEFAULT_IMAGE_PROMPT;
    const attachment = state.attachment;
    const quality = $("quality").value;
    const size = $("size").value;
    const usePreviousImage = String($("use-latest").checked);
    setSidebar(false, false);
    showError("");
    showNotice("");
    setBusy("generating");
    startProgress();
    let context = null;
    let submitted = false;
    try {
      // Welcome is local only. A backend chat is created on the first send or explicit New Chat.
      if (!state.chat) acceptChat(await createChat());
      context = { chatId: state.chat.id, beforeIds: new Set(state.chat.messages.map((message) => message.id)) };
      const form = new FormData();
      form.append("prompt", prompt);
      if (attachment) form.append("image", attachment, attachment.name);
      form.append("quality", quality);
      form.append("size", size);
      form.append("usePreviousImage", usePreviousImage);
      submitted = true;
      const chat = validateChat(await api(`${chatPath(context.chatId)}/messages`, { method: "POST", body: form }), context.chatId);
      const result = outcome(chat, context);
      if (result === "completed" || result === "failed") finishConfirmed(chat, result);
      else await reconcileSubmission(context);
    } catch (error) {
      if (submitted) await reconcileSubmission(context, error);
      else draftError(`Could not create the chat. ${errorText(error)} Your draft and attachment are kept; no image-generation request was sent.`);
    } finally {
      stopProgress();
      setBusy(null);
      renderHistory();
      if (!state.unresolved) {
        $("prompt").focus({ preventScroll: true });
        const latest = $("messages").lastElementChild;
        if (latest) latest.scrollIntoView({ behavior: reducedMotion.matches ? "instant" : "smooth", block: "start" });
      } else $("check-result").focus({ preventScroll: true });
    }
  }

  async function checkSavedResult() {
    if (state.busy || !state.unresolved) return;
    const context = state.unresolved;
    setBusy("loading", "Checking the saved generation result…");
    try { await reconcileSubmission(context); }
    finally {
      setBusy(null);
      if (!state.unresolved) $("prompt").focus({ preventScroll: true });
    }
  }

  function openPreview(url, alt) {
    $("large-image").referrerPolicy = "no-referrer";
    $("large-image").src = url;
    $("large-image").alt = alt;
    $("large-image").hidden = false;
    $("preview-error").hidden = true;
    $("preview-download").href = downloadUrl(url);
    $("preview-download").rel = "noopener noreferrer";
    $("image-dialog").showModal();
  }

  function setSidebar(open, restoreFocus = true) {
    state.sidebarOpen = mobile.matches && open;
    document.body.classList.toggle("sidebar-open", state.sidebarOpen);
    $("sidebar-backdrop").hidden = !state.sidebarOpen;
    $("sidebar-toggle").setAttribute("aria-expanded", String(state.sidebarOpen));
    $("sidebar").inert = mobile.matches && !state.sidebarOpen;
    $("main").inert = state.sidebarOpen;
    if (state.sidebarOpen) $("sidebar-close").focus();
    else if (restoreFocus && mobile.matches) $("sidebar-toggle").focus({ preventScroll: true });
  }

  $("sidebar-toggle").addEventListener("click", () => setSidebar(!state.sidebarOpen));
  $("sidebar-close").addEventListener("click", () => setSidebar(false));
  $("sidebar-backdrop").addEventListener("click", () => setSidebar(false));
  mobile.addEventListener("change", () => setSidebar(false, false));
  document.addEventListener("keydown", (event) => {
    if (!state.sidebarOpen || document.querySelector("dialog[open]")) return;
    if (event.key === "Escape") { event.preventDefault(); setSidebar(false); }
    if (event.key === "Tab") {
      const focusable = [...$("sidebar").querySelectorAll("a[href], button:not(:disabled), input:not(:disabled)")].filter((node) => node.tabIndex >= 0 && node.getClientRects().length);
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }
  });

  for (const name of ["chat", "history"]) {
    $(`${name}-tab`).addEventListener("click", () => setTab(name));
    $(`${name}-tab`).addEventListener("keydown", (event) => {
      if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
      event.preventDefault();
      setTab(event.key === "Home" ? "chat" : event.key === "End" ? "history" : name === "chat" ? "history" : "chat", true);
    });
  }
  $("history-search").addEventListener("input", renderHistory);
  $("new-chat").addEventListener("click", () => { void newChat(); });
  $("brand-home").addEventListener("click", (event) => { event.preventDefault(); void showWelcome(); });
  $("refresh-history").addEventListener("click", () => { void refreshHistory(); });
  $("dismiss-error").addEventListener("click", () => showError(""));
  $("settings-open").addEventListener("click", () => $("settings-dialog").showModal());
  $("config-settings").addEventListener("click", () => $("settings-dialog").showModal());
  $("refresh-status").addEventListener("click", () => { void loadStatus(); });
  $("confirm-action").addEventListener("click", () => $("confirm-dialog").close("confirmed"));
  $("confirm-dialog").addEventListener("close", () => {
    const resolve = state.confirmResolve;
    state.confirmResolve = null;
    if (resolve) resolve($("confirm-dialog").returnValue === "confirmed");
  });
  for (const button of document.querySelectorAll("[data-close]")) {
    button.addEventListener("click", () => $(button.dataset.close).close());
  }
  for (const dialog of document.querySelectorAll("dialog")) {
    // Also handle Escape explicitly for embedded browser hosts that suppress native dismissal.
    dialog.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        dialog.close();
      }
    });
    dialog.addEventListener("click", (event) => {
      const rect = dialog.getBoundingClientRect();
      if (event.target === dialog && (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom)) dialog.close();
    });
  }
  $("image-dialog").addEventListener("close", () => $("large-image").removeAttribute("src"));
  $("large-image").addEventListener("error", () => {
    if (!$("large-image").getAttribute("src")) return;
    $("large-image").hidden = true;
    $("preview-error").hidden = false;
  });

  for (const suggestion of document.querySelectorAll(".suggestion")) {
    suggestion.addEventListener("click", () => {
      if (locked()) return;
      $("prompt").value = suggestion.dataset.prompt;
      draftError("");
      updateControls();
      $("prompt").focus();
    });
  }
  $("prompt").addEventListener("input", () => { draftError(""); updateControls(); });
  $("prompt").addEventListener("compositionstart", () => { state.composing = true; });
  $("prompt").addEventListener("compositionend", () => { state.composing = false; });
  $("prompt").addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing && !state.composing && event.keyCode !== 229) {
      event.preventDefault();
      void sendMessage();
    }
  });
  $("composer").addEventListener("submit", (event) => { event.preventDefault(); if (!state.composing) void sendMessage(); });
  $("attach-button").addEventListener("click", () => { if (!locked()) $("image-input").click(); });
  $("image-input").addEventListener("change", (event) => {
    const files = [...event.target.files];
    if (files.length) void addAttachment(files);
    event.target.value = "";
  });
  $("attachment-remove").addEventListener("click", () => {
    if (locked()) return;
    releaseAttachment();
    draftError("");
    showNotice("Attachment removed.");
    $("attach-button").focus();
  });
  $("composer").addEventListener("paste", (event) => {
    const files = [...(event.clipboardData?.files || [])];
    if (files.length) { event.preventDefault(); void addAttachment(files); }
  });
  let dragDepth = 0;
  const hasFiles = (event) => [...(event.dataTransfer?.types || [])].includes("Files");
  document.addEventListener("dragenter", (event) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    dragDepth += 1;
    if (!locked() && !document.querySelector("dialog[open]") && !state.sidebarOpen) $("composer").classList.add("drag-over");
  });
  document.addEventListener("dragover", (event) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    if (event.dataTransfer) event.dataTransfer.dropEffect = locked() ? "none" : "copy";
  });
  document.addEventListener("dragleave", (event) => {
    if (!hasFiles(event)) return;
    dragDepth = Math.max(0, dragDepth - 1);
    if (!dragDepth) $("composer").classList.remove("drag-over");
  });
  document.addEventListener("drop", (event) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    dragDepth = 0;
    $("composer").classList.remove("drag-over");
    if (document.querySelector("dialog[open]") || state.sidebarOpen) return;
    void addAttachment([...(event.dataTransfer?.files || [])]);
  });
  $("check-result").addEventListener("click", () => { void checkSavedResult(); });
  window.addEventListener("beforeunload", (event) => {
    if (state.busy === "generating" || state.unresolved || hasDraft()) {
      event.preventDefault();
      event.returnValue = "";
    }
  });

  async function initialize() {
    setSidebar(false, false);
    setBusy("loading", "Opening your studio…");
    const activeId = storedChatId();
    const results = await Promise.allSettled([loadStatus(), loadHistory()]);
    if (results[1].status === "rejected") showError(`Could not load saved history. ${errorText(results[1].reason)}`);
    try {
      if (activeId && state.historyLoaded && state.history.some((chat) => chat.id === activeId)) {
        acceptChat(await getChat(activeId));
      } else if (state.historyLoaded) storeChatId(null);
    } catch (error) {
      if (error.status === 404) storeChatId(null);
      showError(`The previous chat could not be restored. ${errorText(error)} Select a chat in History to try again.`);
    } finally {
      setBusy(null);
      renderChat();
      renderHistory();
    }
  }

  void initialize();
})();