/** Integration gate. Credentials stay on this host; output contains assertions only. */
const fs = require('node:fs');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');

const env = Object.fromEntries(fs.readFileSync('D:/semind-librechat/codeapi-dev/private.env', 'utf8')
  .trim().split(/\r?\n/).map((line) => { const i = line.indexOf('='); return [line.slice(0, i), line.slice(i + 1)]; }));
const key = crypto.createPrivateKey(Buffer.from(env.CODEAPI_JWT_PRIVATE_KEY_BASE64, 'base64'));
const base = 'http://127.0.0.1:49392/v1';
function token(user) {
  const now = Math.floor(Date.now() / 1000);
  const encode = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const data = `${encode({ alg: 'EdDSA', kid: env.CODEAPI_JWT_KID })}.${encode({
    iss: env.CODEAPI_JWT_ISSUER, aud: env.CODEAPI_JWT_AUDIENCE, sub: user,
    tenant_id: 'semind', principal_source: 'librechat_jwt', auth_context_hash: 'acceptance-probe',
    jti: crypto.randomUUID(), iat: now, nbf: now, exp: now + 300,
  })}`;
  return `${data}.${crypto.sign(null, Buffer.from(data), key).toString('base64url')}`;
}
async function request(user, path, body) {
  return fetch(base + path, { method: body ? 'POST' : 'GET',
    headers: { Authorization: `Bearer ${token(user)}`, 'Content-Type': 'application/json' },
    ...(body ? { body: JSON.stringify(body) } : {}), signal: AbortSignal.timeout(180000) });
}
async function main() {
  if (process.argv.includes('--restore-check')) {
    const stored = JSON.parse(fs.readFileSync('D:/semind-librechat/logs/codeapi-chart.json', 'utf8'));
    const file = stored.files.find((entry) => entry.name === 'chart.png');
    const response = await request('probe-a', `/download/${file.storage_session_id}/${file.id}?kind=user`);
    assert.equal(response.status, 200, 'artifact available after service restart');
    const bytes = Buffer.from(await response.arrayBuffer());
    assert.deepEqual(bytes, fs.readFileSync('D:/semind-librechat/logs/codeapi-chart.png'));
    console.log(JSON.stringify({ passed: true, check: 'exact artifact bytes after service restart' }));
    return;
  }
  const unauthenticated = await fetch(`${base}/exec`, { method: 'POST',
    headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ lang: 'python', code: 'print(1)' }) });
  assert.equal(unauthenticated.status, 401, 'anonymous execution rejected');
  const bash = await request('probe-a', '/exec', { lang: 'bash', code: 'echo forbidden' });
  assert.equal(bash.status, 403, 'Bash rejected');
  const outputs = await Promise.all(['probe-a', 'probe-b'].map(async (user) => {
    const response = await request(user, '/exec', { lang: 'python', code:
      `import os, json\nos.makedirs('/mnt/data', exist_ok=True)\nwith open('/mnt/data/probe.txt', 'w') as f: f.write('${user}')\nprint(json.dumps({'owner': '${user}', 'sum': sum(range(10001))}))` });
    assert.equal(response.status, 200, `execution accepted for ${user}`);
    const result = await response.json();
    fs.writeFileSync(`D:/semind-librechat/logs/codeapi-${user}.json`, JSON.stringify(result, null, 2));
    assert.equal(result.code, 0, `Python succeeded for ${user}`);
    assert.equal(JSON.parse(result.stdout.trim()).sum, 50005000);
    assert.ok(result.files?.length > 0, 'output artifact returned');
    return { user, result };
  }));
  for (const { user, result } of outputs) {
    const file = result.files.find((entry) => entry.name.endsWith('probe.txt'));
    assert.ok(file, 'text artifact present');
    const path = `/download/${encodeURIComponent(file.storage_session_id)}/${encodeURIComponent(file.id)}?kind=user`;
    const own = await request(user, path);
    assert.equal(own.status, 200, 'owner download');
    assert.equal(await own.text(), user, 'exact artifact content');
    const other = await request(user === 'probe-a' ? 'probe-b' : 'probe-a', path);
    assert.ok([403, 404].includes(other.status), 'foreign artifact denied');
  }
  for (const kind of ['user', 'agent', 'skill']) {
    const resource = 'semind-probe-resource';
    const form = new FormData();
    form.set('kind', kind);
    form.set('id', resource);
    if (kind === 'skill') form.set('version', '1');
    form.set('file', new Blob(['x,y\n1,2\n2,4\n3,6\n'], { type: 'text/csv' }), 'input.csv');
    const uploaded = await fetch(`${base}/upload`, { method: 'POST',
      headers: { Authorization: `Bearer ${token('probe-a')}` }, body: form,
      signal: AbortSignal.timeout(30000) });
    assert.equal(uploaded.status, 200, `${kind} upload`);
    const data = await uploaded.json();
    fs.writeFileSync(`D:/semind-librechat/logs/codeapi-upload-${kind}.json`, JSON.stringify(data, null, 2));
    const file = { id: data.files[0].fileId, name: 'input.csv', storage_session_id: data.storage_session_id,
      resource_id: resource, kind, ...(kind === 'skill' ? { version: 1 } : {}) };
    const stolen = await request('probe-b', '/exec', { lang: 'python', code: 'print(1)', files: [file] });
    assert.equal(stolen.status, 403, `foreign ${kind} input denied even with known resource id`);
    if (kind !== 'user') continue;
    const response = await request('probe-a', '/exec', { lang: 'python', files: [file], code:
      "import pandas as pd\nimport matplotlib\nmatplotlib.use('Agg')\nimport matplotlib.pyplot as plt\ndf = pd.read_csv('/mnt/data/input.csv')\nplt.plot(df.x, df.y)\nplt.savefig('/mnt/data/chart.png')\nprint(int(df.y.sum()))" });
    assert.equal(response.status, 200, 'chart execution');
    const chartResult = await response.json();
    fs.writeFileSync('D:/semind-librechat/logs/codeapi-chart.json', JSON.stringify(chartResult, null, 2));
    assert.equal(chartResult.code, 0, 'chart Python succeeded');
    assert.equal(chartResult.stdout.trim(), '12', 'uploaded CSV was read');
    const chart = chartResult.files.find((entry) => entry.name === 'chart.png');
    assert.ok(chart, 'chart artifact emitted');
    const download = await request('probe-a', `/download/${chart.storage_session_id}/${chart.id}?kind=user`);
    assert.equal(download.status, 200, 'chart downloaded');
    const bytes = Buffer.from(await download.arrayBuffer());
    assert.equal(bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a', 'valid PNG signature');
    fs.writeFileSync('D:/semind-librechat/logs/codeapi-chart.png', bytes);
  }
  const isolated = await request('probe-a', '/exec', { lang: 'python', code:
    "import socket, os, json\nreached=[]\nfor host,port in [('redis',6379),('host.docker.internal',49396),('169.254.169.254',80)]:\n try:\n  s=socket.create_connection((host,port),timeout=1); s.close(); reached.append(host)\n except OSError: pass\nprint(json.dumps({'reached':reached,'previous_file':os.path.exists('/mnt/data/probe.txt')}))" });
  assert.equal(isolated.status, 200, 'network isolation probe');
  const isolation = await isolated.json();
  assert.equal(isolation.code, 0, 'isolation Python succeeded');
  assert.deepEqual(JSON.parse(isolation.stdout.trim()), { reached: [], previous_file: false });
  const report = { passed: true, timestamp: new Date().toISOString(), checks: [
    'anonymous execution denied', 'Bash denied', 'two concurrent Python calculations',
    'output artifacts preserved', 'foreign artifact downloads denied',
    'CSV input and PNG chart', 'foreign user/agent/skill input references denied',
    'internal network access denied', 'new execution has a clean workspace',
  ] };
  fs.writeFileSync('D:/semind-librechat/logs/codeapi-probe-result.json', JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
}
main().catch((error) => {
  console.error(JSON.stringify({ passed: false, type: error.name,
    assertion: error.code === 'ERR_ASSERTION' ? error.message : 'CodeAPI probe failed' }));
  process.exitCode = 1;
});
