import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { MemoryStorage } from '../../net/src/util/jsonStore.ts';
import { ApiClient, ApiError, errorMessage, formatWait, HttpErrorCodes, parseRetryAfter, toApiError, type LockManagerLike } from '../src/index.ts';

type Call = { url: string; init: RequestInit };

function json(status: number, body?: unknown, headers: Record<string, string> = {}): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), { status, headers });
}

/** A scripted fetch: `routes[path]` returns the next response for that path. */
function fakeFetch(routes: Record<string, (call: Call) => Response | Promise<Response>>) {
  const calls: Call[] = [];
  const f = async (url: string, init: RequestInit) => {
    calls.push({ url, init });
    const path = url.replace(/^\/api/, '');
    const handler = routes[path];
    if (!handler) throw new TypeError('Failed to fetch');
    return handler({ url, init });
  };
  return { f, calls };
}

const auth = (c: Call) => (c.init.headers as Record<string, string>).Authorization;

describe('ApiError / toApiError', () => {
  it('reads Altruist simple error bodies and Retry-After', async () => {
    const e = await toApiError(json(409, { error: 'name_taken', message: 'That name is in use.', field: 'displayName' }));
    assert.ok(e instanceof ApiError);
    assert.deepEqual([e.status, e.code, e.message, e.field], [409, 'name_taken', 'That name is in use.', 'displayName']);
    const r = await toApiError(json(429, { error: 'rate_limited', message: 'Slow down.' }, { 'Retry-After': '90' }));
    assert.equal(r.retryAfter, 90);
    assert.equal(r.message, 'Slow down — try again in 2 min.');
    const keep = await toApiError(json(429, { error: 'rate_limited', message: 'Wait 5 s.' }, { 'Retry-After': '5' }));
    assert.equal(keep.message, 'Wait 5 s.');
  });

  it('5xx and body-less failures get generic texts; overrides apply', async () => {
    const e = await toApiError(json(500, { error: 'internal', message: 'NullReferenceException at ...' }));
    assert.equal(e.code, 'internal');
    assert.equal(e.message, 'The server had a problem. Please try again shortly.');
    const n = await toApiError(new Response('<html>', { status: 404 }));
    assert.deepEqual([n.code, n.message], ['http_404', 'Not found.']);
    const o = await toApiError(new Response(null, { status: 403 }), { messages: { 403: 'Nope.' } });
    assert.equal(o.message, 'Nope.');
    assert.equal((await toApiError(json(400, { message: 'x'.repeat(500) }))).message.length, 300);
  });

  it('helpers', () => {
    assert.equal(parseRetryAfter('12'), 12);
    assert.equal(parseRetryAfter('0'), undefined);
    assert.equal(parseRetryAfter(null), undefined);
    assert.equal(parseRetryAfter(new Date(10_000).toUTCString(), 0), 10);
    assert.equal(parseRetryAfter('garbage'), undefined);
    assert.equal(formatWait(12.2), '13 s');
    assert.equal(formatWait(61), '2 min');
    assert.equal(errorMessage(new ApiError(0, 'offline', 'Offline!')), 'Offline!');
    assert.equal(errorMessage(new Error('boom')), 'boom');
    assert.equal(errorMessage(42), 'Something went wrong. Please try again.');
    assert.ok(new ApiError(0, HttpErrorCodes.Offline, '').offline);
    assert.ok(new ApiError(401, 'unauthorized', '').unauthorized);
  });
});

