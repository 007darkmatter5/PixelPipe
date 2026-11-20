window.pixelpipeApplyUpdate = async function (confirm) {
  const response = await fetch("/api/app/update/apply", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ confirm: !!confirm }),
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }
  await window.pixelpipeWaitForRestart();
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
      // The installer is replacing this process.
    }
  }
};
