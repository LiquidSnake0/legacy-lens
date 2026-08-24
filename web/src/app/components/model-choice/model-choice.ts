import { Component, OnInit, inject, output, signal } from '@angular/core';

import { LensService } from '../../services/lens';
import { ModelChoice, ModelOptions } from '../../models/lens';

/**
 * Which model writes the answer.
 *
 * Local by default, and the default is the point: the front page of this tool
 * says no source code leaves the machine, so the moment that stops being true
 * has to be a decision somebody made on purpose, with the sentence in front of
 * them.
 *
 * The key lives in this component and nowhere else. It is not put in local
 * storage, not sent to the API except as part of the question it is needed
 * for, and gone when the tab closes. Storing it would be more convenient and
 * would make this tool a place where API keys live, which is a different thing
 * to be responsible for.
 */
@Component({
  selector: 'lens-model-choice',
  templateUrl: './model-choice.html',
  styleUrl: './model-choice.scss',
})
export class ModelChoiceComponent implements OnInit {
  private readonly lens = inject(LensService);

  /** Raised whenever the choice changes, so the page can send it with a question. */
  readonly chosen = output<ModelChoice>();

  readonly options = signal<ModelOptions | null>(null);
  readonly provider = signal<'local' | 'hosted'>('local');
  readonly apiKey = signal('');
  readonly model = signal('');
  readonly open = signal(false);

  ngOnInit(): void {
    this.lens.models().subscribe({
      next: (options) => {
        this.options.set(options);
        this.model.set(options.hosted.model);
      },
      // Not worth an error banner. Without this the local model is used, which
      // is what would have happened anyway.
      error: () => this.options.set(null),
    });
  }

  toggle(): void {
    this.open.update((open) => !open);
  }

  choose(provider: 'local' | 'hosted'): void {
    this.provider.set(provider);
    this.emit();
  }

  setKey(value: string): void {
    this.apiKey.set(value);
    this.emit();
  }

  setModel(value: string): void {
    this.model.set(value);
    this.emit();
  }

  /** What the page should show in one line, without opening the panel. */
  summary(): string {
    const options = this.options();

    if (this.provider() === 'hosted') {
      return this.model() || options?.hosted.model || 'hosted';
    }

    return options?.local.model ?? 'local';
  }

  /** Whether the hosted option is picked but unusable, so the page can say so. */
  get missingKey(): boolean {
    return this.provider() === 'hosted' && this.apiKey().trim().length === 0;
  }

  private emit(): void {
    this.chosen.emit(
      this.provider() === 'local'
        ? { provider: 'local' }
        : {
            provider: 'hosted',
            model: this.model().trim() || undefined,
            apiKey: this.apiKey().trim() || undefined,
          },
    );
  }
}
