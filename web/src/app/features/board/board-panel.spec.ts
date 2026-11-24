import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { MentionService } from '../../core/mention.service';
import { Board, BoardCard, TicketChange, TicketDetail, TicketStatus } from '../../core/models';
import { RealtimeService } from '../../core/realtime.service';
import { TicketDetailPage } from '../tickets/ticket-detail.page';
import { BoardPage } from './board.page';

const card = (id: string, status: TicketStatus = 'Open'): BoardCard => ({
  id,
  number: Number(id),
  title: `Ticket ${id}`,
  status,
  priority: 'Normal',
  requester: { id: 'r1', displayName: 'Dana Whitfield' },
  assignee: null,
  updatedUtc: '2026-11-24T10:00:00Z',
  commentCount: 0,
  rowVersion: `rv-${id}`,
  allowedTransitions: ['InProgress', 'Resolved', 'Closed'],
});

const board: Board = {
  canMove: true,
  columns: [
    { status: 'Open', total: 2, cards: [card('1'), card('2')] },
    { status: 'InProgress', total: 0, cards: [] },
    { status: 'Resolved', total: 0, cards: [] },
    { status: 'Closed', total: 0, cards: [] },
  ],
};

const detail = (id: string): TicketDetail => ({
  id,
  number: Number(id),
  title: `Ticket ${id}`,
  description: 'Details',
  status: 'Open',
  priority: 'Normal',
  requester: { id: 'r1', displayName: 'Dana Whitfield' },
  assignee: null,
  createdUtc: '2026-11-24T09:00:00Z',
  updatedUtc: '2026-11-24T10:00:00Z',
  rowVersion: 'rv',
  allowedTransitions: ['InProgress'],
  comments: [],
  events: [],
});

