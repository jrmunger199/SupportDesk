import { Component, computed, input } from '@angular/core';

/** Initials on a colour that is stable for a given name, so the same person always looks the same. */
@Component({
  selector: 'app-avatar',
  template: `<span
    class="avatar"
    [class.sm]="size() === 'sm'"
    [style.--hue]="hue()"
    aria-hidden="true"
    >{{ initials() }}</span
  >`,
  styles: `
    .avatar {
      display: inline-grid;
      place-items: center;
      width: 1.9rem;
      height: 1.9rem;
      border-radius: 50%;
      background: hsl(var(--hue) 52% 40%);
      color: #fff;
      font-size: 0.72rem;
      font-weight: 700;
      letter-spacing: 0.02em;
      flex: none;
      user-select: none;
    }

    .avatar.sm {
      width: 1.5rem;
      height: 1.5rem;
      font-size: 0.62rem;
    }
  `,
})
export class Avatar {
  readonly name = input.required<string>();
  readonly size = input<'sm' | 'md'>('md');

  protected readonly initials = computed(() => {
    const parts = this.name().trim().split(/\s+/).filter(Boolean);
    const letters =
      parts.length > 1 ? parts[0][0] + parts[parts.length - 1][0] : (parts[0] ?? '?').slice(0, 2);
    return letters.toUpperCase();
  });

  protected readonly hue = computed(() => {
    let hash = 0;
    for (const char of this.name()) {
      hash = (hash * 31 + char.charCodeAt(0)) % 360;
    }
    return hash;
  });
}
