import { Button } from '@librechat/client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { dataService, QueryKeys, MutationKeys } from 'librechat-data-provider';
import { useLocalize } from '~/hooks';

export default function GameAgentSelection({ agentId }: { agentId: string }) {
  const localize = useLocalize();
  const client = useQueryClient();
  const selection = useQuery([QueryKeys.semindGameAgent], dataService.getSemindGameAgent, {
    retry: false,
  });
  const update = useMutation({
    mutationKey: [MutationKeys.semindGameAgent],
    mutationFn: dataService.setSemindGameAgent,
    onSuccess: (value) => client.setQueryData([QueryKeys.semindGameAgent], value),
  });
  if (!selection.data?.enabled) return null;
  const selected = selection.data.agentId === agentId;
  return (
    <div className="mb-4 flex flex-col items-center gap-2 px-6">
      <Button
        variant="outline"
        disabled={selected || update.isLoading}
        onClick={() => update.mutate(agentId)}
      >
        {localize(selected ? 'com_agents_selected_for_game' : 'com_agents_use_in_game')}
      </Button>
      {selected && (
        <Button variant="ghost" disabled={update.isLoading} onClick={() => update.mutate(null)}>
          {localize('com_agents_use_default_in_game')}
        </Button>
      )}
      <p className="text-center text-sm text-text-secondary">
        {localize('com_agents_game_selection_hint')}
      </p>
      {update.isError && (
        <p role="alert" className="text-sm text-text-primary">
          {localize('com_agents_game_selection_failed')}
        </p>
      )}
    </div>
  );
}