describe('ApiClient', () => {
  it('signs in, sends bearer + CSRF header + credentials, parses JSON and 204', async () => {
    const { f, calls } = fakeFetch({
      '/auth/login': () => json(200, { accessToken: 'A1', me: { id: 'u' } }),
      '/me': () => json(200, { id: 'u' }),
      '/empty': () => new Response(null, { status: 204 }),
    });
    const api = new ApiClient({ fetch: f, locks: null, csrf: { header: 'X-Game', value: '1' }, headers: { 'X-Client': 'test' } });
    const tokens: boolean[] = [];
    api.on('token', (s) => tokens.push(s));
    const r = await api.signIn<{ me: { id: string } }>('/auth/login', { username: 'a', password: 'b' });
    assert.equal(r.me.id, 'u');
    assert.equal(api.token, 'A1');
    assert.deepEqual(await api.get('/me'), { id: 'u' });
    assert.equal(await api.post('/empty', { x: 1 }), undefined);
    const login = calls[0]!.init.headers as Record<string, string>;
    assert.equal(login.Authorization, undefined);
    assert.equal(login['X-Game'], '1');
    assert.equal(login['X-Client'], 'test');
    assert.equal(calls[0]!.init.credentials, 'include');
    assert.equal(auth(calls[1]!), 'Bearer A1');
    assert.equal(calls[2]!.init.body, '{"x":1}');
    assert.deepEqual(tokens, [true]);
  });

  it('401 → one refresh → retry with the new token', async () => {
    let meCalls = 0;
    const { f, calls } = fakeFetch({
      '/me': (c) => (meCalls++, auth(c) === 'Bearer B' ? json(200, { ok: true }) : json(401, { error: 'unauthorized', message: 'x' })),
      '/auth/refresh': () => json(200, { accessToken: 'B' }),
    });
    const api = new ApiClient({ fetch: f, locks: null });
    api.setToken('A');
    assert.deepEqual(await api.get('/me'), { ok: true });
    assert.equal(meCalls, 2);
    assert.equal(api.token, 'B');
    assert.equal(calls.filter((c) => c.url.endsWith('/auth/refresh')).length, 1);
    assert.equal((calls[1]!.init.headers as Record<string, string>)['X-Requested-With'], '1');
  });

  it('concurrent 401s share a single refresh; the Web Lock wraps it', async () => {
    let refreshes = 0;
    const locked: string[] = [];
    const locks: LockManagerLike = { request: (name, cb) => (locked.push(name), cb()) };
    const { f } = fakeFetch({
      '/a': (c) => (auth(c) === 'Bearer N' ? json(200, 1) : json(401)),
      '/b': (c) => (auth(c) === 'Bearer N' ? json(200, 2) : json(401)),
      '/auth/refresh': async () => (refreshes++, await new Promise((r) => setTimeout(r, 5)), json(200, { accessToken: 'N' })),
    });
    const api = new ApiClient({ fetch: f, locks, lockName: 'game-refresh' });
    api.setToken('old');
    assert.deepEqual(await Promise.all([api.get('/a'), api.get('/b')]), [1, 2]);
    assert.equal(refreshes, 1);
    assert.deepEqual(locked, ['game-refresh']);
  });

  it('a rejected refresh signs out; a second 401 is not retried again', async () => {
    const { f } = fakeFetch({ '/me': () => json(401), '/auth/refresh': () => json(401, { error: 'unauthorized', message: 'gone' }) });
    const storage = new MemoryStorage();
    const api = new ApiClient({ fetch: f, locks: null, sessionHintKey: 'hint', storage });
    api.setToken('A');
    assert.equal(storage.getItem('hint'), '1');
    let signedOut = 0;
    api.on('signedOut', () => signedOut++);
    await assert.rejects(api.get('/me'), (e: ApiError) => e.status === 401);
    assert.equal(api.signedIn, false);
    assert.equal(signedOut, 1);
    assert.equal(api.hasSessionHint(), false);
  });

  it('a 5xx refresh keeps the token; an offline refresh rejects with status 0', async () => {
    let mode = 'down';
    const { f } = fakeFetch({
      '/auth/refresh': () => {
        if (mode === 'offline') throw new TypeError('Failed to fetch');
        return json(503);
      },
    });
    const api = new ApiClient({ fetch: f, locks: null });
    api.setToken('A');
    assert.equal(await api.refresh(), false);
    assert.equal(api.token, 'A');
    mode = 'offline';
    await assert.rejects(api.refresh(), (e: ApiError) => e.offline);
    assert.equal(api.token, 'A');
  });

  it('offline, bad JSON, abort, missing token and tickets', async () => {
    const ctrl = new AbortController();
    const { f } = fakeFetch({
      '/bad': () => new Response('{not json', { status: 200 }),
      '/abort': () => {
        const e = new Error('aborted');
        e.name = 'AbortError';
        throw e;
      },
      '/login': () => json(200, { nope: 1 }),
      '/ticket': () => json(200, { ticket: 'T', expiresInSeconds: 30 }),
      '/ticket-old': () => json(200, { ticket: 'U', expiresIn: 20 }),
      '/ticket-bad': () => json(200, {}),
    });
    const api = new ApiClient({ fetch: f, locks: null, baseUrl: '/api' });
    await assert.rejects(api.get('/nowhere'), (e: ApiError) => e.offline && e.code === 'offline');
    await assert.rejects(api.get('/bad'), (e: ApiError) => e.code === 'bad_response');
    await assert.rejects(api.get('/abort', { signal: ctrl.signal }), (e: Error) => e.name === 'AbortError');
    await assert.rejects(api.signIn('/login', {}), (e: ApiError) => e.code === 'bad_response');
    assert.deepEqual(await api.connectionTicket('/ticket'), { ticket: 'T', expiresInSeconds: 30 });
    assert.deepEqual(await api.connectionTicket('/ticket-old'), { ticket: 'U', expiresInSeconds: 20 });
    await assert.rejects(api.connectionTicket('/ticket-bad'), ApiError);
  });

  it('signOut drops the token even when the call fails; hint defaults', async () => {
    const { f } = fakeFetch({});
    const api = new ApiClient({ fetch: f, locks: null, csrf: null });
    api.setToken('A');
    await assert.rejects(api.signOut());
    assert.equal(api.signedIn, false);
    assert.equal(api.hasSessionHint(), true); // no key configured: always try
    const blocked = new ApiClient({
      sessionHintKey: 'k',
      storage: {
        getItem: () => {
          throw new Error('x');
        },
        setItem: () => {
          throw new Error('x');
        },
        removeItem: () => {},
      },
    });
    blocked.setToken('A');
    assert.equal(blocked.hasSessionHint(), true);
  });
});
