import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';

import { LensService } from '../../services/lens';
import { ConversionKind, ConversionOutcome, Landed } from '../../models/lens';

/** One conversion, and the sentence that says what it is for. */
interface Offer {
  kind: ConversionKind;
  title: string;
  what: string;
}

/**
 * The mechanical conversions, as a patch nobody has applied.
 *
 * The rule for this whole part of the tool is that it proposes a diff and a
 * person approves it, so this shows the diff and never applies it. There is no
 * button here that writes to anyone's tree, and there is not going to be one:
 * the moment a tool commits its own output is the moment its mistakes stop
 * being reviewable.
 *
 * The refusals are given as much room as the patch. On a real estate they are
 * the longer list and the one that decides what the work actually is.
 */
@Component({
  selector: 'lens-conversions',
  templateUrl: './conversions.html',
  styleUrl: './conversions.scss',
})
export class Conversions {
  private readonly lens = inject(LensService);

  /** The folder to read. Empty while a repository is still being fetched. */
  readonly rootPath = input.required<string>();

  readonly offers: Offer[] = [
    {
      kind: 'packages',
      title: 'packages.config',
      what: 'Moves package declarations into the project file.',
    },
    {
      kind: 'sdk',
      title: 'SDK format',
      what: 'Rewrites a pre-SDK project file in the modern format.',
    },
    {
      kind: 'versions',
      title: 'Versions',
      what: 'Brings each package to one version across the solution.',
    },
    {
      kind: 'config',
      title: 'Configuration',
      what: 'Carries appSettings and connectionStrings into appsettings.json.',
    },
  ];

  readonly chosen = signal<ConversionKind | null>(null);
  readonly outcome = signal<ConversionOutcome | null>(null);
  readonly loading = signal(false);
  readonly failure = signal<string | null>(null);
  readonly showingAll = signal(false);

  readonly landed = signal<Landed | null>(null);
  readonly applying = signal(false);
  readonly applyFailure = signal<string | null>(null);

  /**
   * How much of a patch is rendered before it is offered as a file instead.
   *
   * A real conversion runs to tens of thousands of lines, and putting that in
   * the DOM makes the page unusable to show something nobody reads in a
   * browser anyway.
   */
  private static readonly Preview = 300;

  private asked: string | null = null;
  private alive = true;

  /**
   * The first note, which is always the count.
   *
   * The rest are per-project and there are dozens of them on a real estate.
   * Listed flat they push the patch and the refusals off the bottom of the
   * screen, so the summary stays out and the detail folds away.
   */
  readonly summary = computed(() => this.outcome()?.notes[0] ?? null);

  readonly detail = computed(() => this.outcome()?.notes.slice(1) ?? []);

  readonly lines = computed(() => {
    const patch = this.outcome()?.patch ?? '';
    if (patch.length === 0) return [];

    const all = patch.replace(/\n$/, '').split('\n');
    return this.showingAll() ? all : all.slice(0, Conversions.Preview);
  });

  readonly truncated = computed(() => {
    const patch = this.outcome()?.patch ?? '';
    if (patch.length === 0 || this.showingAll()) return 0;

    const total = patch.replace(/\n$/, '').split('\n').length;
    return Math.max(0, total - Conversions.Preview);
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.alive = false;
    });

    // Changing project drops whatever was on screen: a patch belongs to the
    // folder it was computed from, and showing it beside another one invites
    // somebody to apply it there.
    effect(() => {
      const path = this.rootPath();
      if (path === this.asked) return;

      this.asked = path;
      this.chosen.set(null);
      this.outcome.set(null);
      this.failure.set(null);
      this.clearLanding();
    });
  }

  choose(kind: ConversionKind): void {
    if (this.chosen() === kind) {
      this.chosen.set(null);
      this.outcome.set(null);
      return;
    }

    const path = this.rootPath();
    if (!path) return;

    this.chosen.set(kind);
    this.outcome.set(null);
    this.failure.set(null);
    this.showingAll.set(false);
    this.clearLanding();
    this.loading.set(true);

    this.lens.convert(path, kind).subscribe({
      next: (outcome) => {
        // A slower answer for a kind the reader has since moved off must not
        // land on top of the one now on screen.
        if (!this.alive || this.chosen() !== kind) return;
        this.outcome.set(outcome);
        this.loading.set(false);
      },
      error: (error: Error) => {
        if (!this.alive || this.chosen() !== kind) return;
        this.failure.set(error.message);
        this.loading.set(false);
      },
    });
  }

  showAll(): void {
    this.showingAll.set(true);
  }

  /**
   * Puts this conversion on a branch of its own.
   *
   * The button is not the risk. The rule has always been that a person
   * approves the diff, and clicking after reading it is a person approving.
   * What would break the rule is writing into the working tree, and this does
   * not: it commits to a new branch and leaves you where you were.
   */
  apply(): void {
    const kind = this.chosen();
    const path = this.rootPath();
    if (!kind || !path || this.applying()) return;

    this.clearLanding();
    this.applying.set(true);

    this.lens.apply(path, kind).subscribe({
      next: (landed) => {
        if (!this.alive) return;
        this.landed.set(landed);
        this.applying.set(false);
      },
      error: (error: Error) => {
        if (!this.alive) return;
        this.applyFailure.set(error.message);
        this.applying.set(false);
      },
    });
  }

  private clearLanding(): void {
    this.landed.set(null);
    this.applying.set(false);
    this.applyFailure.set(null);
  }

  /** Which side of the diff a line is, so the markup can say so once. */
  side(line: string): 'added' | 'removed' | 'file' | 'hunk' | 'context' {
    if (line.startsWith('diff --git') || line.startsWith('---') || line.startsWith('+++')) {
      return 'file';
    }
    if (line.startsWith('@@')) return 'hunk';
    if (line.startsWith('+')) return 'added';
    if (line.startsWith('-')) return 'removed';
    return 'context';
  }

  /** Saves the patch, because a patch is meant to reach `git apply`. */
  download(): void {
    const outcome = this.outcome();
    if (!outcome || outcome.empty) return;

    const url = URL.createObjectURL(new Blob([outcome.patch], { type: 'text/x-patch' }));
    const link = document.createElement('a');

    link.href = url;
    link.download = `${outcome.kind}.patch`;
    link.click();

    URL.revokeObjectURL(url);
  }

  /** The command that produces exactly this, for anyone who would rather script it. */
  command(): string {
    return `convert ${this.rootPath()} ${this.chosen()} > ${this.chosen()}.patch`;
  }
}
