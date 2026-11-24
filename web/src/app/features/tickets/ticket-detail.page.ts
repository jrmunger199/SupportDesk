import { HttpClient, httpResource } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from '../../core/auth.service';
import {
  PRIORITIES,
  TicketDetail,
  TicketEventType,
  TicketPriority,
  TicketStatus,
  UpdateTicketRequest,
  UserDto,
} from '../../core/models';
import { isConflict, problemMessage } from '../../core/problem';
import { RealtimeService } from '../../core/realtime.service';
import { ToastService } from '../../core/toast.service';
import { PriorityBadge, StatusBadge } from '../../shared/badges';
import { MentionTextarea } from '../../shared/mention-textarea';
import { RichText } from '../../shared/rich-text';
import { TimeAgoPipe } from '../../shared/time-ago.pipe';

/** Button wording for moving a ticket into each status. */
const ACTION_LABEL: Record<TicketStatus, string> = {
  Open: 'Reopen',
  InProgress: 'Start progress',
  Resolved: 'Mark resolved',
  Closed: 'Close ticket',
};

type TimelineEntry =
  | { kind: 'comment'; id: string; at: string; who: string; body: string }
  | { kind: 'event'; id: string; at: string; who: string; type: TicketEventType; detail: string };

type TicketChanges = Partial<
  Pick<UpdateTicketRequest, 'status' | 'priority' | 'assigneeId' | 'clearAssignee'>
>;

@Component({
  selector: 'app-ticket-detail-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    StatusBadge,
    PriorityBadge,
    TimeAgoPipe,
    MentionTextarea,
    RichText,
  ],
  templateUrl: './ticket-detail.page.html',
  styleUrl: './ticket-detail.page.css',
  host: { '[class.embedded]': 'embedded()' },
})
export class TicketDetailPage {
  /** Bound from the :id route parameter by withComponentInputBinding(). */
  readonly id = input.required<string>();

  /** True when shown inside the board's side panel: one column, and no "back to all tickets" link. */
  readonly embedded = input(false);

  /** Fires after this view changed the ticket (status, priority, assignee or a new comment). */
  readonly updated = output<void>();

  protected readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly realtime = inject(RealtimeService);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly priorities = PRIORITIES;
  protected readonly actionLabel = ACTION_LABEL;

  protected readonly ticket = httpResource<TicketDetail>(() => `/api/tickets/${this.id()}`);

  // Only staff need the people list (for assigning); returning undefined keeps the resource idle for requesters.
  private readonly people = httpResource<UserDto[]>(() =>
    this.auth.isStaff() ? '/api/users' : undefined,
  );
  protected readonly assignees = computed(() =>
    (this.people.hasValue() ? this.people.value() : []).filter(
      (u) => u.isActive && u.role !== 'Requester',
    ),
  );

  protected readonly saving = signal(false);
  protected readonly posting = signal(false);

  /** Set when the server rejects an edit because someone else changed the ticket first (HTTP 409). */
  protected readonly conflict = signal(false);

  protected readonly commentControl = this.fb.control('', [
    Validators.required,
    Validators.maxLength(4000),
  ]);

  protected readonly timeline = computed<TimelineEntry[]>(() => {
    if (!this.ticket.hasValue()) {
      return [];
    }

    const { comments, events } = this.ticket.value();
    const entries: TimelineEntry[] = [
      ...events.map((e): TimelineEntry => ({
        kind: 'event',
        id: e.id,
        at: e.createdUtc,
        who: e.actor.displayName,
        type: e.type,
        detail: e.detail,
      })),
      ...comments.map((c): TimelineEntry => ({
        kind: 'comment',
        id: c.id,
        at: c.createdUtc,
        who: c.author.displayName,
        body: c.body,
      })),
    ];

    // Array.prototype.sort is stable, so entries with equal timestamps keep their server order.
    return entries.sort((a, b) => new Date(a.at).getTime() - new Date(b.at).getTime());
  });

  protected readonly canComment = computed(
    () => this.ticket.hasValue() && this.ticket.value().status !== 'Closed',
  );

  constructor() {
    this.realtime.changes$
      .pipe(
        filter(
          (change) =>
            change.ticket.id === this.id() && !this.realtime.isOwnChange(change.ticket.id),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.ticket.reload());
  }

  protected apply(changes: TicketChanges): void {
    if (!this.ticket.hasValue()) {
      return;
    }

    const current = this.ticket.value();
    const body: UpdateTicketRequest = {
      clearAssignee: false,
      ...changes,
      rowVersion: current.rowVersion,
    };

    this.saving.set(true);
    this.realtime.markOwnChange(current.id);
    this.http.patch<TicketDetail>(`/api/tickets/${current.id}`, body).subscribe({
      next: (updated) => {
        this.ticket.set(updated);
        this.conflict.set(false);
        this.saving.set(false);
        this.updated.emit();
      },
      error: (error: unknown) => {
        this.saving.set(false);
        if (isConflict(error)) {
          this.conflict.set(true);
        } else {
          this.toast.show(problemMessage(error), 'error');
        }
      },
    });
  }

  protected onPriority(value: string): void {
    this.apply({ priority: value as TicketPriority });
  }

  protected onAssignee(value: string): void {
    this.apply(value ? { assigneeId: value } : { clearAssignee: true });
  }

  protected reloadAfterConflict(): void {
    this.conflict.set(false);
    this.ticket.reload();
  }

  protected postComment(): void {
    if (this.commentControl.invalid || !this.ticket.hasValue()) {
      this.commentControl.markAsTouched();
      return;
    }

    const id = this.ticket.value().id;
    this.posting.set(true);
    this.realtime.markOwnChange(id);
    this.http.post(`/api/tickets/${id}/comments`, { body: this.commentControl.value }).subscribe({
      next: () => {
        this.commentControl.reset('');
        this.posting.set(false);
        this.ticket.reload();
        this.updated.emit();
      },
      error: (error: unknown) => {
        this.posting.set(false);
        this.toast.show(problemMessage(error), 'error');
        this.ticket.reload();
      },
    });
  }
}
