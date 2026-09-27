/**
 * Tauri.Plugin.DotNet Bridge Runtime
 *
 * Typed RPC between the frontend and .NET services through the
 * `tauri-plugin-dotnet` Tauri plugin.
 *
 * This file is copied here by the Tauri.Plugin.DotNet build and replaced whenever the package's
 * copy changes, so do not edit it.
 *
 * Protocol:
 *   call:    invoke("plugin:dotnet|call", { callId, method, args })
 *            resolves with the .NET return value, or rejects with { message, type }
 *   cancel:  invoke("plugin:dotnet|cancel", { callId })
 *   event:   the plugin emits "dotnet:event" with payload { event: "name", data: ... }
 *   .NET -> frontend call: the reserved event "$frontend" { id, service, method, args },
 *            answered by the call "$frontend.Reply" (see registerFrontend)
 */

import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

const CALL_COMMAND = "plugin:dotnet|call";
const CANCEL_COMMAND = "plugin:dotnet|cancel";
const EVENT_CHANNEL = "dotnet:event";

interface BridgeEventMessage {
  event: string;
  data?: unknown;
}

interface PendingCall {
  reject: (error: Error) => void;
  timer?: ReturnType<typeof setTimeout>;
  method: string;
}

/** Options that can be passed to individual calls. */
export interface CallOptions {
  /** Timeout in milliseconds. Overrides the default timeout for this call.
   *  Set to 0 to disable timeout for this specific call. */
  timeoutMs?: number;
}

/** Map of in-flight calls, keyed by callId. */
const pending = new Map<string, PendingCall>();

let callCounter = 0;

/**
 * Default timeout for bridge calls in milliseconds.
 * Calls that don't complete within this time will be rejected
 * with a BridgeTimeoutError and a cancel request is sent to .NET.
 * Set to 0 to disable (not recommended).
 * @default 30000 (30 seconds)
 */
let defaultTimeoutMs = 30_000;

/**
 * Configure the default timeout for all bridge calls.
 * @param ms - Timeout in milliseconds. 0 disables timeouts (not recommended).
 */
export function setDefaultTimeout(ms: number): void {
  defaultTimeoutMs = ms;
}

/** Returns the current default timeout in milliseconds. */
export function getDefaultTimeout(): number {
  return defaultTimeoutMs;
}

function generateCallId(): string {
  return `c_${++callCounter}_${Date.now()}`;
}

/**
 * Send a cancel request to .NET for an in-flight call.
 * This triggers CancellationToken cancellation on the C# side.
 */
function sendCancel(callId: string): void {
  invoke(CANCEL_COMMAND, { callId }).catch(() => {
    // The call may already have completed; nothing to cancel.
  });
}

/**
 * Custom error class for bridge call timeouts.
 */
export class BridgeTimeoutError extends Error {
  public readonly callId: string;
  public readonly method: string;

  constructor(callId: string, method: string, timeoutMs: number) {
    super(
      `Bridge call '${method}' (${callId}) timed out after ${timeoutMs}ms`
    );
    this.name = "BridgeTimeoutError";
    this.callId = callId;
    this.method = method;
  }
}

/**
 * Custom error class for cancelled bridge calls.
 */
export class BridgeCancelledError extends Error {
  public readonly callId: string;
  public readonly method: string;

  constructor(callId: string, method: string) {
    super(`Bridge call '${method}' (${callId}) was cancelled`);
    this.name = "BridgeCancelledError";
    this.callId = callId;
    this.method = method;
  }
}

// ---------------------------------------------------------------------------
// Event System — .NET → JS push notifications
// ---------------------------------------------------------------------------

/** Callback type for event listeners. */
export type EventCallback<T = unknown> = (data: T) => void;

/** Map of event listeners, keyed by event name. */
const eventListeners = new Map<string, Set<EventCallback>>();

let eventUnlisten: Promise<UnlistenFn> | undefined;

/**
 * Lazily subscribe to the plugin's event channel. Events emitted by .NET
 * before this subscription is established are not replayed.
 */
function ensureEventListener(): void {
  if (eventUnlisten) return;

  eventUnlisten = listen<BridgeEventMessage>(EVENT_CHANNEL, (e) => {
    dispatchEvent(e.payload.event, e.payload.data);
  });
  eventUnlisten.catch((err) => {
    console.error("[Bridge] Failed to subscribe to .NET events:", err);
    eventUnlisten = undefined;
  });
}

/**
 * Subscribe to a .NET event.
 *
 * @param eventName - The event name (matches the C# `dispatcher.Emit("name", data)` call)
 * @param callback - Called each time the event fires, with the deserialized payload
 * @returns A dispose function that removes this listener
 *
 * @example
 * ```ts
 * const unsub = on("progress", (data) => console.log(data));
 * // later: unsub();
 * ```
 */
