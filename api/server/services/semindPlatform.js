const { createSemindPlatformTools } = require('@librechat/api');

module.exports = (req, signal) =>
  createSemindPlatformTools({
    req,
    gatewayURL: process.env.SEMIND_MODEL_URL,
    internalKey: process.env.SEMIND_INTERNAL_KEY,
    fetch: global.fetch,
    signal,
  });
