const { createSemindScheduleActions } = require('@librechat/api');
const { getRoleByName } = require('~/models');

module.exports = (req, defaultAgentId) =>
  createSemindScheduleActions({
    req,
    handlers: require('~/server/routes/schedules').handlers,
    getRoleByName,
    defaultAgentId,
  });
