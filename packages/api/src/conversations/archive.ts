import { logger } from '@librechat/data-schemas';
import type { ConversationMethods } from '@librechat/data-schemas';
import type { RequestHandler } from 'express';
import type { Response } from 'express';
import type { ServerRequest } from '~/types';

type ArchiveAllHandlerDependencies = Pick<ConversationMethods, 'archiveAllConvos'>;

/** SE-mind keeps user history permanently; the UI's removal action archives it. */
export function createSemindHistoryPolicy(
  deps: Pick<ConversationMethods, 'archiveAllConvos' | 'saveConvo'>,
): RequestHandler<unknown> {
  return async (request, res, next) => {
    const req = request as ServerRequest;
    if (!req.config?.config?.semind?.enabled) return next();
    const userId = req.user?.id;
    if (!userId) {
      res.status(401).json({ code: 'SEMIND_IDENTITY_REQUIRED' });
      return;
    }
    try {
      if (req.path === '/all') {
        const result = await deps.archiveAllConvos(userId);
        res.status(200).json({ ...result, archived: true });
        return;
      }
      const { conversationId } = request.body?.arg ?? {};
      if (typeof conversationId !== 'string' || !conversationId) {
        res.status(400).json({ code: 'SEMIND_CONVERSATION_REQUIRED' });
        return;
      }
      const result = await deps.saveConvo(
        { userId, interfaceConfig: req.config.interfaceConfig },
        { conversationId, isArchived: true },
        { noUpsert: true, preserveUpdatedAt: true },
      );
      if (!result) {
        res.status(404).json({ code: 'SEMIND_CONVERSATION_NOT_FOUND' });
        return;
      }
      if (!('conversationId' in result)) {
        res.status(503).json({ code: 'SEMIND_HISTORY_UNAVAILABLE' });
        return;
      }
      res.status(200).json({ archived: true, archivedCount: 1, conversationIds: [conversationId] });
    } catch {
      res.status(503).json({ code: 'SEMIND_HISTORY_UNAVAILABLE' });
    }
  };
}

export function createArchiveAllHandler(
  deps: ArchiveAllHandlerDependencies,
): (req: ServerRequest, res: Response) => Promise<Response> {
  return async function archiveAllHandler(req: ServerRequest, res: Response): Promise<Response> {
    try {
      const result = await deps.archiveAllConvos(req.user!.id);
      return res.status(200).json(result);
    } catch (error) {
      logger.error('Error archiving all conversations', error);
      return res.status(500).send('Error archiving all conversations');
    }
  };
}
