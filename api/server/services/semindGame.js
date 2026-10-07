const { createSemindGameRouter } = require('@librechat/api');
const { getAppConfig } = require('./Config');
const db = require('~/models');
const createScheduleExecute = require('./semindSchedules');
const { ensure: ensureDefaultAgent } = require('./semindDefaultAgent');
const { createToolLoader } = require('./Endpoints/agents/initialize');
const { findAccessibleResources } = require('./PermissionService');
const {
  getSkillDbMethods,
  getSkillToolDeps,
  withDeploymentSkillIds,
  buildAgentToolContext,
  enrichLoadedToolsWithAgentContext,
} = require('./Endpoints/agents/skillDeps');
const { loadToolsForExecution } = require('./ToolService');
const { createToolEndCallback } = require('../controllers/agents/callbacks');
const { createProvisionFilesCallback } = require('./Files/provisionCallback');
const { filterFilesByAgentAccess } = require('./Files/permissions');
const {
  provisionToCodeEnv,
  provisionToVectorDB,
  checkSessionsAlive,
  loadCodeApiKey,
} = require('./Files/provision');

module.exports = createSemindGameRouter({
  internalKey: process.env.SEMIND_INTERNAL_KEY,
  gatewayURL: process.env.SEMIND_MODEL_URL,
  fetch: global.fetch,
  getConfig: (actor) => getAppConfig(actor),
  db,
  createScheduleExecute,
  memory: { methods: db, getRoleByName: db.getRoleByName },
  ensureDefaultAgent,
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
    loadToolsForExecution: async (
      req,
      agent,
      config,
      toolNames,
      signal,
      callerCapabilityProjection,
    ) => {
      const ctx = buildAgentToolContext({ agent, config });
      const result = await loadToolsForExecution({
        req,
        res: null,
        signal,
        conversationId: req.body.conversationId,
        requestBody: req.body,
        toolNames,
        agent,
        toolRegistry: ctx.toolRegistry,
        callerCapabilityProjection,
        backgroundToolNames: ctx.backgroundToolNames,
        intentToolNames: ctx.intentToolNames,
        mcpAvailableTools: ctx.mcpAvailableTools,
        requestScopedConnections: ctx.requestScopedConnections,
        userMCPAuthMap: ctx.userMCPAuthMap,
        tool_resources: ctx.tool_resources,
        actionsEnabled: ctx.actionsEnabled,
        accessibleMcpServerNames: ctx.accessibleMcpServerNames,
      });
      return enrichLoadedToolsWithAgentContext({ result, req, ctx });
    },
    toolExecuteOptions: (req, artifacts, agent, config) => ({
      ...getSkillToolDeps(),
      provisionFiles: createProvisionFilesCallback({
        req,
        agentToolContexts: new Map([[agent.id, buildAgentToolContext({ agent, config })]]),
        resolvePrimaryAgentId: () => agent.id,
      }),
      toolEndCallback: createToolEndCallback({
        req,
        res: { headersSent: false, writableEnded: true },
        artifactPromises: artifacts,
      }),
    }),
  },
});
