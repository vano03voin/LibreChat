import { z } from 'zod';
import jwt from 'jsonwebtoken';
import type { Request, RequestHandler, Response } from 'express';
import type { SemindIdentity } from './memory';
import type { ServerRequest } from '~/types';
import { semindCurrentUserText } from './memory';

export interface SemindConfig {
  enabled: boolean;
  apiURL: string;
  sessionRecheckSeconds: number;
}

const identitySchema = z.object({
  steam_id: z.string().regex(/^[0-9]{17}$/),
  display_name: z.string(),
  is_operator: z.boolean(),
  expires_at: z.number().int(),
  server_id: z.string(),
  world_id: z.string().nullable(),
});
const exchangeSchema = identitySchema.extend({ session_token: z.string().min(20).max(256) });
export const SEMIND_SESSION_COOKIE = '__Host-semind_assistant';

interface Dependencies {
  getConfig: () => Promise<SemindConfig | undefined>;
  internalKey: string;
  fetch: typeof fetch;
  getUserSteamId: (req: Request) => string | undefined;
  isTrustedTrigger: (req: Request) => boolean;
  getTrustedGameScope?: (req: Request) => Promise<{ server_id: string; world_id: string } | null>;
}

interface ExchangeActions {
  provisionUser: (identity: {
    steamId: string;
    name: string;
    isOperator: boolean;
  }) => Promise<{ id: string; agentId: string }>;
  setAuthTokens: (id: string, res: Response, session: null, req: Request) => Promise<string>;
}

interface RefreshActions {
  refreshSecret: string;
  getUser: (id: string) => Promise<{ semindSteamId?: string } | null>;
}

interface SemindAuthority {
  guard: RequestHandler;
  verify: (
    req: Request,
    config: SemindConfig,
    expectedSteamId?: string,
  ) => Promise<{
    steam_id: string;
    display_name: string;
    is_operator: boolean;
    expires_at: number;
    server_id: string;
    world_id: string | null;
  }>;
  createExchangeHandler: (actions: ExchangeActions) => RequestHandler;
  createRefreshGuard: (actions: RefreshActions) => RequestHandler;
  revokeSession: RequestHandler;
}

