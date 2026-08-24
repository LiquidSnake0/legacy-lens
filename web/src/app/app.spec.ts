import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';

import { App } from './app';
import { LensService } from './services/lens';
import { Citation, ModelOptions, Workspace } from './models/lens';

/**
 * The answer stream goes through fetch, which Angular's testing backend cannot
 * intercept, so the service is replaced instead. Replacing it is also the
 * better test: what matters is how the component reacts to a sequence of
 * events, not how the bytes arrive.
 */
describe('App', () => {
  let http: HttpTestingController;
  let events: { name: string; data: unknown }[];
  let asked: { workspace: string; model: unknown } | null;
  let started: { workspace: string; path: string }[];

  const billing: Workspace = {
    id: 'w-billing',
    name: 'Billing',
    rootPath: '/repos/billing',
    createdAt: '2026-08-01T00:00:00+00:00',
    chunks: 58,
  };

  const models: ModelOptions = {
    local: { model: 'qwen2.5-coder:3b', description: 'Runs here.' },
    hosted: {
      available: true,
      url: 'https://api.example/v1',
      model: 'gpt-4o-mini',
      description: 'Your own key.',
      warning: 'The excerpts retrieved do leave this machine.',
    },
    embeddings: 'Always local.',
  };

  const citation: Citation = {
    filePath: 'Billing/PriceEngine.cs',
    startLine: 84,
    endLine: 131,
    score: 0.81,
    foundBy: 'both',
  };

  function fake(overrides: Partial<Record<string, unknown>> = {}) {
    return {
      workspaces: () => of([billing]),
      models: () => of(models),
      indexingStatus: () => of(null),
      risk: () => of({ history: { status: 'Read', note: null }, generatedFilesExcluded: 0, ranked: 0, entries: [] }),
      surface: () => of({ catalogue: 'none', packages: [] }),
      diagnose: () => of({ catalogue: 'none', workspace: 'w-billing', dilemmas: [] }),
      startIndexing: (workspace: string, path: string) => {
        started.push({ workspace, path });
        return of({});
      },
      stream: async function* (question: string, workspace: string, model: unknown) {
        asked = { workspace, model };
        for (const event of events) yield event;
      },
      ...overrides,
    };
  }

  async function configure(service: unknown) {
    await TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: LensService, useValue: service },
      ],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  }

  beforeEach(async () => {
    events = [];
    asked = null;
    started = [];
    localStorage.clear();

    await configure(fake());
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

  it('shows how much of the current project is indexed', () => {
    const element = build().nativeElement as HTMLElement;

    const status = element.querySelector('.status')?.textContent ?? '';
    expect(status).toContain('58');
    expect(status).toContain('Billing');
  });

  it('gives one number for how much is indexed, not two', () => {
    // The header count is from the last time the list loaded; the panel below
    // reports what the current run has written since. On screen together they
    // disagree, so while a run is going the panel is the one that speaks.
    const fixture = build();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('.status')?.textContent).toContain('58');

    fixture.componentInstance.onBusy(true);
    fixture.detectChanges();

    expect(element.querySelector('.status')).toBeNull();
  });

  it('selects a project on its own, so the first visit has something to ask about', () => {
    expect(build().componentInstance.store.currentId()).toBe('w-billing');
  });

  it('rejects a question too short to retrieve anything', async () => {
    const app = build().componentInstance;

    app.form.setValue({ question: 'why?' });
    await app.submit();

    expect(app.questionControl.hasError('minlength')).toBe(true);
    expect(app.answer()).toBeNull();
  });

  it('asks about the project that is selected', async () => {
    events = [{ name: 'sources', data: [citation] }];

    const app = build().componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(asked?.workspace).toBe('w-billing');
  });

  it('asks the local model unless told otherwise', async () => {
    // The front page says no source code leaves the machine. Anything else has
    // to be a decision somebody made, not a default they never saw.
    events = [{ name: 'sources', data: [citation] }];

    const app = build().componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(asked?.model).toEqual({ provider: 'local' });
  });

  it('sends the chosen model with the question', async () => {
    events = [{ name: 'sources', data: [citation] }];

    const app = build().componentInstance;
    app.onModel({ provider: 'hosted', model: 'gpt-4o', apiKey: 'sk-test' });
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(asked?.model).toEqual({ provider: 'hosted', model: 'gpt-4o', apiKey: 'sk-test' });
  });

  it('will not ask anything while no project is selected', async () => {
    await configure(fake({ workspaces: () => of([]) }));

    const app = build().componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(app.answer()).toBeNull();
    expect(asked).toBeNull();
  });

  it('offers the form and nothing else when there is no project yet', async () => {
    await configure(fake({ workspaces: () => of([]) }));

    const element = build().nativeElement as HTMLElement;

    expect(element.querySelector('.onboarding')).toBeTruthy();
    expect(element.querySelector('textarea#question')).toBeNull();
  });

  it('starts indexing a folder that was just added', () => {
    const app = build().componentInstance;

    app.onAdded({ ...billing, id: 'w-new', rootPath: '/repos/new' });

    expect(started).toEqual([{ workspace: 'w-new', path: '/repos/new' }]);
  });

  it('leaves a repository alone, because the API is already fetching it', () => {
    // A workspace made from a URL has no path until the clone lands, and the
    // job that clones it goes on to index it.
    const app = build().componentInstance;

    app.onAdded({ ...billing, id: 'w-remote', rootPath: '' });

    expect(started).toEqual([]);
  });

  it('promises that nothing leaves the machine only while that is true', () => {
    const fixture = build();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('footer')?.textContent).toContain('No source code leaves it');

    fixture.componentInstance.onModel({ provider: 'hosted', apiKey: 'sk-test' });
    fixture.detectChanges();

    const footer = element.querySelector('footer')?.textContent ?? '';
    expect(footer).not.toContain('No source code leaves it');
    expect(footer).toContain('are sent to the hosted model');
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
    await configure(
      fake({
        stream: async function* () {
          throw new Error('The API answered 503.');
          yield { name: 'never', data: null };
        },
      }),
    );

    const app = build().componentInstance;
    app.form.setValue({ question: 'Where is pricing calculated?' });
    await app.submit();

    expect(app.error()).toContain('503');
    expect(app.loading()).toBe(false);
  });
});
