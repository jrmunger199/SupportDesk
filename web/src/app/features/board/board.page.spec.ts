import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import {
  Board,
  BoardCard,
  BoardColumn,
  TicketChange,
  TicketDetail,
  TicketStatus,
} from '../../core/models';
import { RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../core/toast.service';
import { BoardPage } from './board.page';

const card = (id: string, status: TicketStatus, overrides: Partial<BoardCard> = {}): BoardCard => ({
  id,
  number: Number(id.replace(/\D/g, '')),
  title: `Ticket ${id}`,
  status,
  priority: 'Normal',
  requester: { id: 'r1', displayName: 'Dana Whitfield' },
  assignee: null,
  updatedUtc: '2026-11-24T10:00:00Z',
  commentCount: 0,
  rowVersion: `rv-${id}`,
  allowedTransitions:
    status === 'Open'
      ? ['InProgress', 'Resolved', 'Closed']
      : status === 'Closed'
        ? ['Open']
        : ['Open', 'Closed'],
  ...overrides,
});

const column = (status: TicketStatus, cards: BoardCard[], total = cards.length): BoardColumn => ({
  status,
  total,
  cards,
});

const sampleBoard = (canMove = true): Board => ({
  canMove,
  columns: [
    column('Open', [card('1', 'Open', { priority: 'Urgent', commentCount: 2 }), card('2', 'Open')]),
    column('InProgress', [
      card('3', 'InProgress', { assignee: { id: 'a1', displayName: 'Priya Nair' } }),
    ]),
    column('Resolved', []),
    column('Closed', [card('4', 'Closed')], 30),
  ],
});

describe('BoardPage', () => {
  let fixture: ComponentFixture<BoardPage>;
  let http: HttpTestingController;
  let root: HTMLElement;
  let page: { moveTo(card: BoardCard, to: TicketStatus): void; dropped(event: unknown): void };
  const changes$ = new Subject<TicketChange>();
  const markOwnChange = vi.fn();

  beforeEach(() => {
    markOwnChange.mockReset();
    sessionStorage.setItem(
      'supportdesk.session',
      JSON.stringify({
        token: 't',
        expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
        organization: 'Acme',
        user: {
          id: 'agent-1',
          email: 'p@acme.test',
          displayName: 'Priya',
          role: 'Agent',
          isActive: true,
        },
      }),
    );

    TestBed.configureTestingModule({
      imports: [BoardPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: RealtimeService,
          useValue: {
            changes$,
            status: signal('offline'),
            markOwnChange,
            isOwnChange: () => false,
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => sessionStorage.clear());

  const boardRequests = () => http.match((r) => r.url === '/api/tickets/board');

  async function render(board: Board = sampleBoard()): Promise<void> {
    fixture = TestBed.createComponent(BoardPage);
    fixture.detectChanges();
    TestBed.tick();
    boardRequests()[0].flush(board);
    await fixture.whenStable();
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
    page = fixture.componentInstance as unknown as typeof page;
  }

  const columns = () => [...root.querySelectorAll('.column')] as HTMLElement[];
  const numbers = (status: TicketStatus) =>
    [
      ...columns()
        .find((c) => c.dataset['status'] === status)!
        .querySelectorAll('.ticket .number'),
    ].map((n) => n.textContent!.trim());
  const count = (status: TicketStatus) =>
    columns()
      .find((c) => c.dataset['status'] === status)!
      .querySelector('.count')!
      .textContent!.trim();

  const patchRequest = (): TestRequest => http.expectOne((r) => r.method === 'PATCH');

  it('requests the board, by default with no filters', async () => {
    fixture = TestBed.createComponent(BoardPage);
    fixture.detectChanges();
    TestBed.tick();

    const [request] = boardRequests();
    expect(request.request.params.keys()).toEqual([]);
    request.flush(sampleBoard());
  });

  it('renders a column per status with its cards and the true total', async () => {
    await render();

    expect(columns().map((c) => c.querySelector('h2')!.textContent!.trim())).toEqual([
      'Open',
      'In progress',
      'Resolved',
      'Closed',
    ]);
    expect(numbers('Open')).toEqual(['#1', '#2']);
    expect(numbers('InProgress')).toEqual(['#3']);
    expect(count('Closed')).toBe('30');
    expect(root.textContent).toContain('Showing 1 of 30');
  });

  it('shows priority, comment count and the assignee on each card', async () => {
    await render();

    const first = root.querySelector('.ticket') as HTMLElement;
    expect(first.dataset['priority']).toBe('Urgent');
    expect(first.textContent).toContain('2');
    expect(root.querySelector('[data-status="InProgress"] .assignee')!.textContent).toContain(
      'Assigned to Priya Nair',
    );
    expect(root.querySelector('[data-status="Open"] .unassigned')!.textContent).toContain(
      'Unassigned',
    );
  });

  it('lets staff drag and offers a keyboard-friendly "move to" list limited to legal moves', async () => {
    await render();

    const firstOpen = root.querySelector('[data-status="Open"] .ticket') as HTMLElement;
    expect(firstOpen.classList.contains('draggable')).toBe(true);
    const options = [...firstOpen.querySelectorAll('select option')]
      .map((o) => (o as HTMLOptionElement).value)
      .filter(Boolean);
    expect(options).toEqual(['InProgress', 'Resolved', 'Closed']);

    const closed = root.querySelector('[data-status="Closed"] .ticket') as HTMLElement;
    expect(
      [...closed.querySelectorAll('select option')]
        .map((o) => (o as HTMLOptionElement).value)
        .filter(Boolean),
    ).toEqual(['Open']);
  });

  it('is read-only for people who cannot move tickets', async () => {
    await render(sampleBoard(false));

    expect(root.querySelector('.ticket.draggable')).toBeNull();
    expect(root.querySelector('.move')).toBeNull();
    expect(root.textContent).toContain('Your tickets by status');
  });

  it("moves a card immediately, then confirms it with the server using the card's row version", async () => {
    await render();
    const toast = TestBed.inject(ToastService);

    page.moveTo(sampleBoard().columns[0].cards[1], 'InProgress'); // #2
    fixture.detectChanges();

    // Optimistic: already in the new column before the server answers.
    expect(numbers('Open')).toEqual(['#1']);
    expect(numbers('InProgress')).toContain('#2');
    expect(count('InProgress')).toBe('2');
    expect(markOwnChange).toHaveBeenCalledWith('2');

    const request = patchRequest();
    expect(request.request.url).toBe('/api/tickets/2');
    expect(request.request.body).toEqual({
      status: 'InProgress',
      clearAssignee: false,
      rowVersion: 'rv-2',
    });

    const confirmed: Partial<TicketDetail> = {
      id: '2',
      number: 2,
      title: 'Ticket 2',
      description: '',
      status: 'InProgress',
      priority: 'Normal',
      requester: { id: 'r1', displayName: 'Dana Whitfield' },
      assignee: null,
      createdUtc: '2026-11-24T09:00:00Z',
      updatedUtc: '2026-11-24T11:00:00Z',
      rowVersion: 'rv-2-new',
      allowedTransitions: ['Open', 'Resolved', 'Closed'],
      comments: [],
      events: [],
    };
    request.flush(confirmed);
    fixture.detectChanges();

    const stored = root.querySelector(
      '[data-status="InProgress"] .ticket:has(.number)',
    ) as HTMLElement | null;
    expect(stored).not.toBeNull();
    expect(toast.toasts().at(-1)!.message).toBe('#2 moved to In progress');
  });

  it('puts the card back and refreshes if the server rejects the move', async () => {
    await render();
    const toast = TestBed.inject(ToastService);

    page.moveTo(sampleBoard().columns[0].cards[1], 'Resolved');
    fixture.detectChanges();
    expect(numbers('Open')).toEqual(['#1']);

    patchRequest().flush(
      { detail: 'A ticket cannot move from Open to Resolved.' },
      { status: 422, statusText: 'Unprocessable' },
    );
    fixture.detectChanges();

    expect(numbers('Open')).toContain('#2');
    expect(toast.toasts().at(-1)!.kind).toBe('error');
    expect(boardRequests()).toHaveLength(1); // fetched the truth again
  });

  it('explains a conflict when someone else changed the ticket first', async () => {
    await render();
    const toast = TestBed.inject(ToastService);

    page.moveTo(sampleBoard().columns[0].cards[0], 'Closed');
    patchRequest().flush({}, { status: 409, statusText: 'Conflict' });

    expect(toast.toasts().at(-1)!.message).toContain('Someone else changed that ticket first');
  });

  it('ignores moves the workflow does not allow, without calling the server', async () => {
    await render();

    page.moveTo(sampleBoard().columns[3].cards[0], 'Resolved'); // Closed can only be reopened
    page.moveTo(sampleBoard().columns[0].cards[0], 'Open'); // same column

    http.expectNone((r) => r.method === 'PATCH');
  });

  it('turns a drop into another column into a move, and a drop in the same column into nothing', async () => {
    await render();
    const open = {};
    const inProgress = {};

    page.dropped({
      previousContainer: open,
      container: open,
      item: { data: sampleBoard().columns[0].cards[0] },
    });
    http.expectNone((r) => r.method === 'PATCH');

    page.dropped({
      previousContainer: open,
      container: Object.assign(inProgress, { data: 'InProgress' }),
      item: { data: sampleBoard().columns[0].cards[0] },
    });
    expect(patchRequest().request.body.status).toBe('InProgress');
  });

  it("filters to the signed-in person's tickets", async () => {
    await render();
    const mine = [...root.querySelectorAll('.scope button')].find((b) =>
      b.textContent!.includes('Assigned to me'),
    ) as HTMLButtonElement;

    mine.click();
    fixture.detectChanges();
    TestBed.tick();

    const request = boardRequests().at(-1)!;
    expect(request.request.params.get('assigneeId')).toBe('agent-1');
    request.flush(sampleBoard());
  });

  it('filters to unassigned tickets', async () => {
    await render();
    const unassigned = [...root.querySelectorAll('.scope button')].find((b) =>
      b.textContent!.includes('Unassigned'),
    ) as HTMLButtonElement;

    unassigned.click();
    fixture.detectChanges();
    TestBed.tick();

    const request = boardRequests().at(-1)!;
    expect(request.request.params.get('unassigned')).toBe('true');
    request.flush(sampleBoard());
  });

  it('refreshes once, after a short pause, when someone else changes tickets', async () => {
    await render();

    // A burst of three changes...
    for (let i = 0; i < 3; i++) {
      changes$.next({ kind: 'updated', ticket: {} as TicketChange['ticket'] });
    }
    TestBed.tick();
    expect(boardRequests()).toHaveLength(0); // ...does nothing straight away...

    await new Promise((resolve) => setTimeout(resolve, 550));
    TestBed.tick();

    const reloads = boardRequests();
    expect(reloads).toHaveLength(1); // ...then becomes a single refresh
    reloads[0].flush(sampleBoard());
  });
});
