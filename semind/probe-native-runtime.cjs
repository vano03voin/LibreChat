const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const dotenv = require('dotenv');
const privateConfig = dotenv.parse(
  fs.readFileSync('D:/semind-librechat/librechat-dev/private.env'),
);
Object.assign(process.env, privateConfig);
const networkFetch = global.fetch;
let modelRequestSequence = 0;
const modelProgress = [];
global.fetch = async (url, options) => {
  let progress;
  if (String(url).includes('/model/v1/responses') && options?.body) {
    progress = { request: ++modelRequestSequence, started_at: new Date().toISOString(), bytes: 0, events: [] };
    modelProgress.push(progress);
    fs.writeFileSync('D:/semind-librechat/logs/native-model-progress.json', JSON.stringify(modelProgress, null, 2));
    const payload = JSON.parse(options.body);
    const summary = {
      keys: Object.keys(payload),
      options: Object.fromEntries(
        Object.entries(payload).filter(
          ([key]) =>
            !['input', 'instructions', 'tools', 'metadata', 'prompt_cache_key', 'user'].includes(
              key,
            ),
        ),
      ),
      inputs: payload.input?.map((item) => ({
        type: item.type,
        role: item.role,
        keys: Object.keys(item),
      })),
      tools: payload.tools,
    };
    fs.writeFileSync(
      'D:/semind-librechat/logs/native-model-structure.json',
      JSON.stringify(summary, null, 2),
    );
  }
  const response = await networkFetch(url, options);
  if (!progress || !response.body) return response;
  progress.status = response.status;
  progress.headers_at = new Date().toISOString();
  fs.writeFileSync('D:/semind-librechat/logs/native-model-progress.json', JSON.stringify(modelProgress, null, 2));
  return new Response(response.body.pipeThrough(new TransformStream({
    transform(chunk, controller) {
      progress.bytes += chunk.length;
      progress.last_bytes_at = new Date().toISOString();
      const text = new TextDecoder().decode(chunk);
      const events = text.match(/response\.[a-z_.]+/g) ?? [];
      progress.events = [...new Set([...progress.events, ...events])];
      fs.writeFileSync('D:/semind-librechat/logs/native-model-progress.json', JSON.stringify(modelProgress, null, 2));
      controller.enqueue(chunk);
    }
  })), { status: response.status, headers: response.headers });
};
process.chdir(path.resolve(__dirname, '..'));
require('module-alias')({ base: path.resolve(__dirname, '../api') });
require('../api/config/credentials');
const express = require('express');
const request = require('supertest');
const mongoose = require('mongoose');
const { createSemindGameRouter } = require('@librechat/api');
const { connectDb } = require('../api/db');
const { getAppConfig } = require('../api/server/services/Config');
const db = require('../api/models');
const { ensure } = require('../api/server/services/semindDefaultAgent');
const { createToolLoader } = require('../api/server/services/Endpoints/agents/initialize');
const { findAccessibleResources } = require('../api/server/services/PermissionService');
const {
  getSkillDbMethods,
  withDeploymentSkillIds,
} = require('../api/server/services/Endpoints/agents/skillDeps');
const { filterFilesByAgentAccess } = require('../api/server/services/Files/permissions');
const {
  provisionToCodeEnv,
  provisionToVectorDB,
  checkSessionsAlive,
  loadCodeApiKey,
} = require('../api/server/services/Files/provision');
const createScheduleExecute = require('../api/server/services/semindSchedules');

