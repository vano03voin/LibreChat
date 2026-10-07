import type { ISchedule } from '@librechat/data-schemas';
import type { createSchedulesHandlers } from '~/schedules/handlers';
import type { ServerRequest } from '~/types';
import {
  createSemindScheduleActions,
  createSemindScheduledRequestScope,
  createSemindScheduleTool,
  createSemindScheduleValidator,
} from './schedule';

jest.mock('~/middleware/access', () => ({ checkAccess: jest.fn().mockResolvedValue(true) }));

describe('trusted SE-mind scheduled scope', () => {
  const req = {
    user: { id: 'owner' },
    body: {
      agentTrigger: {
        event: { source: { type: 'schedule', id: 'task' } },
        metadata: { configRevision: 3, semindContext: { server_id: 'forged', world_id: 'forged' } },
      },
    },
  } as unknown as ServerRequest;
  const owned = {
    id: 'task',
    user: 'owner',
    configRevision: 3,
    executionScope: 'game',
    semindContext: { server_id: 'real-server', world_id: 'real-world' },
  } as unknown as ISchedule;
  test('uses current owned durable task scope and ignores claimed metadata', async () => {
    const get = jest.fn().mockResolvedValue(owned);
    expect(await createSemindScheduledRequestScope({ getScheduleById: get })(req)).toEqual(
      owned.semindContext,
    );
    expect(get).toHaveBeenCalledWith('task', 'owner');
  });
  test.each([
    null,
    { ...owned, user: 'other' },
    { ...owned, configRevision: 4 },
    { ...owned, deleting: true },
    { ...owned, semindContext: undefined },
  ])('refuses a missing, foreign, stale, deleted, or unbound task', async (schedule) => {
    await expect(
      createSemindScheduledRequestScope({ getScheduleById: jest.fn().mockResolvedValue(schedule) })(
        req,
      ),
    ).rejects.toThrow();
  });
  test('profile tasks have no game authority', async () => {
    expect(
      await createSemindScheduledRequestScope({
        getScheduleById: jest.fn().mockResolvedValue({ ...owned, executionScope: 'profile' }),
      })(req),
    ).toBeNull();
  });
  test.each([
    [409, 'world_changed'],
    [403, 'permission_revoked'],
    [404, 'permission_revoked'],
    [200, 'ok'],
  ] as const)('fresh authority status %s resolves to %s', async (status, result) => {
    const fetcher = jest.fn().mockResolvedValue({ ok: status === 200, status });
    const validate = createSemindScheduleValidator({
      getConfig: async () => ({
        enabled: true,
        apiURL: 'https://authority.test',
        sessionRecheckSeconds: 30,
      }),
      internalKey: 'internal',
      fetch: fetcher as unknown as typeof fetch,
    });
    expect(
      await validate(owned as never, { id: 'owner', semindSteamId: '76561198000000001' }),
    ).toBe(result);
    expect(JSON.parse(fetcher.mock.calls[0][1].body)).toEqual({
      ...owned.semindContext,
      steam_id: '76561198000000001',
    });
  });
});

describe('SE-mind schedule management tool', () => {
  test('invalid JSON stays a tool error without invoking storage', async () => {
    const execute = jest.fn();
    const tool = createSemindScheduleTool({ execute });
    expect(await tool.invoke({ action: 'create', args_json: '{' })).toContain(
      'SEMIND_SCHEDULE_ARGUMENTS_INVALID',
    );
    expect(execute).not.toHaveBeenCalled();
  });
  test('cannot substitute owner and forwards only the trusted request', async () => {
    const list = jest.fn(async (request, response) => {
      expect(request.user.id).toBe('owner');
      response.json({ schedules: [] });
    });
    const handlers = { listSchedules: list } as unknown as ReturnType<
      typeof createSchedulesHandlers
    >;
    const req = {
      config: { config: { semind: { enabled: true } } },
      user: { id: 'owner' },
      body: {},
    } as unknown as ServerRequest;
    const execute = createSemindScheduleActions({ req, handlers, getRoleByName: jest.fn() });
    expect(await execute('list', { steam_id: '76561198000000002' })).toMatchObject({ ok: false });
    expect(list).not.toHaveBeenCalled();
    expect(await execute('list', {})).toMatchObject({ ok: true, result: { schedules: [] } });
    expect(list).toHaveBeenCalledTimes(1);
  });
});
