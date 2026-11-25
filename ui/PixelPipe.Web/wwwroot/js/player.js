(() => {
  const keys = new Set();
  const state = {
    pc: null,
    dc: null,
    stream: null,
    timer: 0,
    statsTimer: 0,
    lastPing: 0,
    rtt: null,
    mask: 0,
    engine: "",
    rom: "",
    slot: "1",
    iceServers: [],
    menu: "closed",
    index: 0,
    paused: false,
    started: false,
    stopping: false,
    starting: false,
    chordSince: 0,
    chordFired: false,
    guideWas: false,
    padUp: false,
    padDown: false,
    padAccept: false,
    padBack: false,
    idleTimer: 0,
  };

  const menuApi = () => window.pixelpipeMenu;

  window.addEventListener("keydown", (event) => {
    if (!document.getElementById("pp-stage")) {
      keys.add(event.key);
      return;
    }
    if (event.key === "Escape") {
      event.preventDefault();
      if (state.menu === "closed") openMenu();
      else closeMenu();
      return;
    }
    if (state.menu !== "closed") {
      event.preventDefault();
      if (event.key === "ArrowUp") move(-1);
      else if (event.key === "ArrowDown") move(1);
      else if (event.key === "Enter" || event.key === " ") activate();
      return;
    }
    keys.add(event.key);
    if (["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", " "].includes(event.key)) {
      event.preventDefault();
    }
    wakeToggle();
  });
  window.addEventListener("keyup", (event) => keys.delete(event.key));
  window.addEventListener("pointermove", wakeToggle);
  window.addEventListener("touchstart", wakeToggle, { passive: true });
  window.addEventListener("pagehide", beaconStop);
  requestAnimationFrame(tick);
  document.addEventListener("click", onMenuClick);
  wakeToggle();

  function hud(text) {
    const el = document.getElementById("pp-hud");
    if (el && text) el.textContent = text;
  }

  function onMenuClick(event) {
    const el = event.target instanceof Element ? event.target : null;
    if (!el || !document.getElementById("pp-stage")) return;
    if (el.closest("#pp-menu-toggle")) {
      event.preventDefault();
      toggleMenu();
      return;
    }
    const button = el.closest("#pp-menu button");
    if (!button) return;
    event.preventDefault();
    if (button.getAttribute("data-pp-action") === "close") {
      closeMenu();
      return;
    }
    const buttons = currentButtons();
    const index = buttons.indexOf(button);
    if (index >= 0) state.index = index;
    activate();
  }

  function ensureMenu() {
    wakeToggle();
  }

  function wakeToggle() {
    const toggle = document.getElementById("pp-menu-toggle");
    if (!toggle) return;
    toggle.classList.remove("idle");
    window.clearTimeout(state.idleTimer);
    state.idleTimer = window.setTimeout(() => {
      if (state.menu === "closed") toggle.classList.add("idle");
    }, menuApi()?.IDLE_MS || 2500);
  }

  function currentButtons() {
    const id = state.menu === "save" || state.menu === "load"
      ? "pp-menu-slots"
      : state.menu === "reset"
        ? "pp-menu-reset"
        : "pp-menu-main";
    const page = document.getElementById(id);
    return page ? [...page.querySelectorAll("button")] : [];
  }

  function renderMenu() {
    const overlay = document.getElementById("pp-menu");
    const badge = document.getElementById("pp-paused");
    if (!overlay) return;
    if (badge) badge.hidden = !state.paused;
    overlay.classList.toggle("pp-open", state.menu !== "closed");
    const main = document.getElementById("pp-menu-main");
    const slots = document.getElementById("pp-menu-slots");
    const reset = document.getElementById("pp-menu-reset");
    if (main) main.hidden = state.menu !== "main" && state.menu !== "closed";
    if (slots) slots.hidden = state.menu !== "save" && state.menu !== "load";
    if (reset) reset.hidden = state.menu !== "reset";
    const title = document.getElementById("pp-menu-title");
    if (title) {
      title.textContent =
        state.menu === "save" ? "Save state" :
        state.menu === "load" ? "Load state" :
        state.menu === "reset" ? "Reset this game?" :
        state.paused ? "Paused" : "Menu";
    }
    const pause = document.querySelector("#pp-menu [data-pp-action='pause']");
    if (pause) pause.textContent = state.paused ? "Paused" : "Pause";
    currentButtons().forEach((button, index) => {
      if (index === state.index) button.setAttribute("aria-current", "true");
      else button.removeAttribute("aria-current");
    });
    if (state.menu !== "closed") wakeToggle();
  }

  function openMenu() {
    ensureMenu();
    if (state.menu === "closed") {
      state.menu = "main";
      state.index = 0;
    }
    keys.clear();
    renderMenu();
  }

  function closeMenu() {
    state.menu = "closed";
    state.index = 0;
    renderMenu();
  }

  function toggleMenu() {
    if (state.menu === "closed") openMenu();
    else closeMenu();
  }

  function move(delta) {
    const count = currentButtons().length || 1;
    state.index = menuApi().moveIndex(state.index, delta, count);
    renderMenu();
  }

  async function activate() {
    const button = currentButtons()[state.index];
    if (!button) return;
    const action = button.getAttribute("data-pp-action");
    const slot = button.getAttribute("data-pp-slot");
    if (slot) {
      if (state.menu === "save") await saveState(slot);
      else await loadState(slot);
      closeMenu();
      return;
    }
    if (action === "close") {
      closeMenu();
      return;
    }
    if (action === "back" || action === "reset-no") {
      const returning = state.menu;
      state.menu = "main";
      state.index = returning === "save" ? 2 : returning === "load" ? 3 : 4;
      renderMenu();
      return;
    }
    if (action === "reset-yes") {
      await resetGame();
      state.menu = "main";
      state.index = 4;
      renderMenu();
      return;
    }
    if (action === "resume") {
      await setPaused(false);
      closeMenu();
      return;
    }
    if (action === "pause") {
      await setPaused(!state.paused);
      renderMenu();
      return;
    }
    if (action === "save" || action === "load" || action === "reset") {
      state.menu = action;
      state.index = action === "reset" ? 1 : 0;
      renderMenu();
      return;
    }
    if (action === "fullscreen") {
      toggleFullscreen();
      return;
    }
    if (action === "quit") await quitToLibrary();
  }

  function toggleFullscreen() {
    const stage = document.getElementById("pp-stage");
    if (!stage) return;
    if (document.fullscreenElement) {
      document.exitFullscreen?.();
      return;
    }
    const request = stage.requestFullscreen || stage.webkitRequestFullscreen;
    request?.call(stage);
  }

  function maskFromInput() {
    if (state.menu !== "closed" || state.paused) return 0;
    let mask = 0;
    const down = (key) => keys.has(key);
    if (down("z") || down("Z")) mask |= 1 << 0;
    if (down("x") || down("X")) mask |= 1 << 8;
    if (down("Shift")) mask |= 1 << 2;
    if (down("Enter")) mask |= 1 << 3;
    if (down("ArrowUp") || down("w") || down("W")) mask |= 1 << 4;
    if (down("ArrowDown") || down("s") || down("S")) mask |= 1 << 5;
    if (down("ArrowLeft")) mask |= 1 << 6;
    if (down("ArrowRight") || down("d") || down("D")) mask |= 1 << 7;
    const pads = navigator.getGamepads ? navigator.getGamepads() : [];
    for (const pad of pads) {
      if (!pad) continue;
      const pressed = (index) => pad.buttons[index] && (pad.buttons[index].pressed || pad.buttons[index].value > 0.5);
      if (pressed(1)) mask |= 1 << 0;
      if (pressed(0)) mask |= 1 << 8;
      if (pressed(3)) mask |= 1 << 1;
      if (pressed(2)) mask |= 1 << 9;
      if (pressed(4)) mask |= 1 << 10;
      if (pressed(5)) mask |= 1 << 11;
      if (pressed(8)) mask |= 1 << 2;
      if (pressed(9)) mask |= 1 << 3;
      if (pressed(12) || pad.axes[1] < -0.5) mask |= 1 << 4;
      if (pressed(13) || pad.axes[1] > 0.5) mask |= 1 << 5;
      if (pressed(14) || pad.axes[0] < -0.5) mask |= 1 << 6;
      if (pressed(15) || pad.axes[0] > 0.5) mask |= 1 << 7;
    }
    return mask;
  }

  function bindStream() {
    const video = document.getElementById("pp-video");
    if (video && state.stream && video.srcObject !== state.stream) {
      video.srcObject = state.stream;
      video.play().catch(() => {});
    }
  }

  function sendInput() {
    if (!state.dc || state.dc.readyState !== "open") return;
    state.mask = maskFromInput();
    const message = { mask: state.mask };
    const now = performance.now();
    if (now - state.lastPing > 500) {
      message.ping = now;
      state.lastPing = now;
    }
    state.dc.send(JSON.stringify(message));
  }

  function tick() {
    pollGamepad();
    requestAnimationFrame(tick);
  }

  function pollGamepad() {
    if (!document.getElementById("pp-stage") || !navigator.getGamepads) return;
    let pad = null;
    for (const candidate of navigator.getGamepads()) {
      if (candidate) pad = candidate;
    }
    if (!pad) {
      state.chordSince = 0;
      state.guideWas = false;
      return;
    }
    const pressed = (index) => pad.buttons[index] && (pad.buttons[index].pressed || pad.buttons[index].value > 0.5);
    const now = performance.now();
    if (pressed(8) && pressed(9)) {
      if (!state.chordSince) state.chordSince = now;
    } else {
      state.chordSince = 0;
    }
    const held = state.chordSince ? now - state.chordSince : 0;
    const kind = menuApi().hotkey(pad.buttons, held);
    if (kind === "guide" && !state.guideWas) toggleMenu();
    if (kind === "chord" && !state.chordFired) {
      openMenu();
      state.chordFired = true;
    }
    state.guideWas = kind === "guide";
    if (kind !== "chord") state.chordFired = false;
    if (kind) wakeToggle();
    if (state.menu === "closed") return;
    const up = pressed(12) || pad.axes[1] < -0.5;
    const down = pressed(13) || pad.axes[1] > 0.5;
    const accept = pressed(0);
    const back = pressed(1);
    if (up && !state.padUp) move(-1);
    if (down && !state.padDown) move(1);
    if (accept && !state.padAccept) activate();
    if (back && !state.padBack) {
      if (state.menu === "main") closeMenu();
      else {
        state.menu = "main";
        renderMenu();
      }
    }
    state.padUp = up;
    state.padDown = down;
    state.padAccept = accept;
    state.padBack = back;
  }

  async function waitIce(pc) {
    if (pc.iceGatheringState === "complete") return;
    await new Promise((resolve) => {
      const timer = setTimeout(resolve, 2000);
      pc.addEventListener("icegatheringstatechange", () => {
        if (pc.iceGatheringState === "complete") {
          clearTimeout(timer);
          resolve();
        }
      });
    });
  }

  async function refreshStats() {
    bindStream();
    try {
      const response = await fetch("/api/session/stats", { credentials: "same-origin" });
      if (!response.ok || !state.pc) return;
      const stats = await response.json();
      let line = `${stats.encoder || "h264"} ${stats.width}x${stats.height} ${Number(stats.fps).toFixed(1)} fps`;
      if (stats.paused || state.paused) line += " · paused";
      const reports = await state.pc.getStats();
      reports.forEach((report) => {
        if (report.type === "inbound-rtp" && report.kind === "video" && report.framesDecoded) {
          const decode = (report.totalDecodeTime / report.framesDecoded) * 1000;
          const jitter = report.jitterBufferEmittedCount
            ? (report.jitterBufferDelay / report.jitterBufferEmittedCount) * 1000
            : 0;
          const rtt = state.rtt || 0;
          const estimate = stats.encode_us_avg / 1000 + rtt / 2 + jitter + decode;
          line += `\nencode ${(stats.encode_us_avg / 1000).toFixed(1)} ms · rtt ${rtt.toFixed(1)} ms · jitter ${jitter.toFixed(1)} ms · decode ${decode.toFixed(1)} ms\nest. ${estimate.toFixed(1)} ms · mask 0x${state.mask.toString(16)}`;
        }
      });
      hud(line);
    } catch {
      /* stats are best-effort */
    }
  }

  function resolveStart(options, engineArg) {
    let rom = "";
    let engine = "";
    if (options && typeof options === "object") {
      rom = options.rom || options.Rom || "";
      engine = options.engine || options.Engine || "";
      state.slot = options.slot || options.Slot || "1";
    } else {
      rom = options || "";
      engine = engineArg || "";
    }
    engine = String(engine).replace(/\/$/, "");
    if (!/^https?:\/\//.test(engine)) {
      engine = `${location.protocol}//${location.hostname}:9090`;
    }
    return { rom: String(rom), engine };
  }

  async function start(options, engineArg) {
    if (state.pc || state.starting) {
      bindStream();
      return;
    }
    state.starting = true;
    try {
      const resolved = resolveStart(options, engineArg);
      state.engine = resolved.engine;
      state.rom = resolved.rom;
      hud(`starting ${state.rom}`);
      const started = await fetch("/api/session", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ rom: state.rom, slot: state.slot || "1" }),
      });
      if (!started.ok) {
        hud(`${started.status} /api/session\n${await started.text()}`);
        state.starting = false;
        return;
      }
      const startedBody = await started.json();
      state.iceServers = startedBody.iceServers || startedBody.IceServers || [];
      state.started = true;
      state.stopping = false;
      ensureMenu();
    } catch (err) {
      hud(String(err));
      state.starting = false;
      return;
    }
    const pc = new RTCPeerConnection({ iceServers: state.iceServers || [] });
    state.pc = pc;
    state.stream = new MediaStream();
    pc.ontrack = (event) => {
      state.stream.addTrack(event.track);
      bindStream();
      try {
        if ("playoutDelayHint" in event.receiver) event.receiver.playoutDelayHint = 0;
        if ("jitterBufferTarget" in event.receiver) event.receiver.jitterBufferTarget = 0;
      } catch {
        /* older browsers */
      }
    };
    pc.ondatachannel = (event) => {
      state.dc = event.channel;
      state.dc.onopen = () => {
        state.timer = setInterval(sendInput, 16);
      };
      state.dc.onmessage = (event) => {
        try {
          const message = JSON.parse(event.data);
          if (typeof message.pong === "number") state.rtt = performance.now() - message.pong;
        } catch {
          /* ignore */
        }
      };
    };
    const offerRes = await fetch("/api/session/offer", { method: "POST", credentials: "same-origin" });
    if (!offerRes.ok) {
      hud(await offerRes.text());
      state.starting = false;
      return;
    }
    await pc.setRemoteDescription(await offerRes.json());
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    await waitIce(pc);
    const answered = await fetch("/api/session/answer", {
      credentials: "same-origin",
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: pc.localDescription.type, sdp: pc.localDescription.sdp }),
    });
    if (!answered.ok) {
      hud(await answered.text());
      state.starting = false;
      return;
    }
    document.getElementById("pp-stage")?.focus();
    state.statsTimer = setInterval(refreshStats, 1000);
    state.starting = false;
    hud("connecting");
  }

  async function postJson(path, body) {
    return fetch(path, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body ?? {}),
      keepalive: true,
    });
  }

  async function saveState(slot) {
    await postJson("/api/session/savestate", { slot: slot || state.slot || "1" });
  }

  async function loadState(slot) {
    await postJson("/api/session/loadstate", { slot: slot || state.slot || "1" });
  }

  async function setPaused(paused) {
    const response = await postJson("/api/session/pause", menuApi().pauseBody(paused));
    if (!response.ok) return;
    state.paused = paused;
    const video = document.getElementById("pp-video");
    if (video) {
      if (paused) video.pause();
      else video.play().catch(() => {});
    }
    const badge = document.getElementById("pp-paused");
    if (badge) badge.hidden = !paused;
  }

  async function resetGame() {
    await postJson("/api/session/reset", {});
    if (state.paused) await setPaused(false);
  }

  function teardown() {
    window.clearInterval(state.timer);
    window.clearInterval(state.statsTimer);
    state.timer = 0;
    state.statsTimer = 0;
    try { state.dc?.close(); } catch { /* already closed */ }
    try { state.pc?.close(); } catch { /* already closed */ }
    state.dc = null;
    state.pc = null;
    state.stream = null;
    const video = document.getElementById("pp-video");
    if (video) video.srcObject = null;
    state.started = false;
    state.starting = false;
    state.paused = false;
    closeMenu();
  }

  async function runQuitPlan() {
    for (const step of menuApi().quitPlan()) {
      try {
        await postJson(step.path, step.body);
      } catch {
        /* the engine watchdog still saves if the page is already gone */
      }
    }
  }

  async function stop() {
    if (!state.started || state.stopping) return;
    state.stopping = true;
    await runQuitPlan();
    teardown();
  }

  function beaconStop() {
    if (!state.started || state.stopping) return;
    state.stopping = true;
    for (const step of menuApi().quitPlan()) {
      try {
        navigator.sendBeacon(step.path, new Blob([JSON.stringify(step.body)], { type: "application/json" }));
      } catch {
        /* the engine ends the session after the connection drops */
      }
    }
    teardown();
  }

  async function quitToLibrary() {
    await stop();
    location.assign("/");
  }

  async function togglePause() {
    await setPaused(!state.paused);
    renderMenu();
  }

  window.pixelpipePlayer = { start, saveState, loadState, stop, openMenu, quit: quitToLibrary, togglePause };
})();
