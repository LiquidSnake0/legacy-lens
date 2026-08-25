import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Rewrite } from './rewrite';
import { Behaviour } from '../../models/lens';

/**
 * Checking a rewrite somebody wrote by hand.
 *
 * The case the projection cannot reach: under one, the original imports a dead
 * framework and does not compile here, so the answer is almost always that
 * nothing could be checked. A service rewritten by hand still runs, and until
 * this panel that case had no way in but a terminal.
 */
describe('Rewrite', () => {
  let http: HttpTestingController;

  const nothingMoved: Behaviour = {
    ran: true,
    verified: true,
    claim: '4 method(s) over 41 call(s) returned the same thing in both versions.',
    cases: 41,
    moved: 0,
    methods: [
      {
        type: 'Pricing',
        method: 'WithTax',
        signature: 'WithTax(Int32)',
        cases: 11,
        matched: true,
        note: null,
        divergences: [],
      },
    ],
    refusals: [],
    beforeErrors: [],
    afterErrors: [],
    elapsedMs: 120,
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Rewrite],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build() {
    const fixture = TestBed.createComponent(Rewrite);
    fixture.detectChanges();
    return fixture;
  }

  function fill(fixture: ReturnType<typeof build>, before: string, after: string) {
    fixture.componentInstance.form.setValue({ before, after });
    fixture.detectChanges();
  }

  it('asks for nothing until it has both paths', () => {
    const fixture = build();

    fixture.componentInstance.compare();
    http.expectNone(() => true);

    fill(fixture, '/repos/app/A.cs', '');
    fixture.componentInstance.compare();
    http.expectNone(() => true);
  });

  it('sends the two paths and nothing else', () => {
    // Paths rather than source. A box that accepts pasted code and runs it is
    // a different kind of thing entirely.
    const fixture = build();
    fill(fixture, ' /repos/app/A.cs ', '/repos/modern/A.cs');

    fixture.componentInstance.compare();

    const request = http.expectOne((r) => r.url.endsWith('/equivalence'));
    expect(request.request.body).toEqual({
      before: '/repos/app/A.cs',
      after: '/repos/modern/A.cs',
    });
    request.flush({ behaviour: nothingMoved });
  });

  it('shows the verdict once it has one', () => {
    const fixture = build();
    fill(fixture, '/a.cs', '/b.cs');

    fixture.componentInstance.compare();
    http.expectOne((r) => r.url.endsWith('/equivalence')).flush({ behaviour: nothingMoved });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('lens-behaviour .headline')?.textContent).toContain('Nothing moved');
    expect(element.querySelector('lens-behaviour .claim')?.textContent).toContain('41 call(s)');
  });

  it('shows nothing at all before it has been asked', () => {
    // An empty panel under an empty form reads as a verdict on nothing.
    expect((build().nativeElement as HTMLElement).querySelector('lens-behaviour')).toBeNull();
  });

  it('treats a server that will not run code as an answer rather than a failure', () => {
    // The default on any server. The refusal explains itself, and it belongs
    // beside a verdict rather than in a box away from it.
    const fixture = build();
    fill(fixture, '/a.cs', '/b.cs');

    fixture.componentInstance.compare();
    http.expectOne((r) => r.url.endsWith('/equivalence')).flush(
      { error: 'This server does not run code it was handed.' },
      { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('lens-behaviour .headline')?.textContent).toContain('Not checked');
    expect(element.querySelector('lens-behaviour .claim')?.textContent).toContain('does not run code');
  });

  it('says a path that is not there rather than staying silent', () => {
    const fixture = build();
    fill(fixture, '/nope.cs', '/b.cs');

    fixture.componentInstance.compare();
    http.expectOne((r) => r.url.endsWith('/equivalence')).flush(
      { error: 'No such file: /nope.cs.' },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('lens-behaviour .claim')?.textContent)
      .toContain('No such file');
  });

  it('will not ask twice while the first answer is still coming', () => {
    // Both calls run the code. Two of them at once is twice the work for one
    // answer, and the second would land on a panel showing the first.
    const fixture = build();
    fill(fixture, '/a.cs', '/b.cs');

    fixture.componentInstance.compare();
    fixture.componentInstance.compare();

    http.expectOne((r) => r.url.endsWith('/equivalence')).flush({ behaviour: nothingMoved });
  });
});
