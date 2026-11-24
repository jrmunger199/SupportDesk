import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { problemMessage } from '../../core/problem';
import { DEMO_ACCOUNTS, DEMO_PASSWORD, DemoAccount } from './demo-accounts';

@Component({
  selector: 'app-login-page',
  imports: [ReactiveFormsModule],
  templateUrl: './login.page.html',
  styleUrl: './login.page.css',
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly mode = signal<'signin' | 'register'>('signin');
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly demoAccounts = DEMO_ACCOUNTS; // empty in production builds

  protected readonly signInForm = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

  protected readonly registerForm = this.fb.group({
    organizationName: [
      '',
      [Validators.required, Validators.minLength(2), Validators.maxLength(120)],
    ],
    adminName: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(120)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(10)]],
  });

  protected setMode(mode: 'signin' | 'register'): void {
    this.mode.set(mode);
    this.error.set('');
  }

  protected signIn(): void {
    if (this.signInForm.invalid) {
      this.signInForm.markAllAsTouched();
      return;
    }

    const { email, password } = this.signInForm.getRawValue();
    this.run(this.auth.login(email, password));
  }

  protected useDemo(account: DemoAccount): void {
    this.run(this.auth.login(account.email, DEMO_PASSWORD));
  }

  protected register(): void {
    if (this.registerForm.invalid) {
      this.registerForm.markAllAsTouched();
      return;
    }

    this.run(this.auth.register(this.registerForm.getRawValue()));
  }

  private run(request: ReturnType<AuthService['login']>): void {
    this.busy.set(true);
    this.error.set('');
    request.subscribe({
      next: () => void this.router.navigate(['/tickets']),
      error: (e: unknown) => {
        this.error.set(problemMessage(e));
        this.busy.set(false);
      },
    });
  }
}
