window.pixelpipeApplyUpdate = async function (confirm) {
  const panel = document.getElementById("pp-update");
  const title = document.getElementById("pp-update-title");
  const detail = document.getElementById("pp-update-detail");
  const bar = document.getElementById("pp-update-bar");
  const error = document.getElementById("pp-update-error");
  if (!panel || panel.dataset.running === "1") {
    return;
  }

  panel.dataset.running = "1";
  panel.hidden = false;
  error.hidden = true;
  bar.hidden = true;
  title.textContent = "Checking for the update…";
  detail.textContent = "";

  const poll = window.setInterval(async () => {
    try {
      const response = await fetch("/api/app/update", { cache: "no-store" });
      if (!response.ok) {
        return;
      }
      const snapshot = await response.json();
      paint(snapshot, title, detail, bar);
    } catch {
      title.textContent = "Reconnecting…";
      detail.textContent = "PixelPipe is restarting.";
      bar.hidden = true;
    }
  }, 700);

  try {
    const response = await fetch("/api/app/update/apply", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ confirm: !!confirm }),
    });
    if (!response.ok) {
      window.clearInterval(poll);
      let reason = await response.text();
      try {
        const body = JSON.parse(reason);
        reason = body.error || reason;
      } catch {
        /* plain text */
      }
      showError(title, detail, error, reason);
      panel.dataset.running = "0";
      return;
    }
  } catch (err) {
    window.clearInterval(poll);
    showError(title, detail, error, String(err));
    panel.dataset.running = "0";
    return;
  }

  title.textContent = "Reconnecting…";
  detail.textContent = "Installing. Windows will ask for permission if it needs it. This page will reload when PixelPipe is back.";
  bar.hidden = true;
  await window.pixelpipeWaitForRestart();
  window.clearInterval(poll);
};

window.pixelpipeWaitForRestart = async function () {
  for (let attempt = 0; attempt < 90; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 2000));
    try {
      const response = await fetch("/api/app/update", { cache: "no-store" });
      if (response.ok) {
        location.reload();
        return;
      }
    } catch {
      const title = document.getElementById("pp-update-title");
      const detail = document.getElementById("pp-update-detail");
      if (title) title.textContent = "Reconnecting…";
      if (detail) detail.textContent = "PixelPipe is restarting.";
    }
  }
  showError(
    document.getElementById("pp-update-title"),
    document.getElementById("pp-update-detail"),
    document.getElementById("pp-update-error"),
    "PixelPipe did not come back. Open the log for the last update step.");
  const panel = document.getElementById("pp-update");
  if (panel) panel.dataset.running = "0";
};

function paint(snapshot, title, detail, bar) {
  const phase = snapshot.phase || "idle";
  const done = Number(snapshot.bytesDownloaded || 0);
  const total = snapshot.bytesTotal;
  if (phase === "checking") {
    title.textContent = "Checking for the update…";
    bar.hidden = true;
  } else if (phase === "downloading") {
    title.textContent = "Downloading the installer…";
    bar.hidden = false;
    if (total) {
      bar.value = Math.min(100, Math.round((done / total) * 100));
      detail.textContent = `${mb(done)} MB of ${mb(total)} MB`;
    } else {
      bar.removeAttribute("value");
      detail.textContent = `${mb(done)} MB downloaded`;
    }
  } else if (phase === "verifying") {
    title.textContent = "Verifying the checksum…";
    bar.hidden = true;
    detail.textContent = "";
  } else if (phase === "installing") {
    title.textContent = "Installing…";
    detail.textContent = "Windows will ask for permission to update PixelPipe.";
    bar.hidden = true;
  } else if (phase === "failed") {
    showError(title, detail, document.getElementById("pp-update-error"), snapshot.error || "The update failed.");
  }
}

function showError(title, detail, error, reason) {
  if (title) title.textContent = "The update failed";
  if (detail) detail.textContent = reason;
  if (error) {
    error.hidden = false;
    error.innerHTML = `<a href="/api/app/update/log">Open the log</a>`;
  }
}

function mb(bytes) {
  return (bytes / (1024 * 1024)).toFixed(1);
}
