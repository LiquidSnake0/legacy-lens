import { Component, input } from '@angular/core';

import { Citation } from '../../models/lens';

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
  /** Signal input: the parent passes the list, this component never fetches. */
  readonly sources = input.required<Citation[]>();

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

  lines(citation: Citation): string {
    return citation.startLine === citation.endLine
      ? `line ${citation.startLine}`
      : `lines ${citation.startLine}-${citation.endLine}`;
  }
}
