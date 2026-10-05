import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { setImmediate } from "node:timers/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(new URL("../wwwroot/guest-session.js", import.meta.url), "utf8");
const disconnectedKey = "ossm-guest-connection-lost";

class Element extends EventTarget {
  attributes = new Map();
  dataset = {};
  value = "0";
  min = "0";
  max = "100";
  checked = false;
  disabled = false;
  paused = false;
  pauseAnimations() {
    this.paused = true;
  }
  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }
  removeAttribute(name) {
    this.attributes.delete(name);
  }
  toggleAttribute(name, force) {
    force ? this.setAttribute(name, "") : this.removeAttribute(name);
  }
}

async function createGuest(invoke = async () => ({ ok: true }), url = "https://guest.example/join#invite=test-invitation") {
  const elements = new Map();
  const root = new Element();
  root.querySelector = (selector) => {
    if (!elements.has(selector)) elements.set(selector, new Element());
    return elements.get(selector);
  };
  for (const [name, value] of Object.entries({ speed: 30, stroke: 10, depth: 20, sensation: 50 }))
    root.querySelector(`[data-input="${name}"]`).value = String(value);
  const storage = new Map();
  const location = new URL(url);
  const reloads = [];
  location.reload = () => reloads.push(location.href);
  const document = Object.assign(new EventTarget(), {
    visibilityState: "visible",
    documentElement: new Element(),
    querySelector: root.querySelector,
  });
  const window = new EventTarget();
  const calls = [];
  const context = createContext({
    AbortController,
    URLSearchParams,
    document,
    window,
    location,
    history: {
      replaceState: (_state, _unused, path) => {
        location.href = new URL(path, location).href;
      },
    },
    sessionStorage: {
      getItem: (key) => storage.get(key) ?? null,
      setItem: (key, value) => storage.set(key, value),
      removeItem: (key) => storage.delete(key),
    },
  });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected module dependency"));
  await module.evaluate();
  const guest = module.namespace;
  guest.attach(root, {
    invokeMethodAsync: async (method, payload) => {
      calls.push({ method, payload });
      return invoke(method, payload);
    },
  });
  guest.updateState({ joined: true, ready: true });
  await setImmediate();
  return {
    guest,
    root,
    storage,
    location,
    document,
    window,
    reloads,
    element: root.querySelector,
    click: (selector) => root.querySelector(selector).dispatchEvent(new Event("click")),
    calls: (method) => calls.filter((call) => call.method === method),
  };
}

test("all seven pattern selections survive snapshots and reach preview and requests", async () => {
  const browser = await createGuest();
  for (let pattern = 0; pattern <= 6; pattern++) {
    const input = browser.element("[data-pattern]");
    input.value = String(pattern);
    input.dispatchEvent(new Event("change"));
    browser.guest.updateState({ joined: true, ready: true, current: { pattern: 6 - pattern } });
    browser.click("[data-send-button]");
    await setImmediate();
    assert.equal(browser.calls("SubmitSettings").at(-1).payload.pattern, pattern);
    const display = browser.calls("UpdateDisplay").at(-1).payload;
    assert.equal(display.selected.pattern, pattern);
    assert.equal(display.sent.pattern, pattern);
    assert.equal(input.value, String(pattern));
  }
  browser.guest.dispose();
});

test("speed is sent unchanged and retains the selected speed", async () => {
  const browser = await createGuest();
  for (const [speed, stroke, depth] of [
    [50, 10, 10],
    [50, 80, 100],
    [50, 10, 80],
    [53, 10, 20],
    [100, 100, 100],
    [0, 10, 20],
    [30, 1, 1],
    [100, 1, 1],
    [50, 0, 20],
    [50, 0, 0],
  ]) {
    for (const [name, value] of Object.entries({ depth, stroke, speed })) {
      const input = browser.element(`[data-input="${name}"]`);
      input.value = String(value);
      input.dispatchEvent(new Event("input"));
      assert.equal(browser.element(`[data-value="${name}"]`).textContent, value);
    }
    const count = browser.calls("SubmitSettings").length;
    browser.click("[data-send-button]");
    await setImmediate();
    const command = browser.calls("SubmitSettings").at(-1).payload;
    assert.deepEqual([command.speed, command.stroke, command.depth], [speed, stroke, depth]);
    const display = browser.calls("UpdateDisplay").at(-1).payload;
    assert.equal(display.selected.speed, speed);
    assert.equal(display.sent.speed, speed);
    assert.equal(browser.element('[data-sent="speed"]').textContent, speed);
    assert.equal(browser.element('[data-sent-marker="speed"]').attributes.get("y1"), String(100 - speed));
    browser.click("[data-send-button]");
    await setImmediate();
    assert.equal(browser.calls("SubmitSettings").length, count + 1);
  }
  browser.guest.dispose();
});

