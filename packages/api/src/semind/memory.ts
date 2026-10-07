export interface SemindIdentity {
  steam_id: string;
  server_id: string;
  world_id: string | null;
}

export type SemindMemoryScope = 'profile' | 'game';

const commandPrefix = String.raw`(?:^|[.!?;]\s+|\n\s*|\s(?:и|and)\s+)(?:(?:пожалуйста|please)[,:]?\s+|(?:can|could|would)\s+you\s+(?:please\s+)?)?`;
const explicitSet = new RegExp(
  `${commandPrefix}(?:запомни(?=\\s|[:.,!?]|$)|(?:сохрани|обнови)\\s+(?:это\\s+)?(?:в\\s+)?памят|remember\\b|(?:store|save|update)\\b[^\\n]*\\bmemory\\b)`,
  'u',
);
const explicitDelete = new RegExp(
  `${commandPrefix}(?:забудь(?=\\s|[:.,!?]|$)|удали\\s+(?:из\\s+)?памят|forget\\b|(?:delete|remove)\\b[^\\n]*\\bmemory\\b)`,
  'u',
);

/** The partition comes from an authenticated identity, never from tool arguments. */
export function semindMemoryPartition(
  identity: SemindIdentity | undefined,
  scope: SemindMemoryScope,
): string | undefined {
  if (scope === 'profile') return undefined;
  if (!identity?.server_id || !identity.world_id) throw new Error('semind_memory_world_required');
  return `semind-world:${encodeURIComponent(identity.server_id)}:${identity.world_id}`;
}

/** Explicit memory intent is checked against this turn's user text, not model output. */
export function hasSemindMemoryConsent(text: string, action: 'set' | 'delete'): boolean {
  const normalized = text.normalize('NFKC').trim().toLowerCase();
  if (
    !normalized ||
    /(?:не\s+(?:запоминай|запомни|сохраняй|сохрани)|(?:do\s+not|don't|never)\s+(?:remember|store|save))/u.test(
      normalized,
    )
  )
    return false;
  if (action === 'delete') {
    return explicitDelete.test(normalized);
  }
  return explicitSet.test(normalized);
}

interface TextPart {
  type?: string;
  text?: string;
}
interface UserMessage {
  role?: string;
  content?: string | TextPart[];
}
interface UserBody {
  text?: string;
  messages?: UserMessage[];
  input?: string | UserMessage[];
}

export function semindCurrentUserText(body: UserBody): string {
  if (typeof body.text === 'string') return body.text;
  if (typeof body.input === 'string') return body.input;
  const messages = body.messages ?? (Array.isArray(body.input) ? body.input : []);
  const current = messages[messages.length - 1];
  if (current?.role !== 'user') return '';
  if (typeof current.content === 'string') return current.content;
  return (current.content ?? [])
    .filter((part) => part.type === 'text' || part.type === 'input_text')
    .map((part) => part.text ?? '')
    .join('\n');
}

export const semindMemoryInstructions =
  'Save or delete memory only after an explicit request in the current user message. Use scope="profile" for personal preferences shared across all agents. Use scope="game" for ship, server, or world facts; that scope is bound to the authenticated current server and world. Never copy game facts into the profile or ask a tool to select another owner, server, or world.';
