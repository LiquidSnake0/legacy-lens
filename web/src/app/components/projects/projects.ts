import { Component, inject, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { LensService } from '../../services/lens';
import { WorkspaceStore } from '../../services/workspace-store';
import { Workspace } from '../../models/lens';

/**
 * Which project, and how to add one.
 *
 * The form is the first thing anyone who did not write this tool sees. Until
 * now it assumed an index already existed and that whoever ran it knew the
 * curl commands, which is the whole distance between something built and
 * something someone else can start.
 */
@Component({
  selector: 'lens-projects',
  imports: [ReactiveFormsModule],
  templateUrl: './projects.html',
  styleUrl: './projects.scss',
})
export class Projects {
  private readonly lens = inject(LensService);
  private readonly builder = inject(FormBuilder);

  readonly store = inject(WorkspaceStore);

  /** Raised once a project exists, so the page can start indexing it. */
  readonly added = output<Workspace>();

  readonly adding = signal(false);
  readonly busy = signal(false);
  readonly failure = signal<string | null>(null);

  /**
   * A local folder, or a repository to fetch.
   *
   * Two fields rather than one clever one: a path and a URL fail in different
   * ways, and a single box guessing between them explains neither.
   */
  readonly source = signal<'folder' | 'repository'>('folder');

  readonly form = this.builder.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(2)]],
    rootPath: [''],
    repositoryUrl: [''],
    token: [''],
  });

  open(): void {
    this.adding.set(true);
    this.failure.set(null);
    this.form.reset();
  }

  cancel(): void {
    this.adding.set(false);
    this.form.reset();
  }

  choose(source: 'folder' | 'repository'): void {
    this.source.set(source);
    this.failure.set(null);
  }

  select(id: string): void {
    this.store.select(id);
  }

  submit(): void {
    const value = this.form.getRawValue();
    const wantsRepository = this.source() === 'repository';

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    if (wantsRepository && !value.repositoryUrl.trim()) {
      this.failure.set('A repository URL is required.');
      return;
    }

    if (!wantsRepository && !value.rootPath.trim()) {
      this.failure.set('A folder is required.');
      return;
    }

    this.busy.set(true);
    this.failure.set(null);

    this.lens
      .createWorkspace({
        name: value.name.trim(),
        ...(wantsRepository
          ? {
              repositoryUrl: value.repositoryUrl.trim(),
              // Sent with this one call. Nothing here keeps a copy.
              ...(value.token.trim() ? { token: value.token.trim() } : {}),
            }
          : { rootPath: value.rootPath.trim() }),
      })
      .subscribe({
        next: (created) => {
          this.busy.set(false);
          this.adding.set(false);
          this.form.reset();

          this.store.all.update((all) => [created, ...all]);
          this.store.select(created.id);

          // A repository is already being fetched by the API, so only a folder
          // still needs indexing kicked off.
          this.added.emit(created);
        },
        error: (error: Error) => {
          this.busy.set(false);
          this.failure.set(error.message);
        },
      });
  }

  remove(workspace: Workspace): void {
    this.busy.set(true);

    this.lens.deleteWorkspace(workspace.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.store.refresh();
      },
      error: (error: Error) => {
        this.busy.set(false);
        this.failure.set(error.message);
      },
    });
  }

  get nameControl() {
    return this.form.controls.name;
  }

  /**
   * Whether the form is being shown on its own, with nothing behind it.
   *
   * Reads firstRun rather than an empty list, because an unreachable API also
   * produces an empty list, and inviting someone to point the tool at some
   * code when the API is down sends them to fix the wrong thing.
   */
  get alone(): boolean {
    return this.store.firstRun();
  }
}
