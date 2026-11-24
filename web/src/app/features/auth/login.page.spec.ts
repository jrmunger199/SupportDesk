import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { LoginPage } from './login.page';

describe('LoginPage', () => {
  let fixture: ComponentFixture<LoginPage>;
  let http: HttpTestingController;
  let navigate: ReturnType<typeof vi.spyOn>;
  let root: HTMLElement;

  beforeEach(async () => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    http = TestBed.inject(HttpTestingController);
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    await fixture.whenStable();
    root = fixture.nativeElement as HTMLElement;
  });

  const type = (selector: string, value: string) => {
    const input = root.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };

  const submit = () => {
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  };

  it('starts on the sign-in tab', () => {
    expect(root.querySelector('#si-email')).not.toBeNull();
    expect(root.querySelector('#rg-org')).toBeNull();
  });

  it('does not call the server when the form is invalid, and explains why', () => {
    submit();

    http.expectNone('/api/auth/login');
    expect(root.textContent).toContain('Enter a valid email address.');
    expect(root.textContent).toContain('Enter your password.');
  });

  it('signs in and goes to the ticket list', () => {
    type('#si-email', 'priya@acme.test');
    type('#si-password', 'secret');
    submit();

    const request = http.expectOne('/api/auth/login');
    expect(request.request.body).toEqual({ email: 'priya@acme.test', password: 'secret' });
    request.flush({
      token: 't',
      expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
      organization: 'Acme',
      user: {
        id: 'u1',
        email: 'priya@acme.test',
        displayName: 'Priya',
        role: 'Agent',
        isActive: true,
      },
    });

    expect(navigate).toHaveBeenCalledWith(['/tickets']);
  });

  it('shows the server message when credentials are wrong', () => {
    type('#si-email', 'priya@acme.test');
    type('#si-password', 'wrong');
    submit();

    http
      .expectOne('/api/auth/login')
      .flush(
        { detail: 'The email or password is incorrect.' },
        { status: 401, statusText: 'Unauthorized' },
      );
    fixture.detectChanges();

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      'The email or password is incorrect.',
    );
    expect(navigate).not.toHaveBeenCalled();
  });

  it('switches to registration and enforces the password length', () => {
    (root.querySelector('#tab-register') as HTMLButtonElement).click();
    fixture.detectChanges();

    type('#rg-org', 'Initech');
    type('#rg-name', 'Peter Gibbons');
    type('#rg-email', 'peter@initech.test');
    type('#rg-password', 'short');
    submit();

    http.expectNone('/api/auth/register');
    expect(root.textContent).toContain('Use at least 10 characters.');
  });

  it('registers a new organisation', () => {
    (root.querySelector('#tab-register') as HTMLButtonElement).click();
    fixture.detectChanges();

    type('#rg-org', 'Initech');
    type('#rg-name', 'Peter Gibbons');
    type('#rg-email', 'peter@initech.test');
    type('#rg-password', 'CorrectHorse!123');
    submit();

    const request = http.expectOne('/api/auth/register');
    expect(request.request.body).toEqual({
      organizationName: 'Initech',
      adminName: 'Peter Gibbons',
      email: 'peter@initech.test',
      password: 'CorrectHorse!123',
    });
  });
});
