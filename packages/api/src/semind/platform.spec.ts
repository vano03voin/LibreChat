import type { ServerRequest } from '~/types';
import { createSemindPlatformTools, closeSemindPlatformSession } from './platform';

function setup() {
  const req = {
    body: { messageId: 'message-one' },
    config: { config: { semind: { enabled: true, apiURL: 'https://authority.test' } } },
    user: { id: 'owner', semindSteamId: '76561198000000001' },
    semindIdentity: {
      steam_id: '76561198000000001',
      server_id: 'trusted-server',
      world_id: 'trusted-world',
    },
  } as unknown as ServerRequest;
  const fetcher = jest.fn(async (url, options) => {
    const body = JSON.parse(options.body);
    return {
      ok: true,
      json: async () =>
        String(url).endsWith('/session')
          ? { prepared: true, task_id: body.task_id }
          : String(url).endsWith('/profile/identity')
            ? { steam_id: body.steam_id }
            : { ok: true },
    };
  });
  const controller = new AbortController();
  const tools = createSemindPlatformTools({
    req,
    gatewayURL: 'https://gateway.test/v1',
    internalKey: 'internal',
    fetch: fetcher as unknown as typeof fetch,
    signal: controller.signal,
  });
  return {
    req,
    fetcher,
    controller,
    tools,
    game: tools.find((tool) => tool.name === 'game_execute')!,
    scripts: tools.find((tool) => tool.name === 'script_library')!,
  };
}

describe('website and scheduled native game tools', () => {
  test('cold Workshop imports receive the longer configurable timeout while cancellation and other operation deadlines remain active', async () => {
    const timeout = jest.spyOn(AbortSignal, 'timeout');
    try {
      const s = setup();
      s.req.config!.config!.semind!.libraryImportTimeoutSeconds = 240;
      await s.scripts.invoke({ action: 'import', args_json: '{"item_id":"914445138"}' });
      await s.scripts.invoke({ action: 'list', args_json: '{}' });
      expect(timeout.mock.calls).toEqual([[240000], [30000]]);
      const importSignal = s.fetcher.mock.calls[0][1].signal;
      s.controller.abort();
      expect(importSignal.aborted).toBe(true);
      expect(s.fetcher.mock.calls[1][1].signal.aborted).toBe(true);
    } finally {
      timeout.mockRestore();
    }
  });
  test('requires current host scope and the same Steam owner', async () => {
    const s = setup();
    s.req.user!.semindSteamId = '76561198000000002';
    expect(await s.game.invoke({ operation: 'player.position', args_json: '{}' })).toContain(
      'SEMIND_GAME_SCOPE_REQUIRED',
    );
    expect(s.fetcher).not.toHaveBeenCalled();
  });
  test.each(['{', '{"steam_id":"76561198000000002"}', '{"world_id":"foreign"}'])(
    'rejects invalid or owner-substituting arguments %s',
    async (args_json) => {
      const s = setup();
      expect(await s.game.invoke({ operation: 'player.position', args_json })).toContain(
        '"ok":false',
      );
      expect(s.fetcher).not.toHaveBeenCalled();
    },
  );
  test('one trusted session serves the same Run and routes exact Workshop source operations', async () => {
    const s = setup();
    await s.game.invoke({ operation: 'player.position', args_json: '{}' });
    await s.scripts.invoke({ action: 'import', args_json: '{"item_id":"914445138"}' });
    const calls = s.fetcher.mock.calls.map(([url, options]) => ({
      path: new URL(url).pathname,
      body: JSON.parse(options.body),
    }));
    expect(calls[0]).toEqual({
      path: '/internal/game/session',
      body: {
        task_id: 'web:message-one',
        world_id: 'trusted-world',
        steam_id: '76561198000000001',
      },
    });
    expect(calls.filter((call) => call.path === '/internal/game/session')).toHaveLength(1);
    expect(calls[1].body).toMatchObject({
      operation_id: 'web:message-one:1',
      name: 'player.position',
      args: {},
    });
    expect(calls[2].path).toBe('/internal/script-library/tool');
    expect(calls[2].body).toMatchObject({
      operation_id: 'web:message-one:2',
      action: 'import',
      steam_id: '76561198000000001',
      args: { item_id: '914445138' },
    });
    await closeSemindPlatformSession(s.req);
    expect(JSON.parse(s.fetcher.mock.calls[3][1].body)).toEqual({
      task_id: 'web:message-one',
      canceled: false,
    });
  });
  test('personal script import works from a verified website session without a world', async () => {
    const s = setup();
    s.req.semindIdentity!.world_id = null;
    await s.scripts.invoke({ action: 'import', args_json: '{"item_id":"914445138"}' });
    expect(new URL(s.fetcher.mock.calls[0][0]).pathname).toBe('/internal/script-library/tool');
    expect(s.fetcher).toHaveBeenCalledTimes(1);
    await closeSemindPlatformSession(s.req);
    expect(s.fetcher).toHaveBeenCalledTimes(1);
  });
  test('profile scheduled runs recheck current SQL owner before every personal library call', async () => {
    const s = setup();
    delete s.req.semindIdentity;
    await s.scripts.invoke({ action: 'list', args_json: '{}' });
    await s.scripts.invoke({ action: 'list', args_json: '{}' });
    expect(s.fetcher.mock.calls.map(([url]) => new URL(url).pathname)).toEqual([
      '/internal/assistant/profile/identity',
      '/internal/script-library/tool',
      '/internal/assistant/profile/identity',
      '/internal/script-library/tool',
    ]);
    expect(await s.game.invoke({ operation: 'player.position', args_json: '{}' })).toContain(
      'SEMIND_GAME_SCOPE_REQUIRED',
    );
  });
  test('cancellation cleanup uses a live cleanup signal', async () => {
    const s = setup();
    await s.game.invoke({ operation: 'player.position', args_json: '{}' });
    s.controller.abort();
    await closeSemindPlatformSession(s.req, true);
    expect(s.fetcher.mock.calls[2][1].signal.aborted).toBe(false);
    expect(JSON.parse(s.fetcher.mock.calls[2][1].body).canceled).toBe(true);
  });
});
