import type { TCustomConfig, TStartupConfig } from 'librechat-data-provider';
import type { UserMethods } from '@librechat/data-schemas';

export function publicSemindStartup(
  config: TCustomConfig['semind'],
): Pick<TStartupConfig, 'semindLoginURL'> {
  return config?.enabled && config.portalURL ? { semindLoginURL: config.portalURL } : {};
}

type DefaultAgentDeps = Pick<
  UserMethods,
  'ensureSemindDefaultAgent' | 'provisionSemindUser' | 'getSemindAgentSelection'
> & { instructions: string; provider: string; model: string };
interface DefaultAgentActions {
  ensure: (userId: string) => Promise<string>;
  provision: (
    identity: Parameters<UserMethods['provisionSemindUser']>[0],
  ) => Promise<Awaited<ReturnType<UserMethods['provisionSemindUser']>> & { agentId: string }>;
}

export function createSemindDefaultAgent(deps: DefaultAgentDeps): DefaultAgentActions {
  const ensure = (userId: string) =>
    deps.ensureSemindDefaultAgent(userId, {
      instructions: deps.instructions,
      provider: deps.provider,
      model: deps.model,
    });
  return {
    ensure,
    provision: async (identity) => {
      const user = await deps.provisionSemindUser(identity);
      const [defaultId, selectedId] = await Promise.all([
        ensure(user.id),
        deps.getSemindAgentSelection(user.id),
      ]);
      return { ...user, agentId: selectedId ?? defaultId };
    },
  };
}
