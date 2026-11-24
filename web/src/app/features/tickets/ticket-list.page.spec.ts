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
import { PagedResult, TicketChange, TicketStats, TicketSummary } from '../../core/models';
import { RealtimeService } from '../../core/realtime.service';
import { TicketListPage } from './ticket-list.page';

const ticket = (n: number, overrides: Partial<TicketSummary> = {}): TicketSummary => ({
  id: `id-${n}`,
  number: n,
  title: `Ticket ${n}`,
  status: 'Open',
  priority: 'Normal',
  requester: { id: 'r1', displayName: 'Dana Whitfield' },
  assignee: null,
  createdUtc: '2026-11-24T10:00:00Z',
  updatedUtc: '2026-11-24T10:00:00Z',
  commentCount: 0,
  ...overrides,
});

const page = (items: TicketSummary[], total = items.length): PagedResult<TicketSummary> => ({
  items,
  total,
  page: 1,
  pageSize: 15,
});

const stats: TicketStats = {
  byStatus: { Open: 3, InProgress: 2, Resolved: 1, Closed: 0 },
  unassigned: 2,
  assignedToMe: 1,
};

describe('TicketListPage', () => {
  let fixture: ComponentFixture<TicketListPage>;
  let http: HttpTestingController;
  let root: HTMLElement;

  beforeEach(() => {
    sessionStorage.setItem(
      'supportdesk.session',
      JSON.stringify({
        token: 't',
        expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
        organization: 'Acme',
        user: {
          id: 'agent-1',
          email: 'a@b.test',
          displayName: 'Priya',
          role: 'Agent',
          isActive: true,
        },
      }),
    );

    TestBed.configureTestingModule({
      imports: [TicketListPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        // Unit tests must not open real sockets; a stand-in that never emits is enough here.
        {
          provide: RealtimeService,
          useValue: { changes$: new Subject<TicketChange>(), status: signal('offline') },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => sessionStorage.clear());

  const listRequests = () => http.match((r) => r.url === '/api/tickets');

  async function render(items: TicketSummary[]): Promise<void> {
    fixture = TestBed.createComponent(TicketListPage);
    fixture.detectChanges();
    TestBed.tick();

    const [list] = listRequests();
    list.flush(page(items));
    http.expectOne('/api/tickets/stats').flush(stats);

    // With every request answered the app can settle; httpResource publishes its value asynchronously.
    await fixture.whenStable();
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  }

  const lastListRequest = async (): Promise<TestRequest> => {
    TestBed.tick();
    const requests = listRequests();
    return requests[requests.length - 1];
  };

  it('requests the first page sorted by last update', async () => {
    fixture = TestBed.createComponent(TicketListPage);
    fixture.detectChanges();
    TestBed.tick();

    const [request] = listRequests();
    expect(request.request.params.get('sort')).toBe('updated');
    expect(request.request.params.get('descending')).toBe('true');
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe('15');
    request.flush(page([]));
    http.expectOne('/api/tickets/stats').flush(stats);
  });

  it('renders tickets with their status and priority', async () => {
    await render([
      ticket(1, {
        title: 'VPN drops',
        status: 'InProgress',
        priority: 'High',
        assignee: { id: 'a', displayName: 'Marcus Lee' },
      }),
    ]);

    expect(root.textContent).toContain('VPN drops');
    expect(root.textContent).toContain('In progress');
    expect(root.textContent).toContain('High');
    expect(root.textContent).toContain('Marcus Lee');
  });

  it('shows the status counts from the stats endpoint', async () => {
    await render([ticket(1)]);

    const stat = [...root.querySelectorAll('.stat')].find((s) => s.textContent?.includes('Open'));
    expect(stat?.textContent).toContain('3');
  });

  it('shows an empty state when there are no tickets', async () => {
    await render([]);

    expect(root.textContent).toContain('No tickets here');
    expect(root.textContent).toContain('Raise the first ticket');
  });

  it('filters by status when a summary card is clicked, and clears it when clicked again', async () => {
    await render([ticket(1)]);
    const card = [...root.querySelectorAll<HTMLButtonElement>('.stat')].find((s) =>
      s.textContent?.includes('Resolved'),
    )!;

    card.click();
    fixture.detectChanges();
    const filtered = await lastListRequest();
    expect(filtered.request.params.get('status')).toBe('Resolved');
    filtered.flush(page([]));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(card.getAttribute('aria-pressed')).toBe('true');

    card.click();
    fixture.detectChanges();
    const cleared = await lastListRequest();
    expect(cleared.request.params.has('status')).toBe(false);
  });

  it('filters to unassigned tickets for staff', async () => {
    await render([ticket(1)]);
    const card = [...root.querySelectorAll<HTMLButtonElement>('.stat')].find((s) =>
      s.textContent?.includes('Unassigned'),
    )!;

    card.click();
    fixture.detectChanges();

    expect((await lastListRequest()).request.params.get('unassigned')).toBe('true');
  });

  it('filters to the signed-in agent for "assigned to me"', async () => {
    await render([ticket(1)]);
    const card = [...root.querySelectorAll<HTMLButtonElement>('.stat')].find((s) =>
      s.textContent?.includes('Assigned to me'),
    )!;

    card.click();
    fixture.detectChanges();

    expect((await lastListRequest()).request.params.get('assigneeId')).toBe('agent-1');
  });

  it('disables paging at the ends of the list', async () => {
    await render([ticket(1)]);

    const [previous, next] = [...root.querySelectorAll<HTMLButtonElement>('.pager button')];
    expect(previous.disabled).toBe(true);
    expect(next.disabled).toBe(true);
  });
});
