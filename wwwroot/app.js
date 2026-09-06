"use strict";

(() => {
  const DEFAULT_IMAGE_PROMPT = "Create a new variation of this image, preserving its main subject and style.";
  const MAX_IMAGE_BYTES = 20 * 1024 * 1024;
  const ACTIVE_CHAT_KEY = "frame.activeChatId";
  const POLL_MS = 3000;
  const GPT_MODEL = "gpt-image-2";
  const MAI_MODEL = "mai-image-2.5-flash";
  // Local capabilities keep both choices visible when readiness is unavailable.
  // Readiness is always taken from the selected model, never another deployment.
  const MODEL_DEFAULTS = new Map([
    [GPT_MODEL, { id: GPT_MODEL, label: "GPT-image-2", supportsEditing: true,
      sizes: ["1024x1024", "1536x1024", "1024x1536", "1536x1536", "3840x2160"],
      editSizes: ["1024x1024", "1536x1024", "1024x1536", "1536x1536", "3840x2160"],
      qualities: ["low", "medium", "high"] }],
    [MAI_MODEL, { id: MAI_MODEL, label: "MAI-Image-2.5-Flash (Preview)", supportsEditing: true,
      sizes: ["1024x1024", "768x1024", "1024x768", "768x1365", "1365x768"],
      editSizes: ["auto"], qualities: ["default"] }]
  ]);
  const $ = (id) => document.getElementById(id);
  const mobile = matchMedia("(max-width: 800px)");
  const state = {
    user: null, authBusy: false, initializing: false,
    status: null, statusError: "", statusLoading: false,
    history: [], historyLoaded: false, activeId: null, tab: "chat",
    sessions: new Map(), deleted: new Set(), busy: "loading",
    composing: false, sidebarOpen: false, confirmResolve: null,
    historyRead: null, pollTimer: null, clock: null, stopped: false,
    reads: new Set()
  };

  function newDraft() {
    return { prompt: "", model: GPT_MODEL, quality: "medium", size: "1024x1024", useLatest: true,
      attachment: null, attachmentUrl: null, version: 0 };
  }

  function sessionFor(id) {
    if (!state.sessions.has(id)) {
      state.sessions.set(id, { id, chat: null, draft: newDraft(), request: null,
        serverPending: false, startedAt: null, indicator: "", error: "", notice: "",
        draftError: "", revision: 0, read: null, needsRead: false, missing: false });
    }
    return state.sessions.get(id);
  }

  const activeSession = () => sessionFor(state.activeId);
  const isActive = (session) => !state.stopped && activeSession() === session;
  const pendingChat = (chat) => Boolean(chat?.messages.some((message) => message.role === "assistant" && message.status === "pending"));
  const chatBusy = (session) => Boolean(session.request || session.serverPending);

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

  function modelLabel(id) {
    return MODEL_DEFAULTS.get(id)?.label || (text(id) && !/^gpt$/i.test(id.trim()) ? id : "Legacy / unknown model");
  }

  function modelStatus(id) {
    return state.status?.models.find((model) => model.id === id) || null;
  }

  function effectiveEdit(session) {
    return Boolean(session.draft.attachment || (session.draft.useLatest && session.chat?.messages.some(
      (message) => message.role === "assistant" && message.status === "completed" && message.imageUrl)));
  }

  function normalizeDraft(session) {
    const draft = session.draft;
    const model = modelStatus(draft.model) || MODEL_DEFAULTS.get(draft.model);
    if (!model) return;
    const sizes = effectiveEdit(session) ? model.editSizes : model.sizes;
    if (!sizes.includes(draft.size)) draft.size = sizes.includes("1024x1024") ? "1024x1024" : sizes[0];
    if (!model.qualities.includes(draft.quality)) draft.quality = model.qualities.includes("medium") ? "medium" : model.qualities[0];
    // Context-driven normalization is not a user edit: it must not prevent the
    // originating successful request from clearing its prompt and attachment.
  }

  function selectOptions(id, options, value) {
    const select = $(id);
    const signature = JSON.stringify(options);
    if (select.dataset.options !== signature) {
      select.replaceChildren(...options.map(([key, label]) => {
        const option = element("option", "", label);
        option.value = key;
        return option;
      }));
      select.dataset.options = signature;
    }
    select.value = value;
  }

  function renderModelControls(session) {
    normalizeDraft(session);
    const draft = session.draft;
    const model = modelStatus(draft.model) || MODEL_DEFAULTS.get(draft.model);
    selectOptions("model", [...MODEL_DEFAULTS.values()].map((item) => [item.id, text(modelStatus(item.id)?.label, item.label)]), draft.model);
    if (!model) return;
    const sizes = effectiveEdit(session) ? model.editSizes : model.sizes;
    selectOptions("size", sizes.map((size) => [size, size === "auto" ? "Model determined" : size.replaceAll("x", " × ")]), draft.size);
    selectOptions("quality", model.qualities.map((quality) => [quality, quality === "default" ? "Model default" : quality[0].toUpperCase() + quality.slice(1)]), draft.quality);
    $("model-help").hidden = draft.model !== MAI_MODEL;
  }

  function publicStatus(status) {
    if (!status || typeof status.configured !== "boolean" || (status.models !== undefined && !Array.isArray(status.models))) {
      throw new Error("The backend returned an invalid readiness response.");
    }
    // An older backend reports only GPT readiness. MAI remains selectable but
    // unavailable; never inherit the legacy top-level configuration for it.
    const models = status.models === undefined ? [{ ...MODEL_DEFAULTS.get(GPT_MODEL), ...status, id: GPT_MODEL }] : status.models;
    const ids = new Set();
    const safeModels = models.map((model) => {
      const defaults = MODEL_DEFAULTS.get(model?.id);
      if (!defaults || ids.has(model.id) || typeof model.configured !== "boolean" || typeof model.supportsEditing !== "boolean" ||
          ["sizes", "editSizes", "qualities"].some((key) => !Array.isArray(model[key]) || !model[key].length ||
            model[key].some((value) => !defaults[key].includes(value)))) {
        throw new Error("The backend returned invalid model capabilities. Check readiness again before generating.");
      }
      ids.add(model.id);
      // Keep only public fields. Do not retain or render endpoints or secrets.
      return { id: model.id, label: text(model.label, defaults.label), configured: model.configured,
        deployment: text(model.deployment), authMode: text(model.authMode), message: text(model.message),
        supportsEditing: model.supportsEditing, sizes: [...model.sizes], editSizes: [...model.editSizes], qualities: [...model.qualities] };
    });
    return { models: safeModels };
  }

  function errorText(error) {
    return error instanceof Error ? error.message : "Something went wrong. Please try again.";
  }

  function showError(message, session = activeSession()) {
    if (state.stopped) return;
    session.error = message;
    if (isActive(session)) renderFeedback();
  }

  function showNotice(message, session = activeSession()) {
    if (state.stopped) return;
    session.notice = message;
    if (isActive(session)) renderFeedback();
  }

  function draftError(message, session = activeSession()) {
    if (state.stopped) return;
    session.draftError = message;
    if (isActive(session)) renderFeedback();
  }

  function renderFeedback() {
    if (state.stopped) return;
    const session = activeSession();
    $("app-error-text").textContent = session.error;
    $("app-error").hidden = !session.error;
    $("app-notice").textContent = session.notice;
    $("app-notice").hidden = !session.notice;
    $("composer-error").textContent = session.draftError;
    $("composer-error").hidden = !session.draftError;
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
    if (state.stopped) throw new Error("This page has been closed.");
    // Check the cookie's current account before any chat mutation, not just on
    // the next poll, to detect another tab's sign-in before sending this draft.
    if (options.method && options.method !== "GET" && !path.startsWith("/api/auth/") && !await checkAccount()) {
      throw new Error("This session has ended.");
    }
    const headers = new Headers(options.headers);
    headers.set("Accept", "application/json");
    if (options.method && options.method !== "GET") headers.set("X-ImageChat-Request", "1");
    let response;
    const readOnly = !options.method || options.method === "GET";
    const controller = new AbortController();
    state.reads.add(controller);
    try {
      response = await fetch(path, {
        ...options, headers, credentials: "same-origin", cache: "no-store",
        signal: readOnly || path.startsWith("/api/auth/") ? AbortSignal.any([controller.signal, AbortSignal.timeout(30000)]) : controller.signal
      });
    } catch (error) {
      if (controller) state.reads.delete(controller);
      throw new Error(error.name === "TimeoutError"
        ? "The backend took too long to respond. Please check its status and try again."
        : "The backend could not be reached. Check that it is running and that your network is available.");
    }
    if (state.stopped) { state.reads.delete(controller); throw new Error("This session has ended."); }
    // Handle expiry before reading JSON, including empty/proxy-generated 401s.
    if (response.status === 401) {
      state.reads.delete(controller);
      endSession();
      const error = new Error("Your session ended. Sign in again to continue.");
      error.status = 401;
      throw error;
    }
    if (response.status === 204 && response.ok) {
      if (controller) state.reads.delete(controller);
      return null;
    }
    let payload;
    try {
      payload = await response.json();
    } catch {
      const error = new Error(`The backend returned an unreadable response (HTTP ${response.status}).`);
      error.status = response.status;
      throw error;
    } finally {
      if (controller) state.reads.delete(controller);
    }
    if (state.stopped) throw new Error("This session has ended.");
    if (!response.ok) {
      const error = new Error(text(payload?.error, `The request failed (HTTP ${response.status}).`));
      error.status = response.status;
      error.serverError = typeof payload?.error === "string";
      throw error;
    }
    return payload;
  }

  function unsavedWork() {
    captureDraft();
    return [...state.sessions.values()].some((session) => hasDraft(session) || chatBusy(session));
  }

  function clearPasswords() {
    for (const id of ["password-current", "password-new", "password-confirm"]) $(id).value = "";
  }

  function clearAccountState() {
    state.stopped = true;
    window.clearTimeout(state.pollTimer);
    window.clearInterval(state.clock);
    for (const controller of state.reads) controller.abort();
    state.reads.clear();
    for (const session of state.sessions.values()) {
      releaseAttachment(session);
      session.draft.prompt = "";
      session.chat = null;
      session.request = null;
    }
    state.sessions.clear();
    state.deleted.clear();
    state.history = [];
    state.activeId = null;
    state.user = null;
    state.status = null;
    state.historyLoaded = false;
    clearPasswords();
    $("password-username").value = "";
    $("prompt").value = "";
    $("image-input").value = "";
    $("history-search").value = "";
    $("attachment-image").removeAttribute("src");
    $("attachment-name").textContent = "";
    $("attachment-size").textContent = "";
    $("attachment-preview").hidden = true;
    $("large-image").removeAttribute("src");
    $("preview-download").removeAttribute("href");
    $("chat-list").replaceChildren();
    $("messages").replaceChildren();
    $("chat-title").textContent = "Session ended";
    $("conversation-title").textContent = "Image conversation";
    $("account-name").textContent = "Signed out";
    $("settings-account").textContent = "Signed out";
    $("history-announcement").textContent = "";
    $("confirm-description").textContent = "";
    $("admin-link").hidden = true;
    for (const id of ["app-error", "app-notice", "composer-error", "generation-progress", "recovery-notice"]) $(id).hidden = true;
    for (const id of ["app-error-text", "app-notice", "composer-error", "recovery-text"]) $(id).textContent = "";
    for (const control of document.querySelectorAll("button, input, textarea, select")) control.disabled = true;
    for (const dialog of document.querySelectorAll("dialog[open]")) dialog.close();
    const resolve = state.confirmResolve;
    state.confirmResolve = null;
    if (resolve) resolve(false);
    document.title = "Frame / signed out";
  }

  function endSession(reason = "expired") {
    if (state.stopped) return;
    const hasWork = unsavedWork();
    clearAccountState();
    storeChatId(null);
    // The login page explains draft loss; never keep another account's data alive.
    const notice = hasWork && ["expired", "account-changed"].includes(reason) ? "expired-drafts" : reason;
    location.replace(`/login?reason=${encodeURIComponent(notice)}`);
  }

  async function checkAccount() {
    const session = await api("/api/auth/session");
    if (session?.authenticated === false) { endSession(); return false; }
    if (session?.authenticated !== true || !session.user?.id || typeof session.user.username !== "string" ||
        !["admin", "user"].includes(session.user.role) || session.user.enabled !== true) {
      throw new Error("The backend returned an invalid sign-in response.");
    }
    if (state.user && (state.user.id !== session.user.id || state.user.role !== session.user.role)) {
      endSession("account-changed");
      return false;
    }
    state.user = session.user;
    $("account-name").textContent = `Signed in as ${state.user.username}`;
    $("settings-account").textContent = `${state.user.username} · ${state.user.role === "admin" ? "administrator" : "user"}`;
    $("password-username").value = state.user.username;
    $("admin-link").hidden = state.user.role !== "admin";
    updateControls();
    return true;
  }

  async function logout() {
    if (!state.user || state.authBusy || state.stopped || state.confirmResolve) return;
    const hasWork = unsavedWork();
    state.authBusy = true;
    updateControls();
    try {
      const confirmed = await confirmAction({ title: "Sign out of Frame?", description: `${hasWork ? "You have in-memory drafts, attachments or running chats. " : ""}Signing out clears unsent drafts and attachments in this tab and revokes all sessions for this account, including other browsers. Saved chats remain; running image requests may still finish on the server.`, label: "Sign out", cancelLabel: "Stay in studio" });
      if (!confirmed || state.stopped) return;
      if (!await checkAccount()) return;
      await api("/api/auth/logout", { method: "POST" });
      endSession("signed-out");
    } catch (error) { showError(`Sign-out could not be confirmed. ${errorText(error)} Your drafts are kept until the session ends.`); }
    finally { state.authBusy = false; updateControls(); }
  }

  async function changePassword(event) {
    event.preventDefault();
    if (!state.user || state.authBusy || state.stopped || state.confirmResolve || !$("password-form").reportValidity()) return;
    $("password-error").hidden = true;
    if ($("password-new").value !== $("password-confirm").value) {
      $("password-error").textContent = "The new passwords do not match. Re-enter them to continue.";
      $("password-error").hidden = false;
      clearPasswords();
      $("password-current").focus();
      return;
    }
    const payload = { currentPassword: $("password-current").value, newPassword: $("password-new").value };
    clearPasswords();
    state.authBusy = true;
    updateControls();
    try {
      const hasWork = unsavedWork();
      const confirmed = await confirmAction({ title: "Change password and sign out?", description: `${hasWork ? "You have drafts, attachments or running chats. " : ""}Saving your new password ends your session and clears in-memory drafts and attachments. Saved chats remain; running image requests may still finish.`, label: "Change password", cancelLabel: "Keep current password" });
      if (!confirmed || state.stopped) { $("password-status").textContent = "Change cancelled. Re-enter passwords to try again."; return; }
      if (!await checkAccount()) return;
      $("password-status").textContent = "Changing password…";
      await api("/api/auth/password", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      endSession("password-changed");
    } catch (error) {
      if (!state.stopped) {
        $("password-error").textContent = `Password change could not be confirmed. ${errorText(error)} If the response was lost, try signing in with the new password before retrying.`;
        $("password-error").hidden = false;
        $("password-status").textContent = "Re-enter passwords to try again.";
      }
    } finally {
      payload.currentPassword = "";
      payload.newPassword = "";
      clearPasswords();
      state.authBusy = false;
      updateControls();
    }
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
    return Boolean(state.stopped || !state.user || state.authBusy || state.busy || chatBusy(activeSession()) || activeSession().missing);
  }

  function updateControls() {
    if (state.stopped) return;
    const session = activeSession();
    const request = session.request;
    const disabled = locked();
    renderModelControls(session);
    const selected = modelStatus(session.draft.model);
    const configured = selected?.configured === true;
    const editingUnsupported = effectiveEdit(session) && selected?.supportsEditing === false;
    const accountLocked = !state.user || state.authBusy;
    $("account-logout").disabled = accountLocked;
    $("password-open").disabled = accountLocked;
    $("password-fields").disabled = accountLocked;
    for (const button of document.querySelectorAll('[data-close="password-dialog"]')) button.disabled = state.authBusy;
    for (const id of ["prompt", "model", "quality", "size", "use-latest", "image-input", "attach-button", "attachment-remove"]) {
      $(id).disabled = disabled;
    }
    $("quality").disabled = disabled || session.draft.model === MAI_MODEL;
    $("size").disabled = disabled || (session.draft.model === MAI_MODEL && effectiveEdit(session));
    $("new-chat").disabled = Boolean(state.busy || accountLocked);
    $("refresh-history").disabled = Boolean(state.historyRead || accountLocked);
    for (const button of document.querySelectorAll(".chat-select")) button.disabled = Boolean(state.busy);
    for (const button of document.querySelectorAll(".chat-delete")) {
      button.disabled = Boolean(state.busy || chatBusy(sessionFor(button.dataset.chatId)));
    }
    for (const button of document.querySelectorAll(".suggestion")) button.disabled = disabled;
    $("brand-home").setAttribute("aria-disabled", String(Boolean(state.busy)));
    const hasInput = Boolean(session.draft.prompt.trim() || session.draft.attachment);
    $("send-button").disabled = disabled || !hasInput || !configured || editingUnsupported;
    $("send-button").title = !configured ? `Check ${modelLabel(session.draft.model)} setup in Settings before generating.`
      : editingUnsupported ? "This model does not support editing. Remove the attachment and turn off Use latest image."
      : disabled ? "This chat is busy. You can switch or start another chat." : `Generate one image with ${modelLabel(session.draft.model)}`;
    $("send-label").textContent = request?.phase === "unconfirmed" ? "Awaiting result" : chatBusy(session) ? "Creating…" : "Generate";
    $("check-result").disabled = Boolean(session.read);
    $("resume-draft").hidden = request?.phase !== "unconfirmed";
    $("resume-draft").disabled = Boolean(state.busy || session.read || session.serverPending || !request?.checked);
    $("image-only-help").hidden = !session.draft.attachment || Boolean(session.draft.prompt.trim());
    $("refresh-status").disabled = state.statusLoading || accountLocked;
    $("generation-progress").hidden = !session.serverPending && request?.phase !== "sending";
    $("recovery-notice").hidden = request?.phase !== "unconfirmed";
    $("recovery-text").textContent = request?.detail || "";
    $("navigation-progress").hidden = !state.busy;
    $("conversation").setAttribute("aria-busy", String(Boolean(state.busy === "loading" || chatBusy(session))));
    updateElapsed();
  }

  function setBusy(kind, description = "Loading…") {
    if (state.stopped) return;
    state.busy = kind;
    $("navigation-progress-text").textContent = description;
    updateControls();
  }

  function renderStatus() {
    if (state.stopped) return;
    const id = activeSession().draft.model;
    const selected = modelStatus(id);
    const label = text(selected?.label, modelLabel(id));
    const configured = selected?.configured === true;
    const unknown = !selected;
    const setupPath = id === MAI_MODEL ? "Foundry:Mai" : "Foundry";
    $("status-chip-text").textContent = state.statusLoading ? "Checking setup" : configured ? "Configured · not tested" : unknown ? "Setup unavailable" : "Setup needed";
    $("status-chip").title = `${label}: ${$("status-chip-text").textContent}`;
    $("model-readiness").textContent = $("status-chip-text").textContent;
    $("model-readiness").classList.toggle("not-ready", !configured);
    $("status-chip").classList.toggle("not-ready", !configured && !state.statusLoading);
    $("config-warning").hidden = configured || (state.statusLoading && !state.statusError);
    $("config-title").textContent = `${label}: ${unknown ? "readiness is unavailable" : "setup needed"}`;
    $("config-message").textContent = unknown
      ? `${text(state.statusError, "The backend has not reported readiness for this model.")} Check ${setupPath} on the server, then check readiness again in Settings.`
      : `${text(selected.message, "Configure this model on the backend, then check readiness again in Settings. Your draft can stay here.")} Setup: ${setupPath}. Other models have independent readiness.`;
    $("settings-model").textContent = `${label} · ${id}`;
    $("settings-readiness").textContent = state.statusLoading ? "Checking backend configuration…" : configured ? "Configured — connectivity has not been tested" : unknown ? "Unable to check readiness" : "Not configured — generation is unavailable";
    $("settings-readiness").classList.toggle("not-ready", !configured);
    $("settings-deployment").textContent = text(selected?.deployment, "Not reported");
    $("settings-auth").textContent = text(selected?.authMode, "Not reported");
    $("settings-message").textContent = state.statusError || text(selected?.message, "The backend did not provide an additional status message for this model.");
    $("settings-setup-help").textContent = `Configure ${label} under ${setupPath} on the server. The selected model’s readiness is configuration only, not a connectivity test. Requests never automatically switch to another model or deployment.`;
    updateControls();
  }

  async function loadStatus() {
    if (state.statusLoading || !state.user || state.stopped) return;
    state.statusLoading = true;
    renderStatus();
    try {
      const status = await api("/api/status");
      if (state.stopped) return;
      state.status = publicStatus(status);
      state.statusError = "";
    } catch (error) {
      if (state.stopped) return;
      state.status = null;
      state.statusError = errorText(error);
    } finally {
      state.statusLoading = false;
      renderStatus();
    }
  }

  function olderSummary(candidate, current) {
    return current && ((candidate.messageCount ?? 0) < (current.messageCount ?? 0) ||
      (Date.parse(candidate.updatedAt) || 0) < (Date.parse(current.updatedAt) || 0) ||
      (candidate.messageCount === current.messageCount && current.isGenerating === false && candidate.isGenerating === true));
  }

  function loadHistory() {
    if (!state.user || state.stopped) return Promise.resolve();
    // Manual refresh and the poller share one summary GET. Each chat has its own
    // revision fence, so a late list cannot roll back a create, send or completion.
    if (state.historyRead) return state.historyRead;
    const revisions = new Map([...state.sessions].map(([id, session]) => [id, session.revision]));
    state.historyRead = (async () => {
      const chats = await api("/api/chats");
      if (!Array.isArray(chats) || chats.some((chat) => !chat || typeof chat.id !== "string" || !chat.id)) {
        throw new Error("The backend returned an invalid history list.");
      }
      if (state.stopped) return;
      const merged = new Map();
      for (const chat of chats) {
        if (state.deleted.has(chat.id)) continue;
        const existing = state.sessions.get(chat.id);
        const previous = state.history.find((item) => item.id === chat.id);
        if ((existing && revisions.get(chat.id) !== existing.revision) || olderSummary(chat, previous)) {
          if (previous) merged.set(chat.id, previous);
          continue;
        }
        const session = sessionFor(chat.id);
        session.missing = false;
        if (JSON.stringify(previous) !== JSON.stringify(chat)) {
          session.revision += 1;
          session.needsRead = Boolean(session.chat || session.serverPending || chat.isGenerating);
        }
        if (chat.isGenerating) {
          session.serverPending = true;
          session.startedAt ||= Date.parse(chat.updatedAt) || Date.now();
        }
        // A false summary alone does not finish a turn: inspect its saved messages.
        merged.set(chat.id, chat);
      }
      for (const previous of state.history) {
        if (merged.has(previous.id) || state.deleted.has(previous.id)) continue;
        const session = sessionFor(previous.id);
        if (revisions.get(previous.id) !== session.revision || chatBusy(session)) {
          merged.set(previous.id, previous);
        } else {
          session.missing = true;
          session.error = "This chat was removed from saved history. Its in-memory draft is kept until this page closes; copy it into a new chat.";
          if (hasDraft(session) || isActive(session)) merged.set(previous.id, previous);
          if (isActive(session)) renderFeedback();
        }
      }
      state.history = [...merged.values()];
      state.historyLoaded = true;
      renderHistory();
      updateControls();
    })().finally(() => {
      state.historyRead = null;
      if (!state.stopped) updateControls();
    });
    updateControls();
    return state.historyRead;
  }

  function upsertHistory(chat) {
    const previous = state.history.find((item) => item.id === chat.id);
    const lastImage = [...chat.messages].reverse().find((message) => imageUrl(message.imageUrl));
    const summary = {
      id: chat.id, title: text(chat.title, "Untitled chat"), createdAt: chat.createdAt,
      updatedAt: chat.updatedAt, messageCount: chat.messages.length,
      thumbnailUrl: lastImage?.imageUrl || previous?.thumbnailUrl || null,
      isGenerating: pendingChat(chat)
    };
    state.history = [summary, ...state.history.filter((item) => item.id !== chat.id)];
    renderHistory();
  }

  function renderHistory() {
    if (state.stopped) return;
    const query = $("history-search").value.trim().toLocaleLowerCase();
    let chats = [...state.history].sort((a, b) => (Date.parse(b.updatedAt) || 0) - (Date.parse(a.updatedAt) || 0));
    chats = chats.filter((chat) => text(chat.title, "Untitled chat").toLocaleLowerCase().includes(query));
    // Never hide a running or unreviewed background result behind the recent limit.
    if (state.tab === "chat" && !query) chats = chats.filter((chat, index) => index < 8 || chatBusy(sessionFor(chat.id)) || sessionFor(chat.id).indicator);
    $("history-heading").textContent = state.tab === "chat" ? "Recent chats" : `All chats · ${state.history.length}`;
    $("history-panel").setAttribute("aria-labelledby", state.tab === "chat" ? "chat-tab" : "history-tab");
    const list = $("chat-list");
    const rows = new Map([...list.children].map((row) => [row.dataset.chatId, row]));
    let position = 0;
    for (const chat of chats) {
      const active = state.activeId === chat.id;
      const session = sessionFor(chat.id);
      const title = text(chat.title, "Untitled chat");
      let row = rows.get(chat.id);
      if (!row) {
        row = element("li", "chat-row");
        row.dataset.chatId = chat.id;
        const select = element("button", "chat-select");
        select.type = "button";
        const thumb = element("span", "chat-thumb");
        thumb.setAttribute("aria-hidden", "true");
        const summary = element("span", "chat-summary");
        const badge = element("span", "chat-badge");
        badge.dataset.chatId = chat.id;
        summary.append(element("strong"), element("small"), badge);
        select.append(thumb, summary);
        select.addEventListener("click", () => { void selectChat(chat.id); });
        const remove = element("button", "icon-button chat-delete");
        remove.type = "button";
        remove.dataset.chatId = chat.id;
        remove.append(icon("trash"));
        remove.addEventListener("click", () => { void deleteChat(state.history.find((item) => item.id === chat.id) || chat); });
        row.append(select, remove);
      }
      rows.delete(chat.id);
      row.classList.toggle("active", active);
      const select = row.querySelector(".chat-select");
      const remove = row.querySelector(".chat-delete");
      select.disabled = Boolean(state.busy);
      select.title = title;
      const status = badgeStatus(session);
      select.setAttribute("aria-label", `Open chat: ${title}${status ? `, ${status}` : ""}`);
      if (active) select.setAttribute("aria-current", "true");
      else select.removeAttribute("aria-current");
      const thumb = row.querySelector(".chat-thumb");
      const url = imageUrl(chat.thumbnailUrl);
      if (thumb.dataset.url !== (url || "")) {
        thumb.dataset.url = url || "";
        thumb.replaceChildren(icon("chat"));
        if (url) {
          const img = element("img");
          img.src = url;
          img.alt = "";
          img.loading = "lazy";
          img.referrerPolicy = "no-referrer";
          img.addEventListener("error", () => { thumb.replaceChildren(icon("chat")); }, { once: true });
          thumb.replaceChildren(img);
        }
      }
      const summary = row.querySelector(".chat-summary");
      const count = Number.isFinite(chat.messageCount) ? chat.messageCount : 0;
      summary.querySelector("strong").textContent = title;
      summary.querySelector("small").textContent = [formatDate(chat.updatedAt), `${count} ${count === 1 ? "message" : "messages"}`].filter(Boolean).join(" · ");
      const badge = summary.querySelector(".chat-badge");
      badge.hidden = !status;
      badge.className = `chat-badge ${chatBusy(session) ? "is-pending" : session.indicator === "failed" || session.missing ? "is-failed" : "is-completed"}`;
      badge.textContent = status;
      remove.disabled = Boolean(state.busy || chatBusy(session));
      remove.title = chatBusy(session) ? "This chat has an outstanding turn; other chats can be deleted." : "Delete chat";
      remove.setAttribute("aria-label", `Delete chat: ${title}${chatBusy(session) ? ", unavailable while this chat is busy" : ""}`);
      // Keep DOM nodes (and focus) stable during background progress updates.
      const next = list.children[position];
      if (next !== row) {
        if (row.parentNode === list && list.moveBefore) list.moveBefore(row, next || null);
        else if (!row.contains(document.activeElement)) list.insertBefore(row, next || null);
      }
      position += 1;
    }
    for (const row of rows.values()) row.remove();
    $("history-empty").hidden = chats.length > 0;
    $("history-empty").textContent = query ? "No chats match that title. Try another search." : state.historyLoaded ? "Your ideas will live here. Start a chat to make your first image." : state.busy === "loading" ? "Loading your conversations…" : "History is unavailable. Use Refresh to try again.";
  }

  function badgeStatus(session) {
    if (session.request?.phase === "unconfirmed") return session.serverPending ? `Creating · ${elapsed(session)} · check result` : "Check result";
    if (chatBusy(session)) return `Creating · ${elapsed(session)}`;
    if (session.missing) return "Removed · draft kept";
    if (session.indicator === "completed") return "Image ready";
    if (session.indicator === "failed") return "Generation failed";
    return hasDraft(session) ? "Draft · in this tab" : "";
  }

  function setIndicator(session, value) {
    if (session.indicator === value) return;
    session.indicator = value;
    if (!isActive(session) && !state.stopped && value) {
      const title = text(session.chat?.title, text(state.history.find((chat) => chat.id === session.id)?.title, "Untitled chat"));
      $("history-announcement").textContent = `${title}: ${value === "completed" ? "image ready" : value === "failed" ? "generation failed; draft kept" : "check the saved result; draft kept"}.`;
    }
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
    const meta = [message.role === "assistant" ? modelLabel(message.model) : "",
      message.size === "auto" ? "Model determined" : text(message.size).replaceAll("x", " × "),
      message.quality === "default" ? "Model default" : text(message.quality) ? `${message.quality} quality` : ""].filter(Boolean).join(" · ");
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
    if (state.stopped) return;
    const chat = activeSession().chat;
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
        header.append(avatar, element("span", "message-author", message.role === "user" ? "You" : "Frame"));
        const model = element("span", "message-model", modelLabel(message.model));
        if (text(message.deployment)) model.title = `Deployment: ${message.deployment}`;
        header.append(model, time);
        const content = element("div", "message-content");
        // Render the actual saved user text, including any backend fallback, never an optimistic guess.
        if (text(message.text)) content.append(element("p", "message-text", message.text));
        renderImage(message, content);
        if (message.status === "pending") {
          const pending = element("p", "message-text", "This image is still being generated. You can switch or start other chats while it runs. Saved results are checked automatically; do not resubmit this turn.");
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

  function applyChat(session, chat) {
    if (state.stopped || state.deleted.has(session.id)) return false;
    const previous = session.chat;
    // Chat messages are append-only. Never let an older GET (or an older POST
    // response followed by another tab's turn) remove messages or undo a terminal status.
    if (previous && ((Date.parse(chat.updatedAt) || 0) < (Date.parse(previous.updatedAt) || 0) ||
        previous.messages.some((old) => {
          const next = chat.messages.find((message) => message.id === old.id);
          return !next || (["completed", "failed"].includes(old.status) && next.status !== old.status);
        }))) return false;
    const changed = JSON.stringify(previous) !== JSON.stringify(chat);
    const wasPending = session.serverPending;
    session.chat = chat;
    // Initialize only on the first accepted chat read. Subsequent polls, even
    // another tab's newer model choice, must never overwrite an owned draft.
    if (!previous && !session.request && session.draft.version === 0 && !hasDraft(session)) {
      const latestModel = chat.messages.at(-1)?.model;
      session.draft.model = MODEL_DEFAULTS.has(latestModel) ? latestModel : GPT_MODEL;
    }
    normalizeDraft(session);
    session.missing = false;
    session.serverPending = pendingChat(chat);
    session.needsRead = false;
    if (changed) session.revision += 1;
    if (session.serverPending) {
      const pending = chat.messages.find((message) => message.role === "assistant" && message.status === "pending");
      session.startedAt ||= Date.parse(pending?.createdAt) || Date.now();
    }
    observeSaved(session, wasPending);
    upsertHistory(chat);
    if (isActive(session)) {
      if (!previous) renderStatus();
      if (changed) renderChat();
      renderFeedback();
      updateControls();
    }
    return true;
  }

  function readChat(session) {
    if (session.read) return session.read;
    const revision = session.revision;
    session.read = (async () => {
      const chat = validateChat(await api(chatPath(session.id)), session.id);
      if (state.stopped || state.deleted.has(session.id) || revision !== session.revision) return null;
      const summary = state.history.find((item) => item.id === session.id);
      if (olderSummary({ messageCount: chat.messages.length, updatedAt: chat.updatedAt }, summary)) return null;
      return applyChat(session, chat) ? chat : null;
    })().catch((error) => {
      if (error.status === 404 && revision === session.revision && !state.stopped && !state.deleted.has(session.id)) {
        session.missing = true;
        session.serverPending = false;
        showError("This saved chat is unavailable. Any in-memory draft has been kept; copy it before leaving this page.", session);
      }
      throw error;
    }).finally(() => {
      session.read = null;
      if (isActive(session)) updateControls();
    });
    if (isActive(session)) updateControls();
    return session.read;
  }

  function captureDraft(session = activeSession()) {
    if (!isActive(session)) return;
    const values = { prompt: $("prompt").value, model: $("model").value, quality: $("quality").value,
      size: $("size").value, useLatest: $("use-latest").checked };
    if (Object.entries(values).some(([key, value]) => session.draft[key] !== value)) {
      Object.assign(session.draft, values);
      session.draft.version += 1;
    }
    normalizeDraft(session);
  }

  function renderDraft() {
    if (state.stopped) return;
    const draft = activeSession().draft;
    $("prompt").value = draft.prompt;
    $("prompt").style.height = "";
    $("use-latest").checked = draft.useLatest;
    $("image-input").value = "";
    $("attachment-preview").hidden = !draft.attachment;
    if (draft.attachment) {
      $("attachment-image").src = draft.attachmentUrl;
      $("attachment-name").textContent = draft.attachment.name;
      $("attachment-size").textContent = `${(draft.attachment.size / (1024 * 1024)).toFixed(2)} MB · ${draft.attachment.type === "image/png" ? "PNG" : "JPEG"}`;
    } else $("attachment-image").removeAttribute("src");
    renderFeedback();
    renderStatus();
  }

  function activateSession(session) {
    state.activeId = session.id;
    storeChatId(session.id);
    if (!chatBusy(session)) session.indicator = "";
    state.composing = false;
    renderChat();
    renderDraft();
    renderHistory();
  }

  function releaseAttachment(session) {
    if (session.draft.attachmentUrl) URL.revokeObjectURL(session.draft.attachmentUrl);
    session.draft.attachment = null;
    session.draft.attachmentUrl = null;
  }

  function clearSubmittedDraft(session, context) {
    if (context.draftCleared || session.draft !== context.draft || session.draft.version !== context.draftVersion) return;
    context.draftCleared = true;
    releaseAttachment(session);
    session.draft.prompt = "";
    session.draft.version += 1;
    session.draftError = "";
    if (isActive(session)) renderDraft();
  }

  function hasDraft(session = activeSession()) {
    const draft = session.draft;
    return Boolean(draft.prompt.trim() || draft.attachment || draft.model !== GPT_MODEL || draft.quality !== "medium" || draft.size !== "1024x1024" || !draft.useLatest);
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

  function focusConversation() {
    setSidebar(false, false);
    (activeSession().chat ? $("conversation-title") : $("welcome-title")).focus({ preventScroll: true });
    window.scrollTo({ top: 0, behavior: "instant" });
  }

  async function selectChat(id) {
    if (state.busy || !state.user || state.authBusy || state.stopped) return;
    if (state.activeId === id) { setSidebar(false); return; }
    captureDraft();
    const origin = activeSession();
    const session = sessionFor(id);
    setBusy("loading", "Opening saved conversation…");
    try {
      // Cached chats can be opened immediately, including their in-memory drafts.
      // GET-only polling refreshes them without ever changing the selection.
      if (!session.chat && !session.missing) await readChat(session);
      if (state.stopped) return;
      if (!session.chat && !session.missing) throw new Error("The conversation changed while loading. Please open it again.");
      activateSession(session);
      focusConversation();
    } catch (error) {
      showError(`Could not open this chat. ${errorText(error)}`, origin);
    } finally { setBusy(null); }
  }

  async function createChat() {
    return validateChat(await api("/api/chats", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" }));
  }

  async function newChat() {
    if (state.busy || !state.user || state.authBusy || state.stopped) return;
    captureDraft();
    const origin = activeSession();
    setBusy("creating", "Creating a new conversation…");
    try {
      const chat = await createChat();
      if (state.stopped) return;
      const session = sessionFor(chat.id);
      applyChat(session, chat);
      activateSession(session);
      focusConversation();
    } catch (error) {
      showError(`Could not create a chat. ${errorText(error)} Your draft has been kept.`, origin);
    } finally { setBusy(null); }
  }

  async function showWelcome() {
    if (state.busy || !state.user || state.authBusy || state.stopped) return;
    captureDraft();
    activateSession(sessionFor(null));
    focusConversation();
  }

  async function deleteChat(chat) {
    const session = sessionFor(chat.id);
    if (state.busy || !state.user || state.authBusy || state.stopped || chatBusy(session) || state.confirmResolve) return;
    captureDraft();
    const confirmed = await confirmAction({ title: "Delete this chat?", description: `“${text(chat.title, "Untitled chat") }” and its saved conversation will be deleted from your account’s history. This cannot be undone. Historical administrator telemetry is retained.${hasDraft(session) ? " Its in-memory draft and attachment will also be cleared." : ""}`, label: "Delete chat", cancelLabel: "Keep chat" });
    if (!confirmed || state.busy || state.stopped || chatBusy(session)) return;
    const origin = activeSession();
    setBusy("deleting", "Deleting conversation…");
    session.revision += 1;
    try {
      try { await api(chatPath(chat.id), { method: "DELETE" }); }
      catch (error) { if (error.status !== 404) throw error; }
      if (state.stopped) return;
      state.deleted.add(chat.id);
      state.history = state.history.filter((item) => item.id !== chat.id);
      releaseAttachment(session);
      state.sessions.delete(chat.id);
      if (state.activeId === chat.id && !state.stopped) {
        activateSession(sessionFor(null));
        focusConversation();
      }
      renderHistory();
      showNotice("Chat deleted from local history.");
    } catch (error) {
      if (error.status === 409) { session.serverPending = true; session.needsRead = true; }
      showError(`Could not confirm deletion. ${errorText(error)} Refresh history to check whether the chat was removed.`, origin);
    } finally {
      session.revision += 1;
      setBusy(null);
      renderHistory();
    }
  }

  async function refreshHistory() {
    const origin = activeSession();
    try { await loadHistory(); }
    catch (error) { showError(`Could not load history. ${errorText(error)}`, origin); }
  }

  async function addAttachment(files) {
    if (locked()) { draftError("Wait for the current operation before changing the attachment."); return; }
    if (files.length !== 1) { draftError("Attach exactly one PNG or JPEG image. Multiple files are not supported."); return; }
    const file = files[0];
    if (!["image/png", "image/jpeg"].includes(file.type)) { draftError("Choose a PNG or JPEG image. Other file types are not supported."); return; }
    if (file.size === 0 || file.size > MAX_IMAGE_BYTES) { draftError("Choose a non-empty image no larger than 20 MB."); return; }
    const session = activeSession();
    captureDraft(session);
    setBusy("attachment", "Checking the attached image…");
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
      let decodeTimer;
      try {
        // A corrupt/very large image or a suspended renderer must not leave
        // every chat's navigation locked indefinitely.
        await Promise.race([probe.decode(), new Promise((_, reject) => {
          decodeTimer = window.setTimeout(() => reject(new Error("Image preview decoding timed out. Please try attaching it again.")), 10000);
        })]);
      } finally {
        window.clearTimeout(decodeTimer);
        probe.removeAttribute("src");
      }
      if (state.stopped) return;
      const replacing = Boolean(session.draft.attachment);
      releaseAttachment(session);
      session.draft.attachment = file;
      session.draft.attachmentUrl = url;
      session.draft.version += 1;
      url = null;
      if (isActive(session)) renderDraft();
      showNotice(replacing ? "Attachment replaced. One image is attached." : "Image attached. Add a prompt, or use the default variation request shown below.", session);
    } catch (error) {
      draftError(error instanceof DOMException ? "This image could not be decoded. Choose a valid PNG or JPEG." : errorText(error), session);
    } finally {
      if (url) URL.revokeObjectURL(url);
      if (isActive(session)) $("image-input").value = "";
      setBusy(null);
      renderHistory();
    }
  }

  function elapsed(session) {
    const seconds = Math.max(0, Math.floor((Date.now() - (session.request?.startedAt || session.startedAt || Date.now())) / 1000));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  }

  function updateElapsed() {
    if (state.stopped) return;
    $("generation-elapsed").textContent = elapsed(activeSession());
    for (const badge of document.querySelectorAll(".chat-badge")) {
      const session = state.sessions.get(badge.dataset.chatId);
      if (session && chatBusy(session)) badge.textContent = badgeStatus(session);
    }
  }

  function outcome(chat, context, fromResponse = false) {
    const newMessages = chat.messages.filter((message) => !context.beforeIds.has(message.id));
    let assistant;
    if (fromResponse) {
      assistant = [...newMessages].reverse().find((message) => message.role === "assistant");
      context.assistantId = assistant?.id;
    } else if (context.assistantId) {
      assistant = chat.messages.find((message) => message.id === context.assistantId && message.role === "assistant");
    } else {
      // A matching prompt is only a candidate, never proof of ownership across tabs.
      const userIndex = newMessages.findIndex((message) => message.role === "user" && message.text === context.prompt);
      if (userIndex >= 0) assistant = newMessages.slice(userIndex + 1).find((message) => message.role === "assistant");
    }
    if (assistant?.status === "completed") return "completed";
    if (assistant?.status === "failed") return "failed";
    return pendingChat(chat) ? "pending" : newMessages.length ? "other" : "absent";
  }

  function finishConfirmed(session, context, result) {
    if (state.stopped || session.request !== context) return;
    session.request = null;
    session.revision += 1;
    if (!session.serverPending) session.startedAt = null;
    setIndicator(session, result);
    if (result === "completed") {
      clearSubmittedDraft(session, context);
      showError("", session);
      showNotice("Your image is ready. Keep exploring, or download it to make it yours.", session);
    } else {
      draftError("Generation failed. This chat’s prompt, settings and attachment have been kept for retry. The saved failure is shown in the conversation.", session);
      showNotice("", session);
    }
    renderHistory();
    if (isActive(session)) updateControls();
  }

  function holdUnconfirmed(session, context, detail) {
    if (state.stopped || session.request !== context) return;
    context.phase = "unconfirmed";
    context.detail = `${detail} Your draft and attachment are kept in this tab. You can switch or start other chats. No generation request will be retried automatically.`;
    setIndicator(session, "unconfirmed");
    if (isActive(session)) updateControls();
    renderHistory();
  }

  function observeSaved(session, wasPending) {
    const context = session.request;
    if (context) {
      // Polls may finish before the original HTTP response. Wait for that response
      // before clearing/unlocking: it could still be an explicit 409 or 429.
      if (context.phase === "sending") return;
      context.checked = true;
      const result = outcome(session.chat, context);
      if (context.assistantId && ["completed", "failed"].includes(result)) {
        finishConfirmed(session, context, result);
      } else {
        const detail = result === "pending" ? "A saved turn is still generating. Results are checked automatically."
          : ["completed", "failed"].includes(result) ? `A matching ${result} turn is saved, but the interrupted response cannot be reliably attributed to this request. Review the conversation before choosing “Result reviewed”.`
          : result === "other" ? "Other saved activity is visible, but this request’s outcome is uncertain. Review the conversation before choosing “Result reviewed”."
          : "No new turn is saved yet. The original request may still arrive; an empty result does not prove it stopped. Check again before retrying.";
        holdUnconfirmed(session, context, detail);
      }
    } else if (wasPending && !session.serverPending) {
      const assistant = [...session.chat.messages].reverse().find((message) => message.role === "assistant");
      if (["completed", "failed"].includes(assistant?.status)) {
        if (!session.rejected) setIndicator(session, assistant.status);
        showNotice(session.rejected
          ? "Saved activity in this chat finished. Your rejected request was not retried; its draft is kept."
          : assistant.status === "completed" ? "A saved image is ready. Any unsent draft in this tab has been kept."
          : "A saved generation failed. Any unsent draft in this tab has been kept.", session);
      }
      session.startedAt = null;
    }
  }

  async function checkContext(session, context) {
    try {
      await readChat(session);
    } catch (error) {
      context.checked = false;
      holdUnconfirmed(session, context, `The saved chat could not be checked. ${errorText(error)}`);
      throw error;
    }
  }

  async function sendMessage() {
    if (locked() || state.composing) return;
    const session = activeSession();
    captureDraft(session);
    draftError("");
    const selected = modelStatus(session.draft.model);
    if (selected?.configured !== true) { draftError(`${modelLabel(session.draft.model)} is unavailable until its backend readiness is confirmed in Settings. No other model will be used.`); return; }
    if (effectiveEdit(session) && !selected.supportsEditing) { draftError("This model does not support editing. Remove the attachment and turn off Use latest image."); return; }
    const snapshot = { ...session.draft };
    if (!snapshot.prompt.trim() && !snapshot.attachment) { draftError("Describe an image or attach one PNG or JPEG to begin."); $("prompt").focus(); return; }
    const context = { phase: "sending", startedAt: Date.now(), beforeIds: new Set(session.chat?.messages.map((message) => message.id) || []),
      draft: session.draft, draftVersion: snapshot.version, model: snapshot.model, prompt: snapshot.prompt.trim() || DEFAULT_IMAGE_PROMPT,
      assistantId: null, checked: false, draftCleared: false };
    // Claim the draft synchronously, before even the first lazy-create await.
    // This prevents Enter/double-click from creating or submitting a second turn.
    session.request = context;
    session.rejected = false;
    session.revision += 1;
    session.indicator = "";
    setSidebar(false, false);
    showError("");
    showNotice("");
    updateControls();
    renderHistory();
    let creating = false;
    let submitted = false;
    try {
      if (!session.id) {
        creating = true;
        setBusy("creating", "Creating this draft’s conversation…");
        const chat = await createChat();
        if (state.stopped) return;
        // Move, rather than copy, the welcome draft and its File/URL ownership.
        state.sessions.delete(null);
        session.id = chat.id;
        state.sessions.set(chat.id, session);
        applyChat(session, chat);
        activateSession(session);
        context.beforeIds = new Set(chat.messages.map((message) => message.id));
        creating = false;
        setBusy(null);
      }
      const form = new FormData();
      form.append("prompt", context.prompt);
      form.append("model", snapshot.model);
      if (snapshot.attachment) form.append("image", snapshot.attachment, snapshot.attachment.name);
      if (snapshot.model !== MAI_MODEL) form.append("quality", snapshot.quality);
      form.append("size", snapshot.size);
      form.append("usePreviousImage", String(snapshot.useLatest));
      submitted = true;
      const chat = validateChat(await api(`${chatPath(session.id)}/messages`, { method: "POST", body: form }), session.id);
      if (state.stopped || session.request !== context) return;
      session.revision += 1;
      const result = outcome(chat, context, true);
      applyChat(session, chat);
      if (["completed", "failed"].includes(result)) finishConfirmed(session, context, result);
      else holdUnconfirmed(session, context, "The response did not contain a terminal result. Checking saved messages is safe; resending may create a duplicate.");
    } catch (error) {
      if (state.stopped || session.request !== context) return;
      session.revision += 1;
      if (!submitted) {
        session.request = null;
        draftError(`Could not create the chat. ${errorText(error)} Your draft and attachment are kept; no image-generation request was sent.`, session);
      } else if (error.status >= 400 && error.status < 500 && error.status !== 408) {
        // In particular, NEVER attribute a different tab's saved image to a
        // rejected local 409/429. Keep the draft even if a poll already saw it.
        session.request = null;
        session.rejected = true;
        session.needsRead = true;
        if (error.status === 409) { session.serverPending = true; session.startedAt ||= Date.now(); }
        setIndicator(session, "failed");
        draftError(`${errorText(error)} This request was rejected. Your prompt, settings and attachment are kept; it was not retried.`, session);
      } else {
        holdUnconfirmed(session, context, `The generation response could not be confirmed. ${errorText(error)}`);
      }
    } finally {
      if (creating) setBusy(null);
      renderHistory();
      if (isActive(session)) updateControls();
    }
  }

  async function checkSavedResult() {
    const session = activeSession();
    const context = session.request;
    if (!context || context.phase !== "unconfirmed") return;
    try { await checkContext(session, context); }
    catch { /* The per-chat recovery message contains the GET failure. */ }
  }

  async function resumeDraft() {
    const session = activeSession();
    const context = session.request;
    if (state.busy || state.confirmResolve || !context?.checked || context.phase !== "unconfirmed" || session.serverPending || session.read) return;
    const confirmed = await confirmAction({ title: "Resume this draft?", description: "The original response was not confirmed. Even if no pending turn is visible, it may still arrive. Review the saved images first: sending again could generate and charge for a duplicate. This only unlocks the draft; it does not send or clear anything.", label: "Result reviewed · keep draft", cancelLabel: "Keep checking" });
    if (!confirmed || state.busy || session.request !== context || session.serverPending || session.read || !context.checked) return;
    session.request = null;
    session.revision += 1;
    session.indicator = "";
    draftError("The unconfirmed result was marked reviewed. Your draft is kept. Sending it again is a new, potentially billable request.", session);
    renderHistory();
    if (isActive(session)) updateControls();
  }

  async function poll() {
    if (state.stopped || !state.user) return;
    let failed = false;
    // Detect a different account signing in in another tab before reading chats.
    try { if (!await checkAccount()) return; }
    catch {
      if (!state.stopped) {
        $("sync-status").textContent = "Session check unavailable. Saved-result checks will resume when it succeeds.";
        state.pollTimer = window.setTimeout(() => { void poll(); }, POLL_MS);
      }
      return;
    }
    try { await loadHistory(); }
    catch { failed = true; }
    const sessions = [...state.sessions.values()].filter((session) => session.id &&
      (session.request || session.serverPending || session.needsRead || isActive(session)));
    sessions.sort((a, b) => Number(isActive(b)) - Number(isActive(a)));
    for (const session of sessions) {
      if (state.stopped) return;
      if (state.deleted.has(session.id)) continue;
      try {
        if (session.request?.phase === "unconfirmed") await checkContext(session, session.request);
        else await readChat(session);
      } catch { failed = true; }
    }
    if (state.stopped) return;
    const message = failed ? "Live updates unavailable. Retrying saved-result checks automatically; you can still switch chats." : "Live updates · checks every few seconds";
    if ($("sync-status").textContent !== message) $("sync-status").textContent = message;
    // Schedule only after the entire GET-only cycle finishes: no overlapping polls.
    state.pollTimer = window.setTimeout(() => { void poll(); }, POLL_MS);
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
  $("auth-retry").addEventListener("click", () => { void initialize(); });
  $("account-logout").addEventListener("click", () => { void logout(); });
  $("password-open").addEventListener("click", () => {
    if (!state.user || state.authBusy || state.stopped) return;
    clearPasswords();
    $("password-error").hidden = true;
    $("password-status").textContent = "";
    $("password-dialog").showModal();
    $("password-current").focus();
  });
  $("password-form").addEventListener("submit", (event) => { void changePassword(event); });
  $("password-dialog").addEventListener("close", clearPasswords);
  $("password-dialog").addEventListener("cancel", (event) => { if (state.authBusy) event.preventDefault(); });
  $("confirm-action").addEventListener("click", () => $("confirm-dialog").close("confirmed"));
  $("confirm-dialog").addEventListener("close", () => {
    const resolve = state.confirmResolve;
    state.confirmResolve = null;
    if (resolve) resolve($("confirm-dialog").returnValue === "confirmed");
  });
  for (const button of document.querySelectorAll("[data-close]")) {
    button.addEventListener("click", () => {
      if (button.dataset.close === "password-dialog" && state.authBusy) return;
      $(button.dataset.close).close();
    });
  }
  for (const dialog of document.querySelectorAll("dialog")) {
    // Also handle Escape explicitly for embedded browser hosts that suppress native dismissal.
    dialog.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        if (dialog.id === "password-dialog" && state.authBusy) return;
        dialog.close();
      }
    });
    dialog.addEventListener("click", (event) => {
      if (dialog.id === "password-dialog" && state.authBusy) return;
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
      captureDraft();
      draftError("");
      updateControls();
      renderHistory();
      $("prompt").focus();
    });
  }
  $("prompt").addEventListener("input", () => { captureDraft(); draftError(""); updateControls(); renderHistory(); });
  for (const id of ["model", "quality", "size", "use-latest"]) {
    $(id).addEventListener("change", () => { captureDraft(); renderStatus(); renderHistory(); });
  }
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
    const session = activeSession();
    releaseAttachment(session);
    session.draft.version += 1;
    renderDraft();
    draftError("");
    showNotice("Attachment removed.");
    renderHistory();
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
  $("resume-draft").addEventListener("click", () => { void resumeDraft(); });
  window.addEventListener("beforeunload", (event) => {
    if (state.stopped) return;
    captureDraft();
    if ([...state.sessions.values()].some((session) => session.request || hasDraft(session))) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
  window.addEventListener("pagehide", () => {
    clearAccountState();
  });
  window.addEventListener("pageshow", (event) => {
    // A restored bfcache page has already released its in-memory File URLs.
    if (event.persisted) location.reload();
  });

  async function initialize() {
    if (state.initializing || state.stopped) return;
    state.initializing = true;
    setSidebar(false, false);
    setBusy("loading", "Checking your account…");
    $("auth-warning").hidden = true;
    $("auth-retry").disabled = true;
    try {
      if (!await checkAccount()) return;
    } catch (error) {
      if (!state.stopped) {
        $("auth-message").textContent = `Sign-in could not be checked. ${errorText(error)} No studio data has been requested.`;
        $("auth-warning").hidden = false;
        $("account-name").textContent = "Sign-in check unavailable";
        $("settings-account").textContent = "Sign-in not confirmed";
        $("auth-retry").disabled = false;
        setBusy(null);
      }
      return;
    } finally { state.initializing = false; }
    if (state.stopped) return;
    setBusy("loading", "Opening your studio…");
    const activeId = storedChatId();
    const results = await Promise.allSettled([loadStatus(), loadHistory()]);
    if (state.stopped) return;
    if (results[1].status === "rejected") showError(`Could not load saved history. ${errorText(results[1].reason)}`);
    try {
      if (activeId && state.historyLoaded && state.history.some((chat) => chat.id === activeId)) {
        const session = sessionFor(activeId);
        await readChat(session);
        if (!state.stopped && session.chat) activateSession(session);
      } else if (state.historyLoaded) storeChatId(null);
    } catch (error) {
      if (error.status === 404) storeChatId(null);
      showError(`The previous chat could not be restored. ${errorText(error)} Select a chat in History to try again.`);
    } finally {
      setBusy(null);
      renderChat();
      renderDraft();
      renderHistory();
      if (!state.stopped) {
        state.clock = window.setInterval(updateElapsed, 1000);
        state.pollTimer = window.setTimeout(() => { void poll(); }, POLL_MS);
      }
    }
  }

  void initialize();
})();