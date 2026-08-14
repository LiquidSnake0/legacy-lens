import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, throwError } from 'rxjs';

import { AskResponse, Health, ApiError } from '../models/lens';

/**
 * Everything that talks to the API lives here.
 *
 * Components ask this service for data and never build a URL themselves, so
 * changing the backend address is a one-line edit and the components stay
 * testable without a network.
 */
@Injectable({ providedIn: 'root' })
export class LensService {
  private readonly http = inject(HttpClient);

  /** Where the API listens. Matches the CORS origin the backend allows. */
  private readonly baseUrl = 'http://localhost:8080/api';

  /** How many chunks the index holds, refreshed on demand. */
  readonly indexedChunks = signal<number | null>(null);

  ask(question: string, topK = 6): Observable<AskResponse> {
    return this.http
      .post<AskResponse>(`${this.baseUrl}/ask`, { question, topK })
      .pipe(catchError(this.explain));
  }

  health(): Observable<Health> {
    return this.http
      .get<Health>(`${this.baseUrl}/health`)
      .pipe(catchError(this.explain));
  }

  /**
   * Turns an HTTP failure into a message worth showing.
   *
   * The API already answers 503 with a hint when the model is unreachable, so
   * the useful text is in the body. A bare "Http failure response for
   * http://..." sends the reader looking in the wrong place.
   */
  private explain(response: HttpErrorResponse) {
    if (response.status === 0) {
      return throwError(() => new Error(
        'The API is not responding. Is it running on port 8080?'
      ));
    }

    const body = response.error as ApiError | null;
    const message = [body?.error, body?.hint].filter(Boolean).join(' ');

    return throwError(() => new Error(
      message || `The API answered ${response.status}.`
    ));
  }
}
