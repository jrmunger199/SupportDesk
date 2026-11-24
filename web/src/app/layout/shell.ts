import { Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { TicketChange } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { ToastService } from '../core/toast.service';
import { RoleBadge } from '../shared/badges';
import { NotificationBell } from './notification-bell';

const VERBS: Record<TicketChange['kind'], string> = {
  created: 'New ticket',
  updated: 'Updated',
  commented: 'New comment on',
};

@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, RoleBadge, NotificationBell],
  templateUrl: './shell.html',
  styleUrl: './shell.css',
})
export class Shell {
  protected readonly auth = inject(AuthService);
  protected readonly realtime = inject(RealtimeService);
  private readonly toast = inject(ToastService);

  constructor() {
    this.realtime.changes$.pipe(takeUntilDestroyed()).subscribe((change) => {
      // Don't announce changes the user just made themselves.
      if (!this.realtime.isOwnChange(change.ticket.id)) {
        this.toast.show(`${VERBS[change.kind]} #${change.ticket.number}: ${change.ticket.title}`);
      }
    });
  }
}
