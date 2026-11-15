// Must match CloudflareKvSettings.DefaultKey in the C# poller, which writes this key.
export const TABLE_KEY = "whale-table";
export const MAX_TEXT_LENGTH = 4000;
export const UNAVAILABLE_TEXT = "The whale table is temporarily unavailable. Try again in a minute.";
export const NO_DATA_TEXT = "No whale data has been published yet. The poller publishes it after each run (about every 30 minutes).";

// "\u{1F4CA} <status of whale trades>" in Persian; the stem is matched so the zero-width joiner does not matter.
export const BUTTON_TEXT = "\u{1F4CA} \u0648\u0636\u0639\u06cc\u062a \u0645\u0639\u0627\u0645\u0644\u0627\u062a \u0646\u0647\u0646\u06af\u200c\u0647\u0627";
const BUTTON_STEM = "\u0648\u0636\u0639\u06cc\u062a \u0645\u0639\u0627\u0645\u0644\u0627\u062a \u0646\u0647\u0646\u06af";
const COMMANDS = new Set(["/start", "/status", "/whales"]);

export function isStatusRequest(text) {
  if (typeof text !== "string") return false;
  const trimmed = text.trim();
  if (trimmed.includes(BUTTON_STEM)) return true;
  const command = trimmed.split(/\s/)[0].split("@")[0].toLowerCase();
  return COMMANDS.has(command);
}

// The last line of the table is its "updated at" time, so it is kept when the body has to be shortened.
export function clipForTelegram(text, limit = MAX_TEXT_LENGTH) {
  if (text.length <= limit) return text;
  const lines = text.trimEnd().split("\n");
  const footer = lines.length > 1 ? lines[lines.length - 1] : "";
  const body = (lines.length > 1 ? lines.slice(0, -1) : lines).join("\n");
  const note = "<i>(table shortened)</i>";
  const budget = limit - footer.length - note.length - 2;
  const cut = body.lastIndexOf("\n", budget);
  const head = body.slice(0, cut > 0 ? cut : Math.max(budget, 0));
  return footer ? `${head}\n${note}\n${footer}` : `${head}\n${note}`;
}

async function sha256(value) {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(String(value))));
}

// Both values are hashed first, so the comparison always runs over 32 bytes whatever the input length.
export async function safeEqual(a, b) {
  const [left, right] = await Promise.all([sha256(a), sha256(b)]);
  let diff = 0;
  for (let i = 0; i < left.length; i++) diff |= left[i] ^ right[i];
  return diff === 0;
}

export async function handleUpdate(update, env) {
  const message = update?.message;
  if (!message || !isStatusRequest(message.text)) return null;
  if (String(message.chat?.id) !== String(env.ALLOWED_CHAT_ID)) return null;

  let text;
  try {
    const stored = await env.WHALE_KV.get(TABLE_KEY);
    text = stored && stored.trim().length > 0 ? clipForTelegram(stored) : NO_DATA_TEXT;
  } catch {
    text = UNAVAILABLE_TEXT;
  }

  return {
    method: "sendMessage",
    chat_id: message.chat.id,
    text,
    parse_mode: "HTML",
    disable_web_page_preview: true,
    reply_markup: {
      keyboard: [[{ text: BUTTON_TEXT }]],
      resize_keyboard: true,
    },
  };
}
