import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';
import { MentionService } from '../../core/mention.service';
import { TicketChange, TicketDetail } from '../../core/models';
import { RealtimeService } from '../../core/realtime.service';
import { TicketDetailPage } from './ticket-detail.page';

const detail = (overrides: Partial<TicketDetail> = {}): TicketDetail => ({
  id: 't-1',
  number: 12,
  title: 'Printer jam',
  description: 'It jams on thick paper',
  status: 'Open',
  priority: 'Normal',
  requester: { id: 'r1', displayName: 'Dana Whitfield' },
  assignee: null,
  createdUtc: '2026-11-24T09:00:00Z',
  updatedUtc: '2026-11-24T10:00:00Z',
  rowVersion: 'rv-1',
  allowedTransitions: ['InProgress', 'Resolved', 'Closed'],
  comments: [],
  events: [],
  ...overrides,
});

describe('TicketDetailPage', () => {
  let fixture: ComponentFixture<TicketDetailPage>;
  let http: HttpTestingController;
  let root: HTMLElement;
  const changes$ = new Subject<TicketChange>();

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
      imports: [TicketDetailPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: MentionService, useValue: { candidates: () => of([]) } },
        {
          provide: RealtimeService,
          useValue: {
            changes$,
            status: signal('offline'),
            markOwnChange: vi.fn(),
            isOwnChange: () => false,
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => sessionStorage.clear());

  async function render(
    embedded: boolean | undefined,
    ticket: TicketDetail = detail(),
  ): Promise<void> {
    fixture = TestBed.createComponent(TicketDetailPage);
    fixture.componentRef.setInput('id', 't-1');
    if (embedded !== undefined) fixture.componentRef.setInput('embedded', embedded);
    fixture.detectChanges();
    TestBed.tick();

    http.expectOne('/api/tickets/t-1').flush(ticket);
    http.expectOne('/api/users').flush([]);
    await fixture.whenStable();
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  }

  it('shows the "all tickets" link on its own page, but not inside the side panel', async () => {
    await render(undefined);
    expect(root.textContent).toContain('All tickets');
    expect(root.classList.contains('embedded')).toBe(false);
  });

  it('drops the back link and marks itself embedded inside the side panel', async () => {
    await render(true);

    expect(root.textContent).not.toContain('All tickets');
    expect(root.classList.contains('embedded')).toBe(true);
    expect(root.textContent).toContain('Printer jam');
  });

  it('announces a change after a staff member moves the ticket along', async () => {
    await render(true);
    const updated = vi.fn();
    fixture.componentInstance.updated.subscribe(updated);

    (root.querySelector('.actions button') as HTMLButtonElement).click();
    const patch = http.expectOne((r) => r.method === 'PATCH');
    patch.flush(detail({ status: 'InProgress', rowVersion: 'rv-2' }));

    expect(patch.request.body).toEqual({
      status: 'InProgress',
      clearAssignee: false,
      rowVersion: 'rv-1',
    });
    expect(updated).toHaveBeenCalledOnce();
  });

  it('does not announce a change when the update fails', async () => {
    await render(true);
    const updated = vi.fn();
    fixture.componentInstance.updated.subscribe(updated);

    (root.querySelector('.actions button') as HTMLButtonElement).click();
    http.expectOne((r) => r.method === 'PATCH').flush({}, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(updated).not.toHaveBeenCalled();
    expect(root.textContent).toContain('Someone else changed this ticket');
  });

  it('announces a change after a comment is posted', async () => {
    await render(true);
    const updated = vi.fn();
    fixture.componentInstance.updated.subscribe(updated);

    const textarea = root.querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = 'On it';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    root.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));

    http.expectOne((r) => r.method === 'POST' && r.url === '/api/tickets/t-1/comments').flush({});
    expect(updated).toHaveBeenCalledOnce();
    TestBed.tick();
    http.expectOne('/api/tickets/t-1'); // and it reloads to show the comment
  });
});
