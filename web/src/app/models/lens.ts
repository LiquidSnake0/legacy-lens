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
  history: { status: string; note: string | null; window: string | null };
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

/* ---- what holds a codebase back, and what could replace it ---- */

export interface ApiUse {
  name: string;
  uses: number;
  files: number;
}

export interface FileUse {
  path: string;
  uses: number;
}

/**
 * One candidate replacement, scored against what the codebase uses.
 *
 * `unknown` is not `unavailable`. One means the catalogue says nothing, the
 * other means it says there is nothing. Folding them together is how silence
 * becomes success.
 */
export interface Candidate {
  candidate: string;
  note: string;
  percent: number;
  blocked: boolean;
  covered: number;
  unavailable: ApiUse[];
  unknown: ApiUse[];
  unknownCount: number;
  usesCovered: number;
  usesUnavailable: number;
  usesUnknown: number;
  /**
   * What the framework being migrated to says about the unknown column.
   *
   * Its own field rather than folded into the counts above, because it is read
   * from metadata and those are written by hand. A reader has to be able to see
   * which is which.
   */
  unlisted: Unlisted;
}

/** One group of types the framework gave the same answer about. */
export interface UnlistedGroup {
  types: { name: string; uses: number; where: string | null }[];
  count: number;
  uses: number;
}

/**
 * The unknown column, read against the target framework.
 *
 * Three answers and only one of them is a lead. `elsewhere` is the dangerous
 * one: a name that survived into an unrelated namespace is a trap, not an
 * answer, and it is labelled one. Names the framework still supplies itself
 * never reach here: the surface stops attributing those to the package.
 */
export interface Unlisted {
  /**
   * False when the successor is a package rather than part of the framework.
   *
   * log4net's answer is Serilog, which nothing in the runtime carries, so every
   * type of every predecessor comes back absent. Literally true, and it tells
   * nobody anything.
   */
  applicable: boolean;
  inSuccessor: UnlistedGroup;
  elsewhere: UnlistedGroup;
  gone: UnlistedGroup;
  /** What is left to decide once the noise is out. */
  left: number;
}

export interface PackageSurface {
  package: string;
  uses: number;
  files: number;
  /** How many types carry four fifths of the calls. The number that sizes the work. */
  typesForMostOfIt: number;
  filesForMostOfIt: number;
  types: ApiUse[];
  /** The files leaning on it hardest. The first is the projection worth making. */
  heaviest: FileUse[];
  notes: string[];
  candidates: Candidate[];
}

export interface SurfaceReport {
  catalogue: string;
  packages: PackageSurface[];
}

/** Where a conversion landed, once somebody pressed the button. */
export interface Landed {
  branch: string;
  commit: string;
  files: number;
  /** How to read it, keep it, or drop it. */
  notes: string[];
}

/** One file rewritten, and what the compiler said about it. */
export interface Projection {
  path: string;
  package: string;
  before: string;
  after: string;
  compiles: boolean;
  /** Whether anything was invented. The question that survives a real file. */
  sound: boolean;
  claim: string;
  target: string;
  invented: string[];
  fromProject: string[];
  unimported: string[];
  attempts: number;
  given: string[];
  notes: string[];
  /**
   * What both versions did when called with the same values.
   *
   * Null when the server was not allowed to run code, which is the default.
   * The reason is in `notes` either way, so a reader is never left wondering
   * whether nothing moved or nothing was tried.
   */
  behaviour: Behaviour | null;
  /** Why there is none, when there is none. */
  behaviourRefusal: string | null;
}

/** One call that did not do the same thing in both versions. */
export interface Divergence {
  arguments: string;
  before: string;
  after: string;
}

/** One method, called the same way in both versions. */
export interface ComparedMethod {
  type: string;
  method: string;
  signature: string;
  cases: number;
  matched: boolean;
  /** Something true about the pair that is not a divergence, such as a changed return type. */
  note: string | null;
  divergences: Divergence[];
}

/** Why a method was passed over, and how many were. */
export interface Refusal {
  reason: string;
  count: number;
  explanation: string;
}

/**
 * What was compared, what moved, and what was never looked at.
 *
 * The third of those is the one that has to be read. A report saying eleven
 * methods matched, with nothing about the forty passed over, is the sentence
 * that gets a rewrite signed off.
 */
export interface Behaviour {
  ran: boolean;
  /** True only when something was compared and none of it moved. */
  verified: boolean;
  claim: string;
  cases: number;
  moved: number;
  methods: ComparedMethod[];
  refusals: Refusal[];
  beforeErrors: string[];
  afterErrors: string[];
  elapsedMs: number;
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

/** One of the finitely many places a dilemma can end. */
export interface Outcome {
  id: string;
  name: string;
  note: string;
}

/** An answer, and what choosing it rules out. */
export interface Choice {
  answer: string;
  eliminates: string[];
  because: string;
}

/** Something the code cannot say, asked once. */
export interface Question {
  id: string;
  ask: string;
  why: string;
  choices: Choice[];
}

/** One answer somebody gave. */
export interface Answered {
  questionId: string;
  answer: string;
}

/** A line of code that raised the question. */
export interface Site {
  path: string;
  line: number;
  name: string;
  text: string;
}

/**
 * A decision in progress.
 *
 * Everything derived comes from the server, which computes it from the answers
 * every time. The alternative was to fold in the browser, which means two
 * implementations of the same rule and one of them eventually wrong.
 */
export interface DiagnosisState {
  id: string;
  name: string;
  what: string;
  answers: Answered[];
  remaining: Outcome[];
  outcomes: number;
  next: Question | null;
  settled: boolean;
  reasoning: string[];
  /** The one outcome left, when exactly one is. */
  landed: Outcome | null;
  /** Every outcome ruled out, which is an answer of a different kind. */
  exhausted: boolean;
}

/** A dilemma this codebase raises, and where. */
export interface RaisedDilemma {
  diagnosis: DiagnosisState;
  files: number;
  mentions: number;
  sites: Site[];
}

export interface DiagnoseReport {
  catalogue: string;
  workspace: string;
  dilemmas: RaisedDilemma[];
}
