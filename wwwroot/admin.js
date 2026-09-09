"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  const metrics = ["requests", "generatedImages", "editedImages", "legacyImages", "totalImages", "failed", "rejected", "pending"];
  const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone || "browser local timezone";
  const dates = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "long" });
  const numbers = new Intl.NumberFormat();
  const state = { user: null, stopped: false, checking: false, telemetryLoading: false,
    usersLoading: false, mutation: false, editing: null, confirmResolve: null,
    query: new URLSearchParams(), page: 1, pageSize: 50, total: 0, reads: new Set() };

  function node(tag, value, className) {
    const result = document.createElement(tag);
    if (value !== undefined) result.textContent = String(value);
    if (className) result.className = className;
    return result;
  }

  function error(id, message = "") {
    if (state.stopped) return;
    $(id).textContent = message;
    $(id).hidden = !message;
  }

  function formatTime(value) {
    if (!value) return "—";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "Unavailable" : dates.format(date);
  }

  function modelLabel(id) {
    if (id === "gpt-image-2") return "GPT-image-2";
    if (id === "mai-image-2.5-flash") return "MAI-Image-2.5-Flash (Preview)";
    if (id === "flux.2-pro") return "FLUX.2-pro";
    if (id === "gpt-5.6-sol") return "GPT-5.6-sol";
    return typeof id === "string" && id.trim() && !/^gpt$/i.test(id.trim()) ? id : "Legacy / unknown model";
  }

  function clearSelection() {
    try { localStorage.removeItem("frame.activeChatId"); } catch { /* Storage is optional. */ }
  }

  function stop() {
    state.stopped = true;
    for (const controller of state.reads) controller.abort();
    state.reads.clear();
    state.user = null;
    state.editing = null;
    state.query = new URLSearchParams();
    $("user-form").reset();
    $("user-password").value = "";
    $("admin-account").textContent = "";
    $("admin-content").hidden = true;
    $("admin-logout").hidden = true;
    $("admin-retry").hidden = true;
    $("admin-error").hidden = true;
    $("user-rows").replaceChildren();
    $("telemetry-rows").replaceChildren();
    for (const key of metrics) $(`total-${key}`).textContent = "—";
    for (const dialog of document.querySelectorAll("dialog[open]")) dialog.close();
    const resolve = state.confirmResolve;
    state.confirmResolve = null;
    if (resolve) resolve(false);
  }

  function endSession(reason = "expired") {
    if (state.stopped) return;
    stop();
    clearSelection();
    location.replace(`/login?reason=${encodeURIComponent(reason)}`);
  }

  function forbidden() {
    if (state.stopped) return;
    stop();
    $("admin-status").textContent = "Administrator access is required.";
    $("forbidden").hidden = false;
    $("forbidden-title").tabIndex = -1;
    $("forbidden-title").focus();
  }

  async function api(path, options = {}) {
    if (state.stopped) throw new Error("This session has ended.");
    const controller = new AbortController();
    state.reads.add(controller);
    const headers = new Headers(options.headers);
    headers.set("Accept", "application/json");
    if (options.method && options.method !== "GET") headers.set("X-ImageChat-Request", "1");
    try {
      let response;
      try {
        response = await fetch(path, { ...options, headers, credentials: "same-origin", cache: "no-store",
          signal: AbortSignal.any([controller.signal, AbortSignal.timeout(30000)]) });
      } catch {
        throw new Error(options.method ? "The response could not be confirmed. Refresh accounts before retrying; the change may have completed." : "The server could not be reached. Check your connection and try again.");
      }
      if (state.stopped) throw new Error("This session has ended.");
      if (response.status === 401) { endSession(); throw new Error("Sign in again to continue."); }
      if (response.status === 403) { forbidden(); throw new Error("Administrator access is required."); }
      if (response.ok && response.status === 204) return null;
      let payload;
      try { payload = await response.json(); }
      catch { throw new Error(`The server returned an unreadable response (HTTP ${response.status}). Refresh before retrying a change.`); }
      if (state.stopped) throw new Error("This session has ended.");
      if (!response.ok) {
        const message = typeof payload?.error === "string" ? payload.error : `Request failed (HTTP ${response.status}).`;
        throw new Error(response.status === 409 ? `Conflict: ${message} The last enabled administrator must be preserved. Refresh accounts before trying again.` : message);
      }
      return payload;
    } finally { state.reads.delete(controller); }
  }

  const json = (method, body) => ({ method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  const userPath = (id) => `/api/admin/users/${encodeURIComponent(id)}`;

  function validUser(user) {
    return user && typeof user.id === "string" && user.id && typeof user.username === "string" &&
      ["admin", "user"].includes(user.role) && typeof user.enabled === "boolean";
  }

  async function verifyAccount() {
    const session = await api("/api/auth/session");
    if (session?.authenticated === false) { endSession(); return false; }
    if (session?.authenticated !== true || !validUser(session.user)) throw new Error("The server returned an invalid session. Reload before making changes.");
    if (!session.user.enabled) { endSession(); return false; }
    if (session.user.role !== "admin") { forbidden(); return false; }
    if (state.user && session.user.id !== state.user.id) { endSession("account-changed"); return false; }
    state.user = session.user;
    return true;
  }

  function telemetryControls() {
    const disabled = state.telemetryLoading || state.stopped;
    $("filter-fields").disabled = disabled;
    $("telemetry-refresh").disabled = disabled;
    $("page-previous").disabled = disabled || state.page <= 1 || state.total === 0;
    $("page-next").disabled = disabled || state.page * state.pageSize >= state.total;
    $("telemetry-results").setAttribute("aria-busy", String(state.telemetryLoading));
  }

  function renderTelemetry(result) {
    if (!result || !Array.isArray(result.items) || !result.summary ||
        metrics.some((key) => !Number.isSafeInteger(result.summary[key]) || result.summary[key] < 0) ||
        !Number.isSafeInteger(result.page) || result.page < 1 || !Number.isSafeInteger(result.pageSize) || result.pageSize < 1 ||
        !Number.isSafeInteger(result.total) || result.total < 0) throw new Error("The server returned invalid activity data.");
    const fragment = document.createDocumentFragment();
    for (const item of result.items) {
      if (!item || typeof item.id !== "string") throw new Error("The server returned an invalid activity record.");
      const row = node("tr");
      const operation = item.operation === "generation" ? "Generation" : item.operation === "edit" ? "Edit" : item.operation === "chat" ? "Text chat" : "Legacy / unknown";
      for (const value of [formatTime(item.requestedAt), formatTime(item.completedAt), item.username || "Historical / unavailable", item.ip || "Unavailable", operation]) row.append(node("td", value));
      row.append(node("td", modelLabel(item.model)), node("td", typeof item.deployment === "string" && item.deployment.trim() ? item.deployment : "Not recorded"));
      const status = node("td");
      const statusName = ["pending", "completed", "failed", "rejected", "interrupted", "legacy"].includes(item.status) ? item.status : "unknown";
      const className = ["failed", "rejected", "interrupted"].includes(statusName) ? " problem" : statusName === "pending" ? " pending" : "";
      status.append(node("span", statusName, `status-badge${className}`));
      row.append(status, node("td", Number.isSafeInteger(item.imageCount) ? numbers.format(item.imageCount) : "—"), node("td", item.httpStatus ?? "—"), node("td", item.id));
      fragment.append(row);
    }
    state.page = result.page;
    state.pageSize = result.pageSize;
    state.total = result.total;
    for (const key of metrics) $(`total-${key}`).textContent = numbers.format(result.summary[key]);
    $("telemetry-rows").replaceChildren(fragment);
    $("telemetry-empty").hidden = result.items.length !== 0;
    const pages = Math.max(1, Math.ceil(result.total / result.pageSize));
    $("page-description").textContent = `Page ${result.page} of ${pages} · ${numbers.format(result.total)} matching records · ${result.pageSize} per page`;
    $("telemetry-status").textContent = `${state.query.size ? "Filtered history" : "All-time history"} loaded. Totals include all matching pages. Times: ${timezone}.`;
  }

  async function loadTelemetry(page = 1) {
    if (state.telemetryLoading || state.stopped) return;
    state.telemetryLoading = true;
    error("telemetry-error");
    $("telemetry-status").textContent = "Loading historical activity…";
    $("telemetry-rows").replaceChildren();
    $("telemetry-empty").hidden = true;
    $("page-description").textContent = "Loading page…";
    for (const key of metrics) $(`total-${key}`).textContent = "—";
    telemetryControls();
    try {
      const query = new URLSearchParams(state.query);
      query.set("page", String(page));
      query.set("pageSize", "50");
      const result = await api(`/api/admin/telemetry?${query}`);
      if (!state.stopped) renderTelemetry(result);
    } catch (failure) {
      if (!state.stopped) {
        state.total = 0;
        error("telemetry-error", failure.message);
        $("telemetry-status").textContent = "Activity unavailable. Adjust filters or choose Refresh activity to retry.";
        $("page-description").textContent = "No page loaded.";
      }
    } finally {
      state.telemetryLoading = false;
      telemetryControls();
    }
  }

  function usersControls() {
    $("users-refresh").disabled = state.usersLoading || state.mutation || state.stopped;
    $("user-create").disabled = state.mutation || state.stopped;
    $("user-fields").disabled = state.mutation || state.stopped;
    $("user-close").disabled = state.mutation || state.stopped;
    $("admin-logout").disabled = state.mutation || state.stopped;
    $("users-results").setAttribute("aria-busy", String(state.usersLoading));
    for (const button of $("user-rows").querySelectorAll("button")) button.disabled = state.mutation || state.stopped;
  }

  function accountButton(label, className, handler) {
    const button = node("button", label, className);
    button.type = "button";
    button.addEventListener("click", handler);
    return button;
  }

  async function loadUsers(notice = "") {
    if (state.usersLoading || state.stopped) return;
    state.usersLoading = true;
    error("users-error");
    $("users-status").textContent = "Loading accounts…";
    $("user-rows").replaceChildren();
    $("users-empty").hidden = true;
    usersControls();
    try {
      const users = await api("/api/admin/users");
      if (state.stopped) return;
      if (!Array.isArray(users) || users.some((user) => !validUser(user))) throw new Error("The server returned an invalid accounts list.");
      const fragment = document.createDocumentFragment();
      for (const value of users) {
        // Retain only the public fields used by the editor; never render raw JSON.
        const user = { id: value.id, username: value.username, role: value.role, enabled: value.enabled, createdAt: value.createdAt };
        const row = node("tr");
        row.append(node("td", `${user.username}${user.id === state.user.id ? " (you)" : ""}`), node("td", user.role === "admin" ? "Administrator" : "User"), node("td", user.enabled ? "Yes" : "No"), node("td", formatTime(user.createdAt)));
        const actions = node("td");
        const buttons = node("div", undefined, "row-actions");
        const edit = accountButton("Edit", "secondary-button", () => openEditor(user));
        edit.setAttribute("aria-label", `Edit ${user.username}`);
        const remove = accountButton("Delete", "danger-button", () => { void deleteUser(user); });
        remove.setAttribute("aria-label", `Delete ${user.username}`);
        buttons.append(edit, remove);
        actions.append(buttons);
        row.append(actions);
        fragment.append(row);
      }
      $("user-rows").replaceChildren(fragment);
      $("users-empty").hidden = users.length !== 0;
      $("users-status").textContent = `${notice ? `${notice} ` : ""}${numbers.format(users.length)} accounts. Created times: ${timezone}.`;
    } catch (failure) {
      error("users-error", `${notice ? `${notice} ` : ""}${failure.message}`);
      if (!state.stopped) $("users-status").textContent = "Accounts unavailable. Choose Refresh accounts to retry.";
    } finally {
      state.usersLoading = false;
      usersControls();
    }
  }

  function openEditor(user = null) {
    if (state.mutation || state.stopped) return;
    state.editing = user;
    $("user-form").reset();
    error("user-error");
    $("user-status").textContent = "";
    $("user-dialog-title").textContent = user ? `Edit ${user.username}` : "Create account";
    $("user-username").value = user?.username || "";
    $("user-username").readOnly = Boolean(user);
    $("user-role").value = user?.role || "user";
    $("user-enabled").checked = user?.enabled ?? true;
    $("user-enabled-field").hidden = !user;
    $("user-password").value = "";
    $("user-password").required = !user;
    $("user-password-label").textContent = user ? "Reset password (optional)" : "Initial password";
    $("user-password-help").textContent = user ? "Leave empty to keep the current password. A reset requires 12–128 characters and revokes the account’s sessions." : "12–128 characters. Share securely outside this application; the password will not be displayed again.";
    $("user-save").textContent = user ? "Save changes" : "Create account";
    usersControls();
    $("user-dialog").showModal();
    $(user ? "user-role" : "user-username").focus();
  }

  function confirmAction(title, description, label) {
    if (state.confirmResolve || state.stopped) return Promise.resolve(false);
    $("confirm-title").textContent = title;
    $("confirm-description").textContent = description;
    $("confirm-accept").textContent = label;
    $("admin-confirm").returnValue = "";
    $("admin-confirm").showModal();
    return new Promise((resolve) => { state.confirmResolve = resolve; });
  }

  async function saveUser(event) {
    event.preventDefault();
    if (state.mutation || state.stopped || !$("user-form").reportValidity()) return;
    const user = state.editing;
    const payload = { role: $("user-role").value };
    if (user) payload.enabled = $("user-enabled").checked;
    else payload.username = $("user-username").value;
    if ($("user-password").value) payload.password = $("user-password").value;
    $("user-password").value = "";
    error("user-error");
    state.mutation = true;
    usersControls();
    try {
      const changes = [];
      if (user && payload.role !== user.role) changes.push(`change the role to ${payload.role}`);
      if (user && payload.enabled !== user.enabled) changes.push(payload.enabled ? "enable the account" : "disable the account");
      if (user && payload.password) changes.push("reset the password and revoke sessions");
      if (user && !changes.length) { $("user-status").textContent = "No changes to save."; return; }
      if (user || payload.role === "admin") {
        const description = user ? `For “${user.username}”: ${changes.join(", ")}.${user.id === state.user.id ? " This is your account; saving will sign you out. Unsent drafts and attachments in your studio tabs will be cleared when they detect the session end." : " Existing sessions may be revoked."}` : "This new account will be able to manage all accounts and view administrator telemetry.";
        if (!await confirmAction(user ? "Save account changes?" : "Create an administrator?", description, user ? "Save changes" : "Create administrator")) return;
      }
      if (!await verifyAccount()) return;
      $("user-status").textContent = "Saving account…";
      const result = await api(user ? userPath(user.id) : "/api/admin/users", json(user ? "PUT" : "POST", payload));
      if (state.stopped) return;
      if (user?.id === state.user.id) { endSession("account-changed"); return; }
      if (!validUser(result)) throw new Error("The change may have completed, but the response was invalid. Close this dialog and refresh accounts before retrying.");
      $("user-dialog").close();
      await loadUsers(user ? "Account updated." : "Account created.");
    } catch (failure) {
      error("user-error", failure.message);
      if (!state.stopped) $("user-status").textContent = "Account change not confirmed. Re-enter a password if retrying a reset or creation.";
    } finally {
      delete payload.password;
      $("user-password").value = "";
      state.mutation = false;
      usersControls();
    }
  }

  async function deleteUser(user) {
    if (state.mutation || state.stopped) return;
    state.mutation = true;
    usersControls();
    error("users-error");
    try {
      if (!await confirmAction("Delete this account?", `Delete “${user.username}”? This cannot be undone and revokes account access.${user.id === state.user.id ? " This is your account; you will be signed out and unsent studio drafts and attachments will be cleared when those tabs detect the session end." : ""} The server will reject removal of the last enabled administrator.`, "Delete account")) return;
      if (!await verifyAccount()) return;
      await api(userPath(user.id), { method: "DELETE" });
      if (state.stopped) return;
      if (user.id === state.user.id) { endSession("account-changed"); return; }
      await loadUsers("Account deleted.");
    } catch (failure) { error("users-error", failure.message); }
    finally { state.mutation = false; usersControls(); }
  }

  async function logout() {
    if (state.mutation || state.stopped) return;
    state.mutation = true;
    usersControls();
    error("admin-error");
    try {
      if (!await confirmAction("Sign out of Frame?", "This signs out all sessions for your account. Studio tabs will lose unsent drafts and attachments when they detect sign-out. Saved chats remain; running requests may still finish on the server.", "Sign out")) return;
      if (!await verifyAccount()) return;
      await api("/api/auth/logout", { method: "POST" });
      endSession("signed-out");
    } catch (failure) { error("admin-error", failure.message); }
    finally { state.mutation = false; usersControls(); }
  }

  $("timezone-help").textContent = `Times are local to ${timezone}; submitted as UTC. From is inclusive; to is exclusive. During a daylight-saving overlap, the browser selects the earlier occurrence.`;
  $("activity-caption").textContent = `Historical requests · times in ${timezone}`;
  $("filter-form").addEventListener("submit", (event) => {
    event.preventDefault();
    if (state.telemetryLoading || state.stopped || !$("filter-form").reportValidity()) return;
    error("telemetry-error");
    const query = new URLSearchParams();
    try {
      for (const key of ["from", "to"]) {
        const value = $(`filter-${key}`).value;
        if (!value) continue;
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) throw new Error("Enter a valid local date and time.");
        query.set(key, date.toISOString());
      }
      if (query.has("from") && query.has("to") && Date.parse(query.get("from")) >= Date.parse(query.get("to"))) throw new Error("From must be earlier than To. The end time is exclusive.");
      const ip = $("filter-ip").value.trim();
      if (ip) query.set("ip", ip);
      state.query = query;
      void loadTelemetry();
    } catch (failure) { error("telemetry-error", failure.message); }
  });
  $("filter-clear").addEventListener("click", () => {
    if (state.telemetryLoading || state.stopped) return;
    $("filter-form").reset();
    state.query = new URLSearchParams();
    void loadTelemetry();
  });
  $("telemetry-refresh").addEventListener("click", () => { void loadTelemetry(state.page); });
  $("page-previous").addEventListener("click", () => { if (state.page > 1) void loadTelemetry(state.page - 1); });
  $("page-next").addEventListener("click", () => { if (state.page * state.pageSize < state.total) void loadTelemetry(state.page + 1); });
  $("users-refresh").addEventListener("click", () => { void loadUsers(); });
  $("user-create").addEventListener("click", () => openEditor());
  $("user-form").addEventListener("submit", (event) => { void saveUser(event); });
  for (const id of ["user-close", "user-cancel"]) $(id).addEventListener("click", () => { if (!state.mutation) $("user-dialog").close(); });
  $("user-dialog").addEventListener("cancel", (event) => { if (state.mutation) event.preventDefault(); });
  $("user-dialog").addEventListener("close", () => { $("user-password").value = ""; state.editing = null; });
  $("confirm-cancel").addEventListener("click", () => $("admin-confirm").close());
  $("confirm-accept").addEventListener("click", () => $("admin-confirm").close("confirmed"));
  $("admin-confirm").addEventListener("close", () => {
    const resolve = state.confirmResolve;
    state.confirmResolve = null;
    if (resolve) resolve($("admin-confirm").returnValue === "confirmed");
  });
  $("admin-logout").addEventListener("click", () => { void logout(); });
  $("admin-retry").addEventListener("click", () => { void initialize(); });
  window.addEventListener("pagehide", stop);
  window.addEventListener("pageshow", (event) => { if (event.persisted) location.reload(); });
  window.addEventListener("focus", () => {
    if (!state.user || state.stopped || state.checking || state.mutation) return;
    state.checking = true;
    void verifyAccount().catch(() => { error("admin-error", "Could not recheck your session. Check your connection before making changes."); }).finally(() => { state.checking = false; });
  });

  async function initialize() {
    if (state.checking || state.stopped) return;
    state.checking = true;
    $("admin-retry").hidden = true;
    error("admin-error");
    $("admin-status").textContent = "Checking administrator access…";
    try {
      if (!await verifyAccount()) return;
      $("admin-account").textContent = state.user.username;
      $("admin-logout").hidden = false;
      $("admin-content").hidden = false;
      $("admin-status").textContent = "Administrator access confirmed.";
      await Promise.all([loadTelemetry(), loadUsers()]);
    } catch (failure) {
      if (!state.stopped) {
        error("admin-error", failure.message);
        $("admin-status").textContent = "Access could not be checked. No administrative data was requested.";
        $("admin-retry").hidden = false;
      }
    } finally { state.checking = false; }
  }

  void initialize();
})();