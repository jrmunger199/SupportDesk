import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { MentionService } from '../core/mention.service';
import { MentionItem } from '../core/models';
import { NotificationBell } from './notification-bell';

const item = (id: string, overrides: Partial<MentionItem> = {}): MentionItem => ({
  id,
  ticketId: `ticket-${id}`,
  ticketNumber: Number(id) + 10,
  ticketTitle: `Ticket ${id}`,
  actor: { id: 'a1', displayName: 'Priya Nair' },
  snippet: 'Please look @Marcus Lee',
  createdUtc: new Date().toISOString(),
  isRead: false,
  ...overrides,
});

describe('NotificationBell', () => {
  let fixture: ComponentFixture<NotificationBell>;
  let root: HTMLElement;
  const items = signal<MentionItem[]>([]);
  const unread = signal(0);
  const markRead = vi.fn();
  const markAllRead = vi.fn();
  let navigate: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    items.set([]);
    unread.set(0);
    markRead.mockReset();
    markAllRead.mockReset();

    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: MentionService, useValue: { items, unread, markRead, markAllRead } },
      ],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(NotificationBell);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  const bell = () => root.querySelector('.bell') as HTMLButtonElement;
  const panel = () => root.querySelector('.panel');
  const open = () => {
    bell().click();
    fixture.detectChanges();
  };

  it('describes itself to screen readers, including how many are unread', () => {
    expect(bell().getAttribute('aria-label')).toBe('Mentions, none unread');

    unread.set(3);
    fixture.detectChanges();

    expect(bell().getAttribute('aria-label')).toBe('Mentions, 3 unread');
  });

  it('shows a count badge only when there is something unread, capped at 9+', () => {
    expect(root.querySelector('.badge-count')).toBeNull();

    unread.set(4);
    fixture.detectChanges();
    expect(root.querySelector('.badge-count')!.textContent!.trim()).toBe('4');

    unread.set(25);
    fixture.detectChanges();
    expect(root.querySelector('.badge-count')!.textContent!.trim()).toBe('9+');
  });

  it('opens and closes the panel from the button', () => {
    expect(panel()).toBeNull();

    open();
    expect(panel()).not.toBeNull();
    expect(bell().getAttribute('aria-expanded')).toBe('true');

    open();
    expect(panel()).toBeNull();
  });

  it('explains what will appear when there are no mentions', () => {
    open();

    expect(panel()!.textContent).toContain('mentions you on a ticket');
  });

  it('lists mentions with who, which ticket, and the excerpt', () => {
    items.set([
      item('1'),
      item('2', { isRead: true, actor: { id: 'a2', displayName: 'Dana Whitfield' } }),
    ]);
    open();

    const entries = [...root.querySelectorAll('.entry')] as HTMLElement[];
    expect(entries).toHaveLength(2);
    expect(entries[0].textContent).toContain('Priya Nair mentioned you on #11 Ticket 1');
    expect(entries[0].textContent).toContain('Please look @Marcus Lee');
    expect(entries[0].classList.contains('unread')).toBe(true);
    expect(entries[1].classList.contains('unread')).toBe(false);
  });

  it('opens the ticket, marks the mention read, and closes when one is clicked', () => {
    const mention = item('1');
    items.set([mention]);
    open();

    (root.querySelector('.entry') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(markRead).toHaveBeenCalledWith(mention);
    expect(navigate).toHaveBeenCalledWith(['/tickets', 'ticket-1']);
    expect(panel()).toBeNull();
  });

  it('marks everything read, but only offers to when something is unread', () => {
    items.set([item('1')]);
    unread.set(1);
    open();
    const markAll = root.querySelector('header .btn-link') as HTMLButtonElement;
    expect(markAll.disabled).toBe(false);

    markAll.click();

    expect(markAllRead).toHaveBeenCalledOnce();

    unread.set(0);
    fixture.detectChanges();
    expect((root.querySelector('header .btn-link') as HTMLButtonElement).disabled).toBe(true);
  });

  it('closes on Escape', () => {
    open();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();

    expect(panel()).toBeNull();
  });

  it('closes when the page is clicked elsewhere, but not when its own panel is clicked', () => {
    items.set([item('1')]);
    open();

    (root.querySelector('header h2') as HTMLElement).dispatchEvent(
      new MouseEvent('click', { bubbles: true }),
    );
    fixture.detectChanges();
    expect(panel()).not.toBeNull();

    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    fixture.detectChanges();
    expect(panel()).toBeNull();
  });
});
