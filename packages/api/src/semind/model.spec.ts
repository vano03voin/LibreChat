import jwt from 'jsonwebtoken';
import { createOwnerModelFetch } from './model';

describe('SE-mind model transport', () => {
  test('mints a fresh owner grant for each request and refuses another destination', async () => {
    const key = Buffer.alloc(48, 19);
    const delegate = jest.fn().mockResolvedValue(new Response('{}'));
    const fetcher = createOwnerModelFetch({ userId: 'owner-a', steamId: '76561198000000001',
      signingKeyBase64: key.toString('base64'), baseURL: 'http://127.0.0.1:49391/model/v1', fetch: delegate });
    await fetcher('http://127.0.0.1:49391/model/v1/responses', { method: 'POST', headers: { Authorization: 'Bearer injected' } });
    const init = delegate.mock.calls[0][1];
    const token = init.headers.get('Authorization').slice(7);
    expect(jwt.verify(token, key, { algorithms: ['HS256'], issuer: 'semind-librechat', audience: 'semind-model' })).toMatchObject({ sub: 'owner-a', steam_id: '76561198000000001' });
    expect(init.redirect).toBe('error');
    await expect(fetcher('https://external.invalid/responses')).rejects.toThrow('semind_model_destination_rejected');
    expect(delegate).toHaveBeenCalledTimes(1);
  });

  test('cannot initialize without a Steam-backed owner', () => {
    expect(() => createOwnerModelFetch({ userId: 'owner', steamId: '', signingKeyBase64: Buffer.alloc(48).toString('base64'), baseURL: 'http://localhost/model/v1', fetch: globalThis.fetch })).toThrow('semind_model_identity_required');
  });
});
