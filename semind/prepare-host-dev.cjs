const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const yaml = require('js-yaml');
const dotenv = require('dotenv');
const root = 'D:/semind-librechat';
const target = path.join(root, 'librechat-dev');
fs.mkdirSync(target, { recursive: true });
const site = dotenv.parse(
  fs.readFileSync('C:/Users/lena0/Desktop/Cursor/se-mind-project/server/.env'),
);
const code = dotenv.parse(fs.readFileSync(path.join(root, 'codeapi-dev/private.env')));
const envPath = path.join(target, 'private.env');
if (!fs.existsSync(envPath)) {
  const env = {
    HOST: '127.0.0.1',
    PORT: '49390',
    NODE_ENV: 'production',
    DOMAIN_CLIENT: 'http://127.0.0.1:49390',
    DOMAIN_SERVER: 'http://127.0.0.1:49390',
    MONGO_URI: 'mongodb://127.0.0.1:49394/LibreChat',
    REDIS_URI: 'redis://127.0.0.1:49395',
    USE_REDIS: 'true',
    USE_REDIS_STREAMS: 'true',
    SEARCH: 'false',
    ALLOW_REGISTRATION: 'false',
    ALLOW_EMAIL_LOGIN: 'false',
    ALLOW_SOCIAL_LOGIN: 'false',
    ALLOW_PASSWORD_RESET: 'false',
    JWT_SECRET: crypto.randomBytes(48).toString('hex'),
    JWT_REFRESH_SECRET: crypto.randomBytes(48).toString('hex'),
    CREDS_KEY: crypto.randomBytes(32).toString('hex'),
    CREDS_IV: crypto.randomBytes(16).toString('hex'),
    CONFIG_PATH: path.join(target, 'librechat.yaml'),
    SEMIND_INTERNAL_KEY: site.SEMIND_ASSISTANT_INTERNAL_KEY,
    SEMIND_MODEL_KEY: fs.readFileSync(path.join(root, 'gateway-dev/grant.key')).toString('base64'),
    SEMIND_MODEL_URL: 'http://127.0.0.1:49391/model/v1',
    CODE_INTERPRETER_DOMAIN: 'http://127.0.0.1:49392',
    CODEAPI_AUTH_PROVIDER: 'librechat-jwt',
    CODEAPI_JWT_ENABLED: 'true',
    CODEAPI_JWT_PRIVATE_KEY_BASE64: code.CODEAPI_JWT_PRIVATE_KEY_BASE64,
    CODEAPI_JWT_ALGORITHM: 'EdDSA',
    CODEAPI_JWT_KID: code.CODEAPI_JWT_KID,
    CODEAPI_JWT_ISSUER: code.CODEAPI_JWT_ISSUER,
    CODEAPI_JWT_AUDIENCE: code.CODEAPI_JWT_AUDIENCE,
    CODEAPI_JWT_SINGLE_TENANT_ID: 'semind',
  };
  if (!env.SEMIND_INTERNAL_KEY) throw new Error('SE-mind dev internal key is missing');
  fs.writeFileSync(
    envPath,
    Object.entries(env)
      .map(([k, v]) => `${k}=${v}`)
      .join('\n') + '\n',
    { flag: 'wx', mode: 0o600 },
  );
}
const config = yaml.load(fs.readFileSync(path.join(__dirname, 'librechat.yaml'), 'utf8'));
config.semind = {
  enabled: true,
  apiURL: 'http://127.0.0.1:49396',
  portalURL: 'https://127.0.0.1.sslip.io/assistant-login',
  sessionRecheckSeconds: 30,
  libraryImportTimeoutSeconds: 240,
};
fs.writeFileSync(path.join(target, 'librechat.yaml'), yaml.dump(config));
fs.writeFileSync(
  path.join(target, 'site-ports.json'),
  JSON.stringify({ services: { api: { ports: ['127.0.0.1:49396:8000'] } } }, null, 2),
);
console.log('Private host-dev configuration prepared. Credentials were not printed.');
