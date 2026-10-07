import type { IUser } from '@librechat/data-schemas';

/** Reuses only a fresh user loaded by the same server-side auth operation. */
export async function resolveAuthTokenUser(
  userId: string,
  getUserById: (id: string) => Promise<IUser | null>,
  loadedUser?: IUser,
): Promise<IUser | null> {
  if (loadedUser === undefined) {
    return getUserById(userId);
  }

  const source = typeof loadedUser.toObject === 'function' ? loadedUser.toObject() : loadedUser;
  if (source._id == null || source._id.toString() !== userId.toString()) {
    throw new Error('AUTH_TOKEN_USER_MISMATCH');
  }
  return loadedUser;
}
