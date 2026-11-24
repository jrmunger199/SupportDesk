import { HttpClient, httpResource } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../../core/auth.service';
import { ROLES, Role, UserDto } from '../../core/models';
import { problemMessage } from '../../core/problem';
import { ToastService } from '../../core/toast.service';
import { RoleBadge } from '../../shared/badges';

@Component({
  selector: 'app-users-page',
  imports: [ReactiveFormsModule, RoleBadge],
  template: `
    <h1>People</h1>
    <p class="muted">
      Everyone in {{ auth.organization() }}. Deactivated people can no longer sign in.
    </p>

    <div class="grid">
      <section class="card list" aria-label="People">
        @if (users.hasValue()) {
          <div class="table-wrap">
            <table class="table">
              <caption class="sr-only">
                People in your organisation
              </caption>
              <thead>
                <tr>
                  <th scope="col">Name</th>
                  <th scope="col">Email</th>
                  <th scope="col">Role</th>
                  <th scope="col">Status</th>
                  <th scope="col"><span class="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                @for (person of users.value(); track person.id) {
                  <tr [class.inactive]="!person.isActive">
                    <td>
                      <strong>{{ person.displayName }}</strong>
                      @if (person.id === auth.user()?.id) {
                        <span class="muted"> (you)</span>
                      }
                    </td>
                    <td class="muted">{{ person.email }}</td>
                    <td><app-role-badge [role]="person.role" /></td>
                    <td>{{ person.isActive ? 'Active' : 'Deactivated' }}</td>
                    <td>
                      @if (person.id !== auth.user()?.id) {
                        <button
                          type="button"
                          class="btn btn-sm"
                          [class.btn-danger]="person.isActive"
                          (click)="setActive(person, !person.isActive)"
                        >
                          {{ person.isActive ? 'Deactivate' : 'Reactivate' }}
                        </button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        } @else if (users.error()) {
          <p class="banner error" role="alert">Could not load people.</p>
        } @else {
          <div class="skeleton"></div>
        }
      </section>

      <form class="card stack" [formGroup]="form" (ngSubmit)="add()">
        <h2>Add a person</h2>
        @if (error()) {
          <p class="banner error" role="alert">{{ error() }}</p>
        }
        <div class="field">
          <label for="u-name">Name</label>
          <input id="u-name" type="text" formControlName="displayName" />
        </div>
        <div class="field">
          <label for="u-email">Email</label>
          <input id="u-email" type="email" formControlName="email" autocomplete="off" />
        </div>
        <div class="field">
          <label for="u-role">Role</label>
          <select id="u-role" formControlName="role">
            @for (role of roles; track role) {
              <option [value]="role">{{ role }}</option>
            }
          </select>
        </div>
        <div class="field">
          <label for="u-password">Temporary password</label>
          <input
            id="u-password"
            type="password"
            formControlName="password"
            autocomplete="new-password"
          />
          <span class="muted hint">At least 10 characters. Share it securely.</span>
        </div>
        <button type="submit" class="btn btn-primary" [disabled]="busy()">
          {{ busy() ? 'Adding…' : 'Add person' }}
        </button>
      </form>
    </div>
  `,
  styles: `
    .grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 20rem;
      gap: 1rem;
      align-items: start;
      margin-top: 1rem;
    }

    .list {
      padding: 0;
    }

    tr.inactive {
      opacity: 0.6;
    }

    .hint {
      font-size: 0.82rem;
    }

    @media (max-width: 900px) {
      .grid {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class UsersPage {
  protected readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly roles = ROLES;
  protected readonly users = httpResource<UserDto[]>(() => '/api/users');
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected readonly form = this.fb.group({
    displayName: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(120)]],
    email: ['', [Validators.required, Validators.email]],
    role: this.fb.control<Role>('Agent'),
    password: ['', [Validators.required, Validators.minLength(10)]],
  });

  protected add(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set('Fill in every field. The password needs at least 10 characters.');
      return;
    }

    this.busy.set(true);
    this.error.set('');
    this.http.post<UserDto>('/api/users', this.form.getRawValue()).subscribe({
      next: (created) => {
        this.toast.show(`${created.displayName} was added as ${created.role}.`, 'success');
        this.form.reset({ displayName: '', email: '', role: 'Agent', password: '' });
        this.busy.set(false);
        this.users.reload();
      },
      error: (e: unknown) => {
        this.error.set(problemMessage(e));
        this.busy.set(false);
      },
    });
  }

  protected setActive(person: UserDto, isActive: boolean): void {
    this.http.put<UserDto>(`/api/users/${person.id}/active`, { isActive }).subscribe({
      next: () => this.users.reload(),
      error: (e: unknown) => this.toast.show(problemMessage(e), 'error'),
    });
  }
}
