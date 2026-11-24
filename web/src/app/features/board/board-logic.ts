import {
  BoardCard,
  BoardColumn,
  TicketDetail,
  TicketPriority,
  TicketStatus,
} from '../../core/models';

const PRIORITY_RANK: Record<TicketPriority, number> = { Low: 0, Normal: 1, High: 2, Urgent: 3 };

/** The order cards appear in within a column. Mirrors the API: priority first, then the most recently touched. */
export function compareCards(a: BoardCard, b: BoardCard): number {
  return (
    PRIORITY_RANK[b.priority] - PRIORITY_RANK[a.priority] ||
    Date.parse(b.updatedUtc) - Date.parse(a.updatedUtc) ||
    a.id.localeCompare(b.id)
  );
}

/**
 * Moves a card to another column immediately, before the server has answered ("optimistic" update). The card has no
 * known next moves until the server confirms, so it cannot be dragged again in the meantime.
 */
export function moveCard(columns: BoardColumn[], cardId: string, to: TicketStatus): BoardColumn[] {
  const source = columns.find((column) => column.cards.some((card) => card.id === cardId));
  const card = source?.cards.find((candidate) => candidate.id === cardId);
  if (!source || !card || source.status === to) {
    return columns;
  }

  return columns.map((column) => {
    if (column.status === source.status) {
      return {
        ...column,
        total: column.total - 1,
        cards: column.cards.filter((c) => c.id !== cardId),
      };
    }

    if (column.status === to) {
      const moved: BoardCard = { ...card, status: to, allowedTransitions: [] };
      return {
        ...column,
        total: column.total + 1,
        cards: [moved, ...column.cards].sort(compareCards),
      };
    }

    return column;
  });
}

/** Swaps in the server's version of a card (new row version, legal moves, timestamps) once a move is confirmed. */
export function replaceCard(columns: BoardColumn[], card: BoardCard): BoardColumn[] {
  return columns.map((column) =>
    column.status === card.status
      ? {
          ...column,
          cards: column.cards.map((c) => (c.id === card.id ? card : c)).sort(compareCards),
        }
      : column,
  );
}

export function cardFromDetail(detail: TicketDetail): BoardCard {
  return {
    id: detail.id,
    number: detail.number,
    title: detail.title,
    status: detail.status,
    priority: detail.priority,
    requester: detail.requester,
    assignee: detail.assignee,
    updatedUtc: detail.updatedUtc,
    commentCount: detail.comments.length,
    rowVersion: detail.rowVersion,
    allowedTransitions: detail.allowedTransitions,
  };
}