test("Reset Position preserves selection, discards stale sends, and allows independent Stop", async () => {
  let completeRequest;
  let completeReset;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings") return new Promise((resolve) => { completeRequest = resolve; });
    if (method === "ResetPosition") return new Promise((resolve) => { completeReset = resolve; });
    return { ok: true };
  });
  const auto = browser.element("[data-auto-send]");
  auto.checked = true;
  auto.dispatchEvent(new Event("change"));
  await setImmediate();
  browser.element('[data-input="speed"]').value = "40";
  browser.element('[data-input="speed"]').dispatchEvent(new Event("input"));
  browser.click("[data-reset-position-button]");
  await setImmediate();
  assert.equal(browser.calls("ResetPosition").length, 1);
  assert.equal(auto.checked, false);
  assert.equal(browser.element('[data-input="speed"]').value, "40");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  completeRequest({ ok: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent, null);
  browser.click("[data-stop-button]");
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 1);
  completeReset({ ok: false, code: "cancelled" });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.error, null, "late reset cancellation cannot replace successful Stop");
  assert.equal(browser.element("[data-send-button]").disabled, false, "successful concurrent Stop must not require a second Stop");
  browser.guest.dispose();
});

test("Reset Position completion and remote reset keep preview paused until deliberate Send", async () => {
  const browser = await createGuest();
  browser.click("[data-reset-position-button]");
  await setImmediate();
  assert.equal(browser.calls("ResetPosition").length, 1);
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  browser.guest.updateState({ joined: true, ready: true, stopped: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 0);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, false);
  browser.guest.updateState({ joined: true, ready: true, resettingPosition: true });
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, true);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  browser.guest.updateState({ joined: true, ready: true, stopped: true });
  await setImmediate();
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  assert.equal(browser.calls("SubmitSettings").length, 1);
  browser.guest.updateState({ joined: true, ready: false });
  browser.click("[data-reset-position-button]");
  await setImmediate();
  assert.equal(browser.calls("ResetPosition").length, 1, "an unapproved guest cannot reset position");
  browser.guest.dispose();
});

test("positive device recovery resumes the selected preview without sending settings", async () => {
  const browser = await createGuest();
  browser.guest.updateState({ joined: true, ready: false });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, true);
  assert.equal(browser.element('[data-input="speed"]').value, "0");

  browser.guest.updateState({ joined: true, ready: true });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.previewStopped, false);
  assert.equal(browser.calls("SubmitSettings").length, 0);
  assert.equal(browser.element("[data-send-button]").disabled, false);
  browser.guest.dispose();
});

test("stale settings timeout cannot erase reset selection or hide reset failure", async () => {
  let completeRequest;
  let completeReset;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings") return new Promise((resolve) => { completeRequest = resolve; });
    if (method === "ResetPosition") return new Promise((resolve) => { completeReset = resolve; });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-reset-position-button]");
  completeRequest({ ok: false, code: "outcome-unknown" });
  await setImmediate();
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent, null);
  completeReset({ ok: false, code: "device-io-failed" });
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.error, "Reset Position failed (device-io-failed). Use Stop before sending again.");
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "a failed reset stays paused until Stop succeeds");
  browser.click("[data-stop-button]");
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, false);
  browser.guest.dispose();
});

