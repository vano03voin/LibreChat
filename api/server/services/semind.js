const {
  createSemindAuthority,
  createSemindScheduledRequestScope,
  isAgentTriggerRequest,
} = require('@librechat/api');
const { getScheduleById } = require('~/models');
const { getAppConfig } = require('./Config');

module.exports = createSemindAuthority({
  getConfig: async () => (await getAppConfig({ baseOnly: true })).config?.semind,
  internalKey: process.env.SEMIND_INTERNAL_KEY,
  fetch: global.fetch,
  getUserSteamId: (req) => req.user?.semindSteamId,
  isTrustedTrigger: isAgentTriggerRequest,
  getTrustedGameScope: createSemindScheduledRequestScope({ getScheduleById }),
});
