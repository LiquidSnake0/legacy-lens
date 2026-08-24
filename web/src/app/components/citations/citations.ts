import { Component, inject, input, signal } from '@angular/core';

import { LensService } from '../../services/lens';
import { Citation, Excerpt } from '../../models/lens';

/**
 * The sources behind an answer.
 *
 * A child component rather than markup inside the page, because these are the
 * part worth reusing: any future view that shows a retrieval result shows the
 * same list, formatted the same way.
 */
@Component({
  selector: 'lens-citations',
  templateUrl: './citations.html',
  styleUrl: './citations.scss',
})
export class Citations {
  private readonly lens = inject(LensService);

  /** Signal input: the parent passes the list, this component never fetches. */
  readonly sources = input.required<Citation[]>();

  /**
   * Which project the excerpts come from.
   *
   * Two projects can hold a file at the same path with different code in it,
   * so an excerpt fetched without this could show the other one's.
   */
  readonly workspace = input.required<string>();

  /**
   * Which citation is open, keyed the same way the list is tracked.
   *
   * One at a time: several excerpts open at once push the answer off screen,
   * which is the thing the reader is comparing them against.
   */
  readonly opened = signal<string | null>(null);
  readonly excerpt = signal<Excerpt | null>(null);
  readonly loadingExcerpt = signal(false);
  readonly excerptError = signal<string | null>(null);

  /**
   * Opens a citation, or closes it if it was already open.
   *
   * A citation nobody can open is a claim the reader has to take on trust,
   * which defeats the point of citing anything.
   */
  toggle(citation: Citation): void {
    const key = this.key(citation);

    if (this.opened() === key) {
      this.opened.set(null);
      this.excerpt.set(null);
      return;
    }

    this.opened.set(key);
    this.excerpt.set(null);
    this.excerptError.set(null);
    this.loadingExcerpt.set(true);

    this.lens.excerpt(citation.filePath, citation.startLine, this.workspace()).subscribe({
      next: (excerpt) => {
        // The reader may have clicked another citation while this was in
        // flight; the late answer must not overwrite the newer one.
        if (this.opened() === key) this.excerpt.set(excerpt);
        this.loadingExcerpt.set(false);
      },
      error: (failure: Error) => {
        if (this.opened() === key) this.excerptError.set(failure.message);
        this.loadingExcerpt.set(false);
      },
    });
  }

  isOpen(citation: Citation): boolean {
    return this.opened() === this.key(citation);
  }

  key(citation: Citation): string {
    return `${citation.filePath}:${citation.startLine}`;
  }

  /**
   * Retrieval scores sit in a narrow band, roughly 0.5 to 0.75 in practice,
   * because embedding spaces are anisotropic. Mapping that band across the
   * full width of the bar makes differences visible; a raw 0-to-1 scale would
   * render every result as a nearly identical stripe.
   */
  width(score: number): number {
    const floor = 0.45;
    const ceiling = 0.85;
    const clamped = Math.min(Math.max(score, floor), ceiling);
    return ((clamped - floor) / (ceiling - floor)) * 100;
  }

  /** What to show where a score would go, when there is no score. */
  label(citation: Citation): string {
    return citation.foundBy === 'text' ? 'exact' : citation.score.toFixed(2);
  }

  hint(citation: Citation): string {
    return citation.foundBy === 'both'
      ? `Found by both searches. Cosine similarity: ${citation.score.toFixed(3)}`
      : citation.foundBy === 'text'
        ? 'Matched by exact term, not by meaning. No cosine score was computed.'
        : `Cosine similarity: ${citation.score.toFixed(3)}`;
  }

  lines(citation: Citation): string {
    return citation.startLine === citation.endLine
      ? `line ${citation.startLine}`
      : `lines ${citation.startLine}-${citation.endLine}`;
  }

  /** Line numbers down the gutter, starting where the chunk starts. */
  numbers(excerpt: Excerpt): number[] {
    const lines = excerpt.content.split('\n').length;
    return Array.from({ length: lines }, (_, i) => excerpt.startLine + i);
  }
}
