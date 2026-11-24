import { Injectable, signal } from '@angular/core';

export interface Toast {
  id: number;
  message: string;
  kind: 'info' | 'success' | 'error';
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  private readonly items = signal<Toast[]>([]);

  readonly toasts = this.items.asReadonly();

  show(message: string, kind: Toast['kind'] = 'info', durationMs = 5000): void {
    const toast: Toast = { id: this.nextId++, message, kind };
    this.items.update((list) => [...list.slice(-3), toast]);
    setTimeout(() => this.dismiss(toast.id), durationMs);
  }

  dismiss(id: number): void {
    this.items.update((list) => list.filter((t) => t.id !== id));
  }
}
