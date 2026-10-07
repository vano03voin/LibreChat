const assert = require('node:assert/strict');
const fs = require('node:fs');
const crypto = require('node:crypto');
const { getChatModelClass } = require('@librechat/agents');
const { HumanMessage, ToolMessage } = require('@langchain/core/messages');
const { initializeCustom } = require('@librechat/api');
const yaml = require('js-yaml');

const encode = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
const issued = Math.floor(Date.now() / 1000);
const header = encode({ alg: 'HS256', typ: 'JWT' });
const payload = encode({ iss: 'semind-librechat', aud: 'semind-model', sub: 'development-probe', steam_id: '76561198000000001', iat: issued, exp: issued + 300 });
const signed = header + '.' + payload;
const key = fs.readFileSync('D:/semind-librechat/gateway-dev/grant.key');
const grant = signed + '.' + crypto.createHmac('sha256', key).update(signed).digest('base64url');

async function main() {
  const Model = getChatModelClass('openAI');
  const config = yaml.load(fs.readFileSync('D:/semind-librechat/librechat-dev/librechat.yaml', 'utf8'));
  config.endpoints.custom[0].apiKey = key.toString('base64');
  config.endpoints.custom[0].baseURL = 'http://127.0.0.1:49391/model/v1';
  const initialized = await initializeCustom({ endpoint: 'Luna', model_parameters: { model: 'gpt-6-luna' },
    runtime: { appConfig: { config, endpoints: config.endpoints, fileStrategy: 'local' },
      user: { id: 'development-probe', semindSteamId: '76561198000000001' }, requestBody: {} },
    db: { getUserKey: async () => { throw new Error('Unexpected personal credential access'); },
      getUserKeyValues: async () => { throw new Error('Unexpected personal credential access'); } },
  });
  const model = new Model({
    ...initialized.llmConfig,
    model: 'gpt-6-luna',
    apiKey: 'owner-grant',
    useResponsesApi: true,
    zdrEnabled: true,
    streaming: true,
    temperature: 1,
    maxRetries: 0,
    configuration: initialized.configOptions,
    modelKwargs: { instructions: '', store: false, reasoning: { effort: 'low' } },
  });
  const tool = { type: 'function', function: {
    name: 'read_probe_value',
    description: 'Read the fixed verification number.',
    parameters: { type: 'object', properties: {}, required: [], additionalProperties: false },
  } };
  const messages = [new HumanMessage('Call read_probe_value once. Then respond with the returned number only.')];
  const first = await model.bindTools([tool]).invoke(messages);
  assert.equal(first.tool_calls?.length, 1);
  assert.equal(first.tool_calls[0].name, 'read_probe_value');
  const second = await model.invoke([...messages, first, new ToolMessage({ content: '41827', tool_call_id: first.tool_calls[0].id })]);
  assert.match(JSON.stringify(second.content), /41827/);
  const plain = await fetch('http://127.0.0.1:49391/model/v1/responses', {
    method: 'POST', headers: { Authorization: 'Bearer ' + grant, 'Content-Type': 'application/json' },
    body: JSON.stringify({ model: 'gpt-6-luna', instructions: '', input: [{ role: 'user', content: 'Reply exactly OK' }], stream: false }),
  });
  assert.equal(plain.status, 200);
  const result = await plain.json();
  assert.equal(result.status, 'completed');
  fs.writeFileSync('D:/semind-librechat/logs/luna-probe-result.json', JSON.stringify({
    verifiedAt: new Date().toISOString(), library: '@librechat/agents', model: 'gpt-6-luna',
    functionCall: first.tool_calls[0].name, toolResultRead: true, nonStreamingCompleted: true,
    customEndpointOwnerTransport: true,
  }, null, 2));
  console.log('PASS: Luna native Responses tool round-trip and nonstreaming response');
}
main().catch((error) => {
  // SDK exceptions may embed headers. Never print the exception or its request.
  console.error('FAIL:', error.name, Number.isInteger(error.status) ? error.status : 'no HTTP status');
  console.error(String(error.stack ?? '').split('\n').filter((line) => /^\s+at /.test(line)).slice(0, 8).join('\n'));
  process.exitCode = 1;
});
