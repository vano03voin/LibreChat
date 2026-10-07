import { z } from 'zod';
import { Socket } from 'node:net';
import { tool } from '@librechat/agents/langchain/tools';
import { IncomingMessage, ServerResponse } from 'node:http';
import { Permissions, PermissionTypes } from 'librechat-data-provider';
import type { DynamicStructuredTool } from '@librechat/agents/langchain/tools';
import type { ScheduleMethods } from '@librechat/data-schemas';
import type { Response } from 'express';
import type { CheckAccessParams } from '~/middleware/access';
import type { ScheduleEngineDeps } from '~/schedules/types';
import type { SemindConfig } from './session';
import type { ServerRequest } from '~/types';
import { createSchedulesHandlers } from '~/schedules/handlers';
import { checkAccess } from '~/middleware/access';

export type SemindScheduleAction = 'list' | 'create' | 'update' | 'pause' | 'delete';
export type SemindScheduleExecute = (
  action: SemindScheduleAction,
  args: Record<string, unknown>,
) => Promise<object>;

export function createSemindScheduleTool({
  execute,
}: {
  execute: SemindScheduleExecute;
}): DynamicStructuredTool {
  return tool(
    async ({ action, args_json }) => {
      let value: unknown;
      try {
        value = JSON.parse(args_json);
      } catch {
        return JSON.stringify({ ok: false, error: { code: 'SEMIND_SCHEDULE_ARGUMENTS_INVALID' } });
      }
      const parsed = z.record(z.unknown()).safeParse(value);
      if (!parsed.success)
        return JSON.stringify({ ok: false, error: { code: 'SEMIND_SCHEDULE_ARGUMENTS_INVALID' } });
      return JSON.stringify(await execute(action, parsed.data));
    },
    {
      name: 'semind_schedule',
      description:
        'Manage only your own recurring tasks: list, create, update, pause, delete. Creation needs name,prompt,agent_id,cadence,timezone,clientRequestId. Use executionScope="game" for any game read or control; the current server/world are assigned by the trusted host. Use executionScope="profile" for general tasks. Cadence is {frequency:"hourly",hour:0,minute:0} or {frequency:"cron",expression:"0 * * * *"}. At least one hour between runs; max 10 tasks. Results appear as new private website chats, never as game chat. Update/pause/delete need id; update should include expectedConfigRevision from list.',
      schema: z.object({
        action: z.enum(['list', 'create', 'update', 'pause', 'delete']),
        args_json: z.string(),
      }),
    },
  );
}

/** Read the scope from the owned durable task, never from its prompt or metadata. */
export function createSemindScheduledRequestScope(options: {
  getScheduleById: ScheduleMethods['getScheduleById'];
}): (req: ServerRequest) => Promise<{ server_id: string; world_id: string } | null> {
  return async (req: ServerRequest): Promise<{ server_id: string; world_id: string } | null> => {
    const body = req.body as Record<string, unknown>;
    const trigger = z
      .object({
        event: z.object({ source: z.object({ type: z.string(), id: z.string() }) }),
        metadata: z.object({ configRevision: z.number().optional() }).passthrough().optional(),
      })
      .safeParse(body.agentTrigger);
    if (!trigger.success || trigger.data.event.source.type !== 'schedule') return null;
    if (!req.user?.id) throw new Error('semind_schedule_owner_required');
    const schedule = await options.getScheduleById(trigger.data.event.source.id, req.user.id);
    if (!schedule || String(schedule.user) !== req.user.id || schedule.deleting)
      throw new Error('semind_schedule_not_owned');
    if (
      trigger.data.metadata?.configRevision != null &&
      trigger.data.metadata.configRevision !== schedule.configRevision
    )
      throw new Error('semind_schedule_revision_changed');
    if (schedule.executionScope !== 'game') return null;
    if (!schedule.semindContext?.server_id || !schedule.semindContext.world_id)
      throw new Error('semind_schedule_scope_missing');
    return { ...schedule.semindContext };
  };
}

export function createSemindScheduleActions(options: {
  req: ServerRequest;
  handlers: ReturnType<typeof createSchedulesHandlers>;
  getRoleByName: CheckAccessParams['getRoleByName'];
  defaultAgentId?: string;
}): SemindScheduleExecute {
  return async (action, args) => {
    const { req } = options;
    if (!req.config?.config?.semind?.enabled || !req.user)
      return { ok: false, error: { code: 'SEMIND_SCHEDULE_DISABLED' } };
    if (
      ['user', 'userId', 'user_id', 'steam_id', 'server_id', 'world_id', 'semindContext'].some(
        (key) => Object.prototype.hasOwnProperty.call(args, key),
      )
    )
      return { ok: false, error: { code: 'SEMIND_OWNER_ARGUMENTS_REJECTED' } };
    const allowed = await checkAccess({
      user: req.user,
      permissionType: PermissionTypes.SCHEDULES,
      permissions: action === 'list' ? [Permissions.USE] : [Permissions.USE, Permissions.CREATE],
      getRoleByName: options.getRoleByName,
    });
    if (!allowed) return { ok: false, error: { code: 'SEMIND_SCHEDULE_PERMISSION_REQUIRED' } };
    const { id, ...payload } = args;
    if (action === 'create' && !payload.agent_id && options.defaultAgentId)
      payload.agent_id = options.defaultAgentId;
    if (action !== 'list' && action !== 'create' && (typeof id !== 'string' || !id))
      return { ok: false, error: { code: 'SEMIND_SCHEDULE_ID_REQUIRED' } };
    const request: ServerRequest = Object.assign(Object.create(req), {
      body: action === 'pause' ? { enabled: false } : payload,
      params: { id },
      query: {},
    });
    const response = new ServerResponse(new IncomingMessage(new Socket())) as Response;
    let body: object = {};
    response.status = (status) => {
      response.statusCode = status;
      return response;
    };
    response.json = (value) => {
      body = value;
      return response;
    };
    response.set = () => response;
    const handler = {
      list: options.handlers.listSchedules,
      create: options.handlers.createSchedule,
      update: options.handlers.updateSchedule,
      pause: options.handlers.updateSchedule,
      delete: options.handlers.deleteSchedule,
    }[action];
    try {
      await handler(request, response);
      return { ok: response.statusCode < 400, status: response.statusCode, result: body };
    } finally {
      response.emit('finish');
    }
  };
}

export function createSemindScheduleValidator(options: {
  getConfig: () => Promise<SemindConfig | undefined>;
  internalKey: string;
  fetch: typeof fetch;
}): NonNullable<ScheduleEngineDeps['validateSemindGame']> {
  return async (schedule, owner) => {
    if (!schedule.semindContext?.world_id || !owner.semindSteamId) return 'permission_revoked';
    const config = await options.getConfig();
    if (!config?.enabled || !options.internalKey)
      throw new Error('semind_schedule_authority_unavailable');
    const response = await options.fetch(
      new URL('/internal/assistant/game/identity', config.apiURL),
      {
        method: 'POST',
        redirect: 'error',
        headers: {
          Authorization: `Bearer ${options.internalKey}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ ...schedule.semindContext, steam_id: owner.semindSteamId }),
        signal: AbortSignal.timeout(5000),
      },
    );
    if (response.status === 409) return 'world_changed';
    if (response.status === 403 || response.status === 404) return 'permission_revoked';
    if (!response.ok) throw new Error('semind_schedule_authority_unavailable');
    return 'ok';
  };
}
