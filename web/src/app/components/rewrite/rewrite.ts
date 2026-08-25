import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { BehaviourPanel } from '../behaviour/behaviour';
import { LensService } from '../../services/lens';
import { Behaviour } from '../../models/lens';

/**
 * Two files, called with the same values, to see whether one still does what
 * the other did.
 *
 * The same check the projection runs, reachable without one. That is not a
 * convenience: under a projection the original is a file importing a dead
 * framework, which does not compile on this runtime, so the honest answer there
 * is almost always *not checked*. Where this bites is a service somebody
 * rewrote by hand, which still runs here, and that case had no way in but a
 * terminal.
 *
 * Two paths rather than two editors. The tool reads from disk everywhere else,
 * and a box that accepts pasted code and runs it is a different kind of thing.
 */
@Component({
  selector: 'lens-rewrite',
  imports: [ReactiveFormsModule, BehaviourPanel],
  templateUrl: './rewrite.html',
  styleUrl: './rewrite.scss',
})
export class Rewrite {
  private readonly lens = inject(LensService);

  readonly form = inject(FormBuilder).nonNullable.group({
    before: ['', Validators.required],
    after: ['', Validators.required],
  });

  readonly report = signal<Behaviour | null>(null);
  readonly refusal = signal<string | null>(null);
  readonly running = signal(false);
  readonly asked = signal(false);

  private alive = true;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.alive = false;
    });
  }

  compare(): void {
    if (this.form.invalid || this.running()) return;

    const { before, after } = this.form.getRawValue();

    this.running.set(true);
    this.report.set(null);
    this.refusal.set(null);

    this.lens.equivalence(before.trim(), after.trim()).subscribe({
      next: (answer) => {
        if (!this.alive) return;
        this.report.set(answer.behaviour);
        this.asked.set(true);
        this.running.set(false);
      },
      // A refusal is an answer here rather than a failure: the server explains
      // why it will not run code, and that belongs in the panel beside a
      // verdict rather than in a red box away from it.
      error: (error: Error) => {
        if (!this.alive) return;
        this.refusal.set(error.message);
        this.asked.set(true);
        this.running.set(false);
      },
    });
  }
}
