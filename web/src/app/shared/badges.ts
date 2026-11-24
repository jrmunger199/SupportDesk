import { Component, computed, input } from '@angular/core';
import { Role, STATUS_LABEL, TicketPriority, TicketStatus } from '../core/models';

@Component({
  selector: 'app-status-badge',
  template: `<span class="badge" [attr.data-status]="status()">{{ label() }}</span>`,
})
export class StatusBadge {
  readonly status = input.required<TicketStatus>();
  protected readonly label = computed(() => STATUS_LABEL[this.status()]);
}

@Component({
  selector: 'app-priority-badge',
  template: `<span class="badge" [attr.data-priority]="priority()">{{ priority() }}</span>`,
})
export class PriorityBadge {
  readonly priority = input.required<TicketPriority>();
}

@Component({
  selector: 'app-role-badge',
  template: `<span class="badge" [attr.data-role]="role()">{{ role() }}</span>`,
})
export class RoleBadge {
  readonly role = input.required<Role>();
}
