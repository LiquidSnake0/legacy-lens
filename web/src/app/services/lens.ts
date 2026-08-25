import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, catchError, throwError } from 'rxjs';

import {
  AskResponse, Health, ApiError, Excerpt, Workspace, IngestionJob,
  ModelChoice, ModelOptions, RiskReport, ConversionKind, ConversionOutcome,
  SurfaceReport, Projection, Landed, DiagnoseReport, DiagnosisState, Behaviour,
} from '../models/lens';

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

  /**
   * Where the API is, relative to wherever this page came from.
   *
   * Same origin on purpose. Deployed, one nginx serves the page and passes
   * /api through to the API, so there is one address, one certificate, one
   * thing to put a password in front of, and no CORS at all. In development
   * `ng serve` proxies /api to localhost:8080 (see proxy.conf.json), so the
   * code is identical in both.
   */
  private readonly baseUrl = '/api';

  /** How many chunks the index holds, refreshed on demand. */
  readonly indexedChunks = signal<number | null>(null);

  ask(
    question: string,
    workspace: string,
    model: ModelChoice | null = null,
    topK = 6,
  ): Observable<AskResponse> {
    return this.http
      .post<AskResponse>(`${this.baseUrl}/ask`, { question, topK, workspace, model })
      .pipe(catchError(this.explain));
  }

  /* ---- projects ---- */

  workspaces(): Observable<Workspace[]> {
    return this.http
      .get<Workspace[]>(`${this.baseUrl}/workspaces`)
      .pipe(catchError(this.explain));
  }

  /**
   * Creates a project.
   *
   * A repository URL is fetched by the API rather than by the browser, and the
   * token goes with that one call. Nothing here keeps it: it is not put in
   * local storage, not held in a service field, and not sent again.
   */
  createWorkspace(body: {
    name: string;
    rootPath?: string;
    repositoryUrl?: string;
    token?: string;
  }): Observable<Workspace> {
    return this.http
      .post<Workspace>(`${this.baseUrl}/workspaces`, body)
      .pipe(catchError(this.explain));
  }

  deleteWorkspace(id: string): Observable<void> {
    return this.http
      .delete<void>(`${this.baseUrl}/workspaces/${id}`)
      .pipe(catchError(this.explain));
  }

  /* ---- indexing ---- */

  startIndexing(workspace: string, path: string): Observable<IngestionJob> {
    return this.http
      .post<IngestionJob>(`${this.baseUrl}/ingest/start`, { path, workspace })
      .pipe(catchError(this.explain));
  }

  /**
   * How far indexing has got, or null when that project has never been indexed.
   *
   * The API answers 204 for the second case, which arrives here as null rather
   * than as an error: a project nobody has indexed yet is an ordinary state.
   */
  indexingStatus(workspace: string): Observable<IngestionJob | null> {
    const query = new HttpParams().set('workspace', workspace);

    return this.http
      .get<IngestionJob | null>(`${this.baseUrl}/ingest/status`, { params: query })
      .pipe(catchError(this.explain));
  }

  cancelIndexing(workspace: string): Observable<void> {
    const query = new HttpParams().set('workspace', workspace);

    return this.http
      .post<void>(`${this.baseUrl}/ingest/cancel`, null, { params: query })
      .pipe(catchError(this.explain));
  }

  /* ---- the half that needs no model ---- */

  /**
   * The risk ranking for a directory.
   *
   * Seconds, no model, no index. It is what fills the screen while embedding
   * is still running, instead of a spinner.
   */
  risk(path: string, top = 12): Observable<RiskReport> {
    return this.http
      .post<RiskReport>(`${this.baseUrl}/risk`, { path, top })
      .pipe(catchError(this.explain));
  }

  /**
   * Proposes one mechanical conversion over a folder.
   *
   * One kind per call, because two of them rewrite the same project file and a
   * patch carrying both cannot apply.
   */
  convert(path: string, kind: ConversionKind): Observable<ConversionOutcome> {
    return this.http
      .post<ConversionOutcome>(`${this.baseUrl}/convert`, { path, kind })
      .pipe(catchError(this.explain));
  }

  /**
   * What the codebase uses of its dependencies, and what could replace them.
   *
   * One call rather than three: the surface, the candidates and their coverage
   * are one question, and splitting them would make this page responsible for
   * reassembling a thought. No model is involved.
   */
  surface(path: string, pkg?: string): Observable<SurfaceReport> {
    return this.http
      .post<SurfaceReport>(`${this.baseUrl}/surface`, { path, package: pkg ?? null })
      .pipe(catchError(this.explain));
  }

  /**
   * One file, rewritten and compiled before it is shown.
   *
   * Slow: it asks a model and then compiles, twice if the first answer invented
   * something.
   */
  project(path: string, pkg: string, root: string, model: ModelChoice | null = null) {
    return this.http
      .post<Projection>(`${this.baseUrl}/project`, { path, package: pkg, root, model })
      .pipe(catchError(this.explain));
  }

  /**
   * Puts a conversion on a branch of its own.
   *
   * The patch is not sent: the API regenerates it, because one that travelled
   * to a browser and back is one nobody can prove is the one that was read.
   */
  apply(path: string, kind: ConversionKind): Observable<Landed> {
    return this.http
      .post<Landed>(`${this.baseUrl}/apply`, { path, kind })
      .pipe(catchError(this.explain));
  }

  models(): Observable<ModelOptions> {
    return this.http
      .get<ModelOptions>(`${this.baseUrl}/models`)
      .pipe(catchError(this.explain));
  }

  /**
   * Streams an answer as server-sent events.
   *
   * fetch with a reader rather than EventSource: the latter only issues GET
   * requests, and the question belongs in a body rather than a query string.
   *
   * Citations arrive first, before a single token, because retrieval finishes
   * long before generation does. The reader sees which files are about to be
   * discussed while the model is still working.
   */
  async *stream(
    question: string,
    workspace: string,
    model: ModelChoice | null = null,
    topK = 6,
    signal?: AbortSignal,
  ) {
    const response = await fetch(`${this.baseUrl}/ask/stream`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ question, topK, workspace, model }),
      signal,
    });

    if (!response.ok || !response.body) {
      throw new Error(`The API answered ${response.status}.`);
    }

    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    let buffer = '';

    while (true) {
      const { value, done } = await reader.read();
      if (done) break;

      buffer += value;

      // Events are separated by a blank line, and one read can carry a
      // fraction of an event or several at once.
      let boundary: number;
      while ((boundary = buffer.indexOf('\n\n')) >= 0) {
        const raw = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);

        const name = raw.match(/^event: (.+)$/m)?.[1];
        const data = raw.match(/^data: (.+)$/m)?.[1];
        if (!name || !data) continue;

        yield { name, data: JSON.parse(data) as unknown };
      }
    }
  }

  /**
   * The indexed text behind a citation.
   *
   * The API serves it from the index rather than from disk, so what comes back
   * is what the model was given, not what the file says today.
   */
  excerpt(filePath: string, startLine: number, workspace: string): Observable<Excerpt> {
    const query = new HttpParams()
      .set('path', filePath)
      .set('line', startLine)
      .set('workspace', workspace);

    return this.http
      .get<Excerpt>(`${this.baseUrl}/excerpt`, { params: query })
      .pipe(catchError(this.explain));
  }

  /**
   * The decisions this codebase raises, with whatever has been said about them.
   *
   * Fast: it reads the source for the names that raise each dilemma and reads
   * the stored answers. Nothing is asked of a model here, and nothing is
   * decided without somebody answering.
   */
  diagnose(path: string, workspace: string): Observable<DiagnoseReport> {
    return this.http
      .post<DiagnoseReport>(`${this.baseUrl}/diagnose`, { path, workspace })
      .pipe(catchError(this.explain));
  }

  /** Records one answer and returns where the diagnosis now stands. */
  answerDilemma(
    dilemma: string, question: string, answer: string, workspace: string,
  ): Observable<DiagnosisState> {
    return this.http
      .post<DiagnosisState>(`${this.baseUrl}/diagnose/answer`,
        { dilemma, question, answer, workspace })
      .pipe(catchError(this.explain));
  }

  /**
   * Two files, called with the same values, to see whether one still does what
   * the other did.
   *
   * Paths rather than source. Everything else here reads from disk, and a call
   * that accepts a body of code and runs it is a different kind of thing
   * entirely. Refused with 403 unless the server was told it may run code, and
   * the refusal explains itself.
   */
  equivalence(before: string, after: string): Observable<{ behaviour: Behaviour }> {
    return this.http
      .post<{ behaviour: Behaviour }>(`${this.baseUrl}/equivalence`, { before, after })
      .pipe(catchError(this.explain));
  }

  /** Starts one dilemma over, leaving the others alone. */
  forgetDilemma(dilemma: string, workspace: string): Observable<DiagnosisState> {
    return this.http
      .post<DiagnosisState>(`${this.baseUrl}/diagnose/forget`, { dilemma, workspace })
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
