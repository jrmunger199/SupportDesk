import { CdkTrapFocus } from '@angular/cdk/a11y';
import { Component, ElementRef, input, output, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';

let nextId = 0;

/**
 * A modal panel that slides in from the right. It dims the page, keeps keyboard focus inside while open, and closes on
 * Escape or a click on the backdrop. The caller decides what "closed" means (the board removes `?ticket=` from the URL).
 */
@Component({
  selector: 'app-side-panel',
  imports: [CdkTrapFocus, RouterLink],
  // Escape is heard on the document, not just inside the panel, so it works wherever keyboard focus happens to be.
  host: { '(document:keydown)': 'onKeydown($event)' },
  template: `
    <div class="backdrop" (click)="closed.emit()" aria-hidden="true"></div>
    <aside
      #panel
      class="panel"
      role="dialog"
      aria-modal="true"
      [attr.aria-labelledby]="titleId"
      tabindex="-1"
      cdkTrapFocus
      [cdkTrapFocusAutoCapture]="true"
      (focusout)="onFocusOut($event)"
    >
      <header>
        <h2 [id]="titleId">{{ heading() }}</h2>
        <div class="actions">
          @if (fullPage(); as link) {
            <a class="btn btn-sm" [routerLink]="link">Open full page</a>
          }
          <button
            type="button"
            class="btn btn-sm"
            [attr.aria-label]="'Close ' + heading().toLowerCase()"
            (click)="closed.emit()"
          >
            ✕
          </button>
        </div>
      </header>

      <div class="body"><ng-content /></div>
    </aside>
  `,
  styles: `
    .backdrop {
      position: fixed;
      inset: 0;
      z-index: 55;
      background: rgb(0 0 0 / 45%);
      animation: fade 0.15s ease-out;
    }

    .panel {
      position: fixed;
      z-index: 60;
      top: 0;
      right: 0;
      bottom: 0;
      display: flex;
      flex-direction: column;
      width: min(36rem, 100vw);
      background: var(--bg);
      border-left: 1px solid var(--border);
      box-shadow: -12px 0 40px rgb(0 0 0 / 35%);
      animation: slide-in 0.22s cubic-bezier(0.2, 0.8, 0.2, 1);
    }

    /* Focus is moved here programmatically as a safety net; a ring around the whole panel would just be noise. */
    .panel:focus {
      outline: none;
    }

    header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 1rem;
      padding: 0.7rem 1rem;
      border-bottom: 1px solid var(--border);
      background: var(--surface);
    }

    h2 {
      margin: 0;
      font-size: 1rem;
    }

    .actions {
      display: flex;
      gap: 0.5rem;
    }

    .body {
      flex: 1;
      overflow: auto;
      padding: 1rem;
    }

    @keyframes slide-in {
      from {
        transform: translateX(2rem);
        opacity: 0;
      }

      to {
        transform: none;
        opacity: 1;
      }
    }

    @keyframes fade {
      from {
        opacity: 0;
      }

      to {
        opacity: 1;
      }
    }
  `,
})
export class SidePanel {
  readonly heading = input.required<string>();

  /** Router commands for an "Open full page" link in the header. Omit to hide it. */
  readonly fullPage = input<readonly unknown[]>();

  readonly closed = output<void>();

  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');

  protected readonly titleId = `side-panel-title-${nextId++}`;

  protected onKeydown(event: KeyboardEvent): void {
    // Components inside (such as the mention picker) use Escape for their own popups; only close when nothing did.
    if (event.key === 'Escape' && !event.defaultPrevented) {
      this.closed.emit();
    }
  }

  /**
   * A focused control can disappear (a "Start progress" button vanishes once it has done its job), which drops focus to
   * the page behind the panel. Pull it back inside so keyboard users are never left stranded behind a modal.
   */
  protected onFocusOut(event: FocusEvent): void {
    if (event.relatedTarget === null) {
      setTimeout(() => {
        if (document.activeElement === document.body) {
          this.panel().nativeElement.focus();
        }
      });
    }
  }
}
