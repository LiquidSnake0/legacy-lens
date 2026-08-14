import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of, throwError } from 'rxjs';

import { App } from './app';
import { LensService } from './services/lens';
import { Citation } from './models/lens';

/**
 * The health check still goes through HttpClient and is tested with Angular's
 * testing backend. The answer stream goes through fetch, which that backend
 * cannot intercept, so the service is replaced instead. Replacing it is also
 * the better test: what matters is how the component reacts to a sequence of
 * events, not how the bytes arrive.
 */
describe('App', () => {
  let http: HttpTestingController;
  let events: { name: string; data: unknown }[];

  const fake = {
    health: () => of({ status: 'ok', indexedChunks: 58 }),
    stream: async function* () {
      for (const event of events) yield event;
    },
  };

  const citation: Citation = {
    filePath: 'Billing/PriceEngine.cs',
    startLine: 84,
    endLine: 131,
    score: 0.81,
    foundBy: 'both',
  };

  beforeEach(async () => {
    events = [];

    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: LensService, useValue: fake },
      ],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build() {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return fixture;
  }

  it('creates the app', () => {
    expect(build().componentInstance).toBeTruthy();
  });

  it('shows the index size once the health check answers', () => {
    const element = build().nativeElement as HTMLElement;
    expect(element.querySelector('.status')?.textContent).toContain('58');
  });

  it('rejects a question too short to retrieve anything', async () => {
    const app = build().componentInstance;

    app.form.setValue({ question: 'why?' });
    await app.submit();

    expect(app.questionControl.hasError('minlength')).toBe(true);
    expect(app.answer()).toBeNull();
  });

  it('shows the citations before any of the answer text', async () => {
    // Retrieval finishes long before generation does. Holding the sources back
    // until the end would waste the only part of the wait that is informative.
    events = [{ name: 'sources', data: [citation] }];

    const app = build().componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(app.sources().length).toBe(1);
    expect(app.answer()).toBe('');
  });

  it('assembles the answer from the tokens as they arrive', async () => {
    events = [
      { name: 'sources', data: [citation] },
      { name: 'token', data: 'Pricing is computed ' },
      { name: 'token', data: 'in PriceEngine.' },
      { name: 'done', data: {} },
    ];

    const app = build().componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(app.answer()).toBe('Pricing is computed in PriceEngine.');
    expect(app.loading()).toBe(false);
  });

  it('surfaces a failure that arrives mid-stream', async () => {
    // Ollama can die halfway through an answer. The message the API wrote is
    // what the reader needs, not 'stream closed'.
    events = [
      { name: 'sources', data: [citation] },
      { name: 'token', data: 'Pricing is ' },
      { name: 'failed', data: 'Could not reach the model at http://localhost:11434.' },
    ];

    const app = build().componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(app.error()).toContain('Could not reach the model');
    expect(app.loading()).toBe(false);
  });

  it('reports a stream that never opens', async () => {
    const failing = {
      ...fake,
      stream: async function* () {
        throw new Error('The API answered 503.');
        yield { name: 'never', data: null };
      },
    };

    await TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: LensService, useValue: failing },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const app = fixture.componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(app.error()).toContain('503');
    expect(app.loading()).toBe(false);
  });
});
