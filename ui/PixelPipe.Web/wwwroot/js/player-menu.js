// Menu decisions shared by the player page and the node tests.
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.pixelpipeMenu = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  const HOLD_MS = 600;
  const IDLE_MS = 2500;

  function pressed(buttons, index) {
    const button = buttons && buttons[index];
    if (!button) return false;
    if (typeof button === "boolean") return button;
    return !!(button.pressed || button.value > 0.5);
  }

  // "guide" is the Home button. "chord" is Select+Start held.
  function hotkey(buttons, heldMs) {
    if (pressed(buttons, 16)) return "guide";
    if (pressed(buttons, 8) && pressed(buttons, 9) && heldMs >= HOLD_MS) return "chord";
    return "";
  }

  function mainItems(paused) {
    return [
      { id: "resume", label: "Resume" },
      { id: "pause", label: paused ? "Paused" : "Pause" },
      { id: "save", label: "Save state" },
      { id: "load", label: "Load state" },
      { id: "reset", label: "Reset game" },
      { id: "fullscreen", label: "Fullscreen" },
      { id: "quit", label: "Quit to library" },
    ];
  }

  function slotItems() {
    return ["1", "2", "3", "4", "5"]
      .map((slot) => ({ id: slot, label: "Slot " + slot }))
      .concat([{ id: "back", label: "Back" }]);
  }

  function confirmItems() {
    return [
      { id: "yes", label: "Reset game" },
      { id: "no", label: "Don't reset" },
    ];
  }

  function itemsFor(view, paused) {
    if (view === "save" || view === "load") return slotItems();
    if (view === "reset") return confirmItems();
    return mainItems(paused);
  }

  function moveIndex(index, delta, count) {
    const size = count || 1;
    return (index + delta + size) % size;
  }

  function pauseBody(paused) {
    return { paused: !!paused };
  }

  // Quit writes the auto slot first, then stops the engine session.
  function quitPlan() {
    return [
      { method: "POST", path: "/api/session/savestate", body: { slot: "auto" } },
      { method: "POST", path: "/api/session/stop", body: {} },
    ];
  }

  return {
    HOLD_MS,
    IDLE_MS,
    hotkey,
    mainItems,
    slotItems,
    confirmItems,
    itemsFor,
    moveIndex,
    pauseBody,
    quitPlan,
  };
});
