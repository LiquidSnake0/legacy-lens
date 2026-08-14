import { Component, computed, input } from '@angular/core';

/** A run of prose, or a fenced block of code, in the order they were written. */
export interface Segment {
  kind: 'prose' | 'code';
  text: string;
  /** The language on the opening fence, when the model wrote one. */
  language?: string;
}

/** A stretch of prose, split so `identifiers` can be styled as code. */
export interface Run {
  kind: 'text' | 'code';
  text: string;
}

/**
 * Renders a model's answer.
 *
 * Models answering about code write markdown whether or not they were asked
 * to: backticks around identifiers, fenced blocks around snippets. Shown raw,
 * a correct answer reads as a broken one.
 *
 * Only fences and inline code are handled, and the text is bound rather than
 * injected as HTML. A full markdown renderer would mean trusting model output
 * with innerHTML, which is a large hole to open for a bold heading.
 */
@Component({
  selector: 'lens-answer-text',
  templateUrl: './answer-text.html',
  styleUrl: './answer-text.scss',
})
export class AnswerText {
  readonly text = input.required<string>();

  readonly segments = computed(() => AnswerText.split(this.text()));

  /**
   * Splits an answer on ``` fences.
   *
   * An unterminated fence is treated as code to the end of the text, because
   * that is exactly what a streamed answer looks like while it is still being
   * written: the closing fence has not arrived yet.
   */
  static split(text: string): Segment[] {
    const segments: Segment[] = [];
    const fence = /^```([\w+-]*)\s*$/;
    const lines = text.split('\n');

    let buffer: string[] = [];
    let language: string | undefined;
    let inCode = false;

    const flush = () => {
      const joined = buffer.join('\n');
      // Blank prose between two fences is spacing, not content.
      if (inCode || joined.trim().length > 0) {
        segments.push({
          kind: inCode ? 'code' : 'prose',
          text: inCode ? joined : joined.trim(),
          language,
        });
      }
      buffer = [];
    };

    for (const line of lines) {
      const opening = line.match(fence);

      if (opening) {
        flush();
        inCode = !inCode;
        language = inCode ? opening[1] || undefined : undefined;
        continue;
      }

      buffer.push(line);
    }

    flush();
    return segments;
  }

  /** Splits prose on single backticks so identifiers render as code. */
  runs(prose: string): Run[] {
    return prose
      .split(/(`[^`\n]+`)/g)
      .filter((part) => part.length > 0)
      .map((part) =>
        part.startsWith('`') && part.endsWith('`') && part.length > 2
          ? { kind: 'code' as const, text: part.slice(1, -1) }
          : { kind: 'text' as const, text: part }
      );
  }

  /** Paragraphs within one run of prose. */
  paragraphs(prose: string): string[] {
    return prose.split(/\n{2,}/).filter((p) => p.trim().length > 0);
  }
}
