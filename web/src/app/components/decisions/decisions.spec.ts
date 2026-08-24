import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Decisions } from './decisions';
import { DiagnoseReport, DiagnosisState } from '../../models/lens';

/**
 * The questioner, in the browser.
 *
 * What this page has to get right is the difference between a wizard and a
 * chat window: every question names the line that raised it, every answer says
 * what it would rule out before it is clicked, and the asking stops when
 * nothing left can separate what remains. On screen those are three pieces of
 * markup, and without them the same data reads as a survey.
 */
describe('Decisions', () => {
  let http: HttpTestingController;

  const asking: DiagnosisState = {
    id: 'session-state',
    name: 'Where session state goes',
    what: 'This writes state into a per-machine store.',
    answers: [],
    remaining: [
      { id: 'distributed', name: 'Move it out of process', note: 'Redis or SQL Server.' },
      { id: 'sticky', name: 'Pin each visitor to a machine', note: 'Cheapest, and brittle.' },
      { id: 'stateless', name: 'Stop keeping it', note: 'The real fix, and the dearest.' },
    ],
    outcomes: 3,
    next: {
      id: 'machines',
      ask: 'How many machines serve this application?',
      why: 'A per-machine store is only a problem when there is more than one.',
      choices: [
        { answer: 'one', eliminates: ['distributed', 'stateless'], because: 'nothing to lose it to' },
        { answer: 'several', eliminates: ['sticky'], because: 'pinning breaks when one dies' },
      ],
    },
    settled: false,
    reasoning: [],
    landed: null,
    exhausted: false,
  };

  const report: DiagnoseReport = {
    catalogue: '/repo/data/dilemmas.json',
    workspace: 'alpha',
    dilemmas: [
      {
        diagnosis: asking,
        files: 12,
        mentions: 31,
        sites: [
          {
            path: '/repos/shop/Controllers/CartController.cs',
            line: 47,
            name: 'HttpContext',
            text: 'HttpContext.Current.Session["cart"] = cart;',
          },
        ],
      },
    ],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Decisions],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build(answer: DiagnoseReport | null = report, path = '/repos/shop') {
    const fixture = TestBed.createComponent(Decisions);
    fixture.componentRef.setInput('rootPath', path);
    fixture.componentRef.setInput('workspace', 'alpha');
    fixture.detectChanges();

    if (path) {
      const request = http.expectOne((r) => r.url.endsWith('/diagnose'));
      if (answer) request.flush(answer);
      else request.flush({ error: 'No such directory' }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();
    }

    return fixture;
  }

  it('asks about the folder and the project together', () => {
    const fixture = TestBed.createComponent(Decisions);
    fixture.componentRef.setInput('rootPath', '/repos/shop');
    fixture.componentRef.setInput('workspace', 'alpha');
    fixture.detectChanges();

    const request = http.expectOne((r) => r.url.endsWith('/diagnose'));
    expect(request.request.body).toEqual({ path: '/repos/shop', workspace: 'alpha' });
    request.flush(report);
  });

  it('asks nothing while a repository is still being fetched', () => {
    build(null, '');
    http.expectNone(() => true);
  });

  it('shows the line that raised the question, not just the question', () => {
    // The difference between a diagnosis and a questionnaire. Without this the
    // reader can tell nothing was read before they were asked.
    const element = build().nativeElement as HTMLElement;

    expect(element.querySelector('.sites .at')?.textContent).toContain('CartController.cs:47');
    expect(element.querySelector('.sites code')?.textContent).toContain('Session["cart"]');
  });

  it('says what an answer would rule out before it is clicked', () => {
    const element = build().nativeElement as HTMLElement;
    const costs = [...element.querySelectorAll('.choice .cost')].map(n => n.textContent ?? '');

    expect(costs[0]).toContain('Move it out of process');
    expect(costs[0]).toContain('Stop keeping it');
    expect(costs[1]).toContain('Pin each visitor to a machine');
  });

  it('says so plainly when an answer would rule out everything still standing', () => {
    const app = build().componentInstance;

    // The first choice rules out both of these, and saying "rules out A, B"
    // buries the fact that it leaves nothing behind.
    const standing = [asking.remaining[0], asking.remaining[2]];

    const cost = app.cost({ ...asking, remaining: standing }, asking.next!.choices[0]);

    expect(cost).toContain('everything still standing');
  });

  it('records an answer against the project it belongs to', () => {
    // Two projects behind two different load balancers give two different
    // answers, and mixing them describes neither.
    const fixture = build();
    const button = (fixture.nativeElement as HTMLElement)
      .querySelector('.choice') as HTMLButtonElement;

    button.click();

    const request = http.expectOne((r) => r.url.endsWith('/diagnose/answer'));
    expect(request.request.body).toEqual({
      dilemma: 'session-state',
      question: 'machines',
      answer: 'one',
      workspace: 'alpha',
    });
    request.flush({ ...asking, answers: [{ questionId: 'machines', answer: 'one' }] });
  });

  it('shows where it landed once nothing else can be ruled out', () => {
    const fixture = build();
    const landed: DiagnosisState = {
      ...asking,
      answers: [{ questionId: 'machines', answer: 'one' }],
      remaining: [asking.remaining[1]],
      next: null,
      settled: true,
      reasoning: ['You said: How many machines serve this application? one. nothing to lose it to'],
      landed: asking.remaining[1],
    };

    ((fixture.nativeElement as HTMLElement).querySelector('.choice') as HTMLButtonElement).click();
    http.expectOne((r) => r.url.endsWith('/diagnose/answer')).flush(landed);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('.outcome')?.textContent).toContain('Pin each visitor');
    expect(element.querySelector('.choices')).toBeNull();
    expect(element.querySelector('.reasoning')?.textContent).toContain('How many machines');
  });

  it('says every outcome was ruled out rather than showing an empty panel', () => {
    // Settled with nothing left is a real result, and an empty panel reads as
    // though the tool gave up.
    const fixture = build();
    const nothing: DiagnosisState = {
      ...asking,
      answers: [{ questionId: 'machines', answer: 'one' }],
      remaining: [],
      next: null,
      settled: true,
      landed: null,
      exhausted: true,
    };

    ((fixture.nativeElement as HTMLElement).querySelector('.choice') as HTMLButtonElement).click();
    http.expectOne((r) => r.url.endsWith('/diagnose/answer')).flush(nothing);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.outcome')?.textContent)
      .toContain('Nothing here fits');
  });

  it('says two outcomes are both live rather than picking one', () => {
    // Stopping with two standing is the honest answer when nothing left to ask
    // can separate them, and picking one anyway is the failure this avoids.
    const fixture = build();
    const undecided: DiagnosisState = {
      ...asking,
      answers: [{ questionId: 'machines', answer: 'several' }],
      remaining: asking.remaining.slice(0, 2),
      next: null,
      settled: true,
      landed: null,
    };

    ((fixture.nativeElement as HTMLElement).querySelector('.choice') as HTMLButtonElement).click();
    http.expectOne((r) => r.url.endsWith('/diagnose/answer')).flush(undecided);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('.outcome')?.textContent).toContain('still standing');
    expect(element.querySelectorAll('.standing li')).toHaveLength(2);
  });

  it('can start one over, because people find out they were wrong', () => {
    const fixture = build();
    const answered = { ...asking, answers: [{ questionId: 'machines', answer: 'one' }] };

    ((fixture.nativeElement as HTMLElement).querySelector('.choice') as HTMLButtonElement).click();
    http.expectOne((r) => r.url.endsWith('/diagnose/answer')).flush(answered);
    fixture.detectChanges();

    ((fixture.nativeElement as HTMLElement).querySelector('.restart') as HTMLButtonElement).click();

    const request = http.expectOne((r) => r.url.endsWith('/diagnose/forget'));
    expect(request.request.body).toEqual({ dilemma: 'session-state', workspace: 'alpha' });
    request.flush(asking);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.restart')).toBeNull();
  });

  it('keeps the lines it found when an answer comes back', () => {
    // The answer endpoint returns a diagnosis and not the sites, and dropping
    // them would empty the panel the moment somebody answered.
    const fixture = build();

    ((fixture.nativeElement as HTMLElement).querySelector('.choice') as HTMLButtonElement).click();
    http.expectOne((r) => r.url.endsWith('/diagnose/answer'))
      .flush({ ...asking, answers: [{ questionId: 'machines', answer: 'one' }] });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.sites .at')?.textContent)
      .toContain('CartController.cs:47');
  });

  it('says an empty catalogue is short rather than saying the code is clean', () => {
    const element = build({ ...report, dilemmas: [] }).nativeElement as HTMLElement;

    expect(element.querySelector('.pending')?.textContent).toContain('written by hand');
  });

  it('explains a failure instead of showing an empty list', () => {
    const element = build(null).nativeElement as HTMLElement;

    expect(element.querySelector('.failed')?.textContent).toContain('No such directory');
  });
});
