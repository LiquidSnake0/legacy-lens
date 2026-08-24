/**
 * Mirrors the records returned by the API.
 *
 * Kept in one file rather than scattered next to the components that use them:
 * these are the contract with the backend, and when it changes there should be
 * exactly one place to update.
 */

export interface Citation {
  filePath: string;
  startLine: number;
  endLine: number;
  score: number;
  /**
   * Which search retrieved this chunk: 'vector', 'text' or 'both'.
   * A text-only match has no cosine score, so none is shown for it.
   */
  foundBy: 'vector' | 'text' | 'both';
}

export interface AskResponse {
  answer: string;
  sources: Citation[];
}

/** One project, indexed on its own. */
export interface Workspace {
  id: string;
  name: string;
  rootPath: string;
  createdAt: string;
  chunks: number;
}

export interface Health {
  status: string;
  indexedChunks: number;
  /** Counted per project. One number over three of them answers nothing. */
  workspaces: { id: string; name: string; chunks: number }[];
}

/**
 * What an indexing run is doing, or what it did.
 *
 * Embedding runs at roughly two chunks a second, so this is polled for
 * minutes or hours rather than watched for a moment.
 */
export interface IngestionJob {
  workspace: string;
  rootPath: string;
  /** cloning, running, done, failed or cancelled. */
  state: 'cloning' | 'running' | 'done' | 'failed' | 'cancelled';
  filesTotal: number;
  filesDone: number;
  chunksIndexed: number;
  currentFile: string | null;
  startedAt: string;
  finishedAt: string | null;
  error: string | null;
  running: boolean;
  /** Extrapolated, and null until there is anything to extrapolate from. */
  estimatedSecondsLeft: number | null;
}

/** Which model answers, and with whose key. */
export interface ModelChoice {
  provider: 'local' | 'hosted';
  model?: string;
  /** Sent with the question and never stored, here or at the API. */
  apiKey?: string;
}

export interface ModelOptions {
  local: { model: string; description: string };
  hosted: {
    available: boolean;
    url: string;
    model: string;
    description: string;
    warning: string;
  };
  embeddings: string;
}

/**
 * One file in the risk ranking.
 *
 * This is the half that needs no model and no index: it reads a directory and
 * answers in seconds, which is why it is shown while embedding is still
 * running.
 */
export interface RiskEntry {
  path: string;
  score: number;
  complexity: number;
  worstMethodComplexity: number;
  worstMethod: string | null;
  maxNesting: number;
  codeLines: number;
  commits: number;
  authors: number;
  tested: boolean;
  reasons: string[];
}

export interface RiskReport {
  history: { status: string; note: string | null };
  generatedFilesExcluded: number;
  ranked: number;
  entries: RiskEntry[];
}

export interface IngestResponse {
  filesRead: number;
  chunksIndexed: number;
  elapsedMs: number;
}

/** The mechanical conversions, one at a time. */
export type ConversionKind = 'packages' | 'sdk' | 'versions' | 'config';

/**
 * One conversion's result.
 *
 * The patch is never applied by anything here. It is text to read, and to hand
 * to `git apply` if the reader decides to.
 */
export interface ConversionOutcome {
  kind: ConversionKind;
  patch: string;
  /** What the patch does, and what it does not handle. */
  notes: string[];
  /** What was refused, with the reason. Usually the longer list, and the point. */
  refusals: string[];
  empty: boolean;
}

/** What the API answers with when the model cannot be reached. */
export interface ApiError {
  error: string;
  hint?: string;
  detail?: string;
}

/** The text of one indexed chunk, as it was when it was indexed. */
export interface Excerpt {
  filePath: string;
  startLine: number;
  endLine: number;
  content: string;
}
