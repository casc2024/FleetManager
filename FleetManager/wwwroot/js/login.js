// Fleet Manager — sign-in
(function () {
  const form = document.getElementById("loginForm");
  const btn = document.getElementById("loginBtn");
  const errBox = document.getElementById("loginError");

  // Empty string clears the message; a real error passes its own text.
  function showError(msg) { errBox.textContent = msg; }

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    showError("");
    const username = document.getElementById("username").value.trim();
    const password = document.getElementById("password").value;
    if (!username || !password) { showError("Enter your username and password."); return; }

    btn.disabled = true;
    const oldLabel = btn.textContent;
    btn.textContent = "Signing in…";
    try {
      const res = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password })
      });
      if (res.ok) { window.location.href = "/"; return; }
      // Un 5xx es un fallo del servidor (p. ej. la base sin el esquema), no una contraseña incorrecta.
      if (res.status >= 500) { showError("Server error. Check the application log and that the database schema is up to date."); return; }
      let msg = "Invalid username or password.";
      try { const data = await res.json(); if (data && data.mensaje) msg = data.mensaje; } catch { /* keep default */ }
      showError(msg);
    } catch {
      showError("Could not reach the server. Check your connection.");
    } finally {
      btn.disabled = false;
      btn.textContent = oldLabel;
    }
  });
})();
