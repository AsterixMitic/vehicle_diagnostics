import { HttpErrorResponse } from '@angular/common/http';

import { problemMessage } from './problem';

describe('problemMessage', () => {
  it('shows the problem detail that names the parameter', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: {
        title: 'Invalid request',
        detail: "'from' is invalid: 'from' must be earlier than 'to'",
      },
    });

    expect(problemMessage(error)).toBe("'from' is invalid: 'from' must be earlier than 'to'");
  });

  it('falls back to the title', () => {
    expect(
      problemMessage(new HttpErrorResponse({ status: 500, error: { title: 'Server error' } })),
    ).toBe('Server error');
  });

  it('explains a network failure', () => {
    expect(problemMessage(new HttpErrorResponse({ status: 0 }))).toBe('Сервер није доступан.');
  });

  it('describes a response without a problem body', () => {
    expect(problemMessage(new HttpErrorResponse({ status: 502, statusText: 'Bad Gateway' }))).toBe(
      'Грешка 502 (Bad Gateway).',
    );
  });
});
