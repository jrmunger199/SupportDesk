import { CdkDrag, CdkDragDrop, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HttpClient, httpResource } from '@angular/common/http';
import { Component, computed, inject, input, linkedSignal, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { auditTime, debounceTime, distinctUntilChanged, filter } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import {
  Board,
  BoardCard,
  BoardColumn,
  STATUS_LABEL,
  TicketDetail,
  TicketStatus,
} from '../../core/models';
import { isConflict, problemMessage } from '../../core/problem';
import { RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../core/toast.service';
import { Avatar } from '../../shared/avatar';
import { PriorityBadge } from '../../shared/badges';
import { SidePanel } from '../../shared/side-panel';
import { TimeAgoPipe } from '../../shared/time-ago.pipe';
import { TicketDetailPage } from '../tickets/ticket-detail.page';
import { cardFromDetail, moveCard, replaceCard } from './board-logic';

type Scope = 'all' | 'mine' | 'unassigned';

@Component({
  selector: 'app-board-page',
  imports: [
    CdkDrag,
    CdkDropList,
    CdkDropListGroup,
    FormsModule,
    RouterLink,
    Avatar,
    PriorityBadge,
    TimeAgoPipe,
    SidePanel,
    TicketDetailPage,
  ],
  templateUrl: './board.page.html',
  styleUrl: './board.page.css',
})
export class BoardPage {
  /**
   * The ticket open in the side panel. It comes from the `?ticket=<id>` query parameter (bound to this input by
   * withComponentInputBinding), so the open ticket survives a refresh, can be shared as a link, and Back closes it.
   */
  readonly ticket = input<string>();

  protected readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly realtime = inject(RealtimeService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly statusLabel = STATUS_LABEL;
  protected readonly search = signal('');
  protected readonly scope = signal<Scope>('all');

  /** Typing is debounced so each keystroke does not become a request. */
  private readonly debouncedSearch = toSignal(
    toObservable(this.search).pipe(debounceTime(300), distinctUntilChanged()),
    { initialValue: '' },
  );

  private readonly query = computed(() => {
    const query: Record<string, string | number | boolean> = {};
    const search = this.debouncedSearch().trim();
    if (search) query['search'] = search;
    if (this.scope() === 'mine') query['assigneeId'] = this.auth.user()?.id ?? '';
    if (this.scope() === 'unassigned') query['unassigned'] = true;
    return query;
  });

  protected readonly board = httpResource<Board>(() => ({
    url: '/api/tickets/board',
    params: this.query(),
  }));

  /**
   * What is on screen. It follows the server's board, but is writable so a drag can show its result straight away.
   * It keeps the last good columns while a refresh is in flight, so the board never blanks out.
   */
  protected readonly columns = linkedSignal<Board | undefined, BoardColumn[]>({
    source: () => (this.board.hasValue() ? this.board.value() : undefined),
    computation: (board, previous) => board?.columns ?? previous?.value ?? [],
  });

  protected readonly canMove = computed(() => this.board.hasValue() && this.board.value().canMove);

  protected readonly dragging = signal<BoardCard | null>(null);
  protected readonly saving = signal<ReadonlySet<string>>(new Set());

  private refreshAfterDrag = false;

  /** A drag ends with a mouse-up that browsers can also report as a click; that must not open the panel. */
  private justDragged = false;

  /** The ticket that opened the panel, so keyboard focus can go back to its card when the panel closes. */
  private openedFrom: string | null = null;

  constructor() {
    this.realtime.changes$
      .pipe(
        filter((change) => !this.realtime.isOwnChange(change.ticket.id)),
        // A burst of changes becomes one refresh.
        auditTime(400),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        // Reloading under a card the person is holding would snatch it away; wait until they let go.
        if (this.dragging()) {
          this.refreshAfterDrag = true;
        } else {
          this.board.reload();
        }
      });
  }

  /** Only moves the workflow allows are accepted by a column; the others refuse the card. */
  protected readonly canEnter = (drag: CdkDrag, drop: CdkDropList): boolean =>
    (drag.data as BoardCard).allowedTransitions.includes(drop.data as TicketStatus);

  protected isTarget(status: TicketStatus): boolean {
    return this.dragging()?.allowedTransitions.includes(status) ?? false;
  }

  protected isDisabled(card: BoardCard): boolean {
    return !this.canMove() || card.allowedTransitions.length === 0 || this.saving().has(card.id);
  }

  protected dragStarted(card: BoardCard): void {
    this.dragging.set(card);
    this.justDragged = true;
  }

  protected dragEnded(): void {
    this.dragging.set(null);
    setTimeout(() => (this.justDragged = false)); // after the trailing click, if any
    if (this.refreshAfterDrag) {
      this.refreshAfterDrag = false;
      this.board.reload();
    }
  }

  protected dropped(event: CdkDragDrop<TicketStatus, TicketStatus, BoardCard>): void {
    if (event.previousContainer !== event.container) {
      this.moveTo(event.item.data, event.container.data);
    }
  }

  /** The keyboard (and touch) alternative to dragging. */
  protected selected(card: BoardCard, event: Event): void {
    const select = event.target as HTMLSelectElement;
    const status = select.value as TicketStatus | '';
    select.value = '';
    if (status) {
      this.moveTo(card, status);
    }
  }

  /**
   * Opens the ticket in the side panel. The title link is stretched over the whole card (see the CSS), so a click
   * anywhere on the card lands here, and Enter on the focused link works too: one tab stop, no extra key handling.
   */
  protected titleClicked(card: BoardCard, event: MouseEvent): void {
    // Ctrl/Cmd/Shift-click and middle-click keep opening the full page in a new tab or window.
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
      return;
    }

    event.preventDefault();
    this.open(card);
  }

  protected close(): void {
    void this.router
      .navigate([], {
        relativeTo: this.route,
        queryParams: { ticket: null },
        queryParamsHandling: 'merge',
      })
      .then(() => this.focusCard(this.openedFrom));
  }

  private open(card: BoardCard): void {
    if (this.justDragged) {
      return;
    }

    this.openedFrom = card.id;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { ticket: card.id },
      queryParamsHandling: 'merge',
    });
  }

  protected setScope(scope: Scope): void {
    this.scope.set(scope);
  }

  protected moveTo(card: BoardCard, to: TicketStatus): void {
    if (card.status === to || !card.allowedTransitions.includes(to)) {
      return;
    }

    const before = this.columns();
    this.columns.set(moveCard(before, card.id, to));
    this.setSaving(card.id, true);
    this.realtime.markOwnChange(card.id);

    this.http
      .patch<TicketDetail>(`/api/tickets/${card.id}`, {
        status: to,
        clearAssignee: false,
        rowVersion: card.rowVersion,
      })
      .subscribe({
        next: (detail) => {
          this.columns.update((columns) => replaceCard(columns, cardFromDetail(detail)));
          this.setSaving(card.id, false);
          this.toast.show(`#${card.number} moved to ${STATUS_LABEL[to]}`, 'success', 2500);
        },
        error: (error: unknown) => {
          // Undo the optimistic move, then fetch the truth.
          this.columns.set(before);
          this.setSaving(card.id, false);
          this.toast.show(
            isConflict(error)
              ? 'Someone else changed that ticket first, so it was not moved. The board has been refreshed.'
              : problemMessage(error),
            'error',
          );
          this.board.reload();
        },
      });
  }

  /**
   * Looked up by id rather than remembered as an element: a status change can move the card to another column, which
   * replaces its element, and focus should still land on the right card.
   */
  private focusCard(id: string | null): void {
    if (id) {
      // Wait a tick so the board has finished re-rendering.
      setTimeout(() =>
        document.querySelector<HTMLElement>(`.ticket[data-id="${id}"] a.title`)?.focus(),
      );
    }
  }

  private setSaving(id: string, saving: boolean): void {
    this.saving.update((current) => {
      const next = new Set(current);
      if (saving) next.add(id);
      else next.delete(id);
      return next;
    });
  }
}
