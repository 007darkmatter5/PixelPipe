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
  };

  window.addEventListener("keydown", (event) => {
    keys.add(event.key);
    if (["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", " "].includes(event.key)) {
      event.preventDefault();
    }
  });
  window.addEventListener("keyup", (event) => keys.delete(event.key));

  function hud(text) {
    const el = document.getElementById("pp-hud");
    if (el && text) el.textContent = text;
  }

  function maskFromInput() {
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
      const response = await fetch(`${state.engine}/api/session/stats`);
      if (!response.ok || !state.pc) return;
      const stats = await response.json();
      let line = `${stats.encoder || "h264"} ${stats.width}x${stats.height} ${Number(stats.fps).toFixed(1)} fps`;
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
    if (state.pc) {
      bindStream();
      return;
    }
    try {
      const resolved = resolveStart(options, engineArg);
      state.engine = resolved.engine;
      state.rom = resolved.rom;
      hud(`starting ${state.rom}\n${state.engine}`);
      const started = await fetch(`${state.engine}/api/session`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ rom: state.rom }),
      });
      if (!started.ok) {
        hud(`${started.status} ${state.engine}/api/session\n${await started.text()}`);
        state.pc = null;
        return;
      }
    } catch (err) {
      hud(String(err));
      state.pc = null;
      return;
    }
    const pc = new RTCPeerConnection({ iceServers: [] });
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
    const offerRes = await fetch(`${state.engine}/api/session/offer`, { method: "POST" });
    if (!offerRes.ok) {
      hud(await offerRes.text());
      return;
    }
    await pc.setRemoteDescription(await offerRes.json());
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    await waitIce(pc);
    const answered = await fetch(`${state.engine}/api/session/answer`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: pc.localDescription.type, sdp: pc.localDescription.sdp }),
    });
    if (!answered.ok) {
      hud(await answered.text());
      return;
    }
    document.getElementById("pp-stage")?.focus();
    state.statsTimer = setInterval(refreshStats, 1000);
    hud("connecting");
  }

  async function saveState() {
    if (!state.engine) return;
    await fetch(`${state.engine}/api/session/savestate`, { method: "POST" });
  }

  async function loadState() {
    if (!state.engine) return;
    await fetch(`${state.engine}/api/session/loadstate`, { method: "POST" });
  }

  window.pixelpipePlayer = { start, saveState, loadState };
})();
