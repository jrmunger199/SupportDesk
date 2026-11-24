import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, map, of, tap } from 'rxjs';
import { AuthResponse } from './models';

const STORAGE_KEY = 'supportdesk.session';

export interface RegisterRequest {
  organizationName: string;
  adminName: string;
  email: string;
  password: string;
}

/**
 * Holds the signed-in session as a signal so templates and guards react to it directly.
 * The token lives in sessionStorage: it survives a reload but not a closed tab, which limits how long a stolen
 * token is useful. (HttpOnly cookies would be stronger still; see the README's security notes.)
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly state = signal<AuthResponse | null>(readStoredSession());

  readonly user = computed(() => this.state()?.user ?? null);
  readonly organization = computed(() => this.state()?.organization ?? '');
  readonly token = computed(() => this.state()?.token ?? null);
  readonly isAuthenticated = computed(() => this.state() !== null);
  readonly isStaff = computed(() => this.user()?.role === 'Agent' || this.user()?.role === 'Admin');
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');

  login(email: string, password: string): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>('/api/auth/login', { email, password })
      .pipe(tap((r) => this.store(r)));
  }

  register(request: RegisterRequest): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>('/api/auth/register', request)
      .pipe(tap((r) => this.store(r)));
  }

  /** Revalidates a stored session with the server at startup; a rejected or expired token signs the user out. */
  restore(): Observable<void> {
    if (!this.state()) {
      return of(undefined);
    }

    return this.http.get<AuthResponse>('/api/auth/me').pipe(
      tap((r) => this.store(r)),
      map(() => undefined),
      catchError(() => {
        this.clear();
        return of(undefined);
      }),
    );
  }

  logout(): void {
    this.clear();
    void this.router.navigate(['/login']);
  }

  private store(response: AuthResponse): void {
    this.state.set(response);
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(response));
    } catch {
      // Storage can be unavailable (private mode, quota): the session then simply lasts until reload.
    }
  }

  private clear(): void {
    this.state.set(null);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // see store()
    }
  }
}

function readStoredSession(): AuthResponse | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const session = JSON.parse(raw) as AuthResponse;
    return new Date(session.expiresUtc).getTime() > Date.now() ? session : null;
  } catch {
    return null;
  }
}