export function createSemindAuthority(deps: Dependencies): SemindAuthority {
  async function post(config: SemindConfig, route: string, body: object) {
    if (!deps.internalKey) throw new Error('semind_authority_not_configured');
    const response = await deps.fetch(new URL(`/internal/assistant/${route}`, config.apiURL), {
      method: 'POST',
      redirect: 'error',
      headers: { Authorization: `Bearer ${deps.internalKey}`, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(5000),
    });
    if (!response.ok) throw new Error('semind_authority_rejected');
    return response.status === 204 ? null : response.json();
  }

  async function verify(req: Request, config: SemindConfig, expectedSteamId?: string) {
    const secret = req.cookies?.[SEMIND_SESSION_COOKIE];
    if (typeof secret !== 'string' || secret.length < 20 || secret.length > 256) {
      throw new Error('semind_session_required');
    }
    const identity = identitySchema.parse(
      await post(config, 'session/verify', { session_token: secret }),
    );
    if (
      identity.expires_at <= Date.now() / 1000 ||
      !expectedSteamId ||
      identity.steam_id !== expectedSteamId
    ) {
      throw new Error('semind_session_mismatch');
    }
    return identity;
  }

  const guard: RequestHandler = async (req, res, next) => {
    try {
      const config = await deps.getConfig();
      if (!config?.enabled) return next();
      const principal = (req as ServerRequest).user;
      if (principal?.provider === 'semind') principal.role = 'USER';
      if (deps.isTrustedTrigger(req)) {
        const scope = await deps.getTrustedGameScope?.(req);
        if (scope) {
          const steamId = deps.getUserSteamId(req);
          const identity = identitySchema
            .omit({ expires_at: true })
            .parse(await post(config, 'game/identity', { ...scope, steam_id: steamId }));
          if (
            !steamId ||
            identity.steam_id !== steamId ||
            identity.server_id !== scope.server_id ||
            identity.world_id !== scope.world_id
          )
            throw new Error('semind_schedule_identity_mismatch');
          const trustedRequest = req as ServerRequest;
          trustedRequest.semindIdentity = identity satisfies SemindIdentity;
          trustedRequest.semindUserText = semindCurrentUserText(req.body ?? {});
          const grant = z
            .object({
              access_token: z.string().min(20),
              expires_at: z.string(),
              api_prefix: z.literal('/api/player/v1'),
            })
            .parse(
              await post(config, 'data/grant', { source: 'schedule', ...scope, steam_id: steamId }),
            );
          trustedRequest.semindDataGrant = grant;
        }
        return next();
      }
      const steamId = deps.getUserSteamId(req);
      const identity = await verify(req, config, steamId);
      const trustedRequest = req as ServerRequest;
      trustedRequest.semindIdentity = identity;
      trustedRequest.semindUserText = semindCurrentUserText(req.body ?? {});
      // Revalidate open streams as well as HTTP admission. Parent logout must
      // not leave an authenticated stream delivering private results forever.
      if (!res.writableEnded) {
        let checking = false;
        const timer = setInterval(async () => {
          if (checking || res.writableEnded) return;
          checking = true;
          try {
            await verify(req, config, steamId);
          } catch {
            res.end();
          } finally {
            checking = false;
          }
        }, config.sessionRecheckSeconds * 1000);
        timer.unref();
        res.once('close', () => clearInterval(timer));
        res.once('finish', () => clearInterval(timer));
      }
      next();
    } catch {
      res.status(401).json({ code: 'SEMIND_SESSION_REQUIRED' });
    }
  };

  function createExchangeHandler(actions: ExchangeActions): RequestHandler {
    return async (req, res) => {
      const config = await deps.getConfig();
      if (!config?.enabled) {
        res.sendStatus(404);
        return;
      }
      const ticket = req.body?.ticket;
      const origin = req.get('origin');
      if (typeof ticket !== 'string' || ticket.length < 20 || ticket.length > 256 || !origin) {
        res.status(400).json({ code: 'SEMIND_TICKET_REQUIRED' });
        return;
      }
      try {
        const identity = exchangeSchema.parse(
          await post(config, 'sso/exchange', { ticket, origin }),
        );
        try {
          if (identity.expires_at <= Date.now() / 1000) throw new Error('semind_session_expired');
          const user = await actions.provisionUser({
            steamId: identity.steam_id,
            name: identity.display_name,
            isOperator: identity.is_operator,
          });
          await actions.setAuthTokens(user.id, res, null, req);
          res.cookie(SEMIND_SESSION_COOKIE, identity.session_token, {
            httpOnly: true,
            secure: true,
            sameSite: 'lax',
            path: '/',
            expires: new Date(identity.expires_at * 1000),
          });
          res.set('Cache-Control', 'no-store');
          res.redirect(303, `/c/new?agent_id=${encodeURIComponent(user.agentId)}`);
        } catch {
          await post(config, 'session/revoke', { session_token: identity.session_token });
          throw new Error('semind_login_failed');
        }
      } catch {
        res.status(401).json({ code: 'SEMIND_LOGIN_FAILED' });
      }
    };
  }

  function createRefreshGuard(actions: {
    refreshSecret: string;
    getUser: (id: string) => Promise<{ semindSteamId?: string } | null>;
  }): RequestHandler {
    return async (req, res, next) => {
      try {
        const config = await deps.getConfig();
        if (!config?.enabled || !req.cookies?.refreshToken) return next();
        const claims = z
          .object({ id: z.string() })
          .parse(
            jwt.verify(req.cookies.refreshToken, actions.refreshSecret, { algorithms: ['HS256'] }),
          );
        const user = await actions.getUser(claims.id);
        await verify(req, config, user?.semindSteamId);
        next();
      } catch {
        res.status(401).json({ code: 'SEMIND_SESSION_REQUIRED' });
      }
    };
  }

  const revokeSession: RequestHandler = async (req, res, next) => {
    try {
      const config = await deps.getConfig();
      const token = req.cookies?.[SEMIND_SESSION_COOKIE];
      if (config?.enabled && typeof token === 'string') {
        await post(config, 'session/revoke', { session_token: token });
      }
      res.clearCookie(SEMIND_SESSION_COOKIE, {
        httpOnly: true,
        secure: true,
        sameSite: 'lax',
        path: '/',
      });
      next();
    } catch {
      res.status(503).json({ code: 'SEMIND_LOGOUT_UNAVAILABLE' });
    }
  };

  return { guard, verify, createExchangeHandler, createRefreshGuard, revokeSession };
}
