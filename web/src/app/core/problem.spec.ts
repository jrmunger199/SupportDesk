import { HttpErrorResponse } from '@angular/common/http';
import { isConflict, problemMessage } from './problem';

const fail = (status: number, error: unknown = null) => new HttpErrorResponse({ status, error });

describe('problemMessage', () => {
  it('uses the detail from problem details', () => {
    expect(problemMessage(fail(409, { title: 'Conflict', detail: 'Email already exists.' }))).toBe(
      'Email already exists.',
    );
  });

  it('prefers the first validation error', () => {
    const error = fail(400, {
      detail: 'ignored',
      errors: { Password: ['Use at least 10 characters.'], Email: ['Bad email.'] },
    });

    expect(problemMessage(error)).toBe('Use at least 10 characters.');
  });

  it('explains a network failure', () => {
    expect(problemMessage(fail(0))).toMatch(/cannot reach the server/i);
  });

  it('explains rate limiting', () => {
    expect(problemMessage(fail(429))).toMatch(/too many attempts/i);
  });

  it('falls back for unknown errors', () => {
    expect(problemMessage(new Error('boom'))).toMatch(/something went wrong/i);
    expect(problemMessage(fail(500), 'Custom fallback')).toBe('Custom fallback');
  });
});

describe('isConflict', () => {
  it('is true only for HTTP 409', () => {
    expect(isConflict(fail(409))).toBe(true);
    expect(isConflict(fail(400))).toBe(false);
    expect(isConflict(new Error('x'))).toBe(false);
  });
});