export function on<T = unknown>(
  eventName: string,
  callback: EventCallback<T>
): () => void {
  ensureEventListener();

  let listeners = eventListeners.get(eventName);
  if (!listeners) {
    listeners = new Set();
    eventListeners.set(eventName, listeners);
  }
  listeners.add(callback as EventCallback);

  return () => off(eventName, callback);
}

/**
 * Unsubscribe a specific callback from an event.
 */
export function off<T = unknown>(
  eventName: string,
  callback: EventCallback<T>
): void {
  const listeners = eventListeners.get(eventName);
  if (listeners) {
    listeners.delete(callback as EventCallback);
    if (listeners.size === 0) {
      eventListeners.delete(eventName);
    }
  }
}

/**
 * Subscribe to a .NET event for a single occurrence.
 * The listener is automatically removed after the first call.
 *
 * @returns A dispose function that removes this listener (if it hasn't fired yet)
 */
export function once<T = unknown>(
  eventName: string,
  callback: EventCallback<T>
): () => void {
  const wrapper: EventCallback = (data) => {
    off(eventName, wrapper);
    (callback as EventCallback)(data);
  };
  return on(eventName, wrapper);
}

/**
 * Dispatch an event to all registered listeners.
 */
function dispatchEvent(eventName: string, data: unknown): void {
  const listeners = eventListeners.get(eventName);
  if (!listeners || listeners.size === 0) return;

  // Iterate a copy so listeners can safely unsubscribe during dispatch
  for (const cb of [...listeners]) {
    try {
      cb(data);
    } catch (err) {
      console.error(
        `[Bridge] Error in event listener for '${eventName}':`,
        err
      );
    }
  }
}

// ---------------------------------------------------------------------------
// Calls
// ---------------------------------------------------------------------------

/**
 * Converts whatever a rejected `invoke` produced into an Error.
 * .NET failures arrive as `{ message, type }`; the type becomes `Error.name`.
 */
function toError(err: unknown): Error {
  if (err instanceof Error) return err;

  if (typeof err === "object" && err !== null && "message" in err) {
    const { message, type } = err as { message: unknown; type?: unknown };
    const error = new Error(String(message));
    if (typeof type === "string" && type) error.name = type;
    return error;
  }

  return new Error(String(err));
}

/**
 * Removes a call from the pending map and clears its timer.
 * @returns false if the call was already settled (timed out or cancelled).
 */
function settle(callId: string): boolean {
  const entry = pending.get(callId);
  if (!entry) return false;

  if (entry.timer !== undefined) {
    clearTimeout(entry.timer);
  }
  pending.delete(callId);
  return true;
}

function runCall<T>(
  callId: string,
  options: CallOptions,
  method: string,
  args: unknown[]
): Promise<T> {
  const timeoutMs = options.timeoutMs ?? defaultTimeoutMs;

  return new Promise<T>((resolve, reject) => {
    const entry: PendingCall = { reject, method };

    // Set up timeout if configured — also sends cancel to .NET
    if (timeoutMs > 0) {
      entry.timer = setTimeout(() => {
        if (pending.delete(callId)) {
          reject(new BridgeTimeoutError(callId, method, timeoutMs));
          sendCancel(callId);
        }
      }, timeoutMs);
    }

    pending.set(callId, entry);

    invoke<T>(CALL_COMMAND, { callId, method, args }).then(
      (result) => {
        if (settle(callId)) resolve(result);
      },
      (err) => {
        if (settle(callId)) reject(toError(err));
      }
    );
  });
}

/**
 * Cancel an in-flight call by its callId.
 * Rejects the pending promise with a BridgeCancelledError and sends
 * a cancel request to .NET to trigger CancellationToken cancellation.
 *
 * @param callId - The call ID available on CancellablePromise.callId
 * @returns true if the call was found and cancelled, false if it was already completed/cancelled
 */
export function cancelCall(callId: string): boolean {
  const entry = pending.get(callId);
  if (!entry) return false;

  settle(callId);
  entry.reject(new BridgeCancelledError(callId, entry.method));

  // Tell .NET to cancel the in-flight call
  sendCancel(callId);

  return true;
}

/**
 * Call a .NET service method and return a typed promise.
 *
 * @param method - Fully qualified method name: "ServiceName.MethodName"
 * @param args - Positional arguments matching the C# method signature
 * @returns A promise that resolves with the deserialized return value
 *
 * @example
 * ```ts
 * const greeting = await call<string>("GreetService.Greet", "World");
 * ```
 */
export function call<T>(method: string, ...args: unknown[]): Promise<T> {
  return callWithOptions<T>({}, method, ...args);
}

