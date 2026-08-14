import { TestBed } from '@angular/core/testing';

import { AnswerText } from './answer-text';

describe('AnswerText', () => {
  let component: AnswerText;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [AnswerText] }).compileComponents();

    const fixture = TestBed.createComponent(AnswerText);
    fixture.componentRef.setInput('text', '');
    fixture.detectChanges();
    component = fixture.componentInstance;
  });

  it('leaves an answer with no markup as one run of prose', () => {
    const segments = AnswerText.split('Pricing is computed in PriceEngine.');

    expect(segments.length).toBe(1);
    expect(segments[0].kind).toBe('prose');
  });

  it('separates a fenced block from the prose around it', () => {
    const segments = AnswerText.split(
      ['Like this:', '```csharp', 'var x = 1;', '```', 'and that is all.'].join('\n')
    );

    expect(segments.map((s) => s.kind)).toEqual(['prose', 'code', 'prose']);
    expect(segments[1].text).toBe('var x = 1;');
    expect(segments[1].language).toBe('csharp');
  });

  it('treats an unclosed fence as code to the end', () => {
    // What every streamed answer looks like while it is still arriving: the
    // closing fence has not been written yet. Waiting for it would leave the
    // snippet rendered as prose for as long as it takes to finish.
    const segments = AnswerText.split(['Here it is:', '```csharp', 'var x = 1;'].join('\n'));

    expect(segments.map((s) => s.kind)).toEqual(['prose', 'code']);
    expect(segments[1].text).toBe('var x = 1;');
  });

  it('keeps indentation inside a code block', () => {
    const segments = AnswerText.split(['```', 'if (a)', '    return b;', '```'].join('\n'));

    expect(segments[0].text).toBe('if (a)\n    return b;');
  });

  it('drops blank prose between two blocks', () => {
    const segments = AnswerText.split(
      ['```', 'a', '```', '', '```', 'b', '```'].join('\n')
    );

    expect(segments.map((s) => s.kind)).toEqual(['code', 'code']);
  });

  it('marks backticked identifiers as code', () => {
    const runs = component.runs('The `VectorMath` class holds it.');

    expect(runs.map((r) => r.kind)).toEqual(['text', 'code', 'text']);
    expect(runs[1].text).toBe('VectorMath');
  });

  it('leaves a lone backtick alone', () => {
    // An answer mid-stream ends in a half-written span more often than not.
    // Rendering the stray backtick as text is better than swallowing the rest
    // of the sentence into a code span that never closes.
    const runs = component.runs('The `VectorMath class');

    expect(runs.length).toBe(1);
    expect(runs[0].kind).toBe('text');
    expect(runs[0].text).toBe('The `VectorMath class');
  });

  it('splits prose into paragraphs on blank lines', () => {
    expect(component.paragraphs('First.\n\nSecond.').length).toBe(2);
    expect(component.paragraphs('One line only.').length).toBe(1);
  });
});
