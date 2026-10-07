import { z } from 'zod';
import type { UserMethods } from '@librechat/data-schemas';
import type { RequestHandler } from 'express';
import type { ServerRequest } from '~/types';

const selectionSchema = z.object({ agentId: z.string().min(1).max(128).nullable() }).strict();

export function createSemindAgentSelectionHandlers(
  deps: Pick<UserMethods, 'getSemindAgentSelection' | 'setSemindAgentSelection'>,
): { get: RequestHandler; set: RequestHandler } {
  const get: RequestHandler = async (request, res) => {
    const req = request as ServerRequest;
    if (!req.config?.config?.semind?.enabled) {
      res.json({ enabled: false, agentId: null });
      return;
    }
    if (!req.user?.id) {
      res.status(401).json({ code: 'SEMIND_IDENTITY_REQUIRED' });
      return;
    }
    try {
      res.json({ enabled: true, agentId: await deps.getSemindAgentSelection(req.user.id) });
    } catch {
      res.status(503).json({ code: 'SEMIND_AGENT_SELECTION_UNAVAILABLE' });
    }
  };
  const set: RequestHandler = async (request, res) => {
    const req = request as ServerRequest;
    if (!req.config?.config?.semind?.enabled) {
      res.sendStatus(404);
      return;
    }
    if (!req.user?.id) {
      res.status(401).json({ code: 'SEMIND_IDENTITY_REQUIRED' });
      return;
    }
    const body = selectionSchema.safeParse(req.body);
    if (!body.success) {
      res.status(400).json({ code: 'SEMIND_AGENT_SELECTION_INVALID' });
      return;
    }
    try {
      if (!(await deps.setSemindAgentSelection(req.user.id, body.data.agentId))) {
        res.status(404).json({ code: 'SEMIND_AGENT_NOT_FOUND' });
        return;
      }
      res.json({ enabled: true, agentId: body.data.agentId });
    } catch {
      res.status(503).json({ code: 'SEMIND_AGENT_SELECTION_UNAVAILABLE' });
    }
  };
  return { get, set };
}