/**
 * Call a .NET service method with per-call options (e.g. custom timeout).
 *
 * @param options - Call options (timeoutMs, etc.)
 * @param method - Fully qualified method name: "ServiceName.MethodName"
 * @param args - Positional arguments matching the C# method signature
 * @returns A promise that resolves with the deserialized return value
 */
export function callWithOptions<T>(
  options: CallOptions,
  method: string,
  ...args: unknown[]
): Promise<T> {
  return runCall<T>(generateCallId(), options, method, args);
}

/**
 * A promise wrapper that supports cancellation.
 * The callId is exposed so it can be passed to cancelCall().
 *
 * @example
 * ```ts
 * const p = cancellableCall<string>("GreetService.SlowMethod", 30);
 * // Later:
 * p.cancel(); // cancels both JS promise and .NET CancellationToken
 * ```
 */
export class CancellablePromise<T> implements PromiseLike<T> {
  public readonly callId: string;
  private readonly _promise: Promise<T>;

  constructor(options: CallOptions, method: string, ...args: unknown[]) {
    this.callId = generateCallId();
    this._promise = runCall<T>(this.callId, options, method, args);
  }

  /** Cancel this call. Rejects the promise and tells .NET to cancel. */
  cancel(): boolean {
    return cancelCall(this.callId);
  }

  then<TResult1 = T, TResult2 = never>(
    onfulfilled?:
      | ((value: T) => TResult1 | PromiseLike<TResult1>)
      | null,
    onrejected?:
      | ((reason: unknown) => TResult2 | PromiseLike<TResult2>)
      | null
  ): Promise<TResult1 | TResult2> {
    return this._promise.then(onfulfilled, onrejected);
  }

  catch<TResult = never>(
    onrejected?:
      | ((reason: unknown) => TResult | PromiseLike<TResult>)
      | null
  ): Promise<T | TResult> {
    return this._promise.catch(onrejected);
  }

  finally(onfinally?: (() => void) | null): Promise<T> {
    return this._promise.finally(onfinally);
  }
}

/**
 * Call a .NET service method returning a CancellablePromise.
 *
 * @example
 * ```ts
 * const p = cancellableCall<string>("GreetService.SlowMethod", 30);
 * setTimeout(() => p.cancel(), 2000); // cancel after 2s
 * try { await p; } catch (e) { if (e instanceof BridgeCancelledError) ... }
 * ```
 */
export function cancellableCall<T>(
  method: string,
  ...args: unknown[]
): CancellablePromise<T> {
  return new CancellablePromise<T>({}, method, ...args);
}

/**
 * Returns the number of calls currently awaiting a response.
 * Useful for debugging or health checks.
 */
export function getPendingCallCount(): number {
  return pending.size;
}

// ---------------------------------------------------------------------------
// Frontend implementations — .NET → JS calls
// ---------------------------------------------------------------------------

const FRONTEND_EVENT = "$frontend";
const FRONTEND_REPLY = "$frontend.Reply";

interface FrontendRequest {
  id: string;
  service: string;
  method: string;
  args?: unknown[];
}

/** Implementations registered by the generated `implement…` functions, keyed by service name. */
const frontends = new Map<string, object>();

let frontendSubscribed = false;

/**
 * Registers this window's implementation of a C# `[BridgeFrontend]` interface so .NET can call it.
 * The generated `implement<Name>` functions call this; application code should use those.
 * Register at startup: a request that reaches the window before that is not replayed.
 *
 * @returns A function that removes the implementation.
 */
export function registerFrontend(service: string, implementation: object): () => void {
  frontends.set(service, implementation);

  if (!frontendSubscribed) {
    frontendSubscribed = true;
    on<FrontendRequest>(FRONTEND_EVENT, (request) => {
      void answerFrontendRequest(request);
    });
  }

  return () => {
    if (frontends.get(service) === implementation) frontends.delete(service);
  };
}

/**
 * Runs the requested method and always answers .NET, with the result or with the error,
 * so the awaiting C# call never has to wait for its timeout.
 */
async function answerFrontendRequest(request: FrontendRequest): Promise<void> {
  try {
    const implementation = frontends.get(request.service) as Record<string, unknown> | undefined;
    const method = implementation?.[request.method];
    if (typeof method !== "function") {
      throw new Error(
        `No implementation of ${request.service}.${request.method} is registered in this window.`
      );
    }

    const result: unknown = await method.apply(implementation, request.args ?? []);
    await call<void>(FRONTEND_REPLY, request.id, result ?? null, "", "");
  } catch (err) {
    const error = err instanceof Error ? err : new Error(String(err));
    // An empty message would read as success on the .NET side, so always send something.
    await call<void>(FRONTEND_REPLY, request.id, null, error.message || error.name || "Error", error.name).catch(
      (replyErr) => console.error("[Bridge] Failed to answer a .NET request:", replyErr)
    );
  }
}
