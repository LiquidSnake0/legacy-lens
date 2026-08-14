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

export interface Health {
  status: string;
  indexedChunks: number;
}

export interface IngestResponse {
  filesRead: number;
  chunksIndexed: number;
  elapsedMs: number;
}

/** What the API answers with when the model cannot be reached. */
export interface ApiError {
  error: string;
  hint?: string;
  detail?: string;
}
