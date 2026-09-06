"use strict";

(() => {
  const $ = (id) => document.getElementById(id);
  let ready = false;
  let busy = false;
  let leaving = false;

  function forgetSelection() {
    try { localStorage.removeItem("frame.activeChatId"); } catch { /* Storage is optional. */ }
  }

  function enterStudio() {
    leaving = true;
    forgetSelection();
    $("login-password").value = "";
    location.replace("/"); // Never accept a return URL from the address bar or API.
  }

  function update() {
    $("login-fields").disabled = busy || !ready || leaving;
    $("login-retry").disabled = busy;
    $("login-form").setAttribute("aria-busy", String(busy));
  }

  function showError(message) {
    $("login-error").textContent = message;
    $("login-error").hidden = !message;
  }

  async function checkSession() {
    if (busy || leaving) return;
    busy = true;
    ready = false;
    update();
    showError("");
    $("login-status").textContent = "Checking sign-in availability…";
    $("setup-required").hidden = true;
    $("login-retry").hidden = true;
    try {
      const response = await fetch("/api/auth/session", { credentials: "same-origin", cache: "no-store", headers: { Accept: "application/json" }, signal: AbortSignal.timeout(30000) });
      if (!response.ok) throw new Error("Session check failed.");
      const session = await response.json();
      if (typeof session?.authenticated !== "boolean" || typeof session.setupRequired !== "boolean") throw new Error("Invalid session.");
      if (session.authenticated) {
        if (!session.user?.id || session.user.enabled !== true || !["admin", "user"].includes(session.user.role)) throw new Error("Invalid account.");
        if (notices[reason]) {
          // An API may reject a session that the session endpoint still accepts.
          // Require an explicit login after returning here instead of bouncing forever.
          ready = true;
          $("login-status").textContent = "Sign in again to refresh your session before returning to the studio.";
        } else enterStudio();
        return;
      }
      ready = !session.setupRequired;
      $("setup-required").hidden = !session.setupRequired;
      $("login-retry").hidden = !session.setupRequired;
      $("login-status").textContent = session.setupRequired ? "Offline administrator setup required." : "Ready to sign in.";
    } catch {
      showError("Sign-in availability could not be checked. Check your connection and try again.");
      $("login-status").textContent = "Sign-in unavailable.";
      $("login-retry").hidden = false;
    } finally {
      busy = false;
      update();
    }
  }

  $("login-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    if (!ready || busy || leaving || !$("login-form").reportValidity()) return;
    busy = true;
    showError("");
    $("login-status").textContent = "Signing in…";
    update();
    try {
      // Send the password only to the same-origin server, never to storage or logs.
      const request = fetch("/api/auth/login", {
        method: "POST", credentials: "same-origin", cache: "no-store",
        headers: { Accept: "application/json", "Content-Type": "application/json", "X-ImageChat-Request": "1" },
        body: JSON.stringify({ username: $("login-username").value, password: $("login-password").value }),
        signal: AbortSignal.timeout(30000)
      });
      $("login-password").value = "";
      const response = await request;
      if (!response.ok) throw new Error("Sign-in failed.");
      const result = await response.json();
      if (!result?.user?.id) throw new Error("Invalid sign-in response.");
      enterStudio();
    } catch {
      // The same message for bad credentials, disabled accounts and API failures.
      showError("Unable to sign in. Check your username and password, or try again later.");
      $("login-status").textContent = "Not signed in.";
    } finally {
      $("login-password").value = "";
      busy = false;
      update();
      if (!leaving) $("login-password").focus();
    }
  });

  const reason = new URLSearchParams(location.search).get("reason");
  const notices = {
    "expired": "Your session ended. Sign in again to continue.",
    "expired-drafts": "Your session ended. Unsent drafts and attachments in the previous studio tab were cleared for account privacy. Saved chats remain; a running image request may still finish on the server.",
    "signed-out": "You’re signed out. In-memory drafts were cleared; saved chats remain.",
    "password-changed": "Password changed. Sign in again with your new password.",
    "account-changed": "Your account or session changed. Sign in again to continue."
  };
  if (notices[reason]) {
    $("login-notice").textContent = notices[reason];
    $("login-notice").hidden = false;
  }
  $("login-retry").addEventListener("click", () => { void checkSession(); });
  window.addEventListener("pagehide", () => { $("login-password").value = ""; });
  window.addEventListener("pageshow", (event) => { if (event.persisted) location.reload(); });
  void checkSession();
})();