test("preview display skips intermediate slider edits while an update is in flight", async () => {
  let completeDisplay;
  const browser = await createGuest(async (method) => {
    if (method === "UpdateDisplay")
      return new Promise((resolve) => {
        completeDisplay = resolve;
      });
    return { ok: true };
  });
  const input = browser.element('[data-input="depth"]');
  for (let depth = 21; depth <= 80; depth++) {
    input.value = String(depth);
    input.dispatchEvent(new Event("input"));
  }
  assert.equal(browser.element('[data-value="depth"]').textContent, 80);
  assert.equal(browser.calls("UpdateDisplay").length, 1);
  completeDisplay();
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").length, 2);
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.selected.depth, 80, "the preview must receive the latest selection, not replay depth 21");
  assert.equal(browser.calls("SubmitSettings").length, 0, "preview edits must not send device commands");
  input.value = "81";
  input.dispatchEvent(new Event("input"));
  browser.guest.dispose();
  completeDisplay();
  await setImmediate();
  assert.equal(browser.calls("UpdateDisplay").length, 2, "disposal must discard pending display work");
});

test("pattern and sensation supersession keeps the latest preview after a snapshot", async () => {
  let completeDisplay;
  const browser = await createGuest(async (method) => method === "UpdateDisplay"
    ? new Promise((resolve) => { completeDisplay = resolve; }) : { ok: true });
  for (let pattern = 0; pattern <= 6; pattern++) {
    browser.element("[data-pattern]").value = String(pattern);
    browser.element("[data-pattern]").dispatchEvent(new Event("change"));
    browser.element('[data-input="sensation"]').value = String(pattern * 10);
    browser.element('[data-input="sensation"]').dispatchEvent(new Event("input"));
  }
  browser.guest.updateState({ joined: true, ready: true, current: { pattern: 0, sensation: 0 } });
  assert.equal(browser.calls("UpdateDisplay").length, 1);
  completeDisplay();
  await setImmediate();
  const selected = browser.calls("UpdateDisplay").at(-1).payload.selected;
  assert.deepEqual([selected.pattern, selected.sensation], [6, 60]);
  assert.equal(browser.calls("UpdateDisplay").length, 2);
  assert.equal(browser.calls("SubmitSettings").length, 0);
  browser.guest.dispose();
  completeDisplay();
});

test("unknown outcome pauses queued and new sends until an acknowledged recovery Stop", async () => {
  let completeRequest;
  let completeStop;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeRequest = resolve;
      });
    if (method === "StopRun")
      return new Promise((resolve) => {
        completeStop = resolve;
      });
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.element('[data-input="speed"]').value = "40";
  browser.click("[data-send-button]");
  completeRequest({ ok: false, code: "outcome-unknown" });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "queued work must not follow an unknown outcome");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  assert.match(browser.calls("UpdateDisplay").at(-1).payload.error, /may have|unknown/i);
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "polling must not clear uncertainty");
  browser.click("[data-stop-button]");
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "a pending Stop acknowledgement must block sends even when polling says ready");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  completeStop({ ok: false, code: "outcome-unknown" });
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, true, "unacknowledged Stop cannot recover");
  browser.click("[data-stop-button]");
  completeStop({ ok: true, code: "accepted" });
  await setImmediate();
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  completeRequest({ ok: true });
  await setImmediate();
  browser.guest.dispose();
});

test("identical explicit Send is allowed after Stop and re-enable", async () => {
  const browser = await createGuest();
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-stop-button]");
  await setImmediate();
  browser.guest.updateState({ joined: true, ready: false });
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  browser.guest.dispose();
});

test("control revocation cancels queued auto sends and regrant requires deliberate request", async () => {
  let completeRequest;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeRequest = resolve;
      });
    return { ok: true };
  });
  const autoSend = browser.element("[data-auto-send]");
  autoSend.checked = true;
  autoSend.dispatchEvent(new Event("change"));
  await setImmediate();
  browser.element('[data-input="speed"]').value = "40";
  browser.element('[data-input="speed"]').dispatchEvent(new Event("input"));
  browser.guest.updateState({ joined: true, ready: false });
  assert.equal(autoSend.checked, false);
  assert.equal(browser.element('[data-input="speed"]').value, "0");
  assert.equal(browser.element("[data-send-button]").disabled, true);
  completeRequest({ ok: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "revocation must discard already queued settings");
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent, null, "the late reply must not restore sent settings");
  browser.guest.updateState({ joined: true, ready: true });
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1, "regrant must not automatically send");
  assert.equal(autoSend.checked, false);
  browser.element('[data-input="speed"]').value = "30";
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  completeRequest({ ok: true });
  await setImmediate();
  browser.guest.dispose();
});

