import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';

const PUBLIC_AUTH_URLS = ['/api/auth/login', '/api/auth/register'];

/** Attaches the bearer token to API calls and signs the user out if the server says the token is no longer valid. */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const token = auth.token();
  const isApiCall = request.url.startsWith('/api/');

  const outgoing =
    token && isApiCall
      ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
      : request;

  return next(outgoing).pipe(
    catchError((error: unknown) => {
      const expired = error instanceof HttpErrorResponse && error.status === 401;
      if (expired && token && !PUBLIC_AUTH_URLS.includes(request.url)) {
        auth.logout();
      }

      return throwError(() => error);
    }),
  );
};
