import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { MentionService } from './mention.service';
import { MentionInbox, MentionItem } from './models';
import { RealtimeService } from './realtime.service';
import { ToastService } from './toast.service';

const item = (id: string, overrides: Partial<MentionItem> = {}): MentionItem => ({
  id,
  ticketId: `t-${id}`,
  ticketNumber: 12,
  ticketTitle: 'Printer jam',
  actor: { id: 'a1', displayName: 'Priya Nair' },
  snippet: 'Can you look @Marcus Lee?',
  createdUtc: '2026-11-24T10:00:00Z',
  isRead: false,
  ...overrides,
});

describe('MentionService', () => {
  let http: HttpTestingController;
  let mentions$: Subject<MentionItem>;

  function create(signedIn = true) {
    sessionStorage.clear();
    if (signedIn) {
      sessionStorage.setItem(
        'supportdesk.session',
        JSON.stringify({
          token: 't',
          expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
          organization: 'Acme',
          user: {
            id: 'u1',
            email: 'm@acme.test',
            displayName: 'Marcus',
            role: 'Agent',
            isActive: true,
          },
        }),
      );
    }

    mentions$ = new Subject<MentionItem>();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        // No real sockets in unit tests: a stand-in that emits only when the test says so.
        { provide: RealtimeService, useValue: { mentions$, changes$: new Subject() } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(MentionService);
    TestBed.tick(); // runs the effect that loads the inbox
    return service;
  }

  const inbox = (
    items: MentionItem[],
    unreadCount = items.filter((m) => !m.isRead).length,
  ): MentionInbox => ({
    items,
    unreadCount,
  });

  afterEach(() => sessionStorage.clear());

  it('loads the inbox when someone is signed in', () => {
    const service = create();

    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1'), item('2', { isRead: true })]));

    expect(service.items()).toHaveLength(2);
    expect(service.unread()).toBe(1);
  });

  it('does not call the API when nobody is signed in', () => {
    create(false);

    http.expectNone('/api/mentions?limit=30');
  });

  it('shows a new mention immediately, counts it, and announces it', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1')]));
    const toast = TestBed.inject(ToastService);

    mentions$.next(item('2', { snippet: 'Urgent @Marcus Lee' }));

    expect(service.items().map((m) => m.id)).toEqual(['2', '1']);
    expect(service.unread()).toBe(2);
    expect(toast.toasts()[0].message).toBe('Priya Nair mentioned you on #12: Urgent @Marcus Lee');
  });

  it('does not duplicate a mention that arrives twice', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([]));

    mentions$.next(item('1'));
    mentions$.next(item('1'));

    expect(service.items()).toHaveLength(1);
  });

  it('marks one mention read straight away and tells the server', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1'), item('2')]));

    service.markRead(service.items()[0]);

    expect(service.unread()).toBe(1);
    expect(service.items()[0].isRead).toBe(true);
    http.expectOne('/api/mentions/1/read').flush(null, { status: 204, statusText: 'No Content' });
  });

  it('does nothing for a mention that is already read', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1', { isRead: true })]));

    service.markRead(service.items()[0]);

    http.expectNone('/api/mentions/1/read');
    expect(service.unread()).toBe(0);
  });

  it('never lets the unread count go negative', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1')], 0)); // the server said 0 unread

    service.markRead(service.items()[0]);

    expect(service.unread()).toBe(0);
    http.expectOne('/api/mentions/1/read').flush(null, { status: 204, statusText: 'No Content' });
  });

  it('puts the bell right again if marking read fails', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1')]));

    service.markRead(service.items()[0]);
    http.expectOne('/api/mentions/1/read').flush({}, { status: 500, statusText: 'Server Error' });
    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1')])); // the refresh

    expect(service.unread()).toBe(1);
    expect(service.items()[0].isRead).toBe(false);
  });

  it('marks everything read', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([item('1'), item('2')]));

    service.markAllRead();

    expect(service.unread()).toBe(0);
    expect(service.items().every((m) => m.isRead)).toBe(true);
    http.expectOne('/api/mentions/read-all').flush(null, { status: 204, statusText: 'No Content' });
  });

  it('fetches the people who can be mentioned once per ticket and reuses the answer', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([]));
    const results: unknown[] = [];

    service.candidates('t-9').subscribe((r) => results.push(r));
    service.candidates('t-9').subscribe((r) => results.push(r));
    http
      .expectOne('/api/mentions/candidates?ticketId=t-9')
      .flush([{ id: 'p1', displayName: 'Priya Nair', role: 'Agent' }]);

    expect(results).toHaveLength(2);
    http.expectNone('/api/mentions/candidates?ticketId=t-9');
  });

  it('asks without a ticket id when writing a new ticket', () => {
    const service = create();
    http.expectOne('/api/mentions?limit=30').flush(inbox([]));

    service.candidates().subscribe();

    http.expectOne('/api/mentions/candidates').flush([]);
  });
});
