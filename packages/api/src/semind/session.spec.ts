import jwt from 'jsonwebtoken';
import { EventEmitter } from 'node:events';
import type { Request, Response } from 'express';
import { createSemindAuthority, SEMIND_SESSION_COOKIE } from './session';

const steamId = '76561198000000001';
const token = 'child-session-token-for-test';
const config = { enabled: true, apiURL: 'https://authority.test', sessionRecheckSeconds: 5 };

function setup() {
  const identity = {
    steam_id: steamId,
    display_name: 'Player',
    is_operator: false,
    expires_at: Math.floor(Date.now() / 1000) + 60,
    server_id: 'server',
    world_id: 'world',
  };
  const fetcher = jest
    .fn()
    .mockResolvedValue({ ok: true, status: 200, json: async () => identity });
  const deps = {
    getConfig: jest.fn().mockResolvedValue(config),
    internalKey: 'internal-key',
    fetch: fetcher as unknown as typeof fetch,
    getUserSteamId: () => steamId,
    isTrustedTrigger: jest.fn().mockReturnValue(false),
  };
  const req = {
    cookies: { [SEMIND_SESSION_COOKIE]: token },
    headers: {},
    body: {},
    get: jest.fn().mockReturnValue('https://site.test'),
  } as unknown as Request;
  const res = new EventEmitter() as Response;
  res.status = jest.fn().mockReturnValue(res);
  res.json = jest.fn().mockReturnValue(res);
  res.end = jest.fn().mockReturnValue(res);
  res.cookie = jest.fn().mockReturnValue(res);
  res.clearCookie = jest.fn().mockReturnValue(res);
  res.set = jest.fn().mockReturnValue(res);
  res.redirect = jest.fn();
  res.sendStatus = jest.fn().mockReturnValue(res);
  const next = jest.fn();
  return { authority: createSemindAuthority(deps), deps, fetcher, identity, req, res, next };
}

