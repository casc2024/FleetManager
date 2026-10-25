// Fleet Manager — User maintainer (admin). CRUD with client-side validation
// that mirrors the server rules (reject strange characters).
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // Must match SeguridadRepositorioPostgres regexes.
  const RE_USERNAME = /^[A-Za-z0-9._\-]{3,60}$/;
  const RE_NAME = /^\p{L}[\p{L} '\-.]{0,79}$/u;
  const RE_EMAIL = /^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$/;
  const RE_PHONE = /^[0-9 +()\-]{5,40}$/;
  const RE_DOC = /^[\p{L}\p{N}.\-]{1,40}$/u;

  const dlg = $("userDialog");
  let all = [];

  function toast(msg, isError) {
    const t = $("toast");
    t.textContent = msg;
    t.className = "toast show" + (isError ? " error" : "");
    setTimeout(() => (t.className = "toast"), 2600);
  }

  // ---- list --------------------------------------------------------------
  async function load() {
    try {
      const res = await fetch("/api/users");
      if (res.status === 401 || res.status === 403) { window.location.href = "/login"; return; }
      all = await res.json();
    } catch { toast("Could not load users.", true); return; }
    render();
  }

  function render() {
    const q = $("search").value.trim().toLowerCase();
    const rows = all.filter((u) => {
      if (!q) return true;
      return `${u.username} ${u.firstName} ${u.lastName} ${u.email}`.toLowerCase().includes(q);
    });
    $("usersBody").innerHTML = rows.length
      ? rows.map((u) => `<tr>
          <td data-label="Username">${esc(u.username)}</td>
          <td data-label="First name">${esc(u.firstName)}</td>
          <td data-label="Last name">${esc(u.lastName)}</td>
          <td data-label="Email">${esc(u.email)}</td>
          <td data-label="Phone">${esc(u.phone || "—")}</td>
          <td data-label="Document">${esc(u.documentNumber || "—")}</td>
          <td data-label="Role"><span class="role-pill ${u.isAdmin ? "admin" : "user"}">${u.isAdmin ? "Admin" : "User"}</span></td>
          <td data-label="Action" class="action"><div class="row-actions">
            <button class="secondary" data-edit="${u.idUser}">Edit</button>
            <button class="danger" data-del="${u.idUser}">Remove</button>
          </div></td></tr>`).join("")
      : `<tr><td colspan="8" style="text-align:center;color:var(--muted);padding:22px">No users found.</td></tr>`;
    $("rowCount").textContent = `${rows.length} user${rows.length === 1 ? "" : "s"}`;
  }

  // ---- dialog ------------------------------------------------------------
  function openDialog(user) {
    $("dlgError").textContent = "";
    $("idUser").value = user ? user.idUser : "";
    $("username").value = user ? user.username : "";
    $("firstName").value = user ? user.firstName : "";
    $("lastName").value = user ? user.lastName : "";
    $("uemail").value = user ? user.email : "";
    $("phone").value = user && user.phone ? user.phone : "";
    $("document").value = user && user.documentNumber ? user.documentNumber : "";
    $("isAdmin").checked = user ? !!user.isAdmin : false;
    $("password").value = "";
    [$("username"), $("firstName"), $("lastName"), $("uemail"), $("phone"), $("document"), $("password")]
      .forEach((el) => el.classList.remove("invalid"));

    $("dlgTitle").textContent = user ? "Edit user" : "Add user";
    $("pwdLabel").innerHTML = user ? "New password <small>(leave blank to keep)</small>" : "Password *";
    $("pwdHint").textContent = user
      ? "Leave blank to keep the current password."
      : "At least 8 characters, with letters and numbers.";
    dlg.showModal();
    $("firstName").focus();
  }

  function mark(el, ok) { el.classList.toggle("invalid", !ok); return ok; }

  function validate() {
    const isNew = !$("idUser").value;
    const username = $("username").value.trim();
    const first = $("firstName").value.trim();
    const last = $("lastName").value.trim();
    const email = $("uemail").value.trim();
    const phone = $("phone").value.trim();
    const doc = $("document").value.trim();
    const pwd = $("password").value.trim();

    let ok = true;
    ok = mark($("username"), RE_USERNAME.test(username)) && ok;
    ok = mark($("firstName"), RE_NAME.test(first)) && ok;
    ok = mark($("lastName"), RE_NAME.test(last)) && ok;
    ok = mark($("uemail"), email.length <= 160 && RE_EMAIL.test(email)) && ok;
    ok = mark($("phone"), phone === "" || RE_PHONE.test(phone)) && ok;
    ok = mark($("document"), doc === "" || RE_DOC.test(doc)) && ok;

    const needsPwd = isNew || pwd.length > 0;
    const pwdOk = !needsPwd || (pwd.length >= 8 && pwd.length <= 100 && /[A-Za-z]/.test(pwd) && /\d/.test(pwd));
    ok = mark($("password"), pwdOk) && ok;

    if (!ok) $("dlgError").textContent = "Please fix the highlighted fields. Only letters, numbers and basic punctuation are allowed.";
    return ok ? { isNew, username, first, last, email, phone, doc, pwd } : null;
  }

  async function save(e) {
    e.preventDefault();
    const v = validate();
    if (!v) return;

    const body = {
      idUser: $("idUser").value ? Number($("idUser").value) : null,
      username: v.username,
      firstName: v.first, lastName: v.last, email: v.email,
      phone: v.phone || null, documentNumber: v.doc || null,
      isAdmin: $("isAdmin").checked,
      password: v.pwd || null
    };
    const url = v.isNew ? "/api/users" : "/api/users/update";
    $("dlgSave").disabled = true;
    try {
      const res = await fetch(url, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
      });
      const data = await res.json().catch(() => null);
      if (res.ok) {
        dlg.close();
        toast((data && data.mensaje) || "Saved.");
        load();
      } else {
        $("dlgError").textContent = (data && data.mensaje) || "Could not save the user.";
      }
    } catch {
      $("dlgError").textContent = "Could not reach the server.";
    } finally {
      $("dlgSave").disabled = false;
    }
  }

  async function remove(id) {
    const u = all.find((x) => x.idUser === id);
    if (!u) return;
    if (!confirm(`Remove ${u.firstName} ${u.lastName}? They will no longer be able to sign in.`)) return;
    try {
      const res = await fetch("/api/users/delete", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ idUser: id })
      });
      const data = await res.json().catch(() => null);
      if (res.ok) { toast((data && data.mensaje) || "User removed."); load(); }
      else toast((data && data.mensaje) || "Could not remove the user.", true);
    } catch { toast("Could not reach the server.", true); }
  }

  // ---- wiring ------------------------------------------------------------
  $("addUser").addEventListener("click", () => openDialog(null));
  $("search").addEventListener("input", render);
  $("userForm").addEventListener("submit", save);
  $("dlgCancel").addEventListener("click", () => dlg.close());
  dlg.addEventListener("cancel", (e) => { e.preventDefault(); dlg.close(); });
  $("usersBody").addEventListener("click", (e) => {
    const edit = e.target.closest("[data-edit]");
    const del = e.target.closest("[data-del]");
    if (edit) openDialog(all.find((x) => x.idUser === Number(edit.dataset.edit)));
    else if (del) remove(Number(del.dataset.del));
  });

  load();
})();
