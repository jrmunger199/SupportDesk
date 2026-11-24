import {
  Component,
  ElementRef,
  computed,
  forwardRef,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import {
  MentionPick,
  activeMentionQuery,
  filterCandidates,
  fromWire,
  insertMention,
  prunePicks,
  toWire,
} from '../core/mentions';
import { MentionService } from '../core/mention.service';
import { MentionCandidate } from '../core/models';
import { Avatar } from './avatar';

let nextId = 0;

/**
 * A textarea with @-autocomplete. People type and see plain `@Priya Nair`; the form control it is bound to holds the
 * API's token format, so validators and submit need no special handling.
 *
 * Accessibility: it follows the ARIA combobox pattern (role, aria-expanded, aria-activedescendant), and every
 * choice can be made from the keyboard: arrows to move, Enter or Tab to pick, Escape to dismiss.
 */
@Component({
  selector: 'app-mention-textarea',
  imports: [Avatar],
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => MentionTextarea), multi: true },
  ],
  template: `
    <div class="wrap">
      <textarea
        #box
        role="combobox"
        aria-autocomplete="list"
        aria-haspopup="listbox"
        [id]="inputId()"
        [attr.aria-expanded]="open()"
        [attr.aria-controls]="open() ? listId : null"
        [attr.aria-activedescendant]="open() ? optionId(active()) : null"
        [attr.aria-describedby]="hintId"
        [placeholder]="placeholder()"
        [disabled]="disabled()"
        [value]="text()"
        (input)="onInput()"
        (keydown)="onKeydown($event)"
        (click)="syncQuery()"
        (keyup)="onKeyup($event)"
        (blur)="onBlur()"
      ></textarea>

      @if (open()) {
        <ul class="popup" role="listbox" [id]="listId" aria-label="People to mention">
          @for (person of matches(); track person.id; let i = $index) {
            <li
              role="option"
              [id]="optionId(i)"
              [attr.aria-selected]="i === active()"
              [class.active]="i === active()"
              (mousedown)="$event.preventDefault(); choose(person)"
            >
              <app-avatar [name]="person.displayName" size="sm" />
              <span class="name">{{ person.displayName }}</span>
              <span class="role">{{ person.role }}</span>
            </li>
          }
        </ul>
      }
    </div>
    <p class="hint muted" [id]="hintId">Type &#64; to mention someone.</p>
  `,
  styles: `
    :host {
      display: block;
    }

    .wrap {
      position: relative;
    }

    .hint {
      margin: 0.25rem 0 0;
      font-size: 0.8rem;
    }

    .popup {
      position: absolute;
      left: 0;
      right: 0;
      top: calc(100% + 4px);
      z-index: 20;
      margin: 0;
      padding: 0.25rem;
      list-style: none;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 10px;
      box-shadow: 0 10px 28px rgb(0 0 0 / 22%);
      max-height: 15rem;
      overflow: auto;
    }

    .popup li {
      display: flex;
      align-items: center;
      gap: 0.6rem;
      padding: 0.4rem 0.55rem;
      border-radius: 8px;
      cursor: pointer;
    }

    .popup li.active,
    .popup li:hover {
      background: var(--accent-soft);
    }

    .name {
      font-weight: 600;
      flex: 1;
    }

    .role {
      color: var(--muted);
      font-size: 0.75rem;
    }
  `,
})
export class MentionTextarea implements ControlValueAccessor {
  /** The id the label's `for` attribute points at. */
  readonly inputId = input('');
  /** Limits suggestions to people who can see this ticket. Omit when writing a new one. */
  readonly ticketId = input<string>();
  readonly placeholder = input('');

  private readonly mentions = inject(MentionService);
  private readonly box = viewChild.required<ElementRef<HTMLTextAreaElement>>('box');
  private readonly uid = nextId++;

  protected readonly listId = `mention-list-${this.uid}`;
  protected readonly hintId = `mention-hint-${this.uid}`;
  protected readonly optionId = (index: number): string => `mention-option-${this.uid}-${index}`;

  protected readonly text = signal('');
  protected readonly disabled = signal(false);
  protected readonly active = signal(0);

  private readonly picks = signal<MentionPick[]>([]);
  private readonly query = signal<{ start: number; query: string } | null>(null);
  private readonly candidates = signal<MentionCandidate[]>([]);
  private readonly dismissed = signal(false);
  private loaded = false;

  protected readonly matches = computed(() => {
    const query = this.query();
    return query ? filterCandidates(this.candidates(), query.query).slice(0, 6) : [];
  });

  protected readonly open = computed(
    () => this.query() !== null && this.matches().length > 0 && !this.dismissed(),
  );

  private onChange: (value: string) => void = () => undefined;
  private onTouched: () => void = () => undefined;

  // ---- ControlValueAccessor ----

  writeValue(wire: string | null): void {
    const { text, picks } = fromWire(wire ?? '');
    this.text.set(text);
    this.picks.set(picks);
    this.query.set(null);
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }

  // ---- Editing ----

  protected onInput(): void {
    const text = this.element().value;
    this.text.set(text);
    this.picks.update((picks) => prunePicks(text, picks));
    this.dismissed.set(false);
    this.emit();
    this.syncQuery();
  }

  protected onKeyup(event: KeyboardEvent): void {
    // Moving the caret with the arrow keys can enter or leave a half-typed mention.
    if (!this.open() && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
      this.syncQuery();
    }
  }

  protected syncQuery(): void {
    const query = activeMentionQuery(
      this.text(),
      this.element().selectionStart ?? this.text().length,
    );
    this.query.set(query);
    if (query) {
      this.active.set(0);
      this.loadCandidates();
    }
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (!this.open()) {
      return;
    }

    const count = this.matches().length;
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.active.update((i) => (i + 1) % count);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.active.update((i) => (i - 1 + count) % count);
        break;
      case 'Enter':
      case 'Tab':
        event.preventDefault();
        this.choose(this.matches()[this.active()]);
        break;
      case 'Escape':
        event.preventDefault();
        this.dismissed.set(true);
        break;
    }
  }

  protected onBlur(): void {
    this.onTouched();
    this.dismissed.set(true);
  }

  protected choose(person: MentionCandidate): void {
    const query = this.query();
    if (!query) {
      return;
    }

    const element = this.element();
    const caret = element.selectionStart ?? this.text().length;
    const next = insertMention(this.text(), query, caret, person.displayName);

    this.text.set(next.text);
    this.picks.update((picks) => [
      ...picks.filter((p) => p.id !== person.id),
      { id: person.id, name: person.displayName },
    ]);
    this.query.set(null);
    this.emit();

    // The textarea's value is bound to the signal; put the caret after the inserted name once it has rendered.
    element.value = next.text;
    element.setSelectionRange(next.caret, next.caret);
    element.focus();
  }

  private loadCandidates(): void {
    if (this.loaded) {
      return;
    }

    this.loaded = true;
    this.mentions.candidates(this.ticketId()).subscribe({
      next: (people) => this.candidates.set(people),
      error: () => (this.loaded = false), // try again the next time someone types @
    });
  }

  private emit(): void {
    this.onChange(toWire(this.text(), this.picks()));
  }

  private element(): HTMLTextAreaElement {
    return this.box().nativeElement;
  }
}
