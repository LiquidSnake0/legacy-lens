import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';

import { BehaviourPanel } from '../behaviour/behaviour';
import { LensService } from '../../services/lens';
import {
  Candidate, ModelChoice, PackageSurface, Projection, SurfaceReport,
} from '../../models/lens';

/**
 * What holds a codebase back, and what could take its place.
 *
 * The hard question about a dependency with no future is never what the
 * alternatives are. It is which alternative covers what you actually use, and
 * that depends on code nobody has counted. This counts it.
 *
 * Three answers per type, never two: replaced, recorded as having no
 * replacement, or absent from the catalogue. The third is given as much room as
 * the others, because folding it into the second turns "we have not looked at
 * this" into "this is fine".
 */
@Component({
  selector: 'lens-dependencies',
  imports: [BehaviourPanel],
  templateUrl: './dependencies.html',
  styleUrl: './dependencies.scss',
})
export class Dependencies {
  private readonly lens = inject(LensService);

  /** The folder to read. Empty while a repository is still being fetched. */
  readonly rootPath = input.required<string>();

  /** Which model writes a projection, when one is asked for. */
  readonly model = input<ModelChoice | null>(null);

  readonly report = signal<SurfaceReport | null>(null);
  readonly loading = signal(false);
  readonly failure = signal<string | null>(null);

  readonly opened = signal<string | null>(null);

  readonly projection = signal<Projection | null>(null);
  readonly projecting = signal<string | null>(null);
  readonly projectionFailure = signal<string | null>(null);

  private asked: string | null = null;
  private alive = true;

  /** Packages the catalogue can say something about, worst first. */
  readonly packages = computed(() => this.report()?.packages ?? []);

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
      this.clearProjection();

      if (path) this.load(path);
    });
  }

  private load(path: string): void {
    this.loading.set(true);

    this.lens.surface(path).subscribe({
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

  toggle(surface: PackageSurface): void {
    this.opened.set(this.opened() === surface.package ? null : surface.package);
    this.clearProjection();
  }

  isOpen(surface: PackageSurface): boolean {
    return this.opened() === surface.package;
  }

  /**
   * Rewrites one file and shows what the compiler said.
   *
   * One file, because these rewrites are repetitive: whoever reads one before
   * and after knows what the remaining forty-six cost, and forty-seven of them
   * is a wait nobody sits through.
   */
  project(surface: PackageSurface, path: string): void {
    this.clearProjection();
    this.projecting.set(path);

    this.lens.project(path, surface.package, this.rootPath(), this.model()).subscribe({
      next: (projection) => {
        if (!this.alive || this.projecting() !== path) return;
        this.projection.set(projection);
        this.projecting.set(null);
      },
      error: (error: Error) => {
        if (!this.alive || this.projecting() !== path) return;
        this.projectionFailure.set(error.message);
        this.projecting.set(null);
      },
    });
  }

  private clearProjection(): void {
    this.projection.set(null);
    this.projecting.set(null);
    this.projectionFailure.set(null);
  }

  /** The share of a bar, floored so a package used once is still visible. */
  width(surface: PackageSurface): number {
    const worst = this.packages()[0]?.uses ?? 0;
    return worst > 0 ? Math.max(3, (surface.uses / worst) * 100) : 0;
  }

  /**
   * What a candidate's coverage means in one line.
   *
   * The unknown count is in it on purpose. A percentage on its own reads as a
   * verdict, and this one is a measurement against a catalogue that is still
   * being written.
   */
  reading(candidate: Candidate): string {
    if (candidate.unknownCount === 0) {
      return `${candidate.percent}% of the calls, and the catalogue has an answer for the rest.`;
    }

    return `${candidate.percent}% of the calls. ${candidate.unknownCount} type(s) `
      + `over ${candidate.usesUnknown} call(s) are not in the catalogue yet, which is `
      + `unknown rather than fine.`;
  }

  /** The last two segments of a path, which is what a reader recognises. */
  short(path: string): string {
    return path.split(/[\\/]/).slice(-2).join('/');
  }

  lines(text: string): string[] {
    return text.replace(/\n$/, '').split('\n');
  }

}