async function main() {
  await connectDb();
  const config = await getAppConfig();
  const nativeId = 'native-component-probe';
  const platformId = crypto.randomUUID();
  config.config.semind.gameServerIds = {
    ...config.config.semind.gameServerIds,
    [nativeId]: platformId,
  };
  const world = crypto.randomUUID();
  const records = ['76561198000009901', '76561198000009902'].map((steam, index) => ({
    steam,
    marker: `SEMIND_PRIVATE_OWNER_${index}_${crypto.randomBytes(5).toString('hex')}`,
    task: crypto.randomUUID(),
    conversation: crypto.randomUUID(),
    calls: 0,
  }));
  const byTask = new Map(records.map((record) => [record.task, record]));
  const fetcher = async (url, options) => {
    const route = new URL(url).pathname;
    const body = JSON.parse(options.body);
    if (route === '/internal/assistant/game/identity') {
      const owner = records.find((record) => record.steam === body.steam_id);
      assert(owner);
      assert.equal(body.server_id, platformId);
      assert.equal(body.world_id, world);
      return Response.json({
        steam_id: owner.steam,
        display_name: 'Native component probe',
        is_operator: false,
        server_id: platformId,
        world_id: world,
      });
    }
    assert.equal(route, '/internal/game/tool');
    const owner = byTask.get(body.task_id);
    assert(owner);
    assert.equal(body.name, 'player.position');
    assert.deepEqual(body.args, {});
    owner.calls++;
    return Response.json({ ok: true, result: { probe_value: owner.marker } });
  };
  const app = express();
  app.use(express.json());
  app.use(
    '/probe',
    createSemindGameRouter({
      internalKey: privateConfig.SEMIND_INTERNAL_KEY,
      gatewayURL: privateConfig.SEMIND_MODEL_URL,
      onError(error, stage) {
        let diagnostic = `${stage}: ${error.stack ?? error.name}`;
        for (const value of Object.values(privateConfig).filter((value) => value.length > 12))
          diagnostic = diagnostic.replaceAll(value, '[private]');
        console.error('COMPONENT DIAGNOSTIC:', diagnostic);
      },
      fetch: fetcher,
      getConfig: async () => config,
      db,
      ensureDefaultAgent: ensure,
      createScheduleExecute,
      memory: { methods: db, getRoleByName: db.getRoleByName },
      agentRuntime: {
        db: {
          ...db,
          ...getSkillDbMethods(),
          filterFilesByAgentAccess,
          provisionToCodeEnv,
          provisionToVectorDB,
          checkSessionsAlive,
          loadCodeApiKey,
        },
        createToolLoader,
        findAccessibleResources,
        withDeploymentSkillIds,
      },
    }),
  );
  const attempts = await Promise.allSettled(
    records.map(async (owner) => {
      const response = await request(app)
        .post('/probe/run')
        .set('Authorization', `Bearer ${privateConfig.SEMIND_INTERNAL_KEY}`)
        .send({
          task_id: owner.task,
          conversation_id: owner.conversation,
          history: [],
          input: {
            input_id: owner.task,
            server_id: nativeId,
            world_id: world,
            steam_id: owner.steam,
            identity_id: '1',
            source_run_id: 'component-probe',
            source_sequence: 1,
            modality: 'text',
            text: 'Это изолированная проверка личного канала. Вызови game_execute ровно один раз: operation="player.position", args_json="{}". Выведи только значение поля probe_value из результата. Другие инструменты не вызывай.',
          },
        });
      assert.equal(response.status, 200);
      assert.equal(response.body.ok, true);
      assert.equal(owner.calls, 1);
      assert(response.body.text.includes(owner.marker));
      const foreign = records.find((candidate) => candidate !== owner);
      assert(!response.body.text.includes(foreign.marker));
      assert.equal(response.body.history[0].type, 'human');
      assert(response.body.history[0].data.content.includes('Это изолированная проверка личного канала'));
      const followTask = crypto.randomUUID();
      byTask.set(followTask, owner);
      const followPayload = { task_id: followTask, conversation_id: owner.conversation,
        parent_message_id: response.body.message_id, history: response.body.history,
        agent_selection_initialized: true, selected_agent_id: response.body.selected_agent_id,
        input: { input_id: followTask, server_id: nativeId, world_id: world, steam_id: owner.steam, identity_id: '1',
          source_run_id: 'component-probe', source_sequence: 2, modality: 'text',
          text: 'Теперь без вызова инструментов повтори точное значение probe_value, которое получил в предыдущем ходе. Затем напиши, что исходная просьба была изолированной проверкой личного канала.' } };
      const follow = await request(app).post('/probe/run').set('Authorization', `Bearer ${privateConfig.SEMIND_INTERNAL_KEY}`).send(followPayload);
      assert.equal(follow.status, 200); assert.equal(follow.body.ok, true);
      assert.equal(owner.calls, 1); assert(follow.body.text.includes(owner.marker));
      assert(!follow.body.text.includes(foreign.marker)); assert(/личного канала|изолированн/iu.test(follow.body.text));
      const replay = await request(app).post('/probe/run').set('Authorization', `Bearer ${privateConfig.SEMIND_INTERNAL_KEY}`).send(followPayload);
      assert.equal(replay.status, 200); assert.equal(replay.body.message_id, follow.body.message_id); assert.equal(owner.calls, 1);
      const conflict = await request(app).post('/probe/run').set('Authorization', `Bearer ${privateConfig.SEMIND_INTERNAL_KEY}`).send({ ...followPayload, input: { ...followPayload.input, steam_id: foreign.steam } });
      assert.equal(conflict.status, 409); assert.equal(conflict.body.code, 'SEMIND_GAME_INPUT_CONFLICT');
      const user = await db.provisionSemindUser({
        steamId: owner.steam,
        name: 'Native component probe',
        isOperator: false,
      });
      owner.user = user.id;
      const messages = await db.getMessages({ conversationId: owner.conversation, user: user.id });
      assert.equal(messages.length, 4);
      const reply = messages.find((message) => message.messageId === response.body.message_id);
      assert(reply.text.includes(owner.marker));
      assert(JSON.stringify(reply.content).includes('game_execute'));
      assert(!JSON.stringify(reply).includes(foreign.marker));
      assert.equal(
        (await db.getMessages({ conversationId: foreign.conversation, user: user.id })).length,
        0,
      );
      return {
        steamId: owner.steam,
        userId: user.id,
        taskId: owner.task,
        conversationId: owner.conversation,
        actualAgentId: response.body.selected_agent_id,
        nativeToolCalls: owner.calls,
        replySaved: true,
        nativeToolContentSaved: true,
        foreignHistoryDenied: true,
        historyMessages: response.body.history.length,
        followUpHistoryMessages: follow.body.history.length,
        consecutiveTurns: 2, userCommandAndToolResultRecalled: true, replayDidNotRerun: true, cachedForeignActorDenied: true,
      };
    }),
  );
  const results = attempts.map((attempt) => {
    if (attempt.status === 'rejected') throw attempt.reason;
    return attempt.value;
  });
  assert.notEqual(results[0].actualAgentId, results[1].actualAgentId);
  fs.writeFileSync(
    'D:/semind-librechat/logs/native-runtime-component.json',
    JSON.stringify(
      {
        verifiedAt: new Date().toISOString(),
        acceptance: 'COMPONENT_ONLY',
        actualGameInputSubmitted: false,
        actualGameActionsExecuted: false,
        pipeline:
          'initializeAgent -> createToolLoader -> createRun -> real Luna -> external read response -> private Mongo history',
        concurrentOwners: results,
      },
      null,
      2,
    ),
  );
  console.log(
    'PASS: two concurrent native agent Runs, distinct owner agents, real Luna tools and isolated persisted conversations. No game requests/actions.',
  );
  await mongoose.disconnect();
  process.exit(0);
}
main().catch(async (error) => {
  console.error(
    'FAIL:',
    error.name,
    typeof error.code === 'string' ? error.code : 'native component check failed',
  );
  await mongoose.disconnect();
  process.exit(1);
});
