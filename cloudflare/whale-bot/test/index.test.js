import assert from "node:assert/strict";
import { test } from "node:test";
import {
  BUTTON_TEXT,
  NO_DATA_TEXT,
  UNAVAILABLE_TEXT,
  clipForTelegram,
  handleUpdate,
  isStatusRequest,
  safeEqual,
} from "../src/bot.js";
import worker from "../src/index.js";

const SECRET = "test-secret";
const CHAT = "42";

function makeEnv(stored, overrides = {}) {
  return {
    WEBHOOK_SECRET: SECRET,
    ALLOWED_CHAT_ID: CHAT,
    WHALE_KV: { get: async () => stored },
    ...overrides,
  };
}

function webhook(body, headers = { "X-Telegram-Bot-Api-Secret-Token": SECRET }) {
  return new Request("https://example.workers.dev/webhook", {
    method: "POST",
    headers,
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

const message = (text, chatId = 42) => ({ update_id: 1, message: { text, chat: { id: chatId } } });

test("the keyboard button text is recognised", () => {
  assert.equal(isStatusRequest(BUTTON_TEXT), true);
});

test("the Persian text is recognised with or without the zero-width joiner and the emoji", () => {
  assert.equal(isStatusRequest("\u0648\u0636\u0639\u06cc\u062a \u0645\u0639\u0627\u0645\u0644\u0627\u062a \u0646\u0647\u0646\u06af\u0647\u0627"), true);
  assert.equal(isStatusRequest("  " + BUTTON_TEXT + "  "), true);
});

test("commands are recognised, including the bot-name suffix", () => {
  for (const text of ["/start", "/status", "/whales", "/STATUS", "/status@SomeBot", "/status now"]) {
    assert.equal(isStatusRequest(text), true, text);
  }
});

test("other text and non-strings are ignored", () => {
  for (const text of ["hello", "/help", "/statuses", "", undefined, null, 5]) {
    assert.equal(isStatusRequest(text), false, String(text));
  }
});

test("clipForTelegram keeps short text and cuts long text at a line boundary", () => {
  assert.equal(clipForTelegram("short"), "short");
  const line = "x".repeat(99);
  const long = [...Array.from({ length: 100 }, () => line), "<i>updated: 2026-11-14 12:00 UTC</i>"].join("\n");
  const clipped = clipForTelegram(long, 1000);
  assert.ok(clipped.length <= 1000);
  const lines = clipped.split("\n");
  assert.ok(lines.slice(0, -2).every((l) => l === line));
  assert.equal(lines.at(-2), "<i>(table shortened)</i>");
});

test("clipForTelegram keeps the updated-at line when it shortens the table", () => {
  const line = "y".repeat(99);
  const footer = "<i>updated: 2026-11-14 12:00 UTC</i>";
  const clipped = clipForTelegram([...Array.from({ length: 100 }, () => line), footer].join("\n"), 1000);
  assert.ok(clipped.endsWith(footer));
});

test("clipForTelegram copes with a single very long line", () => {
  const clipped = clipForTelegram("z".repeat(5000), 1000);
  assert.ok(clipped.length <= 1000);
  assert.ok(clipped.endsWith("<i>(table shortened)</i>"));
});

test("safeEqual compares values whatever their length", async () => {
  assert.equal(await safeEqual("abc", "abc"), true);
  assert.equal(await safeEqual("abc", "abd"), false);
  assert.equal(await safeEqual("abc", "abcd"), false);
  assert.equal(await safeEqual("", ""), true);
  assert.equal(await safeEqual("", "x"), false);
});

test("handleUpdate replies with the stored table and the keyboard for the owner chat", async () => {
  const reply = await handleUpdate(message(BUTTON_TEXT), makeEnv("<b>table</b>"));
  assert.equal(reply.method, "sendMessage");
  assert.equal(reply.chat_id, 42);
  assert.equal(reply.text, "<b>table</b>");
  assert.equal(reply.parse_mode, "HTML");
  assert.equal(reply.reply_markup.keyboard[0][0].text, BUTTON_TEXT);
});

test("handleUpdate says so when no table has been published", async () => {
  for (const stored of [null, "", "   "]) {
    const reply = await handleUpdate(message("/status"), makeEnv(stored));
    assert.equal(reply.text, NO_DATA_TEXT);
  }
});

test("handleUpdate answers with a retry message when the KV read fails", async () => {
  const env = makeEnv("t", { WHALE_KV: { get: async () => { throw new Error("kv down"); } } });
  const reply = await handleUpdate(message("/status"), env);
  assert.equal(reply.text, UNAVAILABLE_TEXT);
});

test("the webhook answers 200, not 500, when the KV read fails", async () => {
  const env = makeEnv("t", { WHALE_KV: { get: async () => { throw new Error("kv down"); } } });
  const response = await worker.fetch(webhook(message("/status")), env);
  assert.equal(response.status, 200);
  assert.equal((await response.json()).text, UNAVAILABLE_TEXT);
});

test("handleUpdate ignores other chats and updates without a message", async () => {
  assert.equal(await handleUpdate(message("/status", 999), makeEnv("t")), null);
  assert.equal(await handleUpdate({ update_id: 2, edited_message: {} }, makeEnv("t")), null);
  assert.equal(await handleUpdate(message("hello"), makeEnv("t")), null);
});

test("the webhook rejects a missing or wrong secret", async () => {
  const none = await worker.fetch(webhook(message("/status"), {}), makeEnv("t"));
  assert.equal(none.status, 401);
  const wrong = await worker.fetch(webhook(message("/status"), { "X-Telegram-Bot-Api-Secret-Token": "nope" }), makeEnv("t"));
  assert.equal(wrong.status, 401);
});

test("the webhook refuses to run without its configuration", async () => {
  const response = await worker.fetch(webhook(message("/status")), makeEnv("t", { WEBHOOK_SECRET: undefined }));
  assert.equal(response.status, 503);
  const noChat = await worker.fetch(webhook(message("/status")), makeEnv("t", { ALLOWED_CHAT_ID: "" }));
  assert.equal(noChat.status, 503);
});

test("the webhook answers the owner with a sendMessage reply", async () => {
  const response = await worker.fetch(webhook(message("/whales")), makeEnv("<b>table</b>"));
  assert.equal(response.status, 200);
  const body = await response.json();
  assert.equal(body.method, "sendMessage");
  assert.equal(body.text, "<b>table</b>");
});

test("the webhook answers 200 with an empty object for updates it ignores", async () => {
  const response = await worker.fetch(webhook(message("hello")), makeEnv("t"));
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), {});
});

test("the webhook rejects malformed JSON", async () => {
  const response = await worker.fetch(webhook("{not json"), makeEnv("t"));
  assert.equal(response.status, 400);
});

test("routes: health, wrong method and unknown paths", async () => {
  const health = await worker.fetch(new Request("https://example.workers.dev/health"), makeEnv("t"));
  assert.equal(await health.text(), "ok");
  const get = await worker.fetch(new Request("https://example.workers.dev/webhook"), makeEnv("t"));
  assert.equal(get.status, 405);
  const other = await worker.fetch(new Request("https://example.workers.dev/other"), makeEnv("t"));
  assert.equal(other.status, 404);
});
