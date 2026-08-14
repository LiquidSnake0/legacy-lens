import { Component, inject, signal, OnInit } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { AnswerText } from './components/answer-text/answer-text';
import { Citations } from './components/citations/citations';
import { LensService } from './services/lens';
import { Citation } from './models/lens';

@Component({
  selector: 'app-root',
  imports: [ReactiveFormsModule, AnswerText, Citations],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  private readonly lens = inject(LensService);
  private readonly builder = inject(FormBuilder);

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
  readonly indexed = signal<number | null>(null);

  readonly examples = [
    'How does the chunker decide where to cut a file?',
    'Where is cosine similarity computed?',
    'How are files excluded from indexing?',
  ];

  ngOnInit(): void {
    this.lens.health().subscribe({
      next: (health) => this.indexed.set(health.indexedChunks),
      // A failing health check is not worth an error banner: the user has not
      // asked for anything yet. The empty chunk count says enough.
      error: () => this.indexed.set(null),
    });
  }

  async submit(): Promise<void> {
    if (this.form.invalid || this.loading()) {
      this.form.markAllAsTouched();
      return;
    }

    const question = this.form.getRawValue().question.trim();

    this.loading.set(true);
    this.error.set(null);
    this.answer.set(null);
    this.sources.set([]);

    try {
      for await (const event of this.lens.stream(question)) {
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
