/**
 * Small fetch wrapper for the GridWise API. Every failed `/v1` call answers with
 * `{ error: { code, message, requestId, fields? } }`; it is surfaced as an ApiError.
 */

export type FieldErrors = Record<string, string[]>

interface ErrorBody {
  error?: {
    code?: string
    message?: string
    requestId?: string
    fields?: FieldErrors
  }
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
    readonly requestId: string | null = null,
    readonly fields: FieldErrors = {},
  ) {
    super(message)
    this.name = 'ApiError'
  }

  /** Message for the first error on a field, if any. */
  field(name: string): string | undefined {
    return this.fields[name]?.[0]
  }
}

/** Raised for 401 responses so the app can send the user back to sign-in. */
export class UnauthenticatedError extends ApiError {}

const NETWORK_ERROR = 'The API could not be reached. Check your connection and try again.'

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body !== undefined) {
    headers.set('Content-Type', 'application/json')
  }

  let response: Response
  try {
    response = await fetch(path, { ...init, headers, credentials: 'same-origin' })
  } catch {
    throw new ApiError(0, 'network_error', NETWORK_ERROR)
  }

  if (response.status === 204) {
    return undefined as T
  }

  const body: unknown = await response.json().catch(() => null)
  if (response.ok) {
    return body as T
  }

  const error = (body as ErrorBody | null)?.error
  const args = [
    response.status,
    error?.code ?? 'http_error',
    error?.message ?? `The API answered ${response.status}.`,
    error?.requestId ?? response.headers.get('X-Request-ID'),
    error?.fields ?? {},
  ] as const
  throw response.status === 401 ? new UnauthenticatedError(...args) : new ApiError(...args)
}

export function postJson<T>(path: string, payload: unknown): Promise<T> {
  return api<T>(path, { method: 'POST', body: JSON.stringify(payload) })
}

/** Human-readable text for any thrown value, with the request ID when the API sent one. */
export function describeError(error: unknown): string {
  if (error instanceof ApiError) {
    return error.requestId ? `${error.message} (request ${error.requestId})` : error.message
  }
  return 'Something went wrong.'
}
