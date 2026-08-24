import { Component, inject, signal, OnInit } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { AnswerText } from './components/answer-text/answer-text';
import { Citations } from './components/citations/citations';
import { Indexing } from './components/indexing/indexing';
import { ModelChoiceComponent } from './components/model-choice/model-choice';
import { Overview } from './components/overview/overview';
import { Projects } from './components/projects/projects';
import { LensService } from './services/lens';
import { WorkspaceStore } from './services/workspace-store';
import { Citation, IngestionJob, ModelChoice, Workspace } from './models/lens';

@Component({
  selector: 'app-root',
  imports: [
    ReactiveFormsModule, AnswerText, Citations, Indexing, ModelChoiceComponent,
    Overview, Projects,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  private readonly lens = inject(LensService);
  private readonly builder = inject(FormBuilder);

  readonly store = inject(WorkspaceStore);

  /**
   * Reactive form rather than template-driven: validation lives in the class
   * where it can be read and tested, instead of being spread across attributes
   * in the markup.
   */
  readonly form = this.builder.nonNullable.group({
    question: ['', [Validators.required, Validators.minLength(8)]],
  });

  // Signals rather than a stream of booleans: the template reads them
  // directly and Angular only re-renders what actually changed.
  readonly answer = signal<string | null>(null);
  readonly sources = signal<Citation[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  /**
   * Local until somebody decides otherwise, with the warning in front of them.
   *
   * The footer reads this too. A page whose last line promises that no source
   * code leaves the machine has to stop promising it the moment that stops
   * being true.
   */
  readonly model = signal<ModelChoice>({ provider: 'local' });

  readonly examples = [
    'How does the chunker decide where to cut a file?',
    'Where is cosine similarity computed?',
    'How are files excluded from indexing?',
  ];

  ngOnInit(): void {
    this.store.refresh();
  }

  /**
   * A project that was just added.
   *
   * A repository is already being fetched by the API, since only it can clone.
   * A folder is here on disk and needs the run started.
   */
  onAdded(workspace: Workspace): void {
    if (!workspace.rootPath) return;

    this.lens.startIndexing(workspace.id, workspace.rootPath).subscribe({
      next: () => {},
      // Not fatal. The structural half works without an index, and the reason
      // is worth showing rather than swallowing.
      error: (failure: Error) => this.error.set(failure.message),
    });
  }

  /** A run has ended, so the chunk counts on the picker are out of date. */
  onIndexed(_: IngestionJob): void {
    this.store.refresh();
  }

  onModel(choice: ModelChoice): void {
    this.model.set(choice);
  }

  reindex(): void {
    const current = this.store.current();
    if (!current?.rootPath) return;

    this.lens.startIndexing(current.id, current.rootPath).subscribe({
      next: () => {},
      error: (failure: Error) => this.error.set(failure.message),
    });
  }

  async submit(): Promise<void> {
    const workspace = this.store.currentId();

    if (this.form.invalid || this.loading() || !workspace) {
      this.form.markAllAsTouched();
      return;
    }

    const question = this.form.getRawValue().question.trim();

    this.loading.set(true);
    this.error.set(null);
    this.answer.set(null);
    this.sources.set([]);

    try {
      for await (const event of this.lens.stream(question, workspace, this.model())) {
        switch (event.name) {
          case 'sources':
            this.sources.set(event.data as Citation[]);
            // Generation has started, so the answer box appears now rather
            // than when the first token lands.
            this.answer.set('');
            break;

          case 'token':
            this.answer.update((text) => (text ?? '') + (event.data as string));
            break;

          case 'failed':
            this.error.set(event.data as string);
            break;
        }
      }
    } catch (failure) {
      this.error.set(failure instanceof Error ? failure.message : String(failure));
    } finally {
      this.loading.set(false);
    }
  }

  useExample(question: string): void {
    this.form.patchValue({ question });
  }

  get questionControl() {
    return this.form.controls.question;
  }
}
