import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { PRIORITIES, TicketDetail, TicketPriority } from '../../core/models';
import { problemMessage } from '../../core/problem';
import { RealtimeService } from '../../core/realtime.service';
import { MentionTextarea } from '../../shared/mention-textarea';

@Component({
  selector: 'app-ticket-new-page',
  imports: [ReactiveFormsModule, RouterLink, MentionTextarea],
  template: `
    <p><a routerLink="/tickets">← All tickets</a></p>

    <form class="card stack form" [formGroup]="form" (ngSubmit)="submit()">
      <h1>New ticket</h1>
      <p class="muted">
        Describe the problem and what you have already tried. The more detail, the faster it can be
        resolved.
      </p>

      @if (error()) {
        <p class="banner error" role="alert">{{ error() }}</p>
      }

      <div class="field">
        <label for="title">Title</label>
        <input id="title" type="text" formControlName="title" maxlength="200" />
        @if (form.controls.title.touched && form.controls.title.invalid) {
          <span class="error-text">Give the ticket a short title (3 to 200 characters).</span>
        }
      </div>

      <div class="field">
        <label for="description">Description</label>
        <app-mention-textarea
          inputId="description"
          formControlName="description"
          placeholder="What is happening? Mention a colleague with @ to loop them in."
        />
        @if (form.controls.description.touched && form.controls.description.invalid) {
          <span class="error-text">Add a description.</span>
        }
      </div>

      <div class="field narrow">
        <label for="priority">Priority</label>
        <select id="priority" formControlName="priority">
          @for (p of priorities; track p) {
            <option [value]="p">{{ p }}</option>
          }
        </select>
      </div>

      <div class="row">
        <button type="submit" class="btn btn-primary" [disabled]="busy()">
          {{ busy() ? 'Creating…' : 'Create ticket' }}
        </button>
        <a class="btn" routerLink="/tickets">Cancel</a>
      </div>
    </form>
  `,
  styles: `
    .form {
      max-width: 42rem;
    }

    .narrow {
      max-width: 14rem;
    }
  `,
})
export class TicketNewPage {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly realtime = inject(RealtimeService);
  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly priorities = PRIORITIES;
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected readonly form = this.fb.group({
    title: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(200)]],
    description: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(10000)]],
    priority: this.fb.control<TicketPriority>('Normal'),
  });

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set('');
    this.http.post<TicketDetail>('/api/tickets', this.form.getRawValue()).subscribe({
      next: (ticket) => {
        this.realtime.markOwnChange(ticket.id);
        void this.router.navigate(['/tickets', ticket.id]);
      },
      error: (e: unknown) => {
        this.error.set(problemMessage(e));
        this.busy.set(false);
      },
    });
  }
}
