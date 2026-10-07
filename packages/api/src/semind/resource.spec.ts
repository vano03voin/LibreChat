import type { Response, Request } from 'express';
import { createSemindResourceOwnerPolicy } from './resource';

describe('personal Steam resource boundaries', () => {
  test.each(['other', null])(
    'rejects a foreign or missing owner even for a prior ADMIN role',
    async (owner) => {
      const req = {
        user: { id: 'owner', provider: 'semind', role: 'ADMIN' },
        params: { id: 'resource' },
        body: { userId: 'other' },
      } as unknown as Request;
      const response = { sendStatus: jest.fn() } as unknown as Response;
      const next = jest.fn();
      const getResourceOwner = jest.fn().mockResolvedValue(owner);
      await createSemindResourceOwnerPolicy({ resourceIdParam: 'id', getResourceOwner })(
        req,
        response,
        next,
      );
      expect(getResourceOwner).toHaveBeenCalledWith('resource');
      expect(response.sendStatus).toHaveBeenCalledWith(404);
      expect(next).not.toHaveBeenCalled();
    },
  );
  test('admits only the actual owner', async () => {
    const req = {
      user: { id: 'owner', provider: 'semind' },
      params: { agent_id: 'resource' },
    } as unknown as Request;
    const next = jest.fn();
    await createSemindResourceOwnerPolicy({
      resourceIdParam: 'agent_id',
      getResourceOwner: async () => 'owner',
    })(req, {} as Response, next);
    expect(next).toHaveBeenCalledTimes(1);
  });
});
