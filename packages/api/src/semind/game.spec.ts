import express from 'express';
import request from 'supertest';
import type { AppConfig } from '@librechat/data-schemas';
import { createSemindGameRouter } from './game';

const internalKey = 'test-internal-game-authority-key';
const input = {
  input_id: 'input-1',
  server_id: 'native-instance',
  world_id: 'b08f6368-8e1f-422a-855e-a42b99bfe336',
  steam_id: '76561198282450531',
  identity_id: '144115188075855897',
  source_run_id: 'host-run',
  source_sequence: 1,
  text: 'Покажи мои корабли',
  modality: 'text',
};
const body = {
  task_id: input.input_id,
  conversation_id: '00543eb5-4b19-45b7-bc55-32e8e89818c4',
  history: [],
  input,
};
const platform = '9d48e435-08da-4a07-8d62-24a9b94a41b8';
function setup(options: { enabled?: boolean; registered?: boolean } = {}) {
  const db = {
    provisionSemindUser: jest.fn(),
    getUserById: jest.fn(),
    getUserKey: jest.fn(),
    getUserKeyValues: jest.fn(),
    saveMessage: jest.fn(),
    saveConvo: jest.fn(),
    getSemindAgentSelection: jest.fn(),
    getAgent: jest.fn(),
  };
  const fetcher = jest.fn().mockResolvedValue({
    ok: true,
    json: async () => ({
      steam_id: input.steam_id,
      server_id: platform,
      world_id: input.world_id,
      display_name: 'Test',
      is_operator: false,
    }),
  });
  const app = express();
  app.use(express.json());
  app.use(
    '/game',
    createSemindGameRouter({
      internalKey,
      gatewayURL: 'http://127.0.0.1:49391/model/v1',
      fetch: fetcher as unknown as typeof fetch,
      db,
      getConfig: async () =>
        ({
          config: {
            semind: {
              enabled: options.enabled !== false,
              apiURL: 'https://authority.test',
              gameServerIds: options.registered !== false ? { [input.server_id]: platform } : {},
            },
          },
        }) as AppConfig,
    }),
  );
  return { app, db, fetcher };
}

describe('trusted native game intake', () => {
  test.each(['/run', '/steer', '/cancel'])('requires internal authority for %s', async (route) => {
    const s = setup();
    const response = await request(s.app)
      .post('/game' + route)
      .send(body);
    expect(response.status).toBe(401);
    expect(s.fetcher).not.toHaveBeenCalled();
    expect(s.db.provisionSemindUser).not.toHaveBeenCalled();
  });
  test.each(['wrong-key', 'test-internal-game-authority-keY', ''])(
    'rejects invalid credential %s',
    async (credential) => {
      const s = setup();
      expect(
        (
          await request(s.app)
            .post('/game/run')
            .set('Authorization', `Bearer ${credential}`)
            .send(body)
        ).status,
      ).toBe(401);
      expect(s.fetcher).not.toHaveBeenCalled();
    },
  );
  test.each([
    { ...body, task_id: 'different-input' },
    { ...body, conversation_id: '../../../foreign' },
    { ...body, input: { ...input, steam_id: '1' } },
    { ...body, input: { ...input, source_sequence: 0 } },
    { ...body, input: { ...input, text: '' } },
    { ...body, input: { ...input, modality: 'voice' } },
    { ...body, history: [{ type: 'ai', data: {} }] },
  ])('rejects malformed scope/history before upstream authorization', async (payload) => {
    const s = setup();
    expect(
      (
        await request(s.app)
          .post('/game/run')
          .set('Authorization', `Bearer ${internalKey}`)
          .send(payload)
      ).status,
    ).toBe(400);
    expect(s.fetcher).not.toHaveBeenCalled();
    expect(s.db.saveMessage).not.toHaveBeenCalled();
  });
  test('requires an operator-configured native instance/platform server mapping', async () => {
    const s = setup({ registered: false });
    const response = await request(s.app)
      .post('/game/run')
      .set('Authorization', `Bearer ${internalKey}`)
      .send(body);
    expect(response.status).toBe(502);
    expect(s.fetcher).not.toHaveBeenCalled();
    expect(s.db.provisionSemindUser).not.toHaveBeenCalled();
  });
  test.each(['steam_id', 'server_id', 'world_id'])(
    'rejects authority %s mismatch without provisioning/chats/tools',
    async (field) => {
      const s = setup();
      s.fetcher.mockResolvedValue({
        ok: true,
        json: async () => ({
          steam_id: input.steam_id,
          server_id: platform,
          world_id: input.world_id,
          display_name: 'Test',
          is_operator: false,
          [field]: field === 'steam_id' ? '76561198000000001' : 'foreign',
        }),
      });
      const response = await request(s.app)
        .post('/game/run')
        .set('Authorization', `Bearer ${internalKey}`)
        .send(body);
      expect(response.status).toBe(502);
      expect(s.db.provisionSemindUser).not.toHaveBeenCalled();
      expect(s.db.saveMessage).not.toHaveBeenCalled();
      expect(response.text).not.toContain('foreign');
    },
  );
  test('sends the platform server ID to authority and native scope remains absent from model arguments', async () => {
    const s = setup();
    s.db.provisionSemindUser.mockRejectedValue(new Error('DB failed with secret'));
    const response = await request(s.app)
      .post('/game/run')
      .set('Authorization', `Bearer ${internalKey}`)
      .send(body);
    expect(response.status).toBe(502);
    expect(response.text).not.toContain('secret');
    const payload = JSON.parse(s.fetcher.mock.calls[0][1].body);
    expect(payload).toEqual({
      steam_id: input.steam_id,
      server_id: platform,
      world_id: input.world_id,
    });
    expect(s.db.provisionSemindUser).toHaveBeenCalledWith({
      steamId: input.steam_id,
      name: 'Test',
      isOperator: false,
    });
  });
  test('steering a missing/replaced run does not create messages', async () => {
    const s = setup();
    expect(
      (
        await request(s.app)
          .post('/game/steer')
          .set('Authorization', `Bearer ${internalKey}`)
          .send({ task_id: 'gone', input })
      ).status,
    ).toBe(409);
    expect(s.db.saveMessage).not.toHaveBeenCalled();
  });
});
