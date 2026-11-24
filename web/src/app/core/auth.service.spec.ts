import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService } from './auth.service';
import { AuthResponse, Role } from './models';

function session(role: Role = 'Agent', expiresInMs = 3_600_000): AuthResponse {
  return {
    token: 'token-123',
    expiresUtc: new Date(Date.now() + expiresInMs).toISOString(),
    organization: 'Acme Corp',
    user: { id: 'u1', email: 'priya@acme.test', displayName: 'Priya Nair', role, isActive: true },
  };
}

function create() {
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
  return { auth: TestBed.inject(AuthService), http: TestBed.inject(HttpTestingController) };
}

describe('AuthService', () => {
  beforeEach(() => sessionStorage.clear());

  it('starts signed out when nothing is stored', () => {
    const { auth } = create();

    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.token()).toBeNull();
    expect(auth.user()).toBeNull();
  });

  it('stores the session after a successful login and exposes it through signals', () => {
    const { auth, http } = create();

    auth.login('priya@acme.test', 'secret').subscribe();
    const request = http.expectOne('/api/auth/login');
    expect(request.request.body).toEqual({ email: 'priya@acme.test', password: 'secret' });
    request.flush(session('Agent'));

    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.user()?.displayName).toBe('Priya Nair');
    expect(auth.organization()).toBe('Acme Corp');
    expect(auth.isStaff()).toBe(true);
    expect(auth.isAdmin()).toBe(false);
    expect(sessionStorage.getItem('supportdesk.session')).toContain('token-123');
  });

  it.each([
    ['Requester', false, false],
    ['Agent', true, false],
    ['Admin', true, true],
  ] as const)('derives permissions for %s', (role, staff, admin) => {
    sessionStorage.setItem('supportdesk.session', JSON.stringify(session(role)));
    const { auth } = create();

    expect(auth.isStaff()).toBe(staff);
    expect(auth.isAdmin()).toBe(admin);
  });

  it('restores a stored session that has not expired', () => {
    sessionStorage.setItem('supportdesk.session', JSON.stringify(session()));
    const { auth } = create();

    expect(auth.isAuthenticated()).toBe(true);
  });

  it('ignores a stored session that has expired', () => {
    sessionStorage.setItem('supportdesk.session', JSON.stringify(session('Agent', -1000)));
    const { auth } = create();

    expect(auth.isAuthenticated()).toBe(false);
  });

  it('ignores corrupt stored data', () => {
    sessionStorage.setItem('supportdesk.session', '{not json');
    const { auth } = create();

    expect(auth.isAuthenticated()).toBe(false);
  });

  it('clears the session and goes to the login page on logout', async () => {
    sessionStorage.setItem('supportdesk.session', JSON.stringify(session()));
    const { auth } = create();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    auth.logout();

    expect(auth.isAuthenticated()).toBe(false);
    expect(sessionStorage.getItem('supportdesk.session')).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });

  it('revalidates a stored session at startup and refreshes it', () => {
    sessionStorage.setItem('supportdesk.session', JSON.stringify(session()));
    const { auth, http } = create();

    auth.restore().subscribe();
    http.expectOne('/api/auth/me').flush({ ...session(), token: 'fresh-token' });

    expect(auth.token()).toBe('fresh-token');
  });

  it('signs out at startup when the server rejects the stored session', () => {
    sessionStorage.setItem('supportdesk.session', JSON.stringify(session()));
    const { auth, http } = create();

    auth.restore().subscribe();
    http.expectOne('/api/auth/me').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isAuthenticated()).toBe(false);
  });

  it('does not call the server at startup when there is no stored session', () => {
    const { auth, http } = create();

    auth.restore().subscribe();

    http.expectNone('/api/auth/me');
  });
});
