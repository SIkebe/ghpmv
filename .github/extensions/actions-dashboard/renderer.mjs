export const dashboardHtml = `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>GitHub Actions Dashboard</title>
  <link rel="stylesheet" href="/styles.css">
</head>
<body>
  <main>
    <header class="page-header">
      <div>
        <p class="eyebrow">GitHub Actions · CI health</p>
        <h1 id="repository-name">Loading repository...</h1>
        <p id="updated-at" class="muted">Building the health baseline</p>
      </div>
      <button id="refresh" class="primary-button" type="button">
        <span class="refresh-icon" aria-hidden="true">↻</span>
        Refresh
      </button>
    </header>

    <div id="error" class="error-banner" role="alert" hidden></div>
    <section id="summary" class="summary-grid" aria-label="CI health summary"></section>

    <div class="health-layout">
      <section class="panel trend-panel">
        <div class="panel-header">
          <div>
            <h2>Weekly reliability trend</h2>
            <p class="muted">Final success rate for completed runs</p>
          </div>
        </div>
        <div id="trend" class="trend-chart" aria-label="Weekly success rate"></div>
      </section>

      <section class="panel">
        <div class="panel-header">
          <div>
            <h2>Window comparison</h2>
            <p class="muted">Recent change versus the 90-day baseline</p>
          </div>
        </div>
        <div class="table-wrap compact-table">
          <table>
            <thead>
              <tr><th>Window</th><th>Success</th><th>No rerun</th><th>Failures</th></tr>
            </thead>
            <tbody id="windows"></tbody>
          </table>
        </div>
      </section>
    </div>

    <section class="panel" id="pr-panel">
      <div class="panel-header">
        <div>
          <h2>Current PR readiness</h2>
          <p class="muted">Secondary guardrail for the checked-out branch</p>
        </div>
      </div>
      <div id="pr-readiness" class="pr-readiness"></div>
    </section>

    <section class="panel">
      <div class="panel-header">
        <div>
          <h2>Workflow reliability</h2>
          <p class="muted">90-day health, ordered by weakest no-rerun success rate</p>
        </div>
      </div>
      <div id="workflows" class="workflow-grid"></div>
    </section>

    <section class="panel runs-panel">
      <div class="panel-header runs-header">
        <div>
          <h2>Recent runs</h2>
          <p id="run-count" class="muted"></p>
        </div>
        <div class="filters">
          <label><span>Search</span><input id="search-filter" type="search" placeholder="Title or branch"></label>
          <label>
            <span>Status</span>
            <select id="status-filter">
              <option value="all">All statuses</option>
              <option value="running">Running</option>
              <option value="success">Success</option>
              <option value="failed">Failed</option>
              <option value="neutral">Neutral</option>
            </select>
          </label>
          <label>
            <span>Workflow</span>
            <select id="workflow-filter"><option value="all">All workflows</option></select>
          </label>
        </div>
      </div>
      <div class="table-wrap">
        <table>
          <thead>
            <tr><th>Run</th><th>Workflow</th><th>Branch</th><th>Status</th><th>Started</th><th>Queue</th><th>Runtime</th></tr>
          </thead>
          <tbody id="runs"></tbody>
        </table>
        <div id="empty-state" class="empty-state" hidden>No runs match these filters.</div>
        <div class="run-footer"><button id="load-more" class="secondary-button" type="button" hidden>Load more</button></div>
      </div>
    </section>
  </main>
  <script src="/app.js"></script>
</body>
</html>`;

