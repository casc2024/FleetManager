// Fleet Manager — lateral menu + session.
// Builds a fixed sidebar (Dashboard, Mixer Fleet, Users for admins, signed-in user,
// Sign out) on every page that loads this script, adds the ☰ button for phones,
// and locks the page's "You" field to the real signed-in username so the audit
// trail records who made each change.
(function () {
  const LOGO = "/img/jck-logo.jpg";

  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  async function logout() {
    try { await fetch("/api/auth/logout", { method: "POST" }); } catch { /* ignore */ }
    window.location.href = "/login";
  }

  function montarMenu(me) {
    const path = window.location.pathname.replace(/\/+$/, "") || "/";
    const link = (href, label) => {
      const active = (href === "/" ? path === "/" : path.startsWith(href)) ? " active" : "";
      return `<a class="menu-link${active}" href="${href}">${label}</a>`;
    };

    const aside = document.createElement("aside");
    aside.className = "app-sidebar";
    aside.id = "appMenu";
    aside.setAttribute("aria-label", "Main menu");
    aside.innerHTML = `
      <div class="sb-brand"><img src="${LOGO}" alt="JCK"></div>
      <div class="sb-section">Menu</div>
      ${link("/", "Dashboard")}
      ${me && me.isAdmin ? link("/mixer", "Mixer Fleet") : ""}
      ${me && me.isAdmin ? link("/users", "Users") : ""}
      <div class="sb-who">
        <strong>${esc(me && me.name)}</strong>
        <small title="${esc(me && me.email)}">${esc(me && me.email)}</small>
      </div>
      <button type="button" class="menu-logout" id="logoutBtn">Sign out</button>`;
    document.body.prepend(aside);
    document.body.classList.add("has-sidebar");

    // ☰ button and backdrop for phones
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "sb-toggle";
    toggle.setAttribute("aria-label", "Open menu");
    toggle.innerHTML = "&#9776;";
    const backdrop = document.createElement("div");
    backdrop.className = "sb-backdrop";
    const cerrar = () => document.body.classList.remove("sb-open");
    toggle.addEventListener("click", () => document.body.classList.toggle("sb-open"));
    backdrop.addEventListener("click", cerrar);
    aside.querySelectorAll("a.menu-link").forEach((a) => a.addEventListener("click", cerrar));
    document.body.append(toggle, backdrop);

    document.getElementById("logoutBtn").addEventListener("click", logout);
  }

  async function init() {
    let me = null;
    try {
      const res = await fetch("/api/auth/me");
      if (res.status === 401) { window.location.href = "/login"; return; }
      if (res.ok) me = await res.json();
    } catch { /* offline: menu still renders without the user's name */ }

    montarMenu(me);

    // Lock the audit identity on the existing boards.
    if (me && me.username) {
      const u = document.getElementById("userName");
      if (u) { u.value = me.username; u.readOnly = true; u.title = "Signed-in user"; }
    }

    window.APP_ME = me;
    document.dispatchEvent(new CustomEvent("app:me", { detail: me }));
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
