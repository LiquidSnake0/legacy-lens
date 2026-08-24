import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';

import { Indexing } from './indexing';
import { IngestionJob } from '../../models/lens';

/**
 * A run that reports nothing is indistinguishable from a hung one, and this is
 * watched for hours. What matters is that it keeps asking while work is going
 * on, stops when it is over, and does not let a stale answer for one project
 * overwrite the one now on screen.
 */
describe('Indexing', () => {
  let http: HttpTestingController;

  const running: IngestionJob = {
    workspace: 'w-billing',
    rootPath: '/repos/billing',
    state: 'running',
    filesTotal: 40,
    filesDone: 10,
    chunksIndexed: 120,
    currentFile: 'src/PriceEngine.cs',
    startedAt: '2026-08-24T10:00:00+00:00',
    finishedAt: null,
    error: null,
    running: true,
    estimatedSecondsLeft: 180,
  };

  beforeEach(async () => {
    vi.useFakeTimers();

    await TestBed.configureTestingModule({
      imports: [Indexing],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  function build(workspace = 'w-billing') {
    const fixture = TestBed.createComponent(Indexing);
    fixture.componentRef.setInput('workspace', workspace);
    fixture.detectChanges();
    return fixture;
  }

  function answer(job: IngestionJob | null) {
    http.expectOne((r) => r.url.endsWith('/ingest/status')).flush(job, {
      status: job ? 200 : 204,
      statusText: job ? 'OK' : 'No Content',
    });
  }

  it('shows nothing for a project that has never been indexed', () => {
    const fixture = build();
    answer(null);
    fixture.detectChanges();

    expect(fixture.componentInstance.job()).toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('.indexing')).toBeNull();
  });

  it('keeps asking while the run is going', () => {
    build();
    answer(running);

    vi.advanceTimersByTime(2000);
    answer(running);

    vi.advanceTimersByTime(2000);
    answer({ ...running, state: 'done', running: false, filesDone: 40 });

    // Finished: nothing further is scheduled, and http.verify in afterEach is
    // what proves it.
    vi.advanceTimersByTime(10_000);
  });

  it('reports progress against the files that need work', () => {
    const fixture = build();
    answer(running);
    fixture.detectChanges();

    expect(fixture.componentInstance.percent()).toBe(25);
  });

  it('says a run finished so the chunk counts can be refreshed', () => {
    const fixture = build();
    const finished: IngestionJob[] = [];
    fixture.componentInstance.finished.subscribe((job) => finished.push(job));

    answer(running);
    vi.advanceTimersByTime(2000);
    answer({ ...running, state: 'done', running: false, filesDone: 40 });

    expect(finished.length).toBe(1);
    expect(finished[0].state).toBe('done');
  });

  it('does not announce a run it never saw going', () => {
    // Arriving at a project whose indexing finished yesterday is not an event.
    const fixture = build();
    const finished: IngestionJob[] = [];
    fixture.componentInstance.finished.subscribe((job) => finished.push(job));

    answer({ ...running, state: 'done', running: false, filesDone: 40 });

    expect(finished).toEqual([]);
  });

  it('does not repeat a run that finished before anyone arrived', () => {
    // Its summary says what the picker already says, and a run from three days
    // ago is not news.
    const fixture = build();
    answer({ ...running, state: 'done', running: false, filesDone: 40 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.indexing')).toBeNull();
  });

  it('confirms a run that finished while somebody was watching', () => {
    const fixture = build();
    answer(running);
    vi.advanceTimersByTime(2000);
    answer({ ...running, state: 'done', running: false, filesDone: 40 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Indexed');
  });

  it('reports a run that died before anyone arrived', () => {
    // The opposite call from a finished one: arriving at a project whose
    // indexing failed is exactly when you need to be told.
    const fixture = build();
    answer({ ...running, state: 'failed', running: false, error: 'Ollama is not running.' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Indexing failed');
  });

  it('switches to the project it is given', () => {
    const fixture = build();
    answer(running);

    fixture.componentRef.setInput('workspace', 'w-other');
    fixture.detectChanges();

    // The old one's timer is dropped rather than left polling a project
    // nobody is looking at.
    const request = http.expectOne((r) => r.url.endsWith('/ingest/status'));
    expect(request.request.params.get('workspace')).toBe('w-other');
    request.flush(null, { status: 204, statusText: 'No Content' });

    vi.advanceTimersByTime(10_000);
  });

  it('shows why a run failed', () => {
    const fixture = build();
    answer({
      ...running,
      state: 'failed',
      running: false,
      error: 'Could not reach the embedding model. Is Ollama running?',
    });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Ollama');
  });

  it('says that what was already embedded is kept when a run is stopped', () => {
    const fixture = build();
    answer({ ...running, state: 'cancelled', running: false });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('kept');
  });

  it('asks the API to stop, then looks again', () => {
    const fixture = build();
    answer(running);

    fixture.componentInstance.cancel();

    http.expectOne((r) => r.method === 'POST' && r.url.endsWith('/ingest/cancel'))
      .flush(null, { status: 204, statusText: 'No Content' });

    answer({ ...running, state: 'cancelled', running: false });
    vi.advanceTimersByTime(10_000);
  });

  it('counts in minutes and hours rather than in seconds', () => {
    const app = build().componentInstance;
    answer(null);

    expect(app.left(45)).toBe('45s');
    expect(app.left(240)).toBe('4 min');
    expect(app.left(7200)).toBe('2h');
    expect(app.left(5400)).toBe('1h 30m');
  });
});
