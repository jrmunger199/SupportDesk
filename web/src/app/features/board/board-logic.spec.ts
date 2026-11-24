import {
  BoardCard,
  BoardColumn,
  TicketDetail,
  TicketPriority,
  TicketStatus,
} from '../../core/models';
import { cardFromDetail, compareCards, moveCard, replaceCard } from './board-logic';

const card = (id: string, overrides: Partial<BoardCard> = {}): BoardCard => ({
  id,
  number: Number(id.replace(/\D/g, '')) || 1,
  title: `Ticket ${id}`,
  status: 'Open',
  priority: 'Normal',
  requester: { id: 'r1', displayName: 'Dana' },
  assignee: null,
  updatedUtc: '2026-11-24T10:00:00Z',
  commentCount: 0,
  rowVersion: 'AAAA',
  allowedTransitions: ['InProgress', 'Resolved', 'Closed'],
  ...overrides,
});

const column = (status: TicketStatus, cards: BoardCard[], total = cards.length): BoardColumn => ({
  status,
  total,
  cards,
});

const board = (): BoardColumn[] => [
  column('Open', [card('a1', { priority: 'High' }), card('a2')]),
  column('InProgress', [card('b1', { status: 'InProgress', allowedTransitions: ['Open'] })]),
  column('Resolved', []),
  column('Closed', []),
];

describe('compareCards', () => {
  it('orders by priority, then recency, then id', () => {
    const ranked = (p: TicketPriority, at: string, id: string) =>
      card(id, { priority: p, updatedUtc: at });
    const cards = [
      ranked('Low', '2026-11-24T12:00:00Z', 'low'),
      ranked('Urgent', '2026-11-24T08:00:00Z', 'urgent-old'),
      ranked('Urgent', '2026-11-24T11:00:00Z', 'urgent-new'),
      ranked('Normal', '2026-11-24T09:00:00Z', 'normal'),
    ];

    expect(cards.sort(compareCards).map((c) => c.id)).toEqual([
      'urgent-new',
      'urgent-old',
      'normal',
      'low',
    ]);
  });

  it('breaks exact ties by id so the order is stable', () => {
    const cards = [card('b'), card('a')];

    expect(cards.sort(compareCards).map((c) => c.id)).toEqual(['a', 'b']);
  });
});

describe('moveCard', () => {
  it('moves the card, updates both totals, and marks it as unconfirmed', () => {
    const result = moveCard(board(), 'a2', 'InProgress');

    expect(result[0].cards.map((c) => c.id)).toEqual(['a1']);
    expect(result[0].total).toBe(1);
    expect(result[1].cards.map((c) => c.id)).toContain('a2');
    expect(result[1].total).toBe(2);

    const moved = result[1].cards.find((c) => c.id === 'a2')!;
    expect(moved.status).toBe('InProgress');
    expect(moved.allowedTransitions).toEqual([]); // not draggable again until the server confirms
  });

  it('keeps the destination column sorted by priority', () => {
    const result = moveCard(board(), 'a1', 'InProgress'); // High priority joins a Normal one

    expect(result[1].cards.map((c) => c.id)).toEqual(['a1', 'b1']);
  });

  it('counts tickets that are not shown on the board when adjusting totals', () => {
    const columns = [column('Open', [card('a1')], 40), column('Resolved', [], 12)];

    const result = moveCard(columns, 'a1', 'Resolved');

    expect(result.map((c) => c.total)).toEqual([39, 13]);
  });

  it('does not change the original columns', () => {
    const original = board();
    const snapshot = JSON.stringify(original);

    moveCard(original, 'a1', 'Closed');

    expect(JSON.stringify(original)).toBe(snapshot);
  });

  it('ignores a move to the same column or of an unknown card', () => {
    const columns = board();

    expect(moveCard(columns, 'a1', 'Open')).toBe(columns);
    expect(moveCard(columns, 'nope', 'Closed')).toBe(columns);
  });
});

describe('replaceCard', () => {
  it('swaps in the confirmed card with its new row version and moves', () => {
    const moved = moveCard(board(), 'a2', 'InProgress');
    const confirmed = card('a2', {
      status: 'InProgress',
      rowVersion: 'BBBB',
      allowedTransitions: ['Open', 'Resolved', 'Closed'],
    });

    const result = replaceCard(moved, confirmed);

    const stored = result[1].cards.find((c) => c.id === 'a2')!;
    expect(stored.rowVersion).toBe('BBBB');
    expect(stored.allowedTransitions).toEqual(['Open', 'Resolved', 'Closed']);
  });

  it('leaves other columns untouched', () => {
    const columns = board();

    const result = replaceCard(columns, card('a1', { priority: 'High', rowVersion: 'ZZZZ' }));

    expect(result[1]).toBe(columns[1]);
  });
});

describe('cardFromDetail', () => {
  it('maps a ticket detail to a board card', () => {
    const detail: TicketDetail = {
      id: 'x1',
      number: 7,
      title: 'Printer jam',
      description: 'It jams',
      status: 'Resolved',
      priority: 'High',
      requester: { id: 'r1', displayName: 'Dana' },
      assignee: { id: 'a1', displayName: 'Priya' },
      createdUtc: '2026-11-24T09:00:00Z',
      updatedUtc: '2026-11-24T10:00:00Z',
      rowVersion: 'CCCC',
      allowedTransitions: ['Open', 'Closed'],
      comments: [
        {
          id: 'c1',
          author: { id: 'a1', displayName: 'Priya' },
          body: 'x',
          createdUtc: '2026-11-24T09:30:00Z',
        },
      ],
      events: [],
    };

    expect(cardFromDetail(detail)).toEqual({
      id: 'x1',
      number: 7,
      title: 'Printer jam',
      status: 'Resolved',
      priority: 'High',
      requester: { id: 'r1', displayName: 'Dana' },
      assignee: { id: 'a1', displayName: 'Priya' },
      updatedUtc: '2026-11-24T10:00:00Z',
      commentCount: 1,
      rowVersion: 'CCCC',
      allowedTransitions: ['Open', 'Closed'],
    });
  });
});
