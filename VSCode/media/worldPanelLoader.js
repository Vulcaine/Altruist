const root = document.getElementById("world-root");

if (root) {
  root.innerHTML = `<div class="loading">Loading Altruist world view script...</div>`;
}

import("./worldPanel.js").catch((error) => {
  const message = error instanceof Error ? error.stack || error.message : String(error);
  if (root) {
    root.innerHTML = `
      <div class="error-state">
        <div>
          <h2>World viewer script failed to load</h2>
          <p>${escapeHtml(message)}</p>
        </div>
      </div>
    `;
  }
});

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}