export const dashboardStyles = `
:root {
  --success: #1a7f37;
  --success-muted: #dafbe1;
  --danger: var(--true-color-red, #cf222e);
  --danger-muted: var(--true-color-red-muted, #ffebe9);
  --running: var(--true-color-blue, #0969da);
  --running-muted: var(--true-color-blue-muted, #ddf4ff);
  --neutral: #57606a;
  --neutral-muted: #f6f8fa;
  color-scheme: light dark;
}
[data-color-mode="dark"] {
  --success: #3fb950;
  --success-muted: #12261a;
  --neutral: #8c959f;
  --neutral-muted: #20252c;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  background: var(--background-color-default, #fff);
  color: var(--text-color-default, #1f2328);
  font-family: var(--font-sans, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif);
  font-size: var(--text-body-medium, 14px);
  line-height: var(--leading-body-medium, 20px);
}
main { margin: 0 auto; max-width: 1500px; padding: 28px; }
h1, h2, h3, p { margin: 0; }
h1 {
  font-family: var(--font-sans-display, var(--font-sans, sans-serif));
  font-size: var(--text-title-large, 26px);
  font-weight: var(--font-weight-semibold, 600);
  line-height: var(--leading-title-large, 32px);
}
h2 { font-size: 18px; font-weight: var(--font-weight-semibold, 600); line-height: 24px; }
.page-header, .panel-header {
  align-items: flex-end;
  display: flex;
  gap: 24px;
  justify-content: space-between;
}
.page-header { margin-bottom: 24px; }
.eyebrow {
  color: var(--text-color-muted, #656d76);
  font-size: 12px;
  font-weight: var(--font-weight-semibold, 600);
  letter-spacing: .08em;
  margin-bottom: 4px;
  text-transform: uppercase;
}
.muted { color: var(--text-color-muted, #656d76); margin-top: 3px; }
.sr-only {
  clip: rect(0, 0, 0, 0);
  clip-path: inset(50%);
  height: 1px;
  overflow: hidden;
  position: absolute;
  white-space: nowrap;
  width: 1px;
}
button, input, select { font: inherit; }
button, select { cursor: pointer; }
.primary-button {
  align-items: center;
  background: var(--running);
  border: 1px solid transparent;
  border-radius: 8px;
  color: var(--color-white, #fff);
  display: inline-flex;
  font-weight: var(--font-weight-semibold, 600);
  gap: 7px;
  padding: 8px 14px;
}
.primary-button:hover { filter: brightness(.92); }
.primary-button:disabled { cursor: wait; opacity: .65; }
.primary-button:focus-visible, input:focus-visible, select:focus-visible, a:focus-visible {
  outline: 2px solid var(--color-focus-outline, #0969da);
  outline-offset: 2px;
}
.primary-button.loading .refresh-icon { animation: spin .8s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
.error-banner {
  background: var(--danger-muted);
  border: 1px solid var(--danger);
  border-radius: 8px;
  color: var(--danger);
  margin-bottom: 20px;
  padding: 10px 14px;
}
.summary-grid {
  display: grid;
  gap: 12px;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  margin-bottom: 20px;
}
.summary-card, .panel, .workflow-card {
  background: var(--background-color-default, #fff);
  border: 1px solid var(--border-color-default, #d0d7de);
  border-radius: 10px;
}
.summary-card { border-top: 3px solid var(--accent, var(--border-color-default, #d0d7de)); padding: 15px; }
.summary-card .label {
  color: var(--text-color-muted, #656d76);
  display: block;
  font-size: 11px;
  font-weight: var(--font-weight-semibold, 600);
  text-transform: uppercase;
}
.summary-card .value { display: block; font-size: 25px; font-weight: var(--font-weight-semibold, 600); line-height: 32px; margin-top: 4px; }
.summary-card .detail { color: var(--text-color-muted, #656d76); display: block; font-size: 11px; margin-top: 2px; }
.summary-card.good { --accent: var(--success); }
.summary-card.bad { --accent: var(--danger); }
.summary-card.info { --accent: var(--running); }
.health-layout { display: grid; gap: 20px; grid-template-columns: 2fr 1fr; }
.panel { margin-bottom: 20px; overflow: hidden; }
.panel > .panel-header { padding: 18px 20px; }
.trend-chart {
  align-items: stretch;
  border-top: 1px solid var(--border-color-default, #d0d7de);
  display: flex;
  gap: 8px;
  height: 190px;
  padding: 20px 20px 12px;
}
.trend-column { align-items: center; display: flex; flex: 1; flex-direction: column; gap: 5px; min-width: 24px; }
.trend-value { font-size: 10px; font-weight: var(--font-weight-semibold, 600); height: 16px; white-space: nowrap; }
.trend-track { background: var(--neutral-muted); border-radius: 5px; display: flex; flex: 1; flex-direction: column-reverse; overflow: hidden; width: min(34px, 100%); }
.trend-success { background: var(--success); width: 100%; }
.trend-failure { background: var(--danger); width: 100%; }
.trend-label { color: var(--text-color-muted, #656d76); font-size: 9px; white-space: nowrap; }
.workflow-grid {
  border-top: 1px solid var(--border-color-default, #d0d7de);
  display: grid;
  gap: 12px;
  grid-template-columns: repeat(auto-fit, minmax(245px, 1fr));
  padding: 16px 20px 20px;
}
.workflow-card { padding: 14px; }
.workflow-title { align-items: center; display: flex; font-weight: var(--font-weight-semibold, 600); gap: 8px; justify-content: space-between; }
.workflow-rate { font-size: 24px; font-weight: var(--font-weight-semibold, 600); line-height: 30px; margin-top: 10px; }
.workflow-details { color: var(--text-color-muted, #656d76); display: grid; font-size: 12px; gap: 3px; grid-template-columns: 1fr 1fr; margin-top: 8px; }
.progress-track { background: var(--neutral-muted); border-radius: 999px; height: 6px; margin-top: 10px; overflow: hidden; }
.progress-bar { background: var(--success); height: 100%; min-width: 2px; }
.status-dot {
  align-items: center;
  background: var(--neutral);
  border-radius: 999px;
  color: var(--color-white, #fff);
  display: inline-flex;
  flex: 0 0 auto;
  font-size: 10px;
  font-weight: 700;
  height: 16px;
  justify-content: center;
  line-height: 16px;
  width: 16px;
}
.status-dot.success { background: var(--success); }
.status-dot.failed { background: var(--danger); }
.status-dot.running { background: var(--running); }
.pr-readiness {
  border-top: 1px solid var(--border-color-default, #d0d7de);
  display: grid;
  gap: 16px;
  grid-template-columns: minmax(220px, 1fr) 2fr;
  padding: 18px 20px;
}
.pr-title { color: var(--text-color-default, #1f2328); font-size: 16px; font-weight: var(--font-weight-semibold, 600); text-decoration: none; }
.pr-title:hover, .run-title-button:hover { color: var(--running); text-decoration: underline; }
.readiness-counts { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
.check-list { display: grid; gap: 7px; }
.check-row { align-items: center; background: var(--neutral-muted); border-radius: 7px; display: flex; gap: 8px; justify-content: space-between; padding: 7px 10px; }
.check-row a { color: var(--text-color-default, #1f2328); overflow: hidden; text-decoration: none; text-overflow: ellipsis; white-space: nowrap; }
.check-state { color: var(--text-color-muted, #656d76); font-family: var(--font-mono, monospace); font-size: 11px; }
.filters { align-items: flex-end; display: flex; flex-wrap: wrap; gap: 10px; }
.filters label { color: var(--text-color-muted, #656d76); display: grid; font-size: 11px; font-weight: var(--font-weight-semibold, 600); gap: 4px; }
input, select {
  background: var(--background-color-default, #fff);
  border: 1px solid var(--border-color-default, #d0d7de);
  border-radius: 7px;
  color: var(--text-color-default, #1f2328);
  min-height: 34px;
  padding: 6px 9px;
}
input { min-width: 190px; }
.table-wrap { border-top: 1px solid var(--border-color-default, #d0d7de); overflow-x: auto; }
table { border-collapse: collapse; min-width: 900px; width: 100%; }
.compact-table table { min-width: 420px; }
th, td { border-bottom: 1px solid var(--border-color-default, #d8dee4); padding: 10px 14px; text-align: left; vertical-align: middle; }
th { color: var(--text-color-muted, #656d76); font-size: 11px; font-weight: var(--font-weight-semibold, 600); letter-spacing: .03em; text-transform: uppercase; }
tbody tr:last-child td { border-bottom: 0; }
tbody .run-row:hover { background: var(--neutral-muted); }
.run-row { cursor: pointer; }
.run-title-button {
  appearance: none;
  background: transparent;
  border: 0;
  color: var(--text-color-default, #1f2328);
  cursor: pointer;
  display: block;
  font: inherit;
  font-weight: var(--font-weight-semibold, 600);
  margin: 0;
  max-width: 390px;
  overflow: hidden;
  padding: 0;
  text-align: left;
  text-decoration: none;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.run-detail-row > td { background: var(--neutral-muted); padding: 0; }
.run-detail {
  border-bottom: 1px solid var(--border-color-default, #d8dee4);
  padding: 16px 20px 20px;
}
.run-detail-header { align-items: center; display: flex; gap: 16px; justify-content: space-between; margin-bottom: 12px; }
.run-detail-header a { color: var(--running); font-size: 12px; text-decoration: none; white-space: nowrap; }
.run-detail-header a:hover { text-decoration: underline; }
.run-metadata {
  color: var(--text-color-muted, #656d76);
  display: flex;
  flex-wrap: wrap;
  font-size: 12px;
  gap: 7px 16px;
  margin-bottom: 12px;
}
.run-metadata strong { color: var(--text-color-default, #1f2328); font-weight: var(--font-weight-semibold, 600); }
.job-list { display: grid; gap: 9px; }
.job-card { background: var(--background-color-default, #fff); border: 1px solid var(--border-color-default, #d0d7de); border-radius: 8px; overflow: hidden; }
.job-card.failed { border-color: var(--danger); }
.job-card summary { align-items: center; cursor: pointer; display: flex; gap: 9px; list-style: none; padding: 10px 12px; }
.job-card summary::-webkit-details-marker { display: none; }
.job-name { flex: 1; font-weight: var(--font-weight-semibold, 600); }
.step-list { border-top: 1px solid var(--border-color-default, #d8dee4); display: grid; }
.step-row { align-items: center; display: grid; gap: 9px; grid-template-columns: 12px minmax(0, 1fr) auto; padding: 7px 12px; }
.step-row + .step-row { border-top: 1px solid var(--border-color-default, #eaeef2); }
.step-row.failed {
  background: var(--danger-muted);
  border-left: 4px solid var(--danger);
  color: var(--danger);
  font-weight: var(--font-weight-semibold, 600);
  padding-left: 8px;
}
.step-result { align-items: center; display: inline-flex; gap: 8px; }
.failure-summary {
  background: var(--danger-muted);
  border: 1px solid var(--danger);
  border-radius: 8px;
  color: var(--danger);
  display: grid;
  gap: 5px;
  margin-bottom: 12px;
  padding: 10px 12px;
}
.failure-summary strong { font-weight: var(--font-weight-semibold, 600); }
.failure-summary span { color: var(--text-color-default, #1f2328); }
.run-detail-loading, .run-detail-error { color: var(--text-color-muted, #656d76); padding: 8px 0; }
.run-detail-error { color: var(--danger); }
.failure-panel { border-top: 1px solid var(--border-color-default, #d0d7de); margin-top: 16px; padding-top: 16px; }
.failure-header { align-items: center; display: flex; gap: 12px; justify-content: space-between; margin-bottom: 12px; }
.danger-button {
  background: var(--danger);
  border: 1px solid var(--danger);
  border-radius: 7px;
  color: var(--color-white, #fff);
  cursor: pointer;
  font: inherit;
  font-size: 12px;
  font-weight: var(--font-weight-semibold, 600);
  padding: 6px 10px;
}
.danger-button:disabled { cursor: wait; opacity: .65; }
.secondary-button {
  background: var(--background-color-default, #fff);
  border: 1px solid var(--border-color-default, #d0d7de);
  border-radius: 7px;
  color: var(--text-color-default, #1f2328);
  cursor: pointer;
  font: inherit;
  font-weight: var(--font-weight-semibold, 600);
  padding: 7px 12px;
}
.run-footer { display: flex; justify-content: center; padding: 12px; }
.run-footer:has(.secondary-button[hidden]) { display: none; }
.diagnostic-warning {
  background: var(--danger-muted);
  border: 1px solid var(--danger);
  border-radius: 7px;
  color: var(--danger);
  margin-bottom: 10px;
  padding: 8px 10px;
  white-space: pre-wrap;
}
.diagnostic-grid { display: grid; gap: 10px; grid-template-columns: repeat(2, minmax(0, 1fr)); }
.diagnostic-card {
  background: var(--background-color-default, #fff);
  border: 1px solid var(--border-color-default, #d0d7de);
  border-radius: 8px;
  overflow: hidden;
}
.diagnostic-card > h3, .diagnostic-card > summary { font-size: 13px; font-weight: var(--font-weight-semibold, 600); padding: 9px 11px; }
.diagnostic-card > summary { cursor: pointer; }
.diagnostic-list { border-top: 1px solid var(--border-color-default, #d8dee4); display: grid; }
.diagnostic-item { padding: 8px 11px; }
.diagnostic-item + .diagnostic-item { border-top: 1px solid var(--border-color-default, #eaeef2); }
.diagnostic-item .location { color: var(--text-color-muted, #656d76); display: block; font-family: var(--font-mono, monospace); font-size: 11px; margin-top: 3px; }
.log-card { grid-column: 1 / -1; }
.log-excerpt {
  background: var(--neutral-muted);
  border: 0;
  border-top: 1px solid var(--border-color-default, #d8dee4);
  color: var(--text-color-default, #1f2328);
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  line-height: 17px;
  margin: 0;
  max-height: 380px;
  overflow: auto;
  padding: 11px;
  white-space: pre-wrap;
}
.rerun-status { color: var(--text-color-muted, #656d76); font-size: 12px; margin-left: auto; }
.mono { color: var(--text-color-muted, #656d76); font-family: var(--font-mono, monospace); font-size: 12px; }
.badge {
  align-items: center;
  background: var(--neutral-muted);
  border-radius: 999px;
  color: var(--neutral);
  display: inline-flex;
  font-size: 12px;
  font-weight: var(--font-weight-semibold, 600);
  gap: 6px;
  padding: 3px 8px;
  text-transform: capitalize;
}
.badge.success { background: var(--success-muted); color: var(--success); }
.badge.failed { background: var(--danger-muted); color: var(--danger); }
.badge.running { background: var(--running-muted); color: var(--running); }
.empty-state { color: var(--text-color-muted, #656d76); padding: 36px 20px; text-align: center; }
@media (max-width: 1100px) {
  .summary-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
  .health-layout { grid-template-columns: 1fr; }
}
@media (max-width: 760px) {
  main { padding: 18px; }
  .page-header, .panel-header, .pr-readiness { align-items: flex-start; flex-direction: column; }
  .pr-readiness { display: flex; }
  .summary-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .diagnostic-grid { grid-template-columns: 1fr; }
  .log-card { grid-column: auto; }
  .filters, .filters label, input, select { width: 100%; }
}
`;

