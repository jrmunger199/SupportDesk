import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

function setup(signedIn: boolean) {
  sessionStorage.clear();
  if (signedIn) {
    sessionStorage.setItem(
      'supportdesk.session',
      JSON.stringify({
        token: 'abc.def.ghi',
        expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
        organization: 'Acme',
        user: { id: 'u1', email: 'a@b.test', displayName: 'A', role: 'Agent', isActive: true },
      }),
    );
  }

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      provideRouter([]),
    ],
  });

  const auth = TestBed.inject(AuthService);
  const logout = vi.spyOn(auth, 'logout').mockImplementation(() => undefined);
  return {
    http: TestBed.inject(HttpClient),
    controller: TestBed.inject(HttpTestingController),
    logout,
  };
}

describe('authInterceptor', () => {
  it('adds the bearer token to API calls', () => {
    const { http, controller } = setup(true);

    http.get('/api/tickets').subscribe();

    expect(controller.expectOne('/api/tickets').request.headers.get('Authorization')).toBe(
      'Bearer abc.def.ghi',
    );
  });

  it('does not leak the token to other origins', () => {
    const { http, controller } = setup(true);

    http.get('https://example.com/data').subscribe();

    expect(
      controller.expectOne('https://example.com/data').request.headers.has('Authorization'),
    ).toBe(false);
  });

  it('sends no Authorization header when signed out', () => {
    const { http, controller } = setup(false);

    http.get('/api/tickets').subscribe();

    expect(controller.expectOne('/api/tickets').request.headers.has('Authorization')).toBe(false);
  });

  it('signs the user out when the server rejects their token', () => {
    const { http, controller, logout } = setup(true);

    http.get('/api/tickets').subscribe({ error: () => undefined });
    controller.expectOne('/api/tickets').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(logout).toHaveBeenCalledOnce();
  });

  it('does not sign out on other errors', () => {
    const { http, controller, logout } = setup(true);

    http.get('/api/tickets').subscribe({ error: () => undefined });
    controller.expectOne('/api/tickets').flush({}, { status: 403, statusText: 'Forbidden' });

    expect(logout).not.toHaveBeenCalled();
  });

  it('does not sign out when a login attempt itself is rejected', () => {
    const { http, controller, logout } = setup(true);

    http.post('/api/auth/login', {}).subscribe({ error: () => undefined });
    controller.expectOne('/api/auth/login').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(logout).not.toHaveBeenCalled();
  });
});
