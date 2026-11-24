import { httpResource } from '@angular/common/http';
import { Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { auditTime, debounceTime, distinctUntilChanged } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import {
  PRIORITIES,
  PagedResult,
  STATUSES,
  STATUS_LABEL,
  TicketFilters,
  TicketPriority,
  TicketStats,
  TicketStatus,
  TicketSummary,
} from '../../core/models';
import { RealtimeService } from '../../core/realtime.service';
import { PriorityBadge, StatusBadge } from '../../shared/badges';
import { TimeAgoPipe } from '../../shared/time-ago.pipe';

const PAGE_SIZE = 15;

const DEFAULT_FILTERS: TicketFilters = {
  search: '',
  status: '',
  priority: '',
  assignment: 'all',
  sort: 'updated',
  descending: true,
  page: 1,
};

@Component({
  selector: 'app-ticket-list-page',
  imports: [FormsModule, RouterLink, StatusBadge, PriorityBadge, TimeAgoPipe],
  templateUrl: './ticket-list.page.html',
  styleUrl: './ticket-list.page.css',
})
export class TicketListPage {
  protected readonly auth = inject(AuthService);
  private readonly realtime = inject(RealtimeService);

  protected readonly statuses = STATUSES;
  protected readonly priorities = PRIORITIES;
  protected readonly statusLabel = STATUS_LABEL;

  protected readonly filters = signal<TicketFilters>(DEFAULT_FILTERS);
  protected readonly searchText = signal('');

  /** Typing is debounced so each keystroke does not become a request. */
  private readonly search = toSignal(
    toObservable(this.searchText).pipe(debounceTime(300), distinctUntilChanged()),
    { initialValue: '' },
  );

  private readonly query = computed(() => {
    const f = this.filters();
    const query: Record<string, string | number | boolean> = {
      sort: f.sort,
      descending: f.descending,
      page: f.page,
      pageSize: PAGE_SIZE,
    };

    const search = this.search().trim();
    if (search) query['search'] = search;
    if (f.status) query['status'] = f.status;
    if (f.priority) query['priority'] = f.priority;
    if (f.assignment === 'me') query['assigneeId'] = this.auth.user()?.id ?? '';
    if (f.assignment === 'unassigned') query['unassigned'] = true;
    return query;
  });

  // httpResource re-fetches whenever the signals read inside the request function change.
  protected readonly tickets = httpResource<PagedResult<TicketSummary>>(() => ({
    url: '/api/tickets',
    params: this.query(),
  }));

  protected readonly stats = httpResource<TicketStats>(() => '/api/tickets/stats');

  /** Keeps showing the previous page while the next one loads, so the table does not flash empty. */
  protected readonly view = linkedSignal<
    PagedResult<TicketSummary> | undefined,
    PagedResult<TicketSummary> | undefined
  >({
    source: () => (this.tickets.hasValue() ? this.tickets.value() : undefined),
    computation: (next, previous) => next ?? previous?.value,
  });

  protected readonly totalPages = computed(() => {
    const view = this.view();
    return view ? Math.max(1, Math.ceil(view.total / view.pageSize)) : 1;
  });

  protected readonly filtersActive = computed(() => {
    const f = this.filters();
    return !!(this.searchText() || f.status || f.priority || f.assignment !== 'all');
  });

  /** Ids of tickets that just changed remotely, highlighted briefly. */
  protected readonly flashing = signal<ReadonlySet<string>>(new Set());

  constructor() {
    this.realtime.changes$
      .pipe(
        // A burst of changes becomes one refresh.
        auditTime(400),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        this.tickets.reload();
        this.stats.reload();
      });

    this.realtime.changes$
      .pipe(takeUntilDestroyed())
      .subscribe((change) => this.flash(change.ticket.id));
  }

  protected patch(partial: Partial<TicketFilters>): void {
    this.filters.update((f) => ({ ...f, ...partial, page: partial.page ?? 1 }));
  }

  protected onSearch(value: string): void {
    this.searchText.set(value);
    this.filters.update((f) => ({ ...f, page: 1 }));
  }

  protected filterByStatus(status: TicketStatus): void {
    this.patch({ status: this.filters().status === status ? '' : status, assignment: 'all' });
  }

  protected setPriority(value: string): void {
    this.patch({ priority: value as TicketPriority | '' });
  }

  protected setSort(value: string): void {
    this.patch({ sort: value as TicketFilters['sort'] });
  }

  protected reset(): void {
    this.searchText.set('');
    this.filters.set(DEFAULT_FILTERS);
  }

  private flash(id: string): void {
    this.flashing.update((set) => new Set(set).add(id));
    setTimeout(() => {
      this.flashing.update((set) => {
        const next = new Set(set);
        next.delete(id);
        return next;
      });
    }, 2500);
  }
}