test("focus loss and a hidden tab retain motion and selection until page exit", async () => {
  const browser = await createGuest();
  browser.click("[data-send-button]");
  await setImmediate();
  browser.window.dispatchEvent(new Event("blur"));
  browser.document.visibilityState = "hidden";
  browser.document.dispatchEvent(new Event("visibilitychange"));
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 0);
  assert.equal(browser.element('[data-input="speed"]').value, "30");
  assert.equal(browser.calls("UpdateDisplay").at(-1).payload.sent.speed, 30);
  browser.window.dispatchEvent(new Event("pagehide"));
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 1);
  assert.equal(browser.element('[data-input="speed"]').value, "0");
  browser.guest.dispose();
});

test("late request reply cannot restore the duplicate baseline after Stop", async () => {
  let completeRequest;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((resolve) => {
        completeRequest = resolve;
      });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-stop-button]");
  await setImmediate();
  assert.equal(browser.calls("StopRun").length, 1, "Stop must bypass the pending settings request");
  completeRequest({ ok: true });
  await setImmediate();
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  completeRequest({ ok: true });
  await setImmediate();
  browser.guest.dispose();
});

test("known rejection remains retryable and successful duplicate is suppressed", async () => {
  let accepted = false;
  const browser = await createGuest(async () => ({ ok: accepted, code: "invalid-input" }));
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.element("[data-send-button]").disabled, false);
  accepted = true;
  browser.click("[data-send-button]");
  await setImmediate();
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 2);
  browser.guest.dispose();
});

test("interop failure persistently disconnects and later state cannot enable sends", async () => {
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings") throw new Error("Disconnected");
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.storage.get(disconnectedKey), "true");
  assert.equal(browser.document.documentElement.dataset.guestConnectionLost, "true");
  assert.equal(browser.element(".preview-rail").paused, true);
  assert.equal(browser.element('[data-input="speed"]').value, "0");
  browser.guest.updateState({ joined: true, ready: true });
  browser.click("[data-send-button]");
  await setImmediate();
  assert.equal(browser.calls("SubmitSettings").length, 1);
  assert.equal(browser.element("[data-send-button]").disabled, true);
  browser.guest.dispose();
});

for (const event of ["popstate", "hashchange"]) {
  test(`${event} with a reopened invitation reloads before Blazor observes the URI`, async () => {
    const browser = await createGuest();
    assert.equal(browser.guest.takeInvitation(), "test-invitation");
    assert.equal(browser.location.hash, "");
    browser.location.hash = "#invite=reopened-invitation";
    const observed = [];
    browser.window.addEventListener(event, () => observed.push(browser.location.href));
    browser.window.dispatchEvent(new Event(event));
    assert.equal(observed.length, 0, "Blazor must not observe the capability URI");
    assert.equal(browser.reloads.length, 1);
    const reopened = await createGuest(undefined, browser.reloads[0]);
    assert.equal(reopened.location.hash, "");
    assert.equal(reopened.guest.takeInvitation(), "reopened-invitation");
    assert.equal(reopened.guest.takeInvitation(), null);
    browser.guest.dispose();
    reopened.guest.dispose();
  });
}

test("malformed invitation fragments are scrubbed and never redeemed", async () => {
  for (const fragment of ["invite=", "invite=one&invite=two", "invite=one&extra=two", `invite=${"a".repeat(129)}`, "other=value"]) {
    const browser = await createGuest(undefined, `https://guest.example/join#${fragment}`);
    assert.equal(browser.location.hash, "");
    assert.equal(browser.guest.takeInvitation(), null);
    browser.guest.dispose();
  }
});

test("a disposed controller's late failure cannot disconnect its replacement", async () => {
  let failRequest;
  const browser = await createGuest(async (method) => {
    if (method === "SubmitSettings")
      return new Promise((_resolve, reject) => {
        failRequest = reject;
      });
    return { ok: true };
  });
  browser.click("[data-send-button]");
  await setImmediate();
  browser.guest.attach(browser.root, { invokeMethodAsync: async () => ({ ok: true }) });
  browser.guest.updateState({ joined: true, ready: true });
  failRequest(new Error("Old reference disposed"));
  await setImmediate();
  assert.equal(browser.storage.get(disconnectedKey), undefined);
  assert.equal(browser.element("[data-send-button]").disabled, false);
  browser.guest.dispose();
});
