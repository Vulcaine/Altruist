/**
 * `@altruist/client/auth` — {@link ApiClient} (in-memory bearer token, single-flight cookie
 * refresh with CSRF header, 401 → refresh → retry once) and {@link ApiError} (Altruist `simple`
 * HTTP error bodies + Retry-After).
 */
export * from './apiError.ts';
export * from './httpClient.ts';
