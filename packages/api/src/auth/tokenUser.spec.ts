import { Types } from 'mongoose';
import type { IUser } from '@librechat/data-schemas';
import { resolveAuthTokenUser } from './tokenUser';

const owner = new Types.ObjectId();
const foreign = new Types.ObjectId();
const user = { _id: owner, email: 'owner@example.test' } as IUser;

describe('fresh auth token user reuse', () => {
  test('reuses the current auth operation user without another database read', async () => {
    const getUser = jest.fn();
    expect(await resolveAuthTokenUser(owner.toString(), getUser, user)).toBe(user);
    expect(getUser).not.toHaveBeenCalled();
  });

  test('rejects a foreign loaded user instead of minting for a different owner', async () => {
    const getUser = jest.fn();
    await expect(resolveAuthTokenUser(foreign.toString(), getUser, user)).rejects.toThrow(
      'AUTH_TOKEN_USER_MISMATCH',
    );
    expect(getUser).not.toHaveBeenCalled();
  });

  test('requires the persisted id rather than an unproven id property', async () => {
    const getUser = jest.fn();
    await expect(
      resolveAuthTokenUser(owner.toString(), getUser, { id: owner.toString() } as IUser),
    ).rejects.toThrow('AUTH_TOKEN_USER_MISMATCH');
    expect(getUser).not.toHaveBeenCalled();
  });

  test('retains the ordinary fresh lookup when no user was explicitly passed', async () => {
    const getUser = jest.fn().mockResolvedValue(user);
    expect(await resolveAuthTokenUser(owner.toString(), getUser)).toBe(user);
    expect(getUser).toHaveBeenCalledTimes(1);
    expect(getUser).toHaveBeenCalledWith(owner.toString());
  });

  test('accepts a server-loaded Mongoose document with the same persisted owner', async () => {
    const document = { toObject: () => user } as unknown as IUser;
    const getUser = jest.fn();
    expect(await resolveAuthTokenUser(owner.toString(), getUser, document)).toBe(document);
    expect(getUser).not.toHaveBeenCalled();
  });
});
