// Fleet Manager — Email recipients maintainer (admin). CRUD with client-side
// validation that mirrors the server rules.
(function () {
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  const RE_NAME = /^\p{L}[\p{L} .'\-]{0,119}$/u;
  const RE_EMAIL = /^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$/;

  const dlg = $("recipientDialog");
  let all = [];

  function toast(msg, isError) {
    const t = $("toast");
    t.textContent = msg; t.className = "toast show" + (isError ? " error" : "");
    setTimeout(() => (t.className = "toast"), 2600);
  }

  async function load() {
    try {
      const res = await fetch("/api/recipients");
      if (res.status === 401 || res.status === 403) { window.location.href = "/login"; return; }
      all = await res.json();
    } catch { toast("Could not load recipients.", true); return; }
    render();
  }

  function render() {
    const q = $("search").value.trim().toLowerCase();
    const rows = all.filter((u) => !q || `${u.name} ${u.email}`.toLowerCase().includes(q));
    $("recipientsBody").innerHTML = rows.length
      ? rows.map((u) => `<tr>
          <td data-label="Name">${esc(u.name)}</td>
          <td data-label="Email">${esc(u.email)}</td>
          <td data-label="Action" class="action"><div class="row-actions">
            <button class="secondary" data-edit="${u.idRecipient}">Edit</button>
            <button class="danger" data-del="${u.idRecipient}">Remove</button>
          </div></td></tr>`).join("")
      : `<tr><td colspan="3" style="text-align:center;color:var(--muted);padding:22px">No recipients yet.</td></tr>`;
    $("rowCount").textContent = `${rows.length} recipient${rows.length === 1 ? "" : "s"}`;
  }

  function openDialog(r) {
    $("dlgError").textContent = "";
    $("idRecipient").value = r ? r.idRecipient : "";
    $("rname").value = r ? r.name : "";
    $("remail").value = r ? r.email : "";
    [$("rname"), $("remail")].forEach((el) => el.classList.remove("invalid"));
    $("dlgTitle").textContent = r ? "Edit recipient" : "Add recipient";
    dlg.showModal();
    $("rname").focus();
  }

  const mark = (el, ok) => { el.classList.toggle("invalid", !ok); return ok; };

  async function save(e) {
    e.preventDefault();
    const name = $("rname").value.trim(), email = $("remail").value.trim();
    let ok = true;
    ok = mark($("rname"), RE_NAME.test(name)) && ok;
    ok = mark($("remail"), email.length <= 160 && RE_EMAIL.test(email)) && ok;
    if (!ok) { $("dlgError").textContent = "Please fix the highlighted fields."; return; }

    const body = { idRecipient: $("idRecipient").value ? Number($("idRecipient").value) : null, name, email };
    const url = $("idRecipient").value ? "/api/recipients/update" : "/api/recipients";
    $("dlgSave").disabled = true;
    try {
      const res = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      const data = await res.json().catch(() => null);
      if (res.ok) { dlg.close(); toast((data && data.mensaje) || "Saved."); load(); }
      else $("dlgError").textContent = (data && data.mensaje) || "Could not save.";
    } catch { $("dlgError").textContent = "Could not reach the server."; }
    finally { $("dlgSave").disabled = false; }
  }

  async function remove(id) {
    const u = all.find((x) => x.idRecipient === id);
    if (!u || !confirm(`Remove ${u.name}?`)) return;
    try {
      const res = await fetch("/api/recipients/delete", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ idRecipient: id }) });
      const data = await res.json().catch(() => null);
      if (res.ok) { toast((data && data.mensaje) || "Removed."); load(); }
      else toast((data && data.mensaje) || "Could not remove.", true);
    } catch { toast("Could not reach the server.", true); }
  }

  $("addRecipient").addEventListener("click", () => openDialog(null));
  $("search").addEventListener("input", render);
  $("recipientForm").addEventListener("submit", save);
  $("dlgCancel").addEventListener("click", () => dlg.close());
  dlg.addEventListener("cancel", (e) => { e.preventDefault(); dlg.close(); });
  $("recipientsBody").addEventListener("click", (e) => {
    const edit = e.target.closest("[data-edit]"), del = e.target.closest("[data-del]");
    if (edit) openDialog(all.find((x) => x.idRecipient === Number(edit.dataset.edit)));
    else if (del) remove(Number(del.dataset.del));
  });

  load();
})();
