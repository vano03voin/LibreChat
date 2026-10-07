/** Prepare the pinned upstream stack without storing credentials in Git. */
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const yaml = require('js-yaml');

const root = 'D:/semind-librechat';
const source = path.join(root, 'code-interpreter');
const destination = path.join(root, 'codeapi-dev');
fs.mkdirSync(destination, { recursive: true });
const envPath = path.join(destination, 'private.env');
if (!fs.existsSync(envPath)) {
  const keys = crypto.generateKeyPairSync('ed25519');
  const manifest = crypto.generateKeyPairSync('ed25519');
  const encode = (key, type) => key.export({ format: 'der', type }).toString('base64');
  const env = {
    LOCAL_MODE: 'false',
    KVM_ENABLED: 'false',
    CODEAPI_AUTH_PROVIDER: 'librechat-jwt',
    CODEAPI_JWT_ISSUER: 'semind-librechat',
    CODEAPI_JWT_AUDIENCE: 'semind-codeapi',
    CODEAPI_JWT_ALLOWED_ALGS: 'EdDSA',
    CODEAPI_JWT_KID: 'semind-dev-v1',
    CODEAPI_JWT_PUBLIC_KEY: JSON.stringify(keys.publicKey.export({ format: 'jwk' })),
    CODEAPI_JWT_SINGLE_TENANT_ID: 'semind',
    CODEAPI_JWT_PRIVATE_KEY_BASE64: Buffer.from(keys.privateKey.export({ format: 'pem', type: 'pkcs8' })).toString('base64'),
    CODEAPI_EXECUTION_MANIFEST_PRIVATE_KEY: encode(manifest.privateKey, 'pkcs8'),
    SANDBOX_EXECUTION_MANIFEST_PUBLIC_KEY: encode(manifest.publicKey, 'spki'),
    CODEAPI_INTERNAL_SERVICE_TOKEN: crypto.randomBytes(48).toString('hex'),
    CODEAPI_EGRESS_GRANT_SECRET: crypto.randomBytes(48).toString('hex'),
    SEMIND_REDIS_PASSWORD: crypto.randomBytes(32).toString('hex'),
    SEMIND_MINIO_PASSWORD: crypto.randomBytes(32).toString('hex'),
  };
  fs.writeFileSync(envPath, Object.entries(env).map(([key, value]) => `${key}=${value}`).join('\n') + '\n', { flag: 'wx', mode: 0o600 });
}

const compose = yaml.load(fs.readFileSync(path.join(source, 'docker-compose.yaml'), 'utf8'));
const mac = yaml.load(fs.readFileSync(path.join(source, 'docker-compose.mac.yml'), 'utf8'));
compose.name = 'semind-codeapi-dev';
delete compose.services.tool_call_server;
for (const [name, service] of Object.entries(compose.services)) {
  delete service.container_name;
  delete service.ports;
  if (service.build) {
    service.build.context = source;
    service.build.args = { http_proxy: 'http://host.docker.internal:10809', https_proxy: 'http://host.docker.internal:10809' };
    service.image = `semind-codeapi-${name}:1.10.4`;
  }
  if (Array.isArray(service.environment)) {
    service.environment = Object.fromEntries(service.environment.map((entry) => {
      const separator = entry.indexOf('=');
      return [entry.slice(0, separator), entry.slice(separator + 1)];
    }));
    for (const key of Object.keys(service.environment)) {
      if (key.startsWith('CODEAPI_BRIDGE_') || key === 'TOOL_CALL_SERVER_URL' || key === 'EGRESS_GATEWAY_TOOL_CALL_SERVER_URL') {
        delete service.environment[key];
      }
    }
    if ('REDIS_PASSWORD' in service.environment) service.environment.REDIS_PASSWORD = '${SEMIND_REDIS_PASSWORD:?required}';
    if ('MINIO_SECRET_KEY' in service.environment) service.environment.MINIO_SECRET_KEY = '${SEMIND_MINIO_PASSWORD:?required}';
    if ('MINIO_ROOT_PASSWORD' in service.environment) service.environment.MINIO_ROOT_PASSWORD = '${SEMIND_MINIO_PASSWORD:?required}';
  }
  if (Array.isArray(service.depends_on)) service.depends_on = service.depends_on.filter((item) => item !== 'tool_call_server');
  service.restart = 'unless-stopped';
  service.logging = { driver: 'json-file', options: { 'max-size': '10m', 'max-file': '3' } };
}
const runner = compose.services['sandbox-runner'];
runner.build.target = 'sandbox-build';
runner.image = 'semind-code-runner:1.10.4';
runner.entrypoint = mac.services['sandbox-runner'].entrypoint;
delete runner.devices;
runner.volumes = ['packages:/pkgs:ro'];
runner.security_opt = [`seccomp=${source}/seccomp/nsjail.json`];
runner.cap_add = mac.services['sandbox-runner'].cap_add;
runner.healthcheck = { ...runner.healthcheck, ...mac.services['sandbox-runner'].healthcheck };
Object.assign(runner.environment, {
  KVM_ENABLED: 'false',
  SANDBOX_USE_CGROUPV2: 'false',
  SANDBOX_REMOVE_UMOUNT_AFTER_STARTUP: 'false',
  SANDBOX_MAX_CONCURRENT_JOBS: '2',
  SANDBOX_RUN_TIMEOUT: '120000',
  SANDBOX_RUN_CPU_TIME: '60000',
});
compose.services.api.ports = ['127.0.0.1:49392:3112'];
compose.services.api.environment.CODEAPI_SEMIND_PROFILE = 'true';
compose.services.redis.command = ['redis-server', '--requirepass', '${SEMIND_REDIS_PASSWORD:?required}', '--appendonly', 'yes'];
compose.services.redis.image = 'redis:7.4.2-alpine';
compose.services.minio.image = 'semind-minio:2025-10-15';
compose.services['service-worker'].environment.PYTHON_CONCURRENCY = '2';
compose.services['service-worker'].environment.OTHER_CONCURRENCY = '1';
compose.volumes.packages = {};
fs.writeFileSync(path.join(destination, 'compose.json'), JSON.stringify(compose, null, 2) + '\n');
console.log('Prepared isolated Code API configuration. Credentials were not printed.');
