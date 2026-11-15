// A Worker entry module may only export handlers; the helpers live in bot.js so they can be tested.
import { handleUpdate, safeEqual } from "./bot.js";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === "/health" && request.method === "GET") {
      return new Response("ok");
    }

    if (url.pathname !== "/webhook") {
      return new Response("not found", { status: 404 });
    }

    if (request.method !== "POST") {
      return new Response("method not allowed", { status: 405 });
    }

    if (!env.WEBHOOK_SECRET || !env.ALLOWED_CHAT_ID) {
      return new Response("not configured", { status: 503 });
    }

    const provided = request.headers.get("X-Telegram-Bot-Api-Secret-Token") ?? "";
    if (!(await safeEqual(provided, env.WEBHOOK_SECRET))) {
      return new Response("unauthorized", { status: 401 });
    }

    let update;
    try {
      update = await request.json();
    } catch {
      return new Response("bad request", { status: 400 });
    }

    const reply = await handleUpdate(update, env);
    return Response.json(reply ?? {});
  },
};
