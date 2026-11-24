import { HttpErrorResponse } from '@angular/common/http';

interface ProblemDetails {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
}

/** Turns an API error (RFC 9457 problem details) into one sentence a person can act on. */
export function problemMessage(
  error: unknown,
  fallback = 'Something went wrong. Please try again.',
): string {
  if (!(error instanceof HttpErrorResponse)) {
    return fallback;
  }

  if (error.status === 0) {
    return 'Cannot reach the server. Check your connection and try again.';
  }

  if (error.status === 429) {
    return 'Too many attempts. Please wait a minute and try again.';
  }

  const problem = error.error as ProblemDetails | null;
  const firstValidationError = problem?.errors
    ? Object.values(problem.errors).flat()[0]
    : undefined;
  return firstValidationError ?? problem?.detail ?? fallback;
}

export const isConflict = (error: unknown): boolean =>
  error instanceof HttpErrorResponse && error.status === 409;
