/**
 * Typed client for the C# side (see Services/Bridge.cs).
 *
 * Requests are {id, method, payload} and get back {id, ok, result|error}. C# can also
 * push {event, payload} messages, which is how live champ select updates will arrive.
 *
 * When the page runs outside WebView2 - `npm run dev` in a plain browser - `isHosted`
 * is false and every call rejects. Views fall back to mock data so the UI is still
 * workable while styling.
 */

interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: "message", handler: (e: { data: unknown }) => void): void;
}

interface ReplyEnvelope {
  id?: string;
  ok?: boolean;
  result?: unknown;
  error?: string;
  event?: string;
  payload?: unknown;
}

const host: WebViewHost | undefined = (
  window as unknown as { chrome?: { webview?: WebViewHost } }
).chrome?.webview;

export const isHosted = host !== undefined;

type Pending = { resolve: (value: unknown) => void; reject: (reason: Error) => void };

const pending = new Map<string, Pending>();
const listeners = new Map<string, Set<(payload: unknown) => void>>();
let sequence = 0;

host?.addEventListener("message", (e) => {
  const msg = e.data as ReplyEnvelope | null;
  if (!msg || typeof msg !== "object") return;

  if (typeof msg.event === "string") {
    listeners.get(msg.event)?.forEach((fn) => fn(msg.payload));
    return;
  }

  if (typeof msg.id !== "string") return;
  const waiter = pending.get(msg.id);
  if (!waiter) return;
  pending.delete(msg.id);

  if (msg.ok) waiter.resolve(msg.result);
  else waiter.reject(new Error(msg.error ?? "Bridge call failed"));
});

/** Call a C# method. Rejects if the host is absent or the method throws. */
export function call<T>(method: string, payload?: Record<string, unknown>): Promise<T> {
  if (!host) {
    return Promise.reject(new Error(`Not running inside Counterpick (method '${method}')`));
  }
  const id = `r${++sequence}`;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
    host.postMessage({ id, method, payload: payload ?? {} });
  });
}

/** Subscribe to a pushed event. Returns an unsubscribe function. */
export function on(event: string, handler: (payload: unknown) => void): () => void {
  let set = listeners.get(event);
  if (!set) {
    set = new Set();
    listeners.set(event, set);
  }
  set.add(handler);
  return () => set.delete(handler);
}

/**
 * Call the host, falling back to a local value when the host is missing or errors.
 * Lets a view be written once and work both in the app and in `vite dev`.
 */
export async function callOr<T>(
  method: string,
  fallback: T,
  payload?: Record<string, unknown>,
): Promise<T> {
  try {
    return await call<T>(method, payload);
  } catch {
    return fallback;
  }
}
