let connectionTimer;
let durationElement;
let connectedAt;

/** Starts or refreshes the duration display from the rendered connection completion time. */
export function updateConnectionTimer() {
  const element = document.querySelector('[data-testid="device-duration"]');
  const timestamp = element?.dataset.connectedAt;
  if (element === durationElement && timestamp === connectedAt) return;
  disposeConnectionTimer();
  if (!element) return;
  const started = Date.parse(timestamp);
  if (!Number.isFinite(started)) {
    element.textContent = "Unknown";
    return;
  }
  durationElement = element;
  connectedAt = timestamp;
  const update = () => {
    if (!element.isConnected || element.dataset.connectedAt !== timestamp) {
      disposeConnectionTimer();
      return;
    }
    const seconds = Math.max(0, Math.floor((Date.now() - started) / 1000));
    element.textContent = [Math.floor(seconds / 3600), Math.floor(seconds / 60) % 60, seconds % 60]
      .map((value) => String(value).padStart(2, "0"))
      .join(":");
  };
  update();
  connectionTimer = setInterval(update, 1000);
}

/** Releases the duration interval when the page or connection is no longer active. */
export function disposeConnectionTimer() {
  clearInterval(connectionTimer);
  connectionTimer = undefined;
  durationElement = undefined;
  connectedAt = undefined;
}

const attachmentLengthKey = "ossm-admin-attachment-length-cm";

/**
 * Reads the illustrative attachment length, in centimetres.
 * @param {number} fallback value used when nothing valid is stored.
 * @returns {number} the stored length in centimetres, or the fallback.
 */
export function readAttachmentLength(fallback) {
  try {
    const stored = Number.parseFloat(localStorage.getItem(attachmentLengthKey) ?? "");
    return Number.isFinite(stored) ? stored : fallback;
  } catch {
    return fallback;
  }
}

/** Stores the illustrative attachment length, in centimetres, for this browser. */
export function writeAttachmentLength(value) {
  try {
    localStorage.setItem(attachmentLengthKey, String(value));
  } catch {
    // Storage is unavailable; the setting simply does not persist.
  }
}

export async function copy(text) {  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
