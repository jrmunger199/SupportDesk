// Pure helpers for @mentions. The wire format is `@[Display Name](user-id)`, which is what the API stores and
// validates. People never see or type that: the editor shows plain `@Display Name` and these functions convert.

export interface MentionPick {
  id: string;
  name: string;
}

export type Segment =
  { kind: 'text'; text: string } | { kind: 'mention'; id: string; name: string };

export interface ActiveQuery {
  /** Index of the `@` that starts the mention being typed. */
  start: number;
  /** What has been typed after it. */
  query: string;
}

const TOKEN =
  /@\[([^\]\r\n]{1,120})\]\(([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)/g;

const MAX_QUERY_LENGTH = 30;

const escapeRegExp = (value: string): string => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Matches `@Name` only when the name is not just the start of a longer word ("@Ann" must not match "@Annabel"). */
const mentionPattern = (name: string, flags: string): RegExp =>
  new RegExp(`@${escapeRegExp(name)}(?![\\p{L}\\p{N}_])`, flags);

/** Brackets and line breaks would break the token syntax, so they are dropped from names inside tokens. */
const tokenName = (name: string): string => name.replace(/[[\]\r\n]/g, '').trim();

/** Splits stored text into plain runs and mentions, for display. */
export function parseSegments(wire: string): Segment[] {
  const segments: Segment[] = [];
  let last = 0;

  for (const match of wire.matchAll(TOKEN)) {
    const index = match.index ?? 0;
    if (index > last) {
      segments.push({ kind: 'text', text: wire.slice(last, index) });
    }
    segments.push({ kind: 'mention', name: match[1], id: match[2].toLowerCase() });
    last = index + match[0].length;
  }

  if (last < wire.length) {
    segments.push({ kind: 'text', text: wire.slice(last) });
  }

  return segments;
}

/** Stored text -> what the editor shows, remembering who each `@Name` refers to. */
export function fromWire(wire: string): { text: string; picks: MentionPick[] } {
  const picks = new Map<string, MentionPick>();
  let text = '';

  for (const segment of parseSegments(wire)) {
    if (segment.kind === 'text') {
      text += segment.text;
    } else {
      text += `@${segment.name}`;
      picks.set(segment.id, { id: segment.id, name: segment.name });
    }
  }

  return { text, picks: [...picks.values()] };
}

/** What the editor shows -> stored text. Longest names go first so "@Ann Lee" wins over "@Ann". */
export function toWire(text: string, picks: readonly MentionPick[]): string {
  let wire = text;

  for (const pick of [...picks].sort((a, b) => b.name.length - a.name.length)) {
    wire = wire.replace(
      mentionPattern(pick.name, 'gu'),
      () => `@[${tokenName(pick.name)}](${pick.id})`,
    );
  }

  return wire;
}

/** The mention being typed at the caret, if any: an `@` at the start or after whitespace, then a short query. */
export function activeMentionQuery(text: string, caret: number): ActiveQuery | null {
  const before = text.slice(0, caret);
  const at = before.lastIndexOf('@');
  if (at < 0) {
    return null;
  }

  // "name@example.com" is an email address, not a mention.
  if (at > 0 && !/\s/.test(before[at - 1])) {
    return null;
  }

  const query = before.slice(at + 1);
  if (query.length > MAX_QUERY_LENGTH || /[\r\n]/.test(query)) {
    return null;
  }

  return { start: at, query };
}

/** Replaces the half-typed `@que` with the chosen person and a trailing space. */
export function insertMention(
  text: string,
  active: ActiveQuery,
  caret: number,
  name: string,
): { text: string; caret: number } {
  const insertion = `@${name} `;
  return {
    text: text.slice(0, active.start) + insertion + text.slice(caret),
    caret: active.start + insertion.length,
  };
}

/** Forgets picks whose `@Name` was deleted from the text, so they stop counting as mentions. */
export function prunePicks(text: string, picks: readonly MentionPick[]): MentionPick[] {
  return picks.filter((pick) => mentionPattern(pick.name, 'u').test(text));
}

/** Candidates whose name starts with, or has a word starting with, the query; prefix matches first. */
export function filterCandidates<T extends { displayName: string }>(
  candidates: readonly T[],
  query: string,
): T[] {
  const q = query.trim().toLowerCase();
  if (!q) {
    return [...candidates];
  }

  const rank = (name: string): number => {
    const lower = name.toLowerCase();
    if (lower.startsWith(q)) return 0;
    if (lower.split(/\s+/).some((word) => word.startsWith(q))) return 1;
    return lower.includes(q) ? 2 : 3;
  };

  return candidates
    .map((candidate) => ({ candidate, rank: rank(candidate.displayName) }))
    .filter((entry) => entry.rank < 3)
    .sort(
      (a, b) => a.rank - b.rank || a.candidate.displayName.localeCompare(b.candidate.displayName),
    )
    .map((entry) => entry.candidate);
}
