import { Component, inject } from '@angular/core';
import { ToastService } from '../core/toast.service';

/** Renders transient messages. aria-live makes screen readers announce them without stealing focus. */
@Component({
  selector: 'app-toast-host',
  template: `
    <div class="toasts" aria-live="polite" aria-atomic="false">
      @for (toast of toasts.toasts(); track toast.id) {
        <div class="toast" [class]="'toast ' + toast.kind" role="status">
          <span>{{ toast.message }}</span>
          <button
            type="button"
            class="btn-link"
            (click)="toasts.dismiss(toast.id)"
            aria-label="Dismiss message"
          >
            ×
          </button>
        </div>
      }
    </div>
  `,
  styles: `
    .toasts {
      position: fixed;
      right: 1rem;
      bottom: 1rem;
      display: grid;
      gap: 0.5rem;
      z-index: 70;
      max-width: min(26rem, calc(100vw - 2rem));
    }

    .toast {
      display: flex;
      justify-content: space-between;
      gap: 0.75rem;
      align-items: start;
      padding: 0.7rem 0.9rem;
      border-radius: 10px;
      background: var(--surface);
      border: 1px solid var(--border);
      border-left: 4px solid var(--accent);
      box-shadow: 0 6px 20px rgb(0 0 0 / 18%);
    }

    .toast.success {
      border-left-color: var(--success);
    }
    .toast.error {
      border-left-color: var(--danger);
    }
  `,
})
export class ToastHost {
  protected readonly toasts = inject(ToastService);
}
