import { HttpClient } from '@angular/common/http';
import { Injectable, effect, inject, signal } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';
import { AuthService } from './auth.service';
import { MentionCandidate, MentionInbox, MentionItem } from './models';
import { RealtimeService } from './realtime.service';
import { ToastService } from './toast.service';

const INBOX_SIZE = 30;

/**
 * The signed-in person's mentions: an inbox with an unread count, kept current by the live push, plus the list of
 * people who can be mentioned (cached, since it rarely changes within a session).
 */
@Injectable({ providedIn: 'root' })
export class MentionService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly candidateCache = new Map<string, Observable<MentionCandidate[]>>();

  readonly items = signal<MentionItem[]>([]);
  readonly unread = signal(0);

  constructor() {
    const realtime = inject(RealtimeService);

    effect(() => {
      if (this.auth.isAuthenticated()) {
        this.refresh();
      } else {
        this.items.set([]);
        this.unread.set(0);
        this.candidateCache.clear();
      }
    });

    realtime.mentions$.subscribe((mention) => {
      this.items.update((list) =>
        [mention, ...list.filter((m) => m.id !== mention.id)].slice(0, INBOX_SIZE),
      );
      this.unread.update((count) => count + 1);
      this.toast.show(
        `${mention.actor.displayName} mentioned you on #${mention.ticketNumber}: ${mention.snippet}`,
      );
    });
  }

  refresh(): void {
    this.http.get<MentionInbox>(`/api/mentions?limit=${INBOX_SIZE}`).subscribe({
      next: (inbox) => {
        this.items.set(inbox.items);
        this.unread.set(inbox.unreadCount);
      },
      error: () => undefined, // the bell simply stays as it was
    });
  }

  /** People who can be mentioned on a ticket, or in a new ticket when no id is given. */
  candidates(ticketId?: string): Observable<MentionCandidate[]> {
    const key = ticketId ?? '';
    let request = this.candidateCache.get(key);
    if (!request) {
      const query = ticketId ? `?ticketId=${encodeURIComponent(ticketId)}` : '';
      request = this.http
        .get<MentionCandidate[]>(`/api/mentions/candidates${query}`)
        .pipe(shareReplay({ bufferSize: 1, refCount: false }));
      this.candidateCache.set(key, request);
    }
    return request;
  }

  markRead(item: MentionItem): void {
    if (item.isRead) {
      return;
    }

    // Update the bell first; if the server disagrees, the refresh puts it right.
    this.items.update((list) => list.map((m) => (m.id === item.id ? { ...m, isRead: true } : m)));
    this.unread.update((count) => Math.max(0, count - 1));
    this.http
      .post(`/api/mentions/${item.id}/read`, null)
      .subscribe({ error: () => this.refresh() });
  }

  markAllRead(): void {
    this.items.update((list) => list.map((m) => ({ ...m, isRead: true })));
    this.unread.set(0);
    this.http.post('/api/mentions/read-all', null).subscribe({ error: () => this.refresh() });
  }
}
