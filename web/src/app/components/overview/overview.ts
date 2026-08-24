import { Component, DestroyRef, effect, inject, input, signal } from '@angular/core';

import { LensService } from '../../services/lens';
import { RiskEntry, RiskReport } from '../../models/lens';

/**
 * What the tool can say before any model is involved.
 *
 * This is the half that reads a directory and answers in seconds: which files
 * are complicated, change constantly and have no tests. It needs no index and
 * no embedding, so it fills the screen while the slow half is still running,
 * instead of a spinner.
 *
 * It was also, until now, invisible: everything went through the question box,
 * which cannot answer until hours of indexing have finished.
 */
@Component({
  selector: 'lens-overview',
  templateUrl: './overview.html',
  styleUrl: './overview.scss',
})
export class Overview {
  private readonly lens = inject(LensService);

  /** The folder to read. Empty while a repository is still being fetched. */
  readonly rootPath = input.required<string>();

  readonly report = signal<RiskReport | null>(null);
  readonly loading = signal(false);
  readonly failure = signal<string | null>(null);
  readonly opened = signal<string | null>(null);

  private asked: string | null = null;
  private alive = true;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.alive = false;
    });

    effect(() => {
      const path = this.rootPath();
      if (path === this.asked) return;

      this.asked = path;
      this.report.set(null);
      this.failure.set(null);
      this.opened.set(null);

      if (path) this.load(path);
    });
  }

  private load(path: string): void {
    this.loading.set(true);

    this.lens.risk(path).subscribe({
      next: (report) => {
        if (!this.alive || this.asked !== path) return;
        this.report.set(report);
        this.loading.set(false);
      },
      error: (error: Error) => {
        if (!this.alive || this.asked !== path) return;
        this.failure.set(error.message);
        this.loading.set(false);
      },
    });
  }

  toggle(entry: RiskEntry): void {
    this.opened.set(this.opened() === entry.path ? null : entry.path);
  }

  isOpen(entry: RiskEntry): boolean {
    return this.opened() === entry.path;
  }

  /**
   * Scores are relative to the worst file in this codebase, not to an absolute
   * scale. A bar showing 40% of some universal maximum would be a number
   * nobody can act on; against the worst file here, it is a queue.
   */
  width(entry: RiskEntry): number {
    const worst = this.report()?.entries[0]?.score ?? 0;
    return worst > 0 ? Math.max(4, (entry.score / worst) * 100) : 0;
  }

  /** The last path segment, since the full path is already on the row. */
  name(entry: RiskEntry): string {
    const parts = entry.path.split(/[\\/]/);
    return parts[parts.length - 1] ?? entry.path;
  }

  folder(entry: RiskEntry): string {
    const parts = entry.path.split(/[\\/]/);
    return parts.slice(0, -1).join('/');
  }
}
