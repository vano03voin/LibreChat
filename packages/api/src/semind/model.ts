import { createHmac } from 'node:crypto';

type ModelFetch = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>;

/** Owner identity comes from authenticated backend state, never model arguments. */
export function createOwnerModelFetch(options: {
  userId: string;
  steamId: string;
  signingKeyBase64: string;
  baseURL: string;
  fetch: ModelFetch;
}): ModelFetch {
  const key = Buffer.from(options.signingKeyBase64, 'base64');
  if (key.length < 32 || !options.userId || !/^[0-9]{17}$/.test(options.steamId)) {
    throw new Error('semind_model_identity_required');
  }
  const base = new URL(options.baseURL.replace(/\/$/, '') + '/');
  return async (input, init) => {
    const url = new URL(input instanceof Request ? input.url : input.toString());
    if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) {
      throw new Error('semind_model_destination_rejected');
    }
    const now = Math.floor(Date.now() / 1000);
    const encode = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url');
    const data = `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode({
      iss: 'semind-librechat', aud: 'semind-model', sub: options.userId,
      steam_id: options.steamId, iat: now, exp: now + 300,
    })}`;
    const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
    headers.set('Authorization', `Bearer ${data}.${createHmac('sha256', key).update(data).digest('base64url')}`);
    return options.fetch(input, { ...init, headers, redirect: 'error' });
  };
}
