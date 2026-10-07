import type { RequestHandler } from 'express';
import type { ServerRequest } from '~/types';

/** Personal Steam accounts cannot inherit another owner's resource through broad ACLs. */
export function createSemindResourceOwnerPolicy(options: {
  resourceIdParam: string;
  getResourceOwner: (id: string) => Promise<string | null>;
}): RequestHandler {
  return async (request, response, next) => {
    const req = request as ServerRequest;
    if (req.user?.provider !== 'semind') return next();
    const id = (req.params as Record<string, unknown>)[options.resourceIdParam];
    if (!req.user.id || typeof id !== 'string' || !id) {
      response.sendStatus(404);
      return;
    }
    try {
      const owner = await options.getResourceOwner(id);
      if (owner !== req.user.id) {
        response.sendStatus(404);
        return;
      }
      next();
    } catch {
      response.status(503).json({ code: 'SEMIND_RESOURCE_UNAVAILABLE' });
    }
  };
}
