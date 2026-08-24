import { Component, DestroyRef, computed, effect, inject, input, output, signal } from '@angular/core';

import { LensService } from '../../services/lens';
import { IngestionJob } from '../../models/lens';

/**
 * How far indexing has got.
 *
 * Embedding runs at roughly two chunks a second on a CPU, so this is watched
 * for minutes and left alone for hours. What it has to convey is that work is
 * happening and roughly how much is left, because a long run that reports
 * nothing is indistinguishable from a hung one.
 */
@Component({
  selector: 'lens-indexing',
  templateUrl: './indexing.html',
  styleUrl: './indexing.scss',
})
export class Indexing {
  private readonly lens = inject(LensService);

  /** Which project to watch. Changing it starts polling the new one. */
  readonly workspace = input.required<string>();

  /** Raised when a run finishes, so the page can refresh its chunk counts. */
  readonly finished = output<IngestionJob>();

  readonly job = signal<IngestionJob | null>(null);
  readonly failure = signal<string | null>(null);

  private timer: ReturnType<typeof setTimeout> | null = null;
  private watching: string | null = null;
  private wasRunning = false;

  readonly percent = computed(() => {
    const job = this.job();
    if (!job || job.filesTotal === 0) return 0;
    return Math.round((job.filesDone / job.filesTotal) * 100);
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());

    // Reading the input inside an effect is what makes switching projects
    // switch what is polled, without the parent having to tell this component
    // to start over.
    effect(() => {
      const workspace = this.workspace();
      if (workspace === this.watching) return;

      this.watching = workspace;
      this.wasRunning = false;
      this.job.set(null);
      this.failure.set(null);
      this.poll();
    });
  }

  /** Asks the API to stop. What was embedded already stays indexed. */
  cancel(): void {
    this.lens.cancelIndexing(this.watching!).subscribe({
      next: () => this.poll(),
      error: (error: Error) => this.failure.set(error.message),
    });
  }

  private poll(): void {
    this.stop();

    const workspace = this.watching;
    if (!workspace) return;

    this.lens.indexingStatus(workspace).subscribe({
      next: (job) => {
        // A late answer for a project the reader has already navigated away
        // from must not overwrite the one now on screen.
        if (this.watching !== workspace) return;

        this.job.set(job);
        this.failure.set(null);

        if (job?.running) {
          this.wasRunning = true;
          // Two seconds: fast enough that the file being worked on looks live,
          // slow enough that an hour-long run is not thousands of requests.
          this.timer = setTimeout(() => this.poll(), 2000);
          return;
        }

        if (this.wasRunning && job) {
          this.wasRunning = false;
          this.finished.emit(job);
        }
      },
      error: (error: Error) => {
        if (this.watching === workspace) this.failure.set(error.message);
      },
    });
  }

  private stop(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }

  /** "4 min" rather than 240, because nobody counts in seconds past a minute. */
  left(seconds: number): string {
    if (seconds < 60) return `${seconds}s`;
    if (seconds < 3600) return `${Math.round(seconds / 60)} min`;

    const hours = Math.floor(seconds / 3600);
    const minutes = Math.round((seconds % 3600) / 60);
    return minutes === 0 ? `${hours}h` : `${hours}h ${minutes}m`;
  }
}
