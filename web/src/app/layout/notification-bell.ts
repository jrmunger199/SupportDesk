import { Component, ElementRef, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MentionService } from '../core/mention.service';
import { MentionItem } from '../core/models';
import { Avatar } from '../shared/avatar';
import { TimeAgoPipe } from '../shared/time-ago.pipe';

/** A bell with an unread count; opens a panel of the person's @mentions. */
@Component({
  selector: 'app-notification-bell',
  imports: [Avatar, TimeAgoPipe],
  host: {
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'close()',
  },
  template: `
    <button
      type="button"
      class="bell"
      aria-haspopup="true"
      [attr.aria-expanded]="open()"
      [attr.aria-label]="
        inbox.unread() ? 'Mentions, ' + inbox.unread() + ' unread' : 'Mentions, none unread'
      "
      (click)="toggle()"
    >
      <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true" focusable="false">
        <path
          fill="currentColor"
          d="M12 22a2.4 2.4 0 0 0 2.35-1.9h-4.7A2.4 2.4 0 0 0 12 22Zm7-6.1V11a7 7 0 0 0-5.2-6.77V3.5a1.8 1.8 0 0 0-3.6 0v.73A7 7 0 0 0 5 11v4.9l-1.7 1.7V19h17.4v-1.4L19 15.9Z"
        />
      </svg>
      @if (inbox.unread() > 0) {
        <span class="badge-count" aria-hidden="true">{{
          inbox.unread() > 9 ? '9+' : inbox.unread()
        }}</span>
      }
    </button>

    @if (open()) {
      <section class="panel" aria-label="Mentions">
        <header>
          <h2>Mentions</h2>
          <button
            type="button"
            class="btn-link"
            [disabled]="inbox.unread() === 0"
            (click)="inbox.markAllRead()"
          >
            Mark all read
          </button>
        </header>

        @if (inbox.items().length === 0) {
          <p class="empty muted">
            Nothing yet. When someone &#64;mentions you on a ticket, it shows up here.
          </p>
        } @else {
          <ul>
            @for (item of inbox.items(); track item.id) {
              <li>
                <button
                  type="button"
                  class="entry"
                  [class.unread]="!item.isRead"
                  (click)="go(item)"
                >
                  <app-avatar [name]="item.actor.displayName" size="sm" />
                  <span class="text">
                    <span class="line"
                      ><strong>{{ item.actor.displayName }}</strong> mentioned you on
                      <span class="ticket"
                        >#{{ item.ticketNumber }} {{ item.ticketTitle }}</span
                      ></span
                    >
                    <span class="snippet muted">{{ item.snippet }}</span>
                    <span class="when muted">{{ item.createdUtc | timeAgo }}</span>
                  </span>
                  @if (!item.isRead) {
                    <span class="dot" aria-label="Unread"></span>
                  }
                </button>
              </li>
            }
          </ul>
        }
      </section>
    }
  `,
  styles: `
    :host {
      position: relative;
      display: inline-block;
    }

    .bell {
      position: relative;
      display: grid;
      place-items: center;
      width: 2.2rem;
      height: 2.2rem;
      border-radius: 8px;
      border: 1px solid transparent;
      background: none;
      color: var(--muted);
      cursor: pointer;
    }

    .bell:hover,
    .bell[aria-expanded='true'] {
      background: var(--surface-2);
      color: var(--text);
    }

    .badge-count {
      position: absolute;
      top: -0.1rem;
      right: -0.1rem;
      min-width: 1.1rem;
      padding: 0 0.28rem;
      border-radius: 999px;
      background: var(--danger);
      color: var(--surface);
      font-size: 0.68rem;
      font-weight: 800;
      line-height: 1.1rem;
      text-align: center;
    }

    .panel {
      position: absolute;
      right: 0;
      top: calc(100% + 6px);
      z-index: 40;
      width: min(24rem, calc(100vw - 1.5rem));
      max-height: 28rem;
      overflow: auto;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 12px;
      box-shadow: 0 14px 36px rgb(0 0 0 / 28%);
    }

    header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 0.7rem 0.9rem;
      border-bottom: 1px solid var(--border);
    }

    h2 {
      margin: 0;
      font-size: 0.95rem;
    }

    .btn-link:disabled {
      opacity: 0.45;
      cursor: default;
    }

    ul {
      list-style: none;
      margin: 0;
      padding: 0;
    }

    .entry {
      display: flex;
      gap: 0.65rem;
      align-items: flex-start;
      width: 100%;
      padding: 0.7rem 0.9rem;
      border: 0;
      border-bottom: 1px solid var(--border);
      background: none;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }

    .entry:hover {
      background: var(--surface-2);
    }

    .entry.unread {
      background: var(--accent-soft);
    }

    .text {
      display: grid;
      gap: 0.1rem;
      min-width: 0;
      flex: 1;
    }

    .ticket {
      font-weight: 600;
    }

    .snippet {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: 0.85rem;
    }

    .when {
      font-size: 0.75rem;
    }

    .dot {
      width: 0.55rem;
      height: 0.55rem;
      margin-top: 0.35rem;
      border-radius: 50%;
      background: var(--accent);
      flex: none;
    }

    .empty {
      padding: 1.2rem 0.9rem;
      margin: 0;
    }
  `,
})
export class NotificationBell {
  protected readonly inbox = inject(MentionService);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly open = signal(false);

  protected toggle(): void {
    this.open.update((value) => !value);
  }

  protected close(): void {
    this.open.set(false);
  }

  protected onDocumentClick(event: MouseEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  protected go(item: MentionItem): void {
    this.inbox.markRead(item);
    this.close();
    void this.router.navigate(['/tickets', item.ticketId]);
  }
}
