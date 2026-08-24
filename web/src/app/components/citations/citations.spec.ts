import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Citations } from './citations';
import { Citation } from '../../models/lens';

describe('Citations', () => {
  let http: HttpTestingController;

  const engine: Citation = {
    filePath: 'Billing/PriceEngine.cs',
    startLine: 84,
    endLine: 92,
    score: 0.81,
    foundBy: 'both',
  };

  const rules: Citation = {
    filePath: 'Billing/Rules.cs',
    startLine: 12,
    endLine: 30,
    score: 0.64,
    foundBy: 'vector',
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Citations],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build(sources: Citation[] = [engine, rules]) {
    const fixture = TestBed.createComponent(Citations);
    fixture.componentRef.setInput('sources', sources);
    // Excerpts are fetched per project: two of them can hold a file at the
    // same path with different code in it.
    fixture.componentRef.setInput('workspace', 'alpha');
    fixture.detectChanges();
    return fixture;
  }

  it('asks for the excerpt at the line the citation names', () => {
    const app = build().componentInstance;

    app.toggle(engine);

    const request = http.expectOne(
      (r) => r.url.endsWith('/excerpt') && r.params.get('line') === '84'
    );
    expect(request.request.params.get('path')).toBe('Billing/PriceEngine.cs');

    request.flush({ ...engine, content: 'public decimal Price()' });
    expect(app.excerpt()?.content).toBe('public decimal Price()');
    expect(app.loadingExcerpt()).toBe(false);
  });

  it('closes an excerpt that was already open', () => {
    const app = build().componentInstance;

    app.toggle(engine);
    http.expectOne((r) => r.url.endsWith('/excerpt')).flush({ ...engine, content: 'x' });

    app.toggle(engine);

    expect(app.isOpen(engine)).toBe(false);
    expect(app.excerpt()).toBeNull();
  });

  it('keeps only one excerpt open at a time', () => {
    const app = build().componentInstance;

    app.toggle(engine);
    http.expectOne((r) => r.params.get('line') === '84').flush({ ...engine, content: 'first' });

    app.toggle(rules);
    http.expectOne((r) => r.params.get('line') === '12').flush({ ...rules, content: 'second' });

    expect(app.isOpen(engine)).toBe(false);
    expect(app.excerpt()?.content).toBe('second');
  });

  it('ignores an excerpt that arrives after the reader moved on', () => {
    // Two clicks in quick succession. Without the guard the slower first
    // response overwrites the second, and the panel shows one citation's text
    // under another citation's heading.
    const app = build().componentInstance;

    app.toggle(engine);
    const first = http.expectOne((r) => r.params.get('line') === '84');

    app.toggle(rules);
    const second = http.expectOne((r) => r.params.get('line') === '12');

    second.flush({ ...rules, content: 'second' });
    first.flush({ ...engine, content: 'first' });

    expect(app.excerpt()?.content).toBe('second');
  });

  it('reports a citation the index cannot produce', () => {
    const app = build().componentInstance;

    app.toggle(engine);
    http.expectOne((r) => r.url.endsWith('/excerpt')).flush(
      { error: 'No indexed chunk at Billing/PriceEngine.cs:84.' },
      { status: 404, statusText: 'Not Found' }
    );

    expect(app.excerptError()).toContain('No indexed chunk');
    expect(app.loadingExcerpt()).toBe(false);
  });

  it('numbers the excerpt from the line the chunk starts at', () => {
    const app = build().componentInstance;

    expect(app.numbers({ ...engine, content: 'a\nb\nc' })).toEqual([84, 85, 86]);
  });

  it('shows no score for a chunk matched by term alone', () => {
    // A text-only hit carries a fused rank, not a cosine. Rendering it as a
    // similarity would invent a number.
    const app = build().componentInstance;

    expect(app.label({ ...engine, foundBy: 'text' })).toBe('exact');
    expect(app.label(engine)).toBe('0.81');
  });
});
