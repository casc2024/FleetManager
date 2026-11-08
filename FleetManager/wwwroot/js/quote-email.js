// Fleet Manager — Email bar injected into the JCK quoter (/quotes).
// Pick recipients from a dropdown; each one is added as a removable chip.
// Separate To and CC lists. Emails the proposal (with PDF) through the server.
(function () {
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  let recipients = [];           // [{idRecipient, name, email}]
  const toIds = [];
  const ccIds = [];

  const bar = document.createElement("div");
  bar.id = "jckEmailBar";
  bar.innerHTML = `
    <div class="jck-head"><strong>Email quote</strong><button type="button" id="jckEmailHide" title="Hide" aria-label="Hide">✕</button></div>
    <div class="jck-grp">
      <span class="jck-lbl">To</span>
      <div class="jck-chips" id="jckToChips"></div>
      <select id="jckToAdd" aria-label="Add recipient"><option value="">Add recipient…</option></select>
    </div>
    <div class="jck-grp">
      <span class="jck-lbl">CC (optional)</span>
      <div class="jck-chips" id="jckCcChips"></div>
      <select id="jckCcAdd" aria-label="Add CC"><option value="">Add CC…</option></select>
    </div>
    <input id="jckSubject" placeholder="Subject (optional)" maxlength="150">
    <button type="button" id="jckSendEmail">Send</button>
    <a href="/recipients" class="jck-manage">Manage</a>
    <span id="jckEmailMsg"></span>`;
  document.body.appendChild(bar);

  const style = document.createElement("style");
  style.textContent = `
    #jckEmailBar{position:fixed;left:50%;bottom:14px;transform:translateX(-50%);z-index:3000;
      display:flex;gap:10px;align-items:flex-end;flex-wrap:wrap;max-width:94vw;
      background:#12233f;color:#fff;border-radius:12px;padding:10px 14px;box-shadow:0 12px 36px rgba(0,0,0,.3);font:14px 'DM Sans',Segoe UI,sans-serif}
    #jckEmailBar strong{font-weight:700;align-self:center}
    #jckEmailBar .jck-grp{display:flex;flex-direction:column;gap:4px;min-width:210px}
    #jckEmailBar .jck-lbl{font-size:11px;color:#bcd0ea}
    #jckEmailBar .jck-chips{display:flex;flex-wrap:wrap;gap:5px}
    #jckEmailBar .jck-chip{display:inline-flex;align-items:center;gap:6px;background:#e7a14b;color:#12233f;
      border-radius:999px;padding:3px 6px 3px 10px;font-size:12px;font-weight:600;max-width:230px}
    #jckEmailBar .jck-chip span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
    #jckEmailBar .jck-chip button{border:0;background:#0003;color:#12233f;border-radius:50%;width:18px;height:18px;
      line-height:1;cursor:pointer;font-size:13px;padding:0}
    #jckEmailBar select,#jckEmailBar input{border:1px solid #ffffff33;background:#ffffff14;color:#fff;border-radius:8px;padding:7px 9px;font:14px inherit;min-height:36px}
    #jckEmailBar select option{color:#12233f}
    #jckEmailBar input{min-width:140px;min-height:38px;align-self:stretch}
    #jckEmailBar button#jckSendEmail{border:0;border-radius:8px;padding:8px 18px;font:700 14px inherit;cursor:pointer;background:#e7a14b;color:#12233f;min-height:38px;align-self:stretch}
    #jckEmailBar button#jckSendEmail:disabled{opacity:.6;cursor:wait}
    #jckEmailBar .jck-manage{color:#bcd0ea;font-size:12px;text-decoration:underline;align-self:center}
    #jckEmailMsg{font-size:13px;align-self:center}
    #jckEmailMsg.ok{color:#8fe3b0}#jckEmailMsg.err{color:#ffb4ab}
    #jckEmailBar .jck-head{flex:1 1 100%;display:flex;align-items:center;justify-content:space-between;gap:10px}
    #jckEmailBar #jckEmailHide{border:0;background:#ffffff22;color:#fff;border-radius:7px;width:26px;height:26px;cursor:pointer;font-size:13px;line-height:1;padding:0}
    #jckEmailBar #jckEmailHide:hover{background:#ffffff38}
    #jckEmailLauncher{position:fixed;right:14px;bottom:14px;z-index:3000;border:0;border-radius:999px;padding:11px 17px;background:#e7a14b;color:#12233f;font:700 14px 'DM Sans',Segoe UI,sans-serif;box-shadow:0 10px 28px rgba(0,0,0,.28);cursor:pointer}
    #jckEmailLauncher:hover{background:#edac5e}
    @media(max-width:700px){#jckEmailBar{left:8px;right:8px;transform:none;max-width:none}#jckEmailBar .jck-grp{flex:1 1 100%}}`;
  document.head.appendChild(style);

  // Collapsible: a launcher pill shows/hides the email bar; the choice is remembered.
  const launcher = document.createElement("button");
  launcher.id = "jckEmailLauncher";
  launcher.type = "button";
  launcher.textContent = "✉ Email quote";
  document.body.appendChild(launcher);
  function setOpen(open) {
    bar.style.display = open ? "flex" : "none";
    launcher.style.display = open ? "none" : "block";
    try { localStorage.setItem("jck_email_open", open ? "1" : "0"); } catch (e) {}
  }
  let startOpen = false;
  try { startOpen = localStorage.getItem("jck_email_open") === "1"; } catch (e) {}
  setOpen(startOpen);
  document.getElementById("jckEmailHide").addEventListener("click", () => setOpen(false));
  launcher.addEventListener("click", () => setOpen(true));

  const $ = (id) => document.getElementById(id);
  const msg = (t, cls) => { const m = $("jckEmailMsg"); m.textContent = t; m.className = cls || ""; };
  const nameOf = (id) => { const r = recipients.find((x) => x.idRecipient === id); return r ? r.name : "#" + id; };

  // Rebuild a dropdown so it only offers recipients not already chosen in that list.
  function fillSelect(selId, placeholder, chosen) {
    $(selId).innerHTML = `<option value="">${placeholder}</option>` +
      recipients.filter((r) => !chosen.includes(r.idRecipient))
        .map((r) => `<option value="${r.idRecipient}">${esc(r.name)} — ${esc(r.email)}</option>`).join("");
  }

  function renderChips(boxId, ids) {
    $(boxId).innerHTML = ids.map((id) =>
      `<span class="jck-chip"><span>${esc(nameOf(id))}</span><button type="button" data-id="${id}" aria-label="Remove">×</button></span>`).join("");
  }

  function refresh() {
    renderChips("jckToChips", toIds);
    renderChips("jckCcChips", ccIds);
    fillSelect("jckToAdd", "Add recipient…", toIds);
    fillSelect("jckCcAdd", "Add CC…", ccIds);
  }

  async function loadRecipients() {
    try {
      const res = await fetch("/api/recipients");
      if (!res.ok) throw new Error();
      recipients = await res.json();
      if (!recipients.length) msg("No recipients — add one in Manage.", "err");
    } catch { msg("Could not load recipients.", "err"); return; }
    refresh();
  }

  // ---- wiring: add on select, remove on chip × ----
  $("jckToAdd").addEventListener("change", (e) => { const id = Number(e.target.value); if (id) { toIds.push(id); refresh(); } });
  $("jckCcAdd").addEventListener("change", (e) => { const id = Number(e.target.value); if (id) { ccIds.push(id); refresh(); } });
  $("jckToChips").addEventListener("click", (e) => { const b = e.target.closest("[data-id]"); if (b) { toIds.splice(toIds.indexOf(Number(b.dataset.id)), 1); refresh(); } });
  $("jckCcChips").addEventListener("click", (e) => { const b = e.target.closest("[data-id]"); if (b) { ccIds.splice(ccIds.indexOf(Number(b.dataset.id)), 1); refresh(); } });

  // ---- PDF + send ----
  function proposalEl() { return document.getElementById("rfpPaper") || document.getElementById("rfpScreen"); }

  function bufToBase64(buf) {
    const bytes = new Uint8Array(buf);
    let binary = "", chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    return btoa(binary);
  }

  async function makePdf(srcEl) {
    if (!window.html2pdf) return null;
    // Clone the proposal and normalize it: drop the 1000px min-height, the
    // centering margin and the shadow so there is no blank band, and give it a
    // fixed letter-width so html2canvas renders it tightly.
    const W = 740; // letter content width (px) with 10mm margins
    const clone = srcEl.cloneNode(true);
    clone.style.minHeight = "0";
    clone.style.maxWidth = "none";
    clone.style.margin = "0";
    clone.style.boxShadow = "none";
    clone.style.width = W + "px";
    clone.style.background = "#fff";
    // Render in the document flow (top-left), not off-screen with position:fixed,
    // so html2canvas does not offset the capture origin (blank band / shifted content).
    const wrap = document.createElement("div");
    wrap.style.cssText = "position:absolute;left:0;top:0;width:" + W + "px;background:#fff;z-index:-9999;";
    wrap.appendChild(clone);
    document.body.appendChild(wrap);
    const prevScroll = window.scrollY || window.pageYOffset || 0;
    window.scrollTo(0, 0);
    try {
      const opt = {
        margin: [10, 10, 12, 10],
        image: { type: "jpeg", quality: 0.95 },
        html2canvas: { scale: 2, useCORS: true, backgroundColor: "#ffffff", scrollX: 0, scrollY: 0, x: 0, y: 0, windowWidth: W },
        jsPDF: { unit: "mm", format: "letter", orientation: "portrait" },
        // Keep table rows, headers and images from splitting across pages.
        pagebreak: { mode: ["css", "legacy"], avoid: ["tr", "thead", "tfoot", "img"] }
      };
      const buf = await window.html2pdf().set(opt).from(clone).outputPdf("arraybuffer");
      return bufToBase64(buf);
    } finally {
      wrap.remove();
      window.scrollTo(0, prevScroll);
    }
  }

  async function send() {
    if (!toIds.length) { msg("Add at least one recipient (To).", "err"); return; }
    const el = proposalEl();
    const html = el ? el.innerHTML.trim() : "";
    if (!html) { msg("Open the RFP / proposal first.", "err"); return; }

    const btn = $("jckSendEmail"); btn.disabled = true;
    try {
      msg("Building PDF…");
      let pdfBase64 = null;
      try { pdfBase64 = await makePdf(el); } catch { pdfBase64 = null; }

      msg("Sending…");
      const res = await fetch("/api/quote/email", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          recipientIds: toIds, ccIds, subject: $("jckSubject").value.trim(),
          html, pdfBase64, pdfName: "JCK_Quote.pdf"
        })
      });
      if (res.status === 401) { window.location.href = "/login"; return; }
      const data = await res.json().catch(() => null);
      msg((data && data.mensaje) || (res.ok ? "Sent." : "Could not send."), res.ok ? "ok" : "err");
    } catch { msg("Could not reach the server.", "err"); }
    finally { btn.disabled = false; }
  }

  $("jckSendEmail").addEventListener("click", send);
  loadRecipients();
})();
