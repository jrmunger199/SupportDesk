import {
  activeMentionQuery,
  filterCandidates,
  fromWire,
  insertMention,
  parseSegments,
  prunePicks,
  toWire,
} from './mentions';

const PRIYA = '3f2a1c4e-0b5d-4c6e-8f7a-9b0c1d2e3f40';
const MARCUS = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d';

describe('parseSegments', () => {
  it('splits text and mentions in order', () => {
    expect(parseSegments(`Hi @[Priya Nair](${PRIYA}), thanks!`)).toEqual([
      { kind: 'text', text: 'Hi ' },
      { kind: 'mention', id: PRIYA, name: 'Priya Nair' },
      { kind: 'text', text: ', thanks!' },
    ]);
  });

  it('lower-cases ids so they compare reliably', () => {
    const [segment] = parseSegments(`@[Priya](${PRIYA.toUpperCase()})`);

    expect(segment).toEqual({ kind: 'mention', id: PRIYA, name: 'Priya' });
  });

  it('handles adjacent and repeated mentions', () => {
    const segments = parseSegments(`@[A B](${PRIYA})@[C D](${MARCUS})`);

    expect(segments.map((s) => s.kind)).toEqual(['mention', 'mention']);
  });

  it('leaves malformed tokens as plain text', () => {
    const text = 'Hi @[Priya](not-a-guid) and @Priya Nair';

    expect(parseSegments(text)).toEqual([{ kind: 'text', text }]);
  });

  it('returns nothing for empty text', () => {
    expect(parseSegments('')).toEqual([]);
  });
});

describe('fromWire and toWire', () => {
  it('round-trips a mention through the editor representation', () => {
    const wire = `Hello @[Priya Nair](${PRIYA}), please check.`;

    const { text, picks } = fromWire(wire);

    expect(text).toBe('Hello @Priya Nair, please check.');
    expect(picks).toEqual([{ id: PRIYA, name: 'Priya Nair' }]);
    expect(toWire(text, picks)).toBe(wire);
  });

  it('only turns picked names into mentions', () => {
    const wire = toWire('@Priya Nair and @Someone Else', [{ id: PRIYA, name: 'Priya Nair' }]);

    expect(wire).toBe(`@[Priya Nair](${PRIYA}) and @Someone Else`);
  });

  it('does not match a name that is only the start of a longer word', () => {
    const wire = toWire('@Annabel is here, and so is @Ann', [{ id: PRIYA, name: 'Ann' }]);

    expect(wire).toBe(`@Annabel is here, and so is @[Ann](${PRIYA})`);
  });

  it('prefers the longer name when one name contains another', () => {
    const wire = toWire('@Ann Lee and @Ann', [
      { id: PRIYA, name: 'Ann' },
      { id: MARCUS, name: 'Ann Lee' },
    ]);

    expect(wire).toBe(`@[Ann Lee](${MARCUS}) and @[Ann](${PRIYA})`);
  });

  it('replaces every occurrence of a picked name', () => {
    const wire = toWire('@Priya Nair, again @Priya Nair', [{ id: PRIYA, name: 'Priya Nair' }]);

    expect(wire.match(/@\[Priya Nair\]/g)).toHaveLength(2);
  });

  it('copes with names containing regex characters', () => {
    const name = "Pat O'Brien (Dev) [IT]";

    const wire = toWire(`Hi @${name}!`, [{ id: PRIYA, name }]);

    // Brackets would break the token syntax, so they are dropped inside it; the server restores the real name.
    expect(wire).toBe(`Hi @[Pat O'Brien (Dev) IT](${PRIYA})!`);
  });

  it('leaves text alone when there are no picks', () => {
    expect(toWire('Just text @nobody', [])).toBe('Just text @nobody');
  });
});

describe('activeMentionQuery', () => {
  it('finds a mention being typed at the start of the text', () => {
    expect(activeMentionQuery('@pri', 4)).toEqual({ start: 0, query: 'pri' });
  });

  it('finds a mention after a space or a line break', () => {
    expect(activeMentionQuery('hello @pri', 10)).toEqual({ start: 6, query: 'pri' });
    expect(activeMentionQuery('line one\n@pri', 13)).toEqual({ start: 9, query: 'pri' });
  });

  it('shows everyone for a bare @', () => {
    expect(activeMentionQuery('ask @', 5)).toEqual({ start: 4, query: '' });
  });

  it('allows spaces so a full name can be typed', () => {
    expect(activeMentionQuery('cc @Priya N', 11)).toEqual({ start: 3, query: 'Priya N' });
  });

  it('ignores email addresses', () => {
    expect(activeMentionQuery('write to bob@example', 20)).toBeNull();
  });

  it('only considers text before the caret', () => {
    expect(activeMentionQuery('@pri and more', 4)).toEqual({ start: 0, query: 'pri' });
    expect(activeMentionQuery('@pri and more', 0)).toBeNull();
  });

  it('gives up on long queries and on queries that span lines', () => {
    expect(activeMentionQuery(`@${'a'.repeat(40)}`, 41)).toBeNull();
    expect(activeMentionQuery('@pri\nnext', 9)).toBeNull();
  });

  it('is null when there is no @', () => {
    expect(activeMentionQuery('nothing here', 5)).toBeNull();
  });
});

describe('insertMention', () => {
  it('replaces the half-typed query and moves the caret past the inserted name', () => {
    const result = insertMention('Hi @pr there', { start: 3, query: 'pr' }, 6, 'Priya Nair');

    expect(result).toEqual({ text: 'Hi @Priya Nair  there', caret: 15 });
  });

  it('works at the very start and the very end', () => {
    expect(insertMention('@p', { start: 0, query: 'p' }, 2, 'Priya Nair')).toEqual({
      text: '@Priya Nair ',
      caret: 12,
    });
  });
});

describe('prunePicks', () => {
  const picks = [
    { id: PRIYA, name: 'Priya Nair' },
    { id: MARCUS, name: 'Marcus Lee' },
  ];

  it('keeps picks whose name is still in the text', () => {
    expect(prunePicks('@Priya Nair and @Marcus Lee', picks)).toEqual(picks);
  });

  it('drops picks whose name was deleted or edited', () => {
    expect(prunePicks('@Priya Nair and Marcus', picks)).toEqual([picks[0]]);
    expect(prunePicks('@Priya Nai', picks)).toEqual([]);
  });
});

describe('filterCandidates', () => {
  const people = [
    { displayName: 'Marcus Lee' },
    { displayName: 'Priya Nair' },
    { displayName: 'Dana Whitfield' },
    { displayName: 'Anna Marchetti' },
  ];

  it('returns everyone for an empty query', () => {
    expect(filterCandidates(people, '')).toHaveLength(4);
  });

  it('is case-insensitive and ranks prefixes before later words before substrings', () => {
    const names = filterCandidates(people, 'MAR').map((p) => p.displayName);

    expect(names).toEqual(['Marcus Lee', 'Anna Marchetti']);
  });

  it('matches the start of any word in a name', () => {
    expect(filterCandidates(people, 'nair').map((p) => p.displayName)).toEqual(['Priya Nair']);
  });

  it('finds nothing when nothing matches', () => {
    expect(filterCandidates(people, 'zzz')).toEqual([]);
  });
});
