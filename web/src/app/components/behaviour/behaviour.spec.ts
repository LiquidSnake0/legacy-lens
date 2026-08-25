import { TestBed } from '@angular/core/testing';

import { BehaviourPanel } from './behaviour';
import { Behaviour } from '../../models/lens';

/**
 * Whether the rewrite still does the same thing, on screen.
 *
 * The mistake this panel cannot make is looking like a pass when nothing was
 * compared. A file whose work happens through a web framework compares nothing
 * at all, which is the common case rather than the odd one, and a green line on
 * that is how a migration gets signed off and discovered in month four.
 */
describe('BehaviourPanel', () => {
  const compared: Behaviour = {
    ran: true,
    verified: true,
    claim: '2 method(s) over 22 call(s) returned the same thing in both versions.',
    cases: 22,
    moved: 0,
    methods: [
      {
        type: 'Invoice',
        method: 'WithTax',
        signature: 'WithTax(Int32)',
        cases: 11,
        matched: true,
        note: null,
        divergences: [],
      },
      {
        type: 'Invoice',
        method: 'Reference',
        signature: 'Reference(String)',
        cases: 11,
        matched: true,
        note: null,
        divergences: [],
      },
    ],
    refusals: [{ reason: 'NothingToObserve', count: 4, explanation: 'returns void, so only its side effects change anything' }],
    beforeErrors: [],
    afterErrors: [],
    elapsedMs: 140,
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [BehaviourPanel] }).compileComponents();
  });

  function build(report: Behaviour | null, refusal: string | null = null) {
    const fixture = TestBed.createComponent(BehaviourPanel);
    fixture.componentRef.setInput('report', report);
    fixture.componentRef.setInput('refusal', refusal);
    fixture.detectChanges();
    return fixture;
  }

  function text(report: Behaviour | null, refusal: string | null = null): string {
    return (build(report, refusal).nativeElement as HTMLElement).textContent ?? '';
  }

  it('says nothing moved when something was actually compared', () => {
    const element = build(compared).nativeElement as HTMLElement;

    expect(element.querySelector('.headline')?.textContent).toContain('Nothing moved');
    expect(element.querySelectorAll('.same li')).toHaveLength(2);
  });

  it('never says nothing moved when nothing was compared', () => {
    // The most important assertion in this file. Zero methods compared and zero
    // methods moved are the same numbers and opposite answers.
    const nothing: Behaviour = {
      ...compared,
      verified: false,
      methods: [],
      cases: 0,
      claim: 'Nothing was compared. 12 method(s) were passed over.',
    };

    const element = build(nothing).nativeElement as HTMLElement;

    expect(element.querySelector('.headline')?.textContent).toContain('Not checked');
    expect(element.querySelector('.headline')?.textContent).not.toContain('Nothing moved');
  });

  it('never says nothing moved when the original would not compile', () => {
    // The expected outcome on the files this tool exists for.
    const unbuilt: Behaviour = {
      ...compared,
      ran: false,
      verified: false,
      methods: [],
      cases: 0,
      claim: 'Nothing was checked: the original does not compile in this runtime.',
      beforeErrors: ["error CS0246: The type or namespace name 'Controller' could not be found"],
    };

    const element = build(unbuilt).nativeElement as HTMLElement;

    expect(element.querySelector('.headline')?.textContent).toContain('Not checked');
    expect(element.querySelector('.errors')?.textContent).toContain('CS0246');
  });

  it('shows a changed call with the values that produced it', () => {
    // A claim with no inputs is a claim the reader cannot check.
    const moved: Behaviour = {
      ...compared,
      verified: false,
      moved: 1,
      claim: '1 of 2 method(s) returned something different.',
      methods: [
        {
          ...compared.methods[0],
          matched: false,
          divergences: [{ arguments: '3', before: '10', after: '0' }],
        },
        compared.methods[1],
      ],
    };

    const element = build(moved).nativeElement as HTMLElement;

    expect(element.querySelector('.headline')?.textContent).toContain('Something moved');
    expect(element.querySelector('.calls-list .args')?.textContent).toContain('3');
    expect(element.querySelector('.calls-list .was')?.textContent).toContain('10');
    expect(element.querySelector('.calls-list .now')?.textContent).toContain('0');
  });

  it('keeps what did not move beside what did', () => {
    const moved: Behaviour = {
      ...compared,
      verified: false,
      moved: 1,
      methods: [
        { ...compared.methods[0], matched: false, divergences: [{ arguments: '3', before: '10', after: '0' }] },
        compared.methods[1],
      ],
    };

    const element = build(moved).nativeElement as HTMLElement;

    expect(element.querySelectorAll('.method')).toHaveLength(1);
    expect(element.querySelectorAll('.same li')).toHaveLength(1);
  });

  it('puts what was passed over beside the result rather than behind a toggle', () => {
    // Eleven methods matched says nothing until you know how many were never
    // called at all.
    expect(text(compared)).toContain('returns void');
    expect(text(compared)).toContain('4');
  });

  it('says a changed return type without calling it a difference', () => {
    const noted: Behaviour = {
      ...compared,
      methods: [{ ...compared.methods[0], note: 'The return type changed from Int32 to Int64.' }, compared.methods[1]],
    };

    expect(text(noted)).toContain('Int32 to Int64');
    expect(text(noted)).toContain('Nothing moved');
  });

  it('explains itself when the server was not allowed to run anything', () => {
    const element = build(null, 'This server does not run code it was handed.')
      .nativeElement as HTMLElement;

    expect(element.querySelector('.headline')?.textContent).toContain('Not checked');
    expect(element.querySelector('.claim')?.textContent).toContain('does not run code');
  });

  it('still says something when there is no report and no reason', () => {
    expect(text(null)).toContain('Behaviour was not checked');
  });
});
