import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Overview } from './overview';
import { RiskEntry, RiskReport } from '../../models/lens';

/**
 * The half that needs no model and no index. It was there all along and was
 * invisible, because everything went through the question box, which cannot
 * answer until hours of indexing have finished.
 */
describe('Overview', () => {
  let http: HttpTestingController;

  const engine: RiskEntry = {
    path: 'src/Billing/PriceEngine.cs',
    score: 92.5,
    complexity: 180,
    worstMethodComplexity: 42,
    worstMethod: 'Calculate',
    maxNesting: 7,
    codeLines: 1240,
    commits: 96,
    authors: 11,
    tested: false,
    reasons: ['Changes constantly and has no test', 'Deeply nested'],
  };

  const rules: RiskEntry = {
    ...engine,
    path: 'src/Billing/Rules.cs',
    score: 46.25,
    complexity: 60,
    tested: true,
    reasons: ['Large, but covered'],
  };

  const report: RiskReport = {
    history: { status: 'Read', note: null, window: 'the last 24 months' },
    generatedFilesExcluded: 3,
    ranked: 2,
    entries: [engine, rules],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Overview],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build(path = '/repos/billing', answer: RiskReport | null = report) {
    const fixture = TestBed.createComponent(Overview);
    fixture.componentRef.setInput('rootPath', path);
    fixture.detectChanges();

    if (path) {
      const request = http.expectOne((r) => r.url.endsWith('/risk'));
      if (answer) request.flush(answer);
      else request.flush({ error: 'No such directory' }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();
    }

    return fixture;
  }

  it('asks about the folder it was given', () => {
    const fixture = TestBed.createComponent(Overview);
    fixture.componentRef.setInput('rootPath', '/repos/billing');
    fixture.detectChanges();

    const request = http.expectOne((r) => r.url.endsWith('/risk'));
    expect(request.request.body.path).toBe('/repos/billing');
    request.flush(report);
  });

  it('asks nothing while a repository is still being fetched', () => {
    // A workspace made from a URL has no folder until the clone lands.
    build('', null);

    http.expectNone(() => true);
  });

  it('ranks the worst file at the full width of the bar', () => {
    // The scale is this codebase, not an absolute one. A percentage of some
    // universal maximum would be a number nobody can act on.
    const app = build().componentInstance;

    expect(app.width(engine)).toBe(100);
    expect(app.width(rules)).toBe(50);
  });

  it('says the bar and the number are not the same measure', () => {
    // A file with a cyclomatic complexity of 0 can still rank high, on churn
    // alone. Side by side with no explanation, that reads as a bug.
    const element = build().nativeElement as HTMLElement;

    expect(element.querySelector('.note')?.textContent).toContain('not the same as it');
  });

  it('marks an untested file, because that is what changes what you do next', () => {
    const element = build().nativeElement as HTMLElement;

    expect(element.querySelectorAll('.tag.untested').length).toBe(1);
  });

  it('says how many generated files were left out', () => {
    const element = build().nativeElement as HTMLElement;

    expect(element.textContent).toContain('3 generated files were left out');
  });

  it('counts one generated file in the singular', () => {
    const element = build('/repos/billing', {
      ...report, generatedFilesExcluded: 1,
    }).nativeElement as HTMLElement;

    expect(element.textContent).toContain('1 generated file was left out');
  });

  it('gives the reasons when a file is opened', () => {
    const fixture = build();
    fixture.componentInstance.toggle(engine);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Changes constantly and has no test');
    expect(text).toContain('Calculate');
  });

  it('opens one file at a time', () => {
    const app = build().componentInstance;

    app.toggle(engine);
    app.toggle(rules);

    expect(app.isOpen(engine)).toBe(false);
    expect(app.isOpen(rules)).toBe(true);
  });

  it('says which stretch of history the churn was counted over', () => {
    // Not always the one asked for: a repository that has stopped changing is
    // read whole, and two reports of the same codebase are only comparable
    // when the reader can see which was used.
    const element = build().nativeElement as HTMLElement;

    expect(element.textContent).toContain('over the last 24 months');
  });

  it('repeats what git could not answer rather than hiding it', () => {
    const element = build('/repos/billing', {
      ...report,
      history: { status: 'Missing', note: 'Not a git repository, so nothing was ranked on churn.', window: null },
    }).nativeElement as HTMLElement;

    expect(element.querySelector('.caveat')?.textContent).toContain('churn');
  });

  it('says why it could not read the folder', () => {
    const app = build('/nope', null).componentInstance;

    expect(app.failure()).toContain('No such directory');
  });

  it('says plainly when nothing is worth ranking', () => {
    const element = build('/repos/tiny', {
      ...report, ranked: 0, entries: [],
    }).nativeElement as HTMLElement;

    expect(element.textContent).toContain('short enough not to be a worry');
  });
});
