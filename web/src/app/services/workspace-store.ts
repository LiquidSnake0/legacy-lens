import { Injectable, computed, inject, signal } from '@angular/core';

import { LensService } from './lens';
import { Workspace } from '../models/lens';

/**
 * Which project is being looked at.
 *
 * A store rather than an input threaded through four components: the question
 * form, the indexing panel, the overview and the citations all need the same
 * answer, and passing it down by hand means four places to forget.
 */
@Injectable({ providedIn: 'root' })
export class WorkspaceStore {
  private readonly lens = inject(LensService);

  /**
   * Survives a reload, so the form asking where the code is appears once
   * rather than on every visit.
   */
  private static readonly Remembered = 'legacy-lens.workspace';

  readonly all = signal<Workspace[]>([]);
  readonly currentId = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly current = computed(
    () => this.all().find((workspace) => workspace.id === this.currentId()) ?? null,
  );

  /** No projects and nothing failed: this is a first run, not an error. */
  readonly firstRun = computed(() => !this.loading() && !this.error() && this.all().length === 0);

  refresh(): void {
    this.loading.set(true);

    this.lens.workspaces().subscribe({
      next: (found) => {
        this.all.set(found);
        this.error.set(null);
        this.loading.set(false);
        this.settle(found);
      },
      error: (failure: Error) => {
        this.error.set(failure.message);
        this.loading.set(false);
      },
    });
  }

  select(id: string): void {
    this.currentId.set(id);
    this.remember(id);
  }

  /**
   * Picks up where the last visit left off, or on the newest project.
   *
   * A remembered project that has since been deleted is dropped rather than
   * left selected, which would show an empty panel with no way to explain it.
   */
  private settle(found: Workspace[]): void {
    const remembered = this.read();
    const stillThere = found.some((workspace) => workspace.id === remembered);

    if (remembered && stillThere) {
      this.currentId.set(remembered);
      return;
    }

    // The list arrives newest first.
    const first = found[0]?.id ?? null;
    this.currentId.set(first);
    if (first) this.remember(first);
    else this.forget();
  }

  private read(): string | null {
    try {
      return localStorage.getItem(WorkspaceStore.Remembered);
    } catch {
      // Storage is unavailable in a private window in some browsers. Losing
      // the selection is a smaller problem than failing to load the page.
      return null;
    }
  }

  private remember(id: string): void {
    try {
      localStorage.setItem(WorkspaceStore.Remembered, id);
    } catch {
      /* see read() */
    }
  }

  private forget(): void {
    try {
      localStorage.removeItem(WorkspaceStore.Remembered);
    } catch {
      /* see read() */
    }
  }
}
