import { HttpErrorResponse } from '@angular/common/http';

/**
 * The message shown for a failed request: the backend's ProblemDetails `detail` (which names
 * the refused parameter), else its `title`, else a message for the transport failure.
 */
export function problemMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return 'Непозната грешка.';
  }
  if (error.status === 0) {
    return 'Сервер није доступан.';
  }
  const body: unknown = error.error;
  if (isProblem(body)) {
    return body.detail ?? body.title ?? `Грешка ${error.status}.`;
  }
  return `Грешка ${error.status}${error.statusText ? ` (${error.statusText})` : ''}.`;
}

interface Problem {
  readonly title?: string;
  readonly detail?: string;
}

function isProblem(body: unknown): body is Problem {
  return (
    typeof body === 'object' &&
    body !== null &&
    (typeof (body as Problem).detail === 'string' || typeof (body as Problem).title === 'string')
  );
}
