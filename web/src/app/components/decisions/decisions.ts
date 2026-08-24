import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';

import { LensService } from '../../services/lens';
import { Choice, DiagnosisState, RaisedDilemma, Site } from '../../models/lens';

/**
 * The decisions the code cannot make on its own.
 *
 * Everything else here measures. This asks, because some of what decides a
 * migration is not in the repository: how many machines serve this, whether a
 * request may land on a different one than the last, whether anybody would
 * notice a cold cache. Reading the code harder does not produce those answers.
 *
 * Two things separate it from a chat window. The outcomes are written down
 * before anybody is asked anything, so there is a known place to land, and a
 * question is only asked when one of its answers would rule out something still
 * standing. That is a stopping condition rather than a limit: what has no such
 * rule keeps asking until the reader closes the tab.
 */
@Component({
  selector: 'lens-decisions',
  templateUrl: './decisions.html',
  styleUrl: './decisions.scss',
})
export class Decisions {
  private readonly lens = inject(LensService);

  readonly rootPath = input.required<string>();
  readonly workspace = input.required<string>();

  readonly dilemmas = signal<RaisedDilemma[]>([]);
  readonly loading = signal(false);
  readonly failure = signal<string | null>(null);

  readonly opened = signal<string | null>(null);

  /** Which dilemma is waiting on the server, so its buttons can be held. */
  readonly saving = signal<string | null>(null);

  private asked: string | null = null;
  private alive = true;

  readonly settledCount = computed(
    () => this.dilemmas().filter(d => d.diagnosis.settled).length);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.alive = false;
    });

    effect(() => {
      const key = `${this.rootPath()} ${this.workspace()}`;
      if (key === this.asked) return;

      this.asked = key;
      this.dilemmas.set([]);
      this.failure.set(null);
      this.opened.set(null);

      if (this.rootPath()) this.load(key);
    });
  }

  private load(key: string): void {
    this.loading.set(true);

    this.lens.diagnose(this.rootPath(), this.workspace()).subscribe({
      next: (report) => {
        if (!this.alive || this.asked !== key) return;
        this.dilemmas.set(report.dilemmas);
        this.loading.set(false);

        // Opened when there is one, because a single collapsed row asks the
        // reader to click to find out there was only ever one thing here.
        if (report.dilemmas.length === 1) {
          this.opened.set(report.dilemmas[0].diagnosis.id);
        }
      },
      error: (error: Error) => {
        if (!this.alive || this.asked !== key) return;
        this.failure.set(error.message);
        this.loading.set(false);
      },
    });
  }

  toggle(raised: RaisedDilemma): void {
    this.opened.set(this.isOpen(raised) ? null : raised.diagnosis.id);
  }

  isOpen(raised: RaisedDilemma): boolean {
    return this.opened() === raised.diagnosis.id;
  }

  answer(raised: RaisedDilemma, choice: Choice): void {
    const question = raised.diagnosis.next;
    if (!question || this.saving()) return;

    this.saving.set(raised.diagnosis.id);

    this.lens
      .answerDilemma(raised.diagnosis.id, question.id, choice.answer, this.workspace())
      .subscribe({
        next: (state) => this.settle(raised.diagnosis.id, state),
        error: (error: Error) => {
          if (!this.alive) return;
          this.failure.set(error.message);
          this.saving.set(null);
        },
      });
  }

  /**
   * Starts one over.
   *
   * Needed rather than nice to have. Somebody answers "one machine" on Tuesday,
   * finds out on Thursday there are four, and without this the diagnosis stays
   * confidently wrong.
   */
  restart(raised: RaisedDilemma): void {
    if (this.saving()) return;

    this.saving.set(raised.diagnosis.id);

    this.lens.forgetDilemma(raised.diagnosis.id, this.workspace()).subscribe({
      next: (state) => this.settle(raised.diagnosis.id, state),
      error: (error: Error) => {
        if (!this.alive) return;
        this.failure.set(error.message);
        this.saving.set(null);
      },
    });
  }

  /** Puts a fresh diagnosis in, keeping the sites it was found at. */
  private settle(id: string, state: DiagnosisState): void {
    if (!this.alive) return;

    this.dilemmas.update(all => all.map(
      raised => raised.diagnosis.id === id ? { ...raised, diagnosis: state } : raised));

    this.saving.set(null);
  }

  /** How far through, for a bar that means something rather than decorating. */
  ruledOut(state: DiagnosisState): number {
    return state.outcomes > 0
      ? ((state.outcomes - state.remaining.length) / state.outcomes) * 100
      : 0;
  }

  /**
   * What a choice would do, said before it is clicked.
   *
   * The reason this is on screen at all: an answer that quietly narrows things
   * behind the reader's back is what makes a wizard untrustworthy.
   */
  cost(state: DiagnosisState, choice: Choice): string {
    const going = state.remaining.filter(o => choice.eliminates.includes(o.id));

    if (going.length === 0) return 'Rules nothing out.';

    const names = going.map(o => o.name).join(', ');

    return going.length === state.remaining.length
      ? `Rules out everything still standing: ${names}.`
      : `Rules out ${names}.`;
  }

  short(path: string): string {
    const parts = path.split(/[\\/]+/);
    return parts.slice(-2).join('/');
  }

  siteLabel(site: Site): string {
    return `${this.short(site.path)}:${site.line}`;
  }
}
