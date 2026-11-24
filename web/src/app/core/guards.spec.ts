import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { adminGuard, authGuard, guestGuard } from './guards';
import { Role } from './models';

function signInAs(role: Role | null): void {
  sessionStorage.clear();
  if (role) {
    sessionStorage.setItem(
      'supportdesk.session',
      JSON.stringify({
        token: 't',
        expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
        organization: 'Acme',
        user: { id: 'u1', email: 'a@b.test', displayName: 'A', role, isActive: true },
      }),
    );
  }

  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
}

const run = (guard: typeof authGuard) =>
  TestBed.runInInjectionContext(() =>
    guard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot),
  );

const redirectsTo = (result: unknown, path: string): boolean =>
  result instanceof UrlTree && result.toString() === path;

describe('route guards', () => {
  it('authGuard lets signed-in users through', () => {
    signInAs('Requester');
    expect(run(authGuard)).toBe(true);
  });

  it('authGuard sends anonymous users to the login page', () => {
    signInAs(null);
    expect(redirectsTo(run(authGuard), '/login')).toBe(true);
  });

  it('guestGuard keeps signed-in users away from the login page', () => {
    signInAs('Agent');
    expect(redirectsTo(run(guestGuard), '/tickets')).toBe(true);
  });

  it('guestGuard lets anonymous users reach the login page', () => {
    signInAs(null);
    expect(run(guestGuard)).toBe(true);
  });

  it('adminGuard admits admins', () => {
    signInAs('Admin');
    expect(run(adminGuard)).toBe(true);
  });

  it.each(['Agent', 'Requester'] as const)(
    'adminGuard turns %s users back to the ticket list',
    (role) => {
      signInAs(role);
      expect(redirectsTo(run(adminGuard), '/tickets')).toBe(true);
    },
  );
});
