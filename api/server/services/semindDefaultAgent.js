const { createSemindDefaultAgent, semindGameInstructions } = require('@librechat/api');
const {
  ensureSemindDefaultAgent,
  provisionSemindUser,
  getSemindAgentSelection,
} = require('~/models');

module.exports = createSemindDefaultAgent({
  ensureSemindDefaultAgent,
  provisionSemindUser,
  getSemindAgentSelection,
  instructions: semindGameInstructions,
  provider: 'Luna',
  model: 'gpt-6-luna',
});
