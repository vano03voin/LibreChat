import { z } from 'zod';
import { randomUUID } from 'node:crypto';
import type { DynamicStructuredTool } from '@langchain/core/tools';
import type { ServerRequest } from '~/types';
import { createSemindGameTools } from './game';

const requestTools = new WeakMap<ServerRequest, ReturnType<typeof createSemindGameTools>>();
const requestClosures = new WeakMap<ServerRequest, (canceled: boolean) => Promise<void>>();

export async function closeSemindPlatformSession(
  req: ServerRequest,
  canceled = false,
): Promise<void> {
  const close = requestClosures.get(req);
  requestClosures.delete(req);
  if (close) await close(canceled);
}

/** The same game tools serve website and scheduled Runs with host-owned identity. */
export function createSemindPlatformTools(options: {
  req: ServerRequest;
  gatewayURL: string;
  internalKey: string;
  fetch: typeof fetch;
  signal?: AbortSignal;
}): DynamicStructuredTool[] {
  const { req } = options;
  const existing = requestTools.get(req);
  if (existing) return existing;
  const inputId = (req.body as Record<string, unknown>).messageId;
  const taskId = `web:${typeof inputId === 'string' && /^[A-Za-z0-9:_-]{1,100}$/.test(inputId) ? inputId : randomUUID()}`;
  let prepared: Promise<unknown> | undefined;
  let operation = 0;
  async function post(
    route: string,
    body: object,
    useRunSignal = true,
    baseURL = options.gatewayURL,
    timeoutMs = 30000,
  ) {
    const timeout = AbortSignal.timeout(timeoutMs);
    const signal =
      useRunSignal && options.signal ? AbortSignal.any([timeout, options.signal]) : timeout;
    const response = await options.fetch(new URL(route, baseURL), {
      method: 'POST',
      redirect: 'error',
      headers: {
        Authorization: `Bearer ${options.internalKey}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(body),
      signal,
    });
    if (!response.ok) throw new Error('semind_game_service_rejected');
    return response.json();
  }
  const tools = createSemindGameTools({
    execute: async (name, argsJSON) => {
      if (
        !req.config?.config?.semind?.enabled ||
        !options.internalKey ||
        !req.user?.semindSteamId ||
        !/^[0-9]{17}$/.test(req.user.semindSteamId) ||
        (req.semindIdentity && req.user.semindSteamId !== req.semindIdentity.steam_id)
      )
        return JSON.stringify({ ok: false, error: { code: 'SEMIND_GAME_SCOPE_REQUIRED' } });
      let args: Record<string, unknown>;
      try {
        args = z.record(z.unknown()).parse(JSON.parse(argsJSON));
      } catch {
        return JSON.stringify({ ok: false, error: { code: 'SEMIND_GAME_ARGUMENTS_INVALID' } });
      }
      if (
        Object.keys(args).some((key) =>
          [
            'user',
            'userId',
            'steam_id',
            'server_id',
            'world_id',
            'identity_id',
            'task_id',
            'source_run_id',
          ].includes(key),
        )
      )
        return JSON.stringify({ ok: false, error: { code: 'SEMIND_OWNER_ARGUMENTS_REJECTED' } });
      const action = name.startsWith('script.library.')
        ? name.slice('script.library.'.length)
        : undefined;
      if (
        action &&
        ['import', 'list', 'read', 'edit', 'restore', 'compare', 'versions'].includes(action)
      ) {
        if (!req.semindIdentity) {
          const identity = z
            .object({ steam_id: z.string().regex(/^[0-9]{17}$/) })
            .parse(
              await post(
                '/internal/assistant/profile/identity',
                { steam_id: req.user.semindSteamId },
                true,
                req.config.config.semind.apiURL,
              ),
            );
          if (identity.steam_id !== req.user.semindSteamId)
            throw new Error('semind_profile_scope_mismatch');
        }
        return JSON.stringify(
          await post(
            '/internal/script-library/tool',
            {
              steam_id: req.user.semindSteamId,
              operation_id: `${taskId}:${++operation}`,
              action,
              args,
            },
            true,
            options.gatewayURL,
            action === 'import'
              ? (req.config.config.semind.libraryImportTimeoutSeconds ?? 240) * 1000
              : 30000,
          ),
        );
      }
      if (!req.semindIdentity?.world_id)
        return JSON.stringify({ ok: false, error: { code: 'SEMIND_GAME_SCOPE_REQUIRED' } });
      prepared ??= post('/internal/game/session', {
        task_id: taskId,
        world_id: req.semindIdentity.world_id,
        steam_id: req.user.semindSteamId,
      }).then((value) => {
        const session = z.object({ prepared: z.literal(true), task_id: z.string() }).parse(value);
        if (session.task_id !== taskId) throw new Error('semind_game_scope_mismatch');
      });
      await prepared;
      return JSON.stringify(
        await post('/internal/game/tool', {
          task_id: taskId,
          operation_id: `${taskId}:${++operation}`,
          name,
          args,
        }),
      );
    },
  });
  requestTools.set(req, tools);
  requestClosures.set(req, async (canceled) => {
    if (!prepared) return;
    await prepared;
    await post('/internal/game/session/close', { task_id: taskId, canceled }, false);
  });
  return tools;
}
