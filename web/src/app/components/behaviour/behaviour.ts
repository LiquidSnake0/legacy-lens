import { Component, computed, input } from '@angular/core';

import { Behaviour as Report, ComparedMethod } from '../../models/lens';

/**
 * Whether the rewrite still does the same thing.
 *
 * The projection above it proves the code is valid. This is the only part that
 * says anything about what it does, and the distinction is the reason the
 * milestone exists: a file can compile perfectly and quietly return something
 * else.
 *
 * What it must never do is look like a pass when nothing was compared. A file
 * whose work happens through a web framework compares nothing at all, which is
 * the common case rather than the odd one, and a green line on that is how a
 * migration gets signed off and discovered in month four. So the refusals sit
 * beside the result rather than behind a toggle.
 */
@Component({
  selector: 'lens-behaviour',
  templateUrl: './behaviour.html',
  styleUrl: './behaviour.scss',
})
export class BehaviourPanel {
  /** The report, or null when the server was not allowed to run anything. */
  readonly report = input.required<Report | null>();

  /** What to say instead, when there is no report. */
  readonly refusal = input<string | null>(null);

  readonly moved = computed(() => this.methods().filter(m => !m.matched));

  readonly matched = computed(() => this.methods().filter(m => m.matched));

  private methods(): ComparedMethod[] {
    return this.report()?.methods ?? [];
  }

  /**
   * How the whole thing reads at a glance.
   *
   * Three states rather than two, because "nothing was compared" is a real
   * answer and folding it into either of the others is the one mistake this
   * panel cannot make.
   *
   * `verified` is taken from the report rather than worked out again from the
   * counts beside it. The rule that a run comparing nothing has verified
   * nothing lives on the server, where it is tested; a second copy here would
   * agree until the day it did not, and this is the one place where the two
   * disagreeing means showing a pass that was never earned.
   */
  readonly verdict = computed<'verified' | 'moved' | 'unchecked'>(() => {
    const report = this.report();

    if (!report) return 'unchecked';
    if (report.verified) return 'verified';

    return report.ran && report.methods.length > 0 ? 'moved' : 'unchecked';
  });

  readonly headline = computed(() => {
    switch (this.verdict()) {
      case 'verified': return 'Nothing moved';
      case 'moved': return 'Something moved';
      default: return 'Not checked';
    }
  });
}