describe('BoardPage side panel', () => {
  let fixture: ComponentFixture<BoardPage>;
  let http: HttpTestingController;
  let root: HTMLElement;
  let navigate: ReturnType<typeof vi.spyOn>;
  let page: { dragStarted(c: BoardCard): void; dragEnded(): void };

  beforeEach(() => {
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
        { provide: MentionService, useValue: { candidates: () => of([]) } },
        {
          provide: RealtimeService,
          useValue: {
            changes$: new Subject<TicketChange>(),
            status: signal('offline'),
            markOwnChange: vi.fn(),
            isOwnChange: () => false,
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  });

  afterEach(() => sessionStorage.clear());

  const boardRequests = () => http.match((r) => r.url === '/api/tickets/board');

  /** Renders the board, optionally with a ticket already open (as if arriving from a `?ticket=` link). */
  async function render(openTicket?: string): Promise<void> {
    fixture = TestBed.createComponent(BoardPage);
    if (openTicket) fixture.componentRef.setInput('ticket', openTicket);
    fixture.detectChanges();
    TestBed.tick();
    boardRequests()[0].flush(board);

    if (openTicket) {
      TestBed.tick();
      http.expectOne(`/api/tickets/${openTicket}`).flush(detail(openTicket));
      http.expectOne('/api/users').flush([]);
    }

    await fixture.whenStable();
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
    page = fixture.componentInstance as unknown as typeof page;
  }

  const title = (id: string) =>
    root.querySelector(`.ticket[data-id="${id}"] a.title`) as HTMLAnchorElement;
  /** Clicks, and reports whether the page's own handlers cancelled the browser's default (navigation). */
  const click = (el: HTMLElement, init: MouseEventInit = {}): boolean => {
    let prevented = false;
    // Registered after the component's listeners, so it sees their verdict, then stops jsdom trying to navigate.
    el.addEventListener(
      'click',
      (e) => {
        prevented = e.defaultPrevented;
        e.preventDefault();
      },
      { once: true },
    );
    el.dispatchEvent(
      new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ...init }),
    );
    return prevented;
  };

  // ---- Opening ----

  it('opens the ticket in the panel by putting it in the URL, instead of leaving the board', async () => {
    await render();

    expect(click(title('1'))).toBe(true); // no full page navigation
    expect(TestBed.inject(Router).navigateByUrl).not.toHaveBeenCalled(); // and no competing router navigation
    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({ queryParams: { ticket: '1' }, queryParamsHandling: 'merge' }),
    );
  });

  it('keeps the title a real link to the full page for "open in new tab"', async () => {
    await render();

    expect(title('1').getAttribute('href')).toBe('/tickets/1');
  });

  it.each([
    ['ctrl', { ctrlKey: true }],
    ['cmd', { metaKey: true }],
    ['shift', { shiftKey: true }],
    ['alt', { altKey: true }],
    ['middle button', { button: 1 }],
  ])(
    'leaves %s-click alone so the browser can open the full page in a new tab',
    async (_name, init) => {
      await render();

      expect(click(title('1'), init)).toBe(false);
      expect(navigate).not.toHaveBeenCalled();
    },
  );

  it('does not open the panel for the click that ends a drag, but works again right afterwards', async () => {
    await render();
    const first = fixture.componentInstance as unknown as typeof page;
    void first;

    page.dragStarted(card('1'));
    page.dragEnded();
    click(title('1'));
    expect(navigate).not.toHaveBeenCalled();

    await new Promise((resolve) => setTimeout(resolve)); // the guard lifts on the next tick
    click(title('1'));
    expect(navigate).toHaveBeenCalledOnce();
  });

  it('stretches the title link over the whole card so any click on the card opens it', async () => {
    await render();

    // One focusable link per card (plus the select), not a second tab stop for a card-level click handler.
    const focusable = root.querySelectorAll(
      '.ticket[data-id="1"] a, .ticket[data-id="1"] select, .ticket[data-id="1"] button',
    );
    expect([...focusable].map((el) => el.tagName)).toEqual(['A', 'SELECT']);
  });

  // ---- The panel ----

  it('shows no panel until a ticket is chosen', async () => {
    await render();

    expect(root.querySelector('app-side-panel')).toBeNull();
    expect(root.querySelector('.ticket.selected')).toBeNull();
  });

  it('shows the chosen ticket in a panel and highlights its card', async () => {
    await render('2');

    const dialog = root.querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('Ticket 2');
    expect(dialog.querySelector('header a')!.getAttribute('href')).toBe('/tickets/2');
    expect(root.querySelector('.ticket.selected')!.getAttribute('data-id')).toBe('2');
  });

  it('shows the ticket in its compact layout', async () => {
    await render('2');

    const embedded = fixture.debugElement.query(By.directive(TicketDetailPage));
    expect((embedded.nativeElement as HTMLElement).classList.contains('embedded')).toBe(true);
  });

  it.each([
    [
      'the close button',
      () => (document.querySelector('app-side-panel header button') as HTMLElement).click(),
    ],
    [
      'the dimmed backdrop',
      () => (document.querySelector('app-side-panel .backdrop') as HTMLElement).click(),
    ],
    [
      'Escape',
      () =>
        document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })),
    ],
  ])('closes by clearing the ticket from the URL when you use %s', async (_name, act) => {
    await render('2');

    act();

    expect(navigate).toHaveBeenCalledWith(
      [],
      expect.objectContaining({ queryParams: { ticket: null }, queryParamsHandling: 'merge' }),
    );
  });

  it('refreshes the board when something is changed inside the panel', async () => {
    await render('2');
    expect(boardRequests()).toHaveLength(0);

    fixture.debugElement.query(By.directive(TicketDetailPage)).componentInstance.updated.emit();
    TestBed.tick();

    expect(boardRequests()).toHaveLength(1);
  });

  it('starts a fresh panel for each ticket, so a half-written comment never crosses over', async () => {
    await render('2');
    const first = fixture.debugElement.query(By.directive(TicketDetailPage)).nativeElement;

    fixture.componentRef.setInput('ticket', '1');
    fixture.detectChanges();
    TestBed.tick();
    http.expectOne('/api/tickets/1').flush(detail('1'));
    http.expectOne('/api/users').flush([]);
    await fixture.whenStable();
    fixture.detectChanges();

    const second = fixture.debugElement.query(By.directive(TicketDetailPage)).nativeElement;
    expect(second).not.toBe(first);
    expect(root.querySelector('[role="dialog"]')!.textContent).toContain('Ticket 1');
  });
});
