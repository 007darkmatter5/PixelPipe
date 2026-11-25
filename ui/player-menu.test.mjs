import assert from "node:assert/strict";
import test from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const menu = require("./PixelPipe.Web/wwwroot/js/player-menu.js");

function buttons(indexes) {
  const list = [];
  for (const index of indexes) list[index] = { pressed: true, value: 1 };
  return list;
}

test("the menu lists resume, pause, slots, reset, fullscreen, and quit", () => {
  const ids = menu.mainItems(false).map((item) => item.id);
  assert.deepEqual(ids, ["resume", "pause", "save", "load", "reset", "fullscreen", "quit"]);
  assert.equal(menu.mainItems(true)[1].label, "Paused");
  assert.deepEqual(menu.slotItems().map((item) => item.id), ["1", "2", "3", "4", "5", "back"]);
});

test("the guide button opens immediately and select+start needs a hold", () => {
  assert.equal(menu.hotkey(buttons([16]), 0), "guide");
  assert.equal(menu.hotkey(buttons([8, 9]), menu.HOLD_MS - 1), "");
  assert.equal(menu.hotkey(buttons([8, 9]), menu.HOLD_MS), "chord");
  assert.equal(menu.hotkey(buttons([8]), menu.HOLD_MS), "");
  assert.equal(menu.hotkey(buttons([9]), menu.HOLD_MS), "");
});

test("menu navigation wraps", () => {
  assert.equal(menu.moveIndex(0, -1, 7), 6);
  assert.equal(menu.moveIndex(6, 1, 7), 0);
});

test("pause tells the core to pause and quit saves the auto slot before stopping", () => {
  assert.deepEqual(menu.pauseBody(true), { paused: true });
  assert.deepEqual(menu.pauseBody(false), { paused: false });
  assert.deepEqual(menu.quitPlan(), [
    { method: "POST", path: "/api/session/savestate", body: { slot: "auto" } },
    { method: "POST", path: "/api/session/stop", body: {} },
  ]);
});

test("reset asks before it runs and the default choice is no", () => {
  const items = menu.confirmItems();
  assert.equal(items[0].id, "yes");
  assert.equal(items[1].id, "no");
  assert.equal(menu.itemsFor("reset", false)[1].id, "no");
});
