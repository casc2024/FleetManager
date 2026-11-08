// Fleet Manager — Email bar injected into the JCK quoter (/quotes).
// Lets an admin pick a saved recipient and email the current proposal (the RFP
// document) through the server (SMTP/Resend). Recipients come from the
// Recipients maintainer (/api/recipients).
(function () {
  const esc = (s) => String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  const bar = document.createElement("div");
  bar.id = "jckEmailBar";
  bar.innerHTML = `
    <strong>Email quote</strong>
    <select id="jckRecipient" aria-label="Recipient"><option value="">Loading recipients…</option></select>
    <input id="jckSubject" placeholder="Subject (optional)" maxlength="150">
    <button type="button" id="jckSendEmail">Send</button>
    <a href="/recipients" class="jck-manage">Manage</a>
    <span id="jckEmailMsg"></span>`;
  document.body.appendChild(bar);

  const style = document.createElement("style");
  style.textContent = `
    #jckEmailBar{position:fixed;left:50%;bottom:14px;transform:translateX(-50%);z-index:3000;
      display:flex;gap:9px;align-items:center;flex-wrap:wrap;max-width:94vw;
      background:#12233f;color:#fff;border-radius:12px;padding:10px 14px;box-shadow:0 12px 36px rgba(0,0,0,.3);font:14px 'DM Sans',Segoe UI,sans-serif}
    #jckEmailBar strong{font-weight:700}
    #jckEmailBar select,#jckEmailBar input{border:1px solid #ffffff33;background:#ffffff14;color:#fff;border-radius:8px;padding:8px 10px;font:inherit;min-height:38px}
    #jckEmailBar select option{color:#12233f}
    #jckEmailBar input{min-width:150px}
    #jckEmailBar button{border:0;border-radius:8px;padding:8px 16px;font:700 14px inherit;cursor:pointer;background:#e7a14b;color:#12233f;min-height:38px}
    #jckEmailBar button:disabled{opacity:.6;cursor:wait}
    #jckEmailBar .jck-manage{color:#bcd0ea;font-size:12px;text-decoration:underline}
    #jckEmailMsg{font-size:13px}
    #jckEmailMsg.ok{color:#8fe3b0}#jckEmailMsg.err{color:#ffb4ab}
    @media(max-width:600px){#jckEmailBar{left:8px;right:8px;transform:none;max-width:none;justify-content:center}}`;
  document.head.appendChild(style);

  const $ = (id) => document.getElementById(id);
  const msg = (t, cls) => { const m = $("jckEmailMsg"); m.textContent = t; m.className = cls || ""; };

  async function loadRecipients() {
    try {
      const res = await fetch("/api/recipients");
      if (!res.ok) throw new Error();
      const list = await res.json();
      $("jckRecipient").innerHTML = list.length
        ? '<option value="">Select recipient…</option>' + list.map((r) => `<option value="${r.idRecipient}">${esc(r.name)} — ${esc(r.email)}</option>`).join("")
        : '<option value="">No recipients — add one in Manage</option>';
    } catch {
      $("jckRecipient").innerHTML = '<option value="">Could not load recipients</option>';
    }
  }

  function proposalEl() {
    // The quoter renders the proposal inside #rfpPaper; fall back to the RFP screen.
    return document.getElementById("rfpPaper") || document.getElementById("rfpScreen");
  }

  function bufToBase64(buf) {
    const bytes = new Uint8Array(buf);
    let binary = "", chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk)
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    return btoa(binary);
  }

  async function makePdf(el) {
    if (!window.html2pdf) return null;
    const opt = {
      margin: [8, 8, 8, 8],
      image: { type: "jpeg", quality: 0.95 },
      html2canvas: { scale: 2, useCORS: true, backgroundColor: "#ffffff" },
      jsPDF: { unit: "mm", format: "letter", orientation: "portrait" },
      pagebreak: { mode: ["css", "legacy"] }
    };
    const buf = await window.html2pdf().set(opt).from(el).outputPdf("arraybuffer");
    return bufToBase64(buf);
  }

  async function send() {
    const id = $("jckRecipient").value;
    if (!id) { msg("Pick a recipient.", "err"); return; }
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
          recipientId: Number(id), subject: $("jckSubject").value.trim(),
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
