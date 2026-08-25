import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Dependencies } from './dependencies';
import { Projection, SurfaceReport } from '../../models/lens';

/**
 * What holds a codebase back, in the browser.
 *
 * The distinction this page must never blur is between a type the catalogue
 * says has no replacement and a type it says nothing about. On screen those
 * look the same unless the markup keeps them apart, and folding them turns "we
 * have not looked" into "this is fine".
 */
describe('Dependencies', () => {
  let http: HttpTestingController;

  const report: SurfaceReport = {
    catalogue: '/repo/data/successors.json',
    packages: [
      {
        package: 'Microsoft.AspNet.Mvc',
        uses: 4529,
        files: 365,
        typesForMostOfIt: 41,
        filesForMostOfIt: 138,
        types: [
          { name: 'ActionResult', uses: 541, files: 111 },
          { name: 'Controller', uses: 131, files: 118 },
        ],
        heaviest: [
          { path: '/repos/orchard/src/Orchard/Mvc/Html/HtmlHelperExtensions.cs', uses: 149 },
        ],
        notes: ['Read from the syntax, not from a compilation.'],
        candidates: [
          {
            candidate: 'Microsoft.AspNetCore.Mvc',
            note: 'The pipeline changes shape.',
            percent: 61,
            blocked: true,
            covered: 49,
            unavailable: [{ name: 'ChildActionOnly', uses: 3, files: 2 }],
            unknown: [{ name: 'Test', uses: 119, files: 17 }],
            unknownCount: 150,
            usesCovered: 2745,
            usesUnavailable: 5,
            usesUnknown: 1277,
            unlisted: {
              applicable: true,
              inSuccessor: { types: [{ name: 'TagBuilder', uses: 51, where: 'Microsoft.AspNetCore.Mvc.Rendering.TagBuilder' }], count: 17, uses: 136 },
              elsewhere: { types: [{ name: 'HttpContext', uses: 8, where: 'Microsoft.AspNetCore.Http.HttpContext' }], count: 15, uses: 182 },
              gone: { types: [{ name: 'HttpUnauthorizedResult', uses: 408, where: null }], count: 118, uses: 959 },
              left: 133,
            },
          },
        ],
      },
    ],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Dependencies],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build(path = '/repos/orchard', answer: SurfaceReport | null = report) {
    const fixture = TestBed.createComponent(Dependencies);
    fixture.componentRef.setInput('rootPath', path);
    fixture.detectChanges();

    if (path) {
      const request = http.expectOne((r) => r.url.endsWith('/surface'));
      if (answer) request.flush(answer);
      else request.flush({ error: 'No such directory' }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();
    }

    return fixture;
  }

  it('asks about the folder it was given, for every package at once', () => {
    const fixture = TestBed.createComponent(Dependencies);
    fixture.componentRef.setInput('rootPath', '/repos/orchard');
    fixture.detectChanges();

    const request = http.expectOne((r) => r.url.endsWith('/surface'));
    expect(request.request.body).toEqual({ path: '/repos/orchard', package: null });
    request.flush(report);
  });

  it('asks nothing while a repository is still being fetched', () => {
    build('', null);
    http.expectNone(() => true);
  });

  it('leads with the number that sizes the work, not the total', () => {
    // A total cannot tell an afternoon of find-and-replace from a rewrite.
    const element = build().nativeElement as HTMLElement;

    expect(element.querySelector('.shape')?.textContent).toContain('41 type(s)');
  });

  it('says what is unknown beside what is covered, and what is left of it', () => {
    // A column of "nobody has looked at these" is read as work, and most of it
    // is not. On Orchard the framework accounts for 86 of the 219.
    const app = build().componentInstance;
    const reading = app.reading(report.packages[0].candidates[0]);

    expect(reading).toContain('61%');
    expect(reading).toContain('150 type(s)');
    expect(reading).toContain('133 still to decide');
  });

  it('separates what the framework answers from what the catalogue says', () => {
    // The catalogue is a judgement somebody signed. This is what the target
    // runtime answers when asked whether a name still exists, and the page has
    // to say which is which.
    const fixture = build();
    fixture.componentInstance.toggle(report.packages[0]);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    const text = element.textContent ?? '';

    expect(text).toContain('What the framework says about the rest');
    expect(text).toContain('the framework does not have at all');
    expect(element.querySelectorAll('.standing li')).toHaveLength(3);
  });

  it('calls a name that survived somewhere unrelated a trap rather than an answer', () => {
    // System.Web.HttpContext and Microsoft.AspNetCore.Http.HttpContext share a
    // word and nothing else. Reported as a correspondence it would send
    // somebody into the worst of the migration believing it was done.
    const fixture = build();
    fixture.componentInstance.toggle(report.packages[0]);
    fixture.detectChanges();

    const caveat = (fixture.nativeElement as HTMLElement).querySelector('.caveat')?.textContent ?? '';

    expect(caveat).toContain('trap rather than an answer');
    expect(caveat).toContain('HttpContext');
    expect(caveat).toContain('Microsoft.AspNetCore.Http.HttpContext');
  });

  it('says so plainly when the catalogue has an answer for everything', () => {
    const app = build().componentInstance;

    const reading = app.reading({
      ...report.packages[0].candidates[0], unknownCount: 0, usesUnknown: 0,
    });

    expect(reading).toContain('has an answer for the rest');
    expect(reading).not.toContain('unknown');
  });

  it('names what nothing replaces once a package is opened', () => {
    const fixture = build();
    fixture.componentInstance.toggle(report.packages[0]);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('ChildActionOnly');
    expect(text).toContain('rather than staying quiet');
  });

  it('offers one file to project rather than forty-seven', () => {
    const fixture = build();
    fixture.componentInstance.toggle(report.packages[0]);
    fixture.detectChanges();

    const files = (fixture.nativeElement as HTMLElement).querySelectorAll('.files li');
    expect(files.length).toBe(1);
    expect(files[0].textContent).toContain('HtmlHelperExtensions.cs');
  });

  it('projects the file it was asked about, against the package it belongs to', () => {
    const fixture = build();
    const surface = report.packages[0];
    fixture.componentInstance.toggle(surface);

    fixture.componentInstance.project(surface, '/repos/orchard/A.cs');

    const request = http.expectOne((r) => r.url.endsWith('/project'));
    expect(request.request.body).toEqual({
      path: '/repos/orchard/A.cs',
      package: 'Microsoft.AspNet.Mvc',
      root: '/repos/orchard',
      model: null,
    });

    request.flush(projection());
  });

  it('shows a projection that invented nothing with the claim it earned', () => {
    const fixture = build();
    const surface = report.packages[0];
    fixture.componentInstance.toggle(surface);
    fixture.componentInstance.project(surface, '/repos/orchard/A.cs');
    http.expectOne((r) => r.url.endsWith('/project')).flush(projection());
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.projection')).toBeTruthy();
    expect(element.querySelector('.projection.unsound')).toBeNull();
    expect(element.textContent).toContain('Nothing invented');
  });

  it('marks a projection that invented something as a failure', () => {
    const fixture = build();
    const surface = report.packages[0];
    fixture.componentInstance.toggle(surface);
    fixture.componentInstance.project(surface, '/repos/orchard/A.cs');

    http.expectOne((r) => r.url.endsWith('/project')).flush({
      ...projection(), sound: false, invented: ['IActionResultFactory'],
      claim: 'Names 1 thing(s) that exist nowhere. Not shown as a migration.',
    });
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.projection.unsound')).toBeTruthy();
    expect(element.textContent).toContain('IActionResultFactory');
  });

  it('says on the page that nothing was written', () => {
    const fixture = build();
    const surface = report.packages[0];
    fixture.componentInstance.toggle(surface);
    fixture.componentInstance.project(surface, '/repos/orchard/A.cs');
    http.expectOne((r) => r.url.endsWith('/project')).flush(projection());
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Nothing was written to your tree');

    // The blanket "it was not run" used to live here. It is now a measurement
    // rather than a disclaimer, so the panel below says what was checked.
    expect(text).toContain('Does it still do the same thing?');
  });

  it('drops what is on screen when the project changes', () => {
    // A surface belongs to the folder it was counted from.
    const fixture = build();
    fixture.componentInstance.toggle(report.packages[0]);

    fixture.componentRef.setInput('rootPath', '/repos/other');
    fixture.detectChanges();

    http.expectOne((r) => r.url.endsWith('/surface')).flush(report);

    expect(fixture.componentInstance.opened()).toBeNull();
    expect(fixture.componentInstance.projection()).toBeNull();
  });

  it('says why it could not read the folder', () => {
    expect(build('/nope', null).componentInstance.failure()).toContain('No such directory');
  });

  it('calls an empty result an answer rather than a blank', () => {
    const element = build('/repos/modern', { catalogue: 'x', packages: [] })
      .nativeElement as HTMLElement;

    expect(element.textContent).toContain('already modern');
  });

  function projection(): Projection {
    return {
      path: '/repos/orchard/A.cs',
      package: 'Microsoft.AspNet.Mvc',
      before: 'using System.Web.Mvc;',
      after: 'using Microsoft.AspNetCore.Mvc;',
      compiles: false,
      sound: true,
      claim: 'Nothing invented. Behaviour not verified.',
      target: '.NET 10, with ASP.NET Core present',
      invented: [],
      fromProject: ['LocalizedTaxonomyController'],
      unimported: [],
      attempts: 1,
      given: ['ActionResult becomes IActionResult'],
      notes: [],
      behaviour: null,
      behaviourRefusal: 'This server does not run code it was handed.',
    };
  }

  it('says behaviour was not checked rather than leaving the question open', () => {
    // The default on any server: comparing two versions means calling both,
    // and a projection is code a model wrote. Silence here would read as a
    // pass, which is the one thing this panel must never look like.
    const fixture = build();
    const surface = report.packages[0];
    fixture.componentInstance.toggle(surface);
    fixture.componentInstance.project(surface, '/repos/orchard/A.cs');
    http.expectOne((r) => r.url.endsWith('/project')).flush(projection());
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('lens-behaviour .headline')?.textContent).toContain('Not checked');
    expect(element.querySelector('lens-behaviour .claim')?.textContent).toContain('does not run code');
  });

  it('drops the old blanket caveat once behaviour can actually be checked', () => {
    // It used to say "it was not run, so nothing here says it behaves the
    // same" under every projection. That sentence is now sometimes false.
    const fixture = build();
    const surface = report.packages[0];
    fixture.componentInstance.toggle(surface);
    fixture.componentInstance.project(surface, '/repos/orchard/A.cs');
    http.expectOne((r) => r.url.endsWith('/project')).flush(projection());
    fixture.detectChanges();

    const caveat = (fixture.nativeElement as HTMLElement).querySelector('.caveat');

    expect(caveat).not.toBeNull();
    expect(caveat?.textContent).not.toContain('it was not run');
  });

  it('says nothing rather than everything when the successor is a package', () => {
    // log4net's answer is Serilog, which nothing in the runtime carries, so
    // every type of every predecessor comes back absent from the framework.
    // Literally true, and a reader concludes twenty-two types are gone when
    // what happened is that the question could not be asked.
    const packaged = {
      ...report,
      packages: [{
        ...report.packages[0],
        candidates: [{
          ...report.packages[0].candidates[0],
          candidate: 'Serilog',
          unlisted: {
            applicable: false,
            inSuccessor: { types: [], count: 0, uses: 0 },
            elsewhere: { types: [], count: 0, uses: 0 },
            gone: { types: [], count: 0, uses: 0 },
            left: 0,
          },
        }],
      }],
    };

    const fixture = build('/repos/orchard', packaged);
    fixture.componentInstance.toggle(packaged.packages[0]);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    const text = element.textContent ?? '';

    expect(element.querySelector('.standing')).toBeNull();
    expect(text).toContain('is a package rather than part of the framework');
    expect(text).toContain('nothing here can narrow that down');
  });
});