describe('SE-mind session authority', () => {
  afterEach(() => jest.useRealTimers());

  test.each(['missing', 'foreign', 'expired', 'unavailable'])(
    'fails closed for %s parent identity',
    async (reason) => {
      const s = setup();
      if (reason === 'missing') s.req.cookies = {};
      if (reason === 'foreign') s.identity.steam_id = '76561198000000002';
      if (reason === 'expired') s.identity.expires_at = 1;
      if (reason === 'unavailable') s.fetcher.mockRejectedValue(new Error('offline'));
      await s.authority.guard(s.req, s.res, s.next);
      expect(s.next).not.toHaveBeenCalled();
      expect(s.res.status).toHaveBeenCalledWith(401);
    },
  );

  test('revokes an open response even if client omits the SSE Accept header', async () => {
    jest.useFakeTimers();
    const s = setup();
    await s.authority.guard(s.req, s.res, s.next);
    expect(s.next).toHaveBeenCalledTimes(1);
    s.fetcher.mockResolvedValue({ ok: false });
    await jest.advanceTimersByTimeAsync(5000);
    expect(s.res.end).toHaveBeenCalledTimes(1);
    s.res.emit('close');
    expect(jest.getTimerCount()).toBe(0);
  });

  test('trusted native scheduled trigger does not require browser cookies', async () => {
    const s = setup();
    s.req.cookies = {};
    s.deps.isTrustedTrigger.mockReturnValue(true);
    await s.authority.guard(s.req, s.res, s.next);
    expect(s.next).toHaveBeenCalledTimes(1);
    expect(s.fetcher).not.toHaveBeenCalled();
  });
  test('existing Steam operator sessions cannot retain global LibreChat administrator capabilities', async () => {
    const s = setup();
    (s.req as import('~/types').ServerRequest).user = {
      id: 'owner',
      provider: 'semind',
      role: 'ADMIN',
    } as import('@librechat/data-schemas').IUser;
    await s.authority.guard(s.req, s.res, s.next);
    expect((s.req as import('~/types').ServerRequest).user?.role).toBe('USER');
    s.res.emit('finish');
  });

  test('each trusted game schedule gets fresh identity and an ephemeral grant', async () => {
    const s = setup();
    s.req.cookies = {};
    s.deps.isTrustedTrigger.mockReturnValue(true);
    const scope = { server_id: 'server', world_id: 'world' };
    const grant = {
      access_token: 'fresh-per-run-data-grant-token',
      expires_at: new Date(Date.now() + 60000).toISOString(),
      api_prefix: '/api/player/v1',
    };
    s.fetcher
      .mockResolvedValueOnce({ ok: true, status: 200, json: async () => s.identity })
      .mockResolvedValueOnce({ ok: true, status: 200, json: async () => grant });
    await createSemindAuthority({ ...s.deps, getTrustedGameScope: async () => scope }).guard(
      s.req,
      s.res,
      s.next,
    );
    expect(s.next).toHaveBeenCalledTimes(1);
    expect((s.req as import('~/types').ServerRequest).semindIdentity).toMatchObject(scope);
    expect((s.req as import('~/types').ServerRequest).semindDataGrant).toEqual(grant);
    expect(JSON.parse(s.fetcher.mock.calls[1][1].body)).toEqual({
      source: 'schedule',
      ...scope,
      steam_id: steamId,
    });
  });

  test('refresh cannot pair one user refresh token with another Steam session', async () => {
    const s = setup();
    s.req.cookies.refreshToken = jwt.sign({ id: 'user-b' }, 'refresh-secret');
    await s.authority.createRefreshGuard({
      refreshSecret: 'refresh-secret',
      getUser: async () => ({ semindSteamId: '76561198000000002' }),
    })(s.req, s.res, s.next);
    expect(s.next).not.toHaveBeenCalled();
    expect(s.res.status).toHaveBeenCalledWith(401);
  });

  test('failed provisioning revokes the newly exchanged child session', async () => {
    const s = setup();
    s.req.body = { ticket: 'single-use-ticket-for-test' };
    s.fetcher.mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ ...s.identity, session_token: token }),
    });
    const provisionUser = jest.fn().mockRejectedValue(new Error('database unavailable'));
    const setAuthTokens = jest.fn();
    await s.authority.createExchangeHandler({ provisionUser, setAuthTokens })(s.req, s.res, s.next);
    expect(setAuthTokens).not.toHaveBeenCalled();
    expect(s.fetcher.mock.calls[1][0].pathname).toBe('/internal/assistant/session/revoke');
    expect(s.res.status).toHaveBeenCalledWith(401);
  });

  test('website login opens the actual owned prepared or selected agent', async () => {
    const s = setup();
    s.req.body = { ticket: 'single-use-ticket-for-test' };
    s.fetcher.mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ ...s.identity, session_token: token }),
    });
    const provisionUser = jest
      .fn()
      .mockResolvedValue({ id: 'owner', agentId: 'agent_semind_owner' });
    await s.authority.createExchangeHandler({
      provisionUser,
      setAuthTokens: jest.fn().mockResolvedValue('access'),
    })(s.req, s.res, s.next);
    expect(s.res.redirect).toHaveBeenCalledWith(303, '/c/new?agent_id=agent_semind_owner');
    expect(s.res.cookie).toHaveBeenCalledWith(
      SEMIND_SESSION_COOKIE,
      token,
      expect.objectContaining({ httpOnly: true, secure: true, path: '/', sameSite: 'lax' }),
    );
  });

  test('logout revokes child token before clearing local authentication', async () => {
    const s = setup();
    await s.authority.revokeSession(s.req, s.res, s.next);
    expect(s.fetcher.mock.calls[0][0].pathname).toBe('/internal/assistant/session/revoke');
    expect(s.res.clearCookie).toHaveBeenCalledWith(
      SEMIND_SESSION_COOKIE,
      expect.objectContaining({ secure: true, path: '/' }),
    );
    expect(s.next).toHaveBeenCalledTimes(1);
  });
});
