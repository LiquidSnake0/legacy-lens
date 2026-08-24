import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ModelChoiceComponent } from './model-choice';
import { ModelChoice, ModelOptions } from '../../models/lens';

/**
 * The default is the point. This tool's front page says no source code leaves
 * the machine, so choosing otherwise has to be deliberate and has to be
 * accompanied by the sentence that says what changes.
 */
describe('ModelChoice', () => {
  let http: HttpTestingController;

  const options: ModelOptions = {
    local: { model: 'qwen2.5-coder:3b', description: 'Runs here. Nothing leaves this machine.' },
    hosted: {
      available: true,
      url: 'https://api.example/v1',
      model: 'gpt-4o-mini',
      description: 'Your own API key, used for the request and never stored.',
      warning: 'The question and the excerpts retrieved for it are sent to https://api.example/v1.',
    },
    embeddings: 'Always local. Embedding reads every file.',
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ModelChoiceComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build(offer: ModelOptions | null = options) {
    const fixture = TestBed.createComponent(ModelChoiceComponent);
    fixture.detectChanges();

    const request = http.expectOne((r) => r.url.endsWith('/models'));
    if (offer) request.flush(offer);
    else request.flush({ error: 'no' }, { status: 500, statusText: 'Server Error' });

    fixture.detectChanges();
    return fixture;
  }

  it('starts on the local model', () => {
    expect(build().componentInstance.provider()).toBe('local');
  });

  it('names the local model without being opened', () => {
    expect(build().componentInstance.summary()).toBe('qwen2.5-coder:3b');
  });

  it('emits nothing until somebody chooses', () => {
    // Silence means the page keeps its own default, which is local.
    const fixture = build();
    const choices: ModelChoice[] = [];
    fixture.componentInstance.chosen.subscribe((c) => choices.push(c));

    fixture.detectChanges();

    expect(choices).toEqual([]);
  });

  it('warns what leaves the machine before a hosted model can be picked', () => {
    const fixture = build();
    fixture.componentInstance.toggle();
    fixture.componentInstance.choose('hosted');
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('are sent to https://api.example/v1');
  });

  it('says the embeddings stay here whatever is chosen', () => {
    const fixture = build();
    fixture.componentInstance.toggle();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Always local');
  });

  it('emits the hosted choice with the key', () => {
    const fixture = build();
    const choices: ModelChoice[] = [];
    fixture.componentInstance.chosen.subscribe((c) => choices.push(c));

    fixture.componentInstance.choose('hosted');
    fixture.componentInstance.setKey('sk-test');

    expect(choices[choices.length - 1]).toEqual({
      provider: 'hosted',
      model: 'gpt-4o-mini',
      apiKey: 'sk-test',
    });
  });

  it('says a hosted model without a key falls back rather than failing', () => {
    const app = build().componentInstance;
    app.choose('hosted');

    expect(app.missingKey).toBe(true);
  });

  it('keeps the key out of storage', () => {
    localStorage.clear();
    const app = build().componentInstance;

    app.choose('hosted');
    app.setKey('sk-secret');

    expect(JSON.stringify(localStorage)).not.toContain('sk-secret');
    expect(JSON.stringify(sessionStorage)).not.toContain('sk-secret');
  });

  it('falls back to local when the API will not say what it offers', () => {
    const app = build(null).componentInstance;

    expect(app.provider()).toBe('local');
    expect(app.summary()).toBe('local');
  });
});
