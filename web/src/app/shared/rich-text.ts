import { Component, computed, inject, input } from '@angular/core';
import { AuthService } from '../core/auth.service';
import { parseSegments } from '../core/mentions';

/**
 * Shows ticket text with @mentions as highlighted chips. Everything is rendered as text nodes, never as HTML,
 * so whatever a person typed (including markup) stays inert.
 */
@Component({
  selector: 'app-rich-text',
  template: `@for (segment of segments(); track $index) {
    @if (segment.kind === 'mention') {
      <span class="mention" [class.me]="segment.id === meId()">&#64;{{ segment.name }}</span>
    } @else {
      <ng-container>{{ segment.text }}</ng-container>
    }
  }`,
  styles: `
    :host {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }

    .mention {
      display: inline-block;
      padding: 0 0.35rem;
      border-radius: 6px;
      background: var(--accent-soft);
      color: var(--accent);
      font-weight: 600;
    }

    .mention.me {
      background: var(--accent);
      color: var(--accent-contrast);
    }
  `,
})
export class RichText {
  readonly text = input.required<string>();

  private readonly auth = inject(AuthService);

  protected readonly segments = computed(() => parseSegments(this.text()));
  protected readonly meId = computed(() => this.auth.user()?.id.toLowerCase());
}
