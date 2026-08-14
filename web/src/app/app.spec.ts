import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { App } from './app';

describe('App', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      // The component calls the API on init, so the test needs a fake backend
      // rather than a real one. Nothing here touches the network.
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build() {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    // ngOnInit fires a health check; answer it so verify() stays happy.
    http.expectOne((r) => r.url.endsWith('/health'))
        .flush({ status: 'ok', indexedChunks: 58 });
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

  it('rejects a question too short to retrieve anything', () => {
    const app = build().componentInstance;

    app.form.setValue({ question: 'why?' });
    app.submit();

    // No request goes out, and the error surfaces on the control rather than
    // as a failed round trip.
    http.expectNone((r) => r.url.endsWith('/ask'));
    expect(app.questionControl.hasError('minlength')).toBe(true);
  });

  it('asks the API and keeps both the answer and its sources', () => {
    const fixture = build();
    const app = fixture.componentInstance;

    app.form.setValue({ question: 'Where is pricing calculated?' });
    app.submit();

    http.expectOne((r) => r.url.endsWith('/ask')).flush({
      answer: 'Pricing is computed in PriceEngine.',
      sources: [{ filePath: 'Billing/PriceEngine.cs', startLine: 84, endLine: 131, score: 0.81 }],
    });

    expect(app.answer()).toContain('PriceEngine');
    expect(app.sources().length).toBe(1);
    expect(app.loading()).toBe(false);
  });

  it('surfaces the API hint when the model is unreachable', () => {
    const app = build().componentInstance;

    app.form.setValue({ question: 'Where is pricing calculated?' });
    app.submit();

    http.expectOne((r) => r.url.endsWith('/ask')).flush(
      { error: 'Could not reach the model at http://localhost:11434.', hint: 'Start Ollama.' },
      { status: 503, statusText: 'Service Unavailable' },
    );

    // The message shown is the one the API wrote, not "Http failure response
    // for http://..." which would send the reader looking in the wrong place.
    expect(app.error()).toContain('Could not reach the model');
    expect(app.error()).toContain('Start Ollama');
    expect(app.loading()).toBe(false);
  });
});