export const appJavaScript = `
const elements = {
  emptyState: document.querySelector("#empty-state"),
  error: document.querySelector("#error"),
  loadMore: document.querySelector("#load-more"),
  prReadiness: document.querySelector("#pr-readiness"),
  refresh: document.querySelector("#refresh"),
  repositoryName: document.querySelector("#repository-name"),
  runCount: document.querySelector("#run-count"),
  runs: document.querySelector("#runs"),
  searchFilter: document.querySelector("#search-filter"),
  statusFilter: document.querySelector("#status-filter"),
  summary: document.querySelector("#summary"),
  trend: document.querySelector("#trend"),
  updatedAt: document.querySelector("#updated-at"),
  windows: document.querySelector("#windows"),
  workflowFilter: document.querySelector("#workflow-filter"),
  workflows: document.querySelector("#workflows"),
};
let dashboard = null;
const runDetailCache = new Map();
const failureDiagnosticCache = new Map();
const expandedRunIds = new Set();
let recentUpdatedAt = null;
let mutationToken = null;
let cacheTimestamp = null;
let filteredRunLimit = 100;
const FILTER_PAGE_SIZE = 100;
let pendingRunFocus = null;

function node(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}
function percent(value) { return value === null ? "—" : value.toFixed(1) + "%"; }
function formatMs(value) {
  if (value === null) return "—";
  const seconds = Math.round(value / 1000);
  if (seconds < 60) return seconds + "s";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return minutes + "m " + (seconds % 60) + "s";
  return Math.floor(minutes / 60) + "h " + (minutes % 60) + "m";
}
function formatBytes(value) {
  if (!Number.isFinite(value)) return "—";
  if (value < 1024) return value + " B";
  if (value < 1024 * 1024) return (value / 1024).toFixed(1) + " KB";
  return (value / (1024 * 1024)).toFixed(1) + " MB";
}
function relativeTime(value) {
  const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  const ranges = [["year",31536000],["month",2592000],["week",604800],["day",86400],["hour",3600],["minute",60]];
  for (const [unit, size] of ranges) if (Math.abs(seconds) >= size) return formatter.format(Math.round(seconds / size), unit);
  return formatter.format(seconds, "second");
}
function statusKind(run) {
  if (run.status !== "completed") return "running";
  if (run.conclusion === "success") return "success";
  if (run.conclusion === "neutral" || run.conclusion === "skipped") return "neutral";
  return "failed";
}
function statusLabel(run) {
  return (run.status !== "completed" ? run.status : run.conclusion || "completed").replaceAll("_", " ");
}
function statusDot(kind) {
  const symbols = { success: "✓", failed: "×", running: "↻", neutral: "−" };
  const icon = node("span", "status-dot " + kind, symbols[kind] || "−");
  icon.setAttribute("aria-hidden", "true");
  return icon;
}

function captureRunFocus() {
  const active = document.activeElement;
  if (!(active instanceof Element) || !active.dataset.focusKey) return null;
  const owner = active.closest("[data-run-id]");
  return owner
    ? { focusKey: active.dataset.focusKey, runId: owner.dataset.runId }
    : null;
}

function restoreRunFocus(runId) {
  if (!pendingRunFocus || pendingRunFocus.runId !== String(runId)) return;
  const owners = document.querySelectorAll('[data-run-id="' + runId + '"]');
  const target = Array.from(owners)
    .map((owner) => owner.querySelector('[data-focus-key="' + pendingRunFocus.focusKey + '"]'))
    .find(Boolean);
  if (target) {
    pendingRunFocus = null;
    target.focus();
  }
}

function renderSummary(metrics, defaultBranch, defaultBranchName) {
  const cards = [
    ["Success rate", percent(metrics.successRate), metrics.success + " successful", "good"],
    ["No-rerun success", percent(metrics.noRerunSuccessRate), metrics.successfulReruns + " successful reruns", "good"],
    ["Default branch", percent(defaultBranch.successRate), defaultBranch.total + " runs on " + (defaultBranchName || "default"), "good"],
    ["Failures", String(metrics.failed), metrics.active + " currently active", metrics.failed ? "bad" : "good"],
    ["P95 queue", formatMs(metrics.queueP95Ms), "P50 " + formatMs(metrics.queueP50Ms), "info"],
    ["P95 runtime", formatMs(metrics.runtimeP95Ms), "P50 " + formatMs(metrics.runtimeP50Ms), "info"],
  ];
  elements.summary.replaceChildren(...cards.map(([label, value, detail, kind]) => {
    const card = node("article", "summary-card " + kind);
    card.append(node("span", "label", label), node("span", "value", value), node("span", "detail", detail));
    return card;
  }));
}

function renderWindows(windows) {
  elements.windows.replaceChildren(...[7, 30, 90].map((days) => {
    const metrics = windows[String(days)];
    const row = document.createElement("tr");
    row.append(
      node("td", null, days + " days"),
      node("td", null, percent(metrics.successRate)),
      node("td", null, percent(metrics.noRerunSuccessRate)),
      node("td", null, String(metrics.failed)),
    );
    return row;
  }));
}

function renderTrend(trend) {
  elements.trend.replaceChildren(...trend.map((bucket) => {
    const column = node("div", "trend-column");
    const value = node("span", "trend-value", percent(bucket.successRate));
    const track = node("div", "trend-track");
    if (bucket.total) {
      const success = node("div", "trend-success");
      success.style.height = bucket.successRate + "%";
      const failure = node("div", "trend-failure");
      failure.style.height = (100 - bucket.successRate) + "%";
      track.append(success, failure);
    }
    track.title = bucket.total + " completed runs · " + bucket.failed + " failed";
    column.append(value, track, node("span", "trend-label", bucket.label));
    return column;
  }));
}

function renderWorkflows(workflows) {
  elements.workflows.replaceChildren(...workflows.map((workflow) => {
    const card = node("article", "workflow-card");
    const title = node("div", "workflow-title");
    const kind = !workflow.latestStatus
      ? "neutral"
      : statusKind({ status: workflow.latestStatus, conclusion: workflow.latestConclusion });
    const latestStatus = !workflow.latestStatus
      ? "no recent runs"
      : workflow.latestStatus !== "completed"
        ? workflow.latestStatus
        : workflow.latestConclusion;
    title.append(
      node("span", null, workflow.name),
      statusDot(kind),
      node("span", "sr-only", "Latest status: " + latestStatus),
    );
    const track = node("div", "progress-track");
    const bar = node("div", "progress-bar");
    bar.style.width = (workflow.noRerunSuccessRate || 0) + "%";
    track.append(bar);
    const details = node("div", "workflow-details");
    if (workflow.total) {
      details.append(
        node("span", null, workflow.failed + " failures"),
        node("span", null, workflow.total + " runs"),
        node("span", null, "P95 queue " + formatMs(workflow.queueP95Ms)),
        node("span", null, "P95 run " + formatMs(workflow.runtimeP95Ms)),
      );
    } else {
      details.append(node("span", null, "No runs in 90 days"), node("span", null, workflow.state));
    }
    card.append(title, node("div", "workflow-rate", workflow.total ? percent(workflow.noRerunSuccessRate) + " no rerun" : "No recent data"), track, details);
    return card;
  }));
}

function readinessBadge(label, count, kind) {
  const badge = node("span", "badge " + kind);
  badge.append(statusDot(kind), document.createTextNode(count + " " + label));
  return badge;
}
function renderPullRequest(pr, error) {
  if (error) {
    elements.prReadiness.replaceChildren(
      node("div", "run-detail-error", "PR readiness is unavailable."),
      node("div", "muted", error),
    );
    return;
  }
  if (!pr) {
    elements.prReadiness.replaceChildren(
      node("div", null, "No pull request is associated with the checked-out branch."),
      node("div", "muted", "CI health remains available without a current PR."),
    );
    return;
  }
  const overview = node("div");
  const link = node("a", "pr-title", "#" + pr.number + " " + pr.title);
  link.href = pr.url; link.target = "_blank"; link.rel = "noopener noreferrer";
  const mergeState = node("p", "muted", "Merge state: " + pr.mergeState.toLowerCase().replaceAll("_", " "));
  const counts = node("div", "readiness-counts");
  counts.append(
    readinessBadge("passed", pr.success, "success"),
    readinessBadge("pending", pr.pending, "running"),
    readinessBadge("failed", pr.failed, pr.failed ? "failed" : "neutral"),
  );
  overview.append(link, mergeState, counts);

  const attention = pr.checks.filter((check) => check.bucket !== "success").slice(0, 8);
  const list = node("div", "check-list");
  if (!attention.length) {
    list.append(node("div", "check-row", "All reported checks have passed."));
  } else {
    for (const check of attention) {
      const row = node("div", "check-row");
      const name = check.link ? node("a", null, check.name) : node("span", null, check.name);
      if (check.link) { name.href = check.link; name.target = "_blank"; name.rel = "noopener noreferrer"; }
      row.append(name, node("span", "check-state", check.state));
      list.append(row);
    }
  }
  elements.prReadiness.replaceChildren(overview, list);
}

function renderWorkflowOptions(workflows) {
  const selected = elements.workflowFilter.value;
  const options = [new Option("All workflows", "all"), ...workflows.map((workflow) => new Option(workflow.name, workflow.id))];
  elements.workflowFilter.replaceChildren(...options);
  if (workflows.some((workflow) => workflow.id === selected)) elements.workflowFilter.value = selected;
}
function filtersActive() {
  return Boolean(elements.searchFilter.value.trim()) ||
    elements.statusFilter.value !== "all" ||
    elements.workflowFilter.value !== "all";
}
function matchingRuns() {
  if (!dashboard) return [];
  const search = elements.searchFilter.value.trim().toLocaleLowerCase();
  const status = elements.statusFilter.value;
  const workflow = elements.workflowFilter.value;
  return dashboard.runs.filter((run) => {
    const matchesSearch = !search || run.displayTitle.toLocaleLowerCase().includes(search) || run.headBranch.toLocaleLowerCase().includes(search);
    return matchesSearch && (status === "all" || statusKind(run) === status) && (workflow === "all" || run.workflowId === workflow);
  });
}

async function loadRunDetail(runId) {
  let pending = runDetailCache.get(runId);
  if (!pending) {
    pending = fetch("/api/runs/" + runId, { cache: "no-store" }).then(async (response) => {
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || "Unable to load run details.");
      return result;
    });
    runDetailCache.set(runId, pending);
  }
  try {
    return await pending;
  } catch (error) {
    runDetailCache.delete(runId);
    throw error;
  }
}

function renderJob(job) {
  const kind = statusKind(job);
  const card = document.createElement("details");
  card.className = "job-card " + kind;
  card.open = kind === "failed";
  const summary = document.createElement("summary");
  summary.dataset.focusKey = "job-" + job.databaseId;
  summary.append(
    statusDot(kind),
    node("span", "job-name", job.name),
    node("span", "mono", formatMs(job.durationMs)),
    node("span", "badge " + kind, statusLabel(job)),
  );
  card.append(summary);
  if (job.steps.length) {
    const steps = node("div", "step-list");
    for (const step of job.steps) {
      const stepKind = statusKind(step);
      const row = node("div", "step-row");
      row.classList.add(stepKind);
      const result = node("span", "step-result");
      result.append(node("span", "mono", formatMs(step.durationMs)));
      if (stepKind === "failed") result.append(node("span", "badge failed", "Failed"));
      row.append(
        statusDot(stepKind),
        node("span", null, step.number + ". " + step.name),
        result,
      );
      row.append(node("span", "sr-only", "Status: " + statusLabel(step)));
      steps.append(row);
    }
    card.append(steps);
  }
  return card;
}

async function loadFailureDiagnostic(runId) {
  let pending = failureDiagnosticCache.get(runId);
  if (!pending) {
    pending = fetch("/api/runs/" + runId + "/failure-diagnostics", { cache: "no-store" }).then(async (response) => {
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || "Unable to load failure diagnostics.");
      return result;
    });
    failureDiagnosticCache.set(runId, pending);
  }
  try {
    return await pending;
  } catch (error) {
    failureDiagnosticCache.delete(runId);
    throw error;
  }
}

function diagnosticCard(title, items, renderItem) {
  const card = node("section", "diagnostic-card");
  card.append(node("h3", null, title));
  const list = node("div", "diagnostic-list");
  if (!items.length) {
    list.append(node("div", "diagnostic-item muted", "None reported"));
  } else {
    for (const item of items) list.append(renderItem(item));
  }
  card.append(list);
  return card;
}

function renderDiagnostics(container, diagnostics) {
  const grid = node("div", "diagnostic-grid");
  grid.append(
    diagnosticCard("Annotations", diagnostics.annotations, (annotation) => {
      const item = node("div", "diagnostic-item");
      item.append(
        node("strong", null, annotation.title || annotation.level),
        node("div", null, annotation.message),
        node("span", "location", annotation.path + ":" + annotation.startLine + (annotation.endLine !== annotation.startLine ? "-" + annotation.endLine : "")),
      );
      return item;
    }),
    diagnosticCard("Artifacts", diagnostics.artifacts, (artifact) => {
      const item = node("div", "diagnostic-item");
      item.append(
        node("strong", null, artifact.name),
        node("span", "location", formatBytes(artifact.sizeBytes) + (artifact.expired ? " · expired" : " · expires " + new Date(artifact.expiresAt).toLocaleDateString())),
      );
      return item;
    }),
  );
  const logCard = document.createElement("details");
  logCard.className = "diagnostic-card log-card";
  logCard.open = true;
  logCard.append(node("summary", null, "Failure log excerpt · " + diagnostics.logLines.length + " lines"));
  const log = node("pre", "log-excerpt", diagnostics.logLines.length ? diagnostics.logLines.join("\\n") : "No failed-step logs were available.");
  logCard.append(log);
  grid.append(logCard);
  const warnings = diagnostics.warnings?.length
    ? node("div", "diagnostic-warning", diagnostics.warnings.join("\\n"))
    : null;
  container.replaceChildren(...(warnings ? [warnings, grid] : [grid]));
}

async function rerunFailed(run, button, status) {
  if (!window.confirm("Rerun failed jobs for #" + run.number + "? This starts a new GitHub Actions attempt.")) return;
  button.disabled = true;
  status.textContent = "Requesting rerun...";
  try {
    const response = await fetch("/api/runs/" + run.databaseId + "/rerun-failed", {
      method: "POST",
      headers: { "X-Canvas-Confirmation": mutationToken },
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || "Unable to rerun failed jobs.");
    runDetailCache.delete(run.databaseId);
    failureDiagnosticCache.delete(run.databaseId);
    status.textContent = "Rerun requested. Recent runs will update shortly.";
    await requestRefresh("/api/refresh/recent", false);
  } catch (error) {
    status.textContent = error.message;
    button.disabled = false;
  }
}

function renderRunDetail(container, run, details) {
  const header = node("div", "run-detail-header");
  const summary = node("strong", null, details.jobs.length + " jobs · " + details.failedJobs + " failed");
  const external = node("a", null, "Open full run on GitHub ↗");
  external.href = run.url;
  external.target = "_blank";
  external.rel = "noopener noreferrer";
  external.dataset.focusKey = "external";
  header.append(summary, external);
  const metadata = node("div", "run-metadata");
  metadata.append(
    node("span", null, "Actor "),
    node("strong", null, details.metadata.actor || "unknown"),
    node("span", null, "Trigger "),
    node("strong", null, details.metadata.event),
    node("span", null, "Attempt "),
    node("strong", null, String(details.metadata.attempt)),
    node("span", null, "Commit "),
    node("strong", null, (details.metadata.headSha || "").slice(0, 7)),
  );
  if (details.metadata.commitMessage) metadata.append(node("span", null, details.metadata.commitMessage));
  const jobs = node("div", "job-list");
  jobs.append(...details.jobs.map(renderJob));
  const failedSteps = details.jobs.flatMap((job) =>
    job.steps
      .filter((step) => statusKind(step) === "failed")
      .map((step) => ({ job: job.name, step })),
  );
  const failureSummary = node("div", "failure-summary");
  if (failedSteps.length) {
    failureSummary.append(node("strong", null, "Failed at"));
    for (const failure of failedSteps) {
      failureSummary.append(
        node("span", null, failure.job + " › " + failure.step.number + ". " + failure.step.name),
      );
    }
  }
  container.replaceChildren(
    header,
    metadata,
    ...(failedSteps.length ? [failureSummary] : []),
    jobs,
  );

  if (details.failedJobs) {
    const failurePanel = node("section", "failure-panel");
    const failureHeader = node("div", "failure-header");
    const rerun =
      details.rerunnableFailedJobs &&
      run.status === "completed" &&
      statusKind(run) === "failed"
      ? node("button", "danger-button", "Rerun failed jobs")
      : null;
    if (rerun) rerun.type = "button";
    if (rerun) rerun.dataset.focusKey = "rerun";
    const rerunStatus = node("span", "rerun-status");
    rerunStatus.setAttribute("role", "status");
    rerunStatus.setAttribute("aria-live", "polite");
    failureHeader.append(
      node("h3", null, "Failure diagnostics"),
      rerunStatus,
      ...(rerun ? [rerun] : []),
    );
    const diagnostics = node("div", "run-detail-loading", "Loading annotations, artifacts, and failure logs...");
    if (rerun) rerun.addEventListener("click", () => rerunFailed(run, rerun, rerunStatus));
    failurePanel.append(failureHeader, diagnostics);
    container.append(failurePanel);
    loadFailureDiagnostic(run.databaseId)
      .then((result) => renderDiagnostics(diagnostics, result))
      .catch((error) => diagnostics.replaceChildren(node("div", "run-detail-error", error.message)));
  }
  restoreRunFocus(run.databaseId);
}

async function toggleRunDetail(run, control, detailRow, container) {
  const opening = detailRow.hidden;
  detailRow.hidden = !opening;
  control.setAttribute("aria-expanded", String(opening));
  if (opening) expandedRunIds.add(run.databaseId);
  else expandedRunIds.delete(run.databaseId);
  if (!opening || detailRow.dataset.loaded === "true") return;

  container.replaceChildren(node("div", "run-detail-loading", "Loading jobs and steps..."));
  try {
    renderRunDetail(container, run, await loadRunDetail(run.databaseId));
    detailRow.dataset.loaded = "true";
  } catch (error) {
    container.replaceChildren(node("div", "run-detail-error", error.message));
  }
}

function renderRuns() {
  pendingRunFocus = captureRunFocus();
  const matches = matchingRuns();
  const hasFilters = filtersActive();
  const runs = hasFilters
    ? matches.slice(0, filteredRunLimit)
    : matches.slice(0, dashboard.recentLimit);
  const rows = runs.flatMap((run) => {
    const row = document.createElement("tr");
    row.className = "run-row";
    row.dataset.runId = String(run.databaseId);
    const runCell = document.createElement("td");
    const title = node("button", "run-title-button", run.displayTitle);
    title.type = "button";
    title.dataset.focusKey = "title";
    title.setAttribute("aria-expanded", "false");
    title.setAttribute("aria-controls", "run-detail-" + run.databaseId);
    runCell.append(title, node("span", "mono", "#" + run.number + " · " + run.event + (run.runAttempt > 1 ? " · attempt " + run.runAttempt : "")));
    const status = node("span", "badge " + statusKind(run));
    status.append(statusDot(statusKind(run)), document.createTextNode(statusLabel(run)));
    const statusCell = document.createElement("td"); statusCell.append(status);
    const effectiveStart = run.startedAt || run.createdAt;
    const startedCell = node("td", null, run.startedAt ? relativeTime(run.startedAt) : "Queued " + relativeTime(run.createdAt));
    startedCell.title = new Date(effectiveStart).toLocaleString();
    row.append(
      runCell,
      node("td", null, run.workflowName),
      node("td", "mono", run.headBranch || "—"),
      statusCell,
      startedCell,
      node("td", "mono", formatMs(run.runAttempt === 1 && run.startedAt ? new Date(run.startedAt) - new Date(run.createdAt) : null)),
      node("td", "mono", formatMs(run.startedAt ? (run.status === "completed" ? new Date(run.updatedAt) : new Date()) - new Date(run.startedAt) : null)),
    );

    const detailRow = document.createElement("tr");
    detailRow.className = "run-detail-row";
    detailRow.id = "run-detail-" + run.databaseId;
    detailRow.hidden = true;
    const detailCell = document.createElement("td");
    detailCell.colSpan = 7;
    const detail = node("div", "run-detail");
    detail.dataset.runId = String(run.databaseId);
    detailCell.append(detail);
    detailRow.append(detailCell);

    row.addEventListener("click", () => toggleRunDetail(run, title, detailRow, detail));
    if (expandedRunIds.has(run.databaseId)) {
      toggleRunDetail(run, title, detailRow, detail);
    }
    return [row, detailRow];
  });
  elements.runs.replaceChildren(...rows);
  if (pendingRunFocus) restoreRunFocus(pendingRunFocus.runId);
  elements.emptyState.hidden = runs.length !== 0;
  elements.loadMore.hidden = !hasFilters || runs.length >= matches.length;
  elements.loadMore.textContent = "Load " + Math.min(FILTER_PAGE_SIZE, matches.length - runs.length) + " more";
  elements.runCount.textContent = hasFilters
    ? runs.length + " of " + matches.length + " matching runs in the 90-day history · auto-refresh 60s" +
      (recentUpdatedAt ? " · updated " + relativeTime(recentUpdatedAt) : "")
    : runs.length + " most recent runs · " + dashboard.runs.length + " in 90-day history · auto-refresh 60s" +
    (recentUpdatedAt ? " · updated " + relativeTime(recentUpdatedAt) : "");
}

function render(state) {
  elements.refresh.disabled = state.loading;
  elements.refresh.classList.toggle("loading", state.loading);
  const error = state.error || state.recentError;
  elements.error.hidden = !error;
  elements.error.textContent = error || "";
  if (!state.data) return;
  dashboard = state.data;
  mutationToken = state.mutationToken;
  const nextCacheTimestamp = state.recentUpdatedAt || state.updatedAt;
  if (cacheTimestamp && nextCacheTimestamp !== cacheTimestamp) {
    runDetailCache.clear();
    failureDiagnosticCache.clear();
  }
  cacheTimestamp = nextCacheTimestamp;
  recentUpdatedAt = nextCacheTimestamp;
  const health = dashboard.health;
  elements.repositoryName.textContent = dashboard.repository.nameWithOwner;
  elements.updatedAt.textContent = "Health updated " + relativeTime(state.healthUpdatedAt || state.updatedAt) + " · " + health.windowDays + "-day health · " + health.selectedWindow.total + " runs" +
    (health.historyComplete ? "" : " · baseline incomplete");
  renderSummary(health.selectedWindow, health.defaultBranch, dashboard.repository.defaultBranch);
  renderWindows(health.windows);
  renderTrend(health.trend);
  renderPullRequest(dashboard.pullRequest, dashboard.pullRequestError);
  renderWorkflows(dashboard.workflows);
  renderWorkflowOptions(dashboard.workflows);
  renderRuns();
}
async function loadState() {
  const response = await fetch("/api/state", { cache: "no-store" });
  if (!response.ok) throw new Error("Unable to load dashboard state.");
  render(await response.json());
}

let fullRefreshPending = false;
let recentRefreshPending = false;

async function requestRefresh(path, full) {
  if (full ? fullRefreshPending : recentRefreshPending) return;
  if (full) fullRefreshPending = true;
  else recentRefreshPending = true;
  if (full) {
    elements.refresh.disabled = true;
    elements.refresh.classList.add("loading");
  }
  try {
    const response = await fetch(path, { method: "POST" });
    if (!response.ok) { const result = await response.json(); throw new Error(result.error || "Refresh failed."); }
  } catch (error) {
    elements.error.hidden = false; elements.error.textContent = error.message;
  } finally {
    if (full) {
      fullRefreshPending = false;
      elements.refresh.disabled = false;
      elements.refresh.classList.remove("loading");
    } else {
      recentRefreshPending = false;
    }
  }
}

elements.refresh.addEventListener("click", () => requestRefresh("/api/refresh", true));
for (const element of [elements.searchFilter, elements.statusFilter, elements.workflowFilter]) {
  const updateFilters = () => {
    filteredRunLimit = FILTER_PAGE_SIZE;
    renderRuns();
  };
  element.addEventListener("input", updateFilters);
  element.addEventListener("change", updateFilters);
}
elements.loadMore.addEventListener("click", () => {
  filteredRunLimit += FILTER_PAGE_SIZE;
  renderRuns();
});
const events = new EventSource("/events");
events.addEventListener("update", () => loadState().catch(() => undefined));
loadState().catch((error) => { elements.error.hidden = false; elements.error.textContent = error.message; });
setInterval(() => requestRefresh("/api/refresh/recent", false), 60 * 1000);
setInterval(() => requestRefresh("/api/refresh", true), 15 * 60 * 1000);
`;
