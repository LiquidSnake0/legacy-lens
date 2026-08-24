import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Conversions } from './conversions';
import { ConversionOutcome } from '../../models/lens';

/**
 * A patch to read, never a patch to apply.
 *
 * The rule for this part of the tool is that it proposes a diff and a person
 * approves it, so the tests that matter are the ones asserting that nothing
 * here writes anything and that the refusals are not tucked away.
 */
describe('Conversions', () => {
  let http: HttpTestingController;

  const sdk: ConversionOutcome = {
    kind: 'sdk',
    patch: [
      'diff --git a/A/A.csproj b/A/A.csproj',
      '--- a/A/A.csproj',
      '+++ b/A/A.csproj',
      '@@ -1,3 +1,2 @@',
      '-<Project ToolsVersion="15.0">',
      '+<Project Sdk="Microsoft.NET.Sdk">',
      '   <PropertyGroup />',
      '',
    ].join('\n'),
    notes: ['1 converted, 2 refused.'],
    refusals: ['B: a custom build target', 'C: depends on Microsoft.AspNet.Mvc'],
    empty: false,
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Conversions],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build(path = '/repos/billing') {
    const fixture = TestBed.createComponent(Conversions);
    fixture.componentRef.setInput('rootPath', path);
    fixture.detectChanges();
    return fixture;
  }

  function answer(outcome: ConversionOutcome | null = sdk) {
    const request = http.expectOne((r) => r.url.endsWith('/convert'));
    if (outcome) request.flush(outcome);
    else request.flush({ error: 'No such directory.' }, { status: 400, statusText: 'Bad Request' });
    return request;
  }

  it('asks for nothing until a conversion is picked', () => {
    build();

    http.expectNone(() => true);
  });

  it('asks for the kind that was picked, over the folder it was given', () => {
    const fixture = build();
    fixture.componentInstance.choose('sdk');

    const request = answer();

    expect(request.request.body).toEqual({ path: '/repos/billing', kind: 'sdk' });
  });

  it('shows the patch as a diff', () => {
    const fixture = build();
    fixture.componentInstance.choose('sdk');
    answer();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelectorAll('.line.added').length).toBe(1);
    expect(element.querySelectorAll('.line.removed').length).toBe(1);
    expect(element.querySelectorAll('.line.hunk').length).toBe(1);
  });

  it('says on the page that nothing was applied', () => {
    // The one sentence this component exists to keep true.
    const fixture = build();
    fixture.componentInstance.choose('sdk');
    answer();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent)
      .toContain('Nothing has been applied');
  });

  it('keeps the count out and folds the rest away', () => {
    // On a real estate there are dozens of per-project notes, and listed flat
    // they push the patch and the refusals off the bottom of the screen.
    const fixture = build();
    fixture.componentInstance.choose('sdk');
    answer({ ...sdk, notes: ['10 converted, 79 refused.', 'A: a property was kept', 'B: an item was dropped'] });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.summary')?.textContent).toContain('10 converted');
    expect(element.querySelector('.notes summary')?.textContent).toContain('2 note(s)');
    expect(element.querySelectorAll('.notes li').length).toBe(2);
  });

  it('gives the refusals a heading rather than hiding them', () => {
    const fixture = build();
    fixture.componentInstance.choose('sdk');
    answer();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.refusals summary')?.textContent).toContain('2 refused');
    expect(element.querySelectorAll('.refusals li').length).toBe(2);
  });

  it('calls an empty patch an answer rather than a silence', () => {
    const fixture = build();
    fixture.componentInstance.choose('versions');

    answer({
      kind: 'versions',
      patch: '',
      notes: ['93 distinct package(s), none pinned to more than one version.'],
      refusals: [],
      empty: true,
    });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('none pinned to more than one version');
    expect(element.querySelector('.patch')).toBeNull();
  });

  it('closes a conversion that is picked twice', () => {
    const fixture = build();
    const app = fixture.componentInstance;

    app.choose('sdk');
    answer();
    app.choose('sdk');

    expect(app.chosen()).toBeNull();
    expect(app.outcome()).toBeNull();
  });

  it('holds back a patch too long to read in a browser', () => {
    const long = Array.from({ length: 500 }, (_, i) => ` line ${i}`).join('\n');

    const fixture = build();
    fixture.componentInstance.choose('packages');
    answer({ ...sdk, kind: 'packages', patch: long });
    fixture.detectChanges();

    expect(fixture.componentInstance.lines().length).toBe(300);
    expect(fixture.componentInstance.truncated()).toBe(200);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('200 more lines');
  });

  it('shows the rest when asked', () => {
    const long = Array.from({ length: 500 }, (_, i) => ` line ${i}`).join('\n');

    const fixture = build();
    fixture.componentInstance.choose('packages');
    answer({ ...sdk, kind: 'packages', patch: long });

    fixture.componentInstance.showAll();

    expect(fixture.componentInstance.lines().length).toBe(500);
    expect(fixture.componentInstance.truncated()).toBe(0);
  });

  it('drops the patch when the project changes', () => {
    // A patch belongs to the folder it was computed from, and showing it
    // beside another one invites somebody to apply it there.
    const fixture = build();
    fixture.componentInstance.choose('sdk');
    answer();

    fixture.componentRef.setInput('rootPath', '/repos/payroll');
    fixture.detectChanges();

    expect(fixture.componentInstance.outcome()).toBeNull();
    expect(fixture.componentInstance.chosen()).toBeNull();
  });

  it('asks nothing while a repository is still being fetched', () => {
    const fixture = build('');
    fixture.componentInstance.choose('sdk');

    http.expectNone(() => true);
    expect(fixture.componentInstance.outcome()).toBeNull();
  });

  it('says why a conversion could not be read', () => {
    const fixture = build();
    fixture.componentInstance.choose('sdk');
    answer(null);
    fixture.detectChanges();

    expect(fixture.componentInstance.failure()).toContain('No such directory');
  });

  it('offers the command that produces the same patch', () => {
    const fixture = build();
    fixture.componentInstance.choose('sdk');
    answer();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent)
      .toContain('convert /repos/billing sdk > sdk.patch');
  });
});
