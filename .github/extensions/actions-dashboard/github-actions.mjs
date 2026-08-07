import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const DAY_MS = 24 * 60 * 60 * 1000;
const RERUN_WINDOW_MS = 30 * DAY_MS;

async function executeGh(args, workspacePath) {
    try {
        return await execFileAsync("gh", args, {
            cwd: workspacePath,
            encoding: "utf8",
            maxBuffer: 16 * 1024 * 1024,
            windowsHide: true,
        });
    } catch (error) {
        const detail =
            typeof error?.stderr === "string" && error.stderr.trim()
                ? error.stderr.trim()
                : error instanceof Error
                  ? error.message
                  : String(error);
        throw new Error(`GitHub CLI request failed: ${detail}`);
    }
}

async function runGh(args, workspacePath) {
    const { stdout } = await executeGh(args, workspacePath);
    return JSON.parse(stdout);
}

async function runGhText(args, workspacePath) {
    const { stdout } = await executeGh(args, workspacePath);
    return stdout;
}

function collectFailureLogLines() {
    const contextLines = [];
    const selectedLines = [];
    const tailLines = [];
    const pattern =
        /\b(error|failed|failure|exception|fatal|exit code|assert|timed out)\b/i;
    let afterMatch = 0;
    let lastSelectedIndex = -1;
    let lineIndex = 0;

    function select(line) {
        if (line.index <= lastSelectedIndex) {
            return;
        }
        selectedLines.push(line);
        lastSelectedIndex = line.index;
        if (selectedLines.length > 160) {
            selectedLines.shift();
        }
    }

    return {
        add(rawLine) {
            const cleanText = rawLine.replace(/\u001b\[[0-9;]*m/g, "");
            if (!cleanText) {
                return;
            }
            const match = cleanText.match(pattern);
            const excerptStart =
                cleanText.length > 8 * 1024
                    ? match
                        ? Math.max(0, (match.index ?? 0) - 4 * 1024)
                        : cleanText.length - 8 * 1024
                    : 0;
            const text = cleanText.slice(excerptStart, excerptStart + 8 * 1024);

            const line = { index: lineIndex, text };
            lineIndex += 1;
            tailLines.push(line);
            if (tailLines.length > 80) {
                tailLines.shift();
            }

            if (match) {
                for (const contextLine of contextLines) {
                    select(contextLine);
                }
                select(line);
                afterMatch = 3;
            } else if (afterMatch > 0) {
                select(line);
                afterMatch -= 1;
            }

            contextLines.push(line);
            if (contextLines.length > 2) {
                contextLines.shift();
            }
        },
        result() {
            return (selectedLines.length > 0 ? selectedLines : tailLines).map(
                (line) => line.text,
            );
        },
    };
}

async function streamFailureLogExcerpt(args, workspacePath) {
    return new Promise((resolve, reject) => {
        const collector = collectFailureLogLines();
        const child = spawn("gh", args, {
            cwd: workspacePath,
            windowsHide: true,
        });
        let pending = "";
        let stderr = "";

        child.stdout.setEncoding("utf8");
        child.stdout.on("data", (chunk) => {
            const lines = `${pending}${chunk}`.split(/\r?\n/);
            pending = lines.pop() ?? "";
            for (const line of lines) {
                collector.add(line);
            }
            while (pending.length > 64 * 1024) {
                collector.add(pending.slice(0, 64 * 1024));
                pending = pending.slice(64 * 1024);
            }
        });
        child.stderr.setEncoding("utf8");
        child.stderr.on("data", (chunk) => {
            stderr = `${stderr}${chunk}`.slice(-16 * 1024);
        });
        child.on("error", (error) => {
            reject(new Error(`GitHub CLI request failed: ${error.message}`));
        });
        child.on("close", (code) => {
            if (pending) {
                collector.add(pending);
            }
            if (code === 0) {
                resolve(collector.result());
                return;
            }
            reject(
                new Error(
                    `GitHub CLI request failed: ${
                        stderr.trim() || `process exited with code ${code}`
                    }`,
                ),
            );
        });
    });
}

async function captureResult(operation) {
    try {
        return { error: null, value: await operation };
    } catch (error) {
        return {
            error: error instanceof Error ? error.message : String(error),
            value: null,
        };
    }
}

async function mapWithConcurrency(items, limit, worker) {
    const results = new Array(items.length);
    let nextIndex = 0;

    async function consume() {
        while (nextIndex < items.length) {
            const index = nextIndex;
            nextIndex += 1;
            results[index] = await worker(items[index], index);
        }
    }

    await Promise.all(
        Array.from(
            { length: Math.min(limit, items.length) },
            () => consume(),
        ),
    );
    return results;
}

function percentile(values, fraction) {
    if (values.length === 0) {
        return null;
    }

    const sorted = [...values].sort((left, right) => left - right);
    return sorted[Math.ceil(sorted.length * fraction) - 1];
}

function elapsedMs(start, end) {
    if (!start || !end) {
        return null;
    }

    const value = new Date(end).getTime() - new Date(start).getTime();
    return Number.isFinite(value) && value >= 0 ? value : null;
}

export function isUnsuccessfulConclusion(conclusion) {
    return (
        Boolean(conclusion) &&
        !["success", "neutral", "skipped"].includes(conclusion)
    );
}

export function isRerunAgeEligible(run, now = Date.now()) {
    const createdAt = Date.parse(run.createdAt);
    return (
        Number.isFinite(createdAt) &&
        createdAt <= now &&
        createdAt >= now - RERUN_WINDOW_MS
    );
}

function isEvaluated(run) {
    return (
        run.status === "completed" &&
        run.conclusion !== "neutral" &&
        run.conclusion !== "skipped" &&
        Boolean(run.conclusion)
    );
}

function buildMetrics(runs, days, now) {
    const cutoff = now.getTime() - days * DAY_MS;
    const selected = runs.filter(
        (run) => new Date(run.createdAt).getTime() >= cutoff,
    );
    const evaluated = selected.filter(isEvaluated);
    const success = evaluated.filter(
        (run) => run.conclusion === "success",
    ).length;
    const failed = evaluated.length - success;
    const neutral = selected.filter(
        (run) =>
            run.status === "completed" &&
            ["neutral", "skipped"].includes(run.conclusion),
    ).length;
    const noRerunSuccess = evaluated.filter(
        (run) => run.conclusion === "success" && run.runAttempt === 1,
    ).length;
    const successfulReruns = evaluated.filter(
        (run) => run.conclusion === "success" && run.runAttempt > 1,
    ).length;
    const queueTimes = selected
        .filter((run) => run.runAttempt === 1)
        .map((run) => elapsedMs(run.createdAt, run.startedAt))
        .filter((value) => value !== null);
    const runtimes = selected
        .filter((run) => run.status === "completed")
        .map((run) => elapsedMs(run.startedAt, run.updatedAt))
        .filter((value) => value !== null);

    return {
        active: selected.filter((run) => run.status !== "completed").length,
        days,
        failed,
        neutral,
        noRerunSuccessRate: evaluated.length
            ? Math.round((noRerunSuccess / evaluated.length) * 1000) / 10
            : null,
        queueP50Ms: percentile(queueTimes, 0.5),
        queueP95Ms: percentile(queueTimes, 0.95),
        successfulReruns,
        runtimeP50Ms: percentile(runtimes, 0.5),
        runtimeP95Ms: percentile(runtimes, 0.95),
        success,
        successRate: evaluated.length
            ? Math.round((success / evaluated.length) * 1000) / 10
            : null,
        total: selected.length,
    };
}

export function buildWorkflowSummary(runs, now, configuredWorkflows) {
    const workflows = new Map(
        configuredWorkflows.map((workflow) => [
            String(workflow.id),
            {
                id: String(workflow.id),
                latestConclusion: null,
                latestStatus: null,
                name: workflow.name,
                runs: [],
                state: workflow.state,
            },
        ]),
    );

    for (const run of runs) {
        const workflowKey =
            run.workflowId ?? `name:${run.workflowName}`;
        const workflow = workflows.get(workflowKey) ?? {
            id: workflowKey,
            latestConclusion: run.conclusion,
            latestStatus: run.status,
            name: run.workflowName,
            runs: [],
            state: "unknown",
        };
        if (workflow.runs.length === 0) {
            workflow.latestConclusion = run.conclusion;
            workflow.latestStatus = run.status;
        }
        workflow.runs.push(run);
        workflows.set(workflowKey, workflow);
    }

    return [...workflows.values()]
        .map((workflow) => ({
            ...buildMetrics(workflow.runs, 90, now),
            id: workflow.id,
            latestConclusion: workflow.latestConclusion,
            latestStatus: workflow.latestStatus,
            name: workflow.name,
            state: workflow.state,
        }))
        .sort((left, right) => {
            const leftRate = left.noRerunSuccessRate ?? 101;
            const rightRate = right.noRerunSuccessRate ?? 101;
            return leftRate - rightRate || left.name.localeCompare(right.name);
        });
}

function buildTrend(runs, days, now) {
    const start = new Date(now.getTime() - days * DAY_MS);
    const bucketCount = Math.ceil(days / 7);
    const buckets = Array.from({ length: bucketCount }, (_, index) => ({
        failed: 0,
        label: new Date(start.getTime() + index * 7 * DAY_MS)
            .toISOString()
            .slice(5, 10),
        success: 0,
        total: 0,
    }));

    for (const run of runs) {
        if (!isEvaluated(run)) {
            continue;
        }

        const index = Math.floor(
            (new Date(run.createdAt).getTime() - start.getTime()) /
                (7 * DAY_MS),
        );
        if (index < 0 || index >= buckets.length) {
            continue;
        }

        const bucket = buckets[index];
        bucket.total += 1;
        if (run.conclusion === "success") {
            bucket.success += 1;
        } else {
            bucket.failed += 1;
        }
    }

    return buckets.map((bucket) => ({
        ...bucket,
        successRate: bucket.total
            ? Math.round((bucket.success / bucket.total) * 1000) / 10
            : null,
    }));
}

function normalizeCheck(check) {
    const state = (
        check.conclusion ??
        check.state ??
        check.status ??
        "UNKNOWN"
    ).toLowerCase();
    let bucket = "pending";
    if (["success", "neutral", "skipped"].includes(state)) {
        bucket = "success";
    } else if (
        [
            "failure",
            "error",
            "cancelled",
            "timed_out",
            "action_required",
            "startup_failure",
            "stale",
        ].includes(state)
    ) {
        bucket = "failed";
    }

    return {
        bucket,
        link: check.detailsUrl ?? check.targetUrl ?? null,
        name: check.name ?? check.context ?? "Unnamed check",
        state,
        workflow: check.workflowName ?? null,
    };
}

function normalizePullRequest(pullRequest) {
    if (!pullRequest) {
        return null;
    }

    const checks = (pullRequest.statusCheckRollup ?? []).map(normalizeCheck);
    return {
        checks,
        failed: checks.filter((check) => check.bucket === "failed").length,
        headSha: pullRequest.headRefOid,
        mergeState: pullRequest.mergeStateStatus,
        number: pullRequest.number,
        pending: checks.filter((check) => check.bucket === "pending").length,
        success: checks.filter((check) => check.bucket === "success").length,
        title: pullRequest.title,
        url: pullRequest.url,
    };
}

async function loadCurrentPullRequest(repository, workspacePath) {
    try {
        const { stdout } = await execFileAsync(
            "git",
            ["branch", "--show-current"],
            {
                cwd: workspacePath,
                encoding: "utf8",
                windowsHide: true,
            },
        );
        const branch = stdout.trim();
        if (!branch) {
            throw new Error(
                "Cannot resolve PR readiness from a detached Git HEAD.",
            );
        }
        return {
            error: null,
            value: await runGh(
                [
                    "pr",
                    "view",
                    branch,
                    "--repo",
                    repository,
                    "--json",
                    "number,title,url,headRefOid,mergeStateStatus,statusCheckRollup",
                ],
                workspacePath,
            ),
        };
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        if (message.includes("no pull requests found for branch")) {
            return { error: null, value: null };
        }
        return { error: message, value: null };
    }
}

function workflowName(run, workflowNames) {
    return (
        workflowNames.get(String(run.workflow_id)) ??
        run.path?.split("/").at(-1)?.replace(/\.ya?ml$/i, "") ??
        "Unknown workflow"
    );
}

function normalizeRun(run, workflowNames) {
    return {
        conclusion: run.conclusion,
        createdAt: run.created_at,
        databaseId: run.id,
        displayTitle: run.display_title ?? run.name ?? "Untitled run",
        event: run.event,
        headBranch: run.head_branch ?? "",
        headSha: run.head_sha,
        number: run.run_number,
        runAttempt: run.run_attempt ?? 1,
        startedAt: run.run_started_at ?? null,
        status: run.status,
        updatedAt: run.updated_at,
        url: run.html_url,
        workflowId: String(run.workflow_id),
        workflowName: workflowName(run, workflowNames),
    };
}

export async function loadRunDetails({
    repository,
    runId,
    workspacePath,
}) {
    const [data, metadata] = await Promise.all([
        runGh(
            [
                "run",
                "view",
                String(runId),
                "--repo",
                repository,
                "--json",
                "jobs",
            ],
            workspacePath,
        ),
        runGh(
            ["api", `repos/${repository}/actions/runs/${runId}`],
            workspacePath,
        ),
    ]);

    const jobs = (data.jobs ?? [])
        .map((job) => ({
            conclusion: job.conclusion,
            databaseId: job.databaseId,
            durationMs: elapsedMs(job.startedAt, job.completedAt),
            name: job.name,
            startedAt: job.startedAt,
            status: job.status,
            steps: (job.steps ?? []).map((step) => ({
                conclusion: step.conclusion,
                durationMs: elapsedMs(step.startedAt, step.completedAt),
                name: step.name,
                number: step.number,
                status: step.status,
            })),
            url: job.url,
        }))
        .sort((left, right) => {
            const leftFailed = isUnsuccessfulConclusion(left.conclusion)
                ? 0
                : 1;
            const rightFailed = isUnsuccessfulConclusion(right.conclusion)
                ? 0
                : 1;
            return leftFailed - rightFailed || left.name.localeCompare(right.name);
        });

    return {
        failedJobs: jobs.filter((job) =>
            isUnsuccessfulConclusion(job.conclusion),
        ).length,
        jobs,
        metadata: {
            actor: metadata.actor?.login ?? null,
            attempt: metadata.run_attempt ?? 1,
            commitMessage:
                metadata.head_commit?.message?.split(/\r?\n/, 1)[0] ?? null,
            event: metadata.event,
            headSha: metadata.head_sha,
            triggeringActor: metadata.triggering_actor?.login ?? null,
        },
        rerunnableFailedJobs: jobs.filter((job) =>
            isUnsuccessfulConclusion(job.conclusion),
        ).length,
        runId,
    };
}

export async function loadRun({
    repository,
    runId,
    workflowName: knownWorkflowName,
    workspacePath,
}) {
    const run = await runGh(
        ["api", `repos/${repository}/actions/runs/${runId}`],
        workspacePath,
    );
    const workflowNames = new Map();
    if (knownWorkflowName) {
        workflowNames.set(String(run.workflow_id), knownWorkflowName);
    }
    return normalizeRun(run, workflowNames);
}

export async function loadFailureDiagnostics({
    jobIds,
    repository,
    runId,
    workspacePath,
}) {
    const [logResult, artifactResult, annotationResults] = await Promise.all([
        captureResult(
            streamFailureLogExcerpt(
                [
                    "run",
                    "view",
                    String(runId),
                    "--repo",
                    repository,
                    "--log-failed",
                ],
                workspacePath,
            ),
        ),
        captureResult(
            runGh(
            [
                "api",
                "--method",
                "GET",
                `repos/${repository}/actions/runs/${runId}/artifacts`,
                "--paginate",
                "--slurp",
                "-f",
                "per_page=100",
            ],
            workspacePath,
            ),
        ),
        mapWithConcurrency(
            jobIds,
            4,
            (jobId) =>
                captureResult(
                    runGh(
                    [
                        "api",
                        "--method",
                        "GET",
                        `repos/${repository}/check-runs/${jobId}/annotations`,
                        "--paginate",
                        "--slurp",
                        "-f",
                        "per_page=100",
                    ],
                    workspacePath,
                    ),
                ),
        ),
    ]);

    const annotations = annotationResults
        .flatMap((result) => result.value ?? [])
        .flatMap((pages) => pages ?? [])
        .flatMap((page) => page ?? [])
        .map((annotation) => ({
            endLine: annotation.end_line,
            level: annotation.annotation_level,
            message: annotation.message,
            path: annotation.path,
            startLine: annotation.start_line,
            title: annotation.title || null,
        }));
    const artifacts = (artifactResult.value ?? [])
        .flatMap((page) => page.artifacts ?? [])
        .map((artifact) => ({
            createdAt: artifact.created_at,
            expired: artifact.expired,
            expiresAt: artifact.expires_at,
            name: artifact.name,
            sizeBytes: artifact.size_in_bytes,
        }));

    return {
        annotations,
        artifacts,
        logLines: logResult.value ?? [],
        runId,
        warnings: [
            ...(logResult.error
                ? [`Failure logs: ${logResult.error}`]
                : []),
            ...(artifactResult.error
                ? [`Artifacts: ${artifactResult.error}`]
                : []),
            ...annotationResults.flatMap((result, index) =>
                result.error
                    ? [
                          `Annotations for job ${jobIds[index]}: ${result.error}`,
                      ]
                    : [],
            ),
        ],
    };
}

export async function rerunFailedJobs({
    repository,
    runId,
    workspacePath,
}) {
    await runGhText(
        [
            "run",
            "rerun",
            String(runId),
            "--failed",
            "--repo",
            repository,
        ],
        workspacePath,
    );
}

export async function loadRecentRuns({
    limit,
    repository,
    workspacePath,
}) {
    const runs = await runGh(
        [
            "run",
            "list",
            "--all",
            "--repo",
            repository,
            "--limit",
            String(limit),
            "--json",
            [
                "attempt",
                "databaseId",
                "displayTitle",
                "event",
                "headBranch",
                "headSha",
                "status",
                "conclusion",
                "workflowName",
                "workflowDatabaseId",
                "createdAt",
                "startedAt",
                "updatedAt",
                "url",
                "number",
            ].join(","),
        ],
        workspacePath,
    );

    return runs.map((run) => ({
        ...run,
        displayTitle: run.displayTitle ?? "Untitled run",
        headBranch: run.headBranch ?? "",
        runAttempt: run.attempt ?? 1,
        workflowId: run.workflowDatabaseId
            ? String(run.workflowDatabaseId)
            : `name:${run.workflowName ?? "Unknown workflow"}`,
        workflowName: run.workflowName ?? "Unknown workflow",
    }));
}

async function loadRunRange({
    end,
    repository,
    start,
    workspacePath,
}) {
    const pages = await runGh(
        [
            "api",
            "--method",
            "GET",
            `repos/${repository}/actions/runs`,
            "--paginate",
            "--slurp",
            "-f",
            "per_page=100",
            "-f",
            `created=${start.toISOString()}..${end.toISOString()}`,
        ],
        workspacePath,
    );
    const runs = pages.flatMap((page) => page.workflow_runs ?? []);
    const totalCount = pages[0]?.total_count ?? runs.length;
    const rangeMs = end.getTime() - start.getTime();

    if (totalCount >= 1000 && rangeMs > 60 * 60 * 1000) {
        const midpoint = new Date(start.getTime() + Math.floor(rangeMs / 2));
        const left = await loadRunRange({
            end: midpoint,
            repository,
            start,
            workspacePath,
        });
        const right = await loadRunRange({
            end,
            repository,
            start: midpoint,
            workspacePath,
        });
        return {
            complete: left.complete && right.complete,
            runs: [...left.runs, ...right.runs],
        };
    }

    return {
        complete: totalCount < 1000,
        runs,
    };
}

async function loadRunHistory(repository, since, now, workspacePath) {
    const ranges = [];
    let start = new Date(since);
    while (start < now) {
        const end = new Date(
            Math.min(start.getTime() + 7 * DAY_MS, now.getTime()),
        );
        ranges.push({ end, repository, start, workspacePath });
        start = end;
    }

    const results = await mapWithConcurrency(
        ranges,
        3,
        (range) => loadRunRange(range),
    );
    const uniqueRuns = new Map();
    for (const run of results.flatMap((result) => result.runs)) {
        uniqueRuns.set(run.id, run);
    }
    return {
        complete: results.every((result) => result.complete),
        runs: [...uniqueRuns.values()],
    };
}

export async function loadDashboard({
    days = 90,
    limit,
    repository,
    workspacePath,
}) {
    const repositoryArgs = repository ? [repository] : [];
    const repositoryData = await runGh(
        [
            "repo",
            "view",
            ...repositoryArgs,
            "--json",
            "nameWithOwner,url,defaultBranchRef",
        ],
        workspacePath,
    );
    const nameWithOwner = repositoryData.nameWithOwner;
    const since = new Date(Date.now() - 90 * DAY_MS).toISOString();

    const historyNow = new Date();
    const [history, workflowData, pullRequestData] = await Promise.all([
        loadRunHistory(nameWithOwner, since, historyNow, workspacePath),
        runGh(
            [
                "workflow",
                "list",
                "--all",
                "--limit",
                "1000",
                "--repo",
                nameWithOwner,
                "--json",
                "id,name,state",
            ],
            workspacePath,
        ),
        loadCurrentPullRequest(nameWithOwner, workspacePath),
    ]);

    const workflowNames = new Map(
        workflowData.map((workflow) => [
            String(workflow.id),
            workflow.name,
        ]),
    );
    const runs = history.runs
        .map((run) => normalizeRun(run, workflowNames))
        .filter((run) => new Date(run.createdAt).getTime() >= Date.parse(since))
        .sort(
            (left, right) =>
                new Date(right.createdAt).getTime() -
                new Date(left.createdAt).getTime(),
        );
    const now = new Date();
    const windows = Object.fromEntries(
        [7, 30, 90].map((windowDays) => [
            windowDays,
            buildMetrics(runs, windowDays, now),
        ]),
    );
    const selectedWindow = windows[String(days)] ?? buildMetrics(runs, days, now);

    return {
        health: {
            defaultBranch: buildMetrics(
                runs.filter(
                    (run) =>
                        run.headBranch ===
                        repositoryData.defaultBranchRef?.name,
                ),
                days,
                now,
            ),
            selectedWindow,
            trend: buildTrend(runs, days, now),
            historyComplete: history.complete,
            windowDays: days,
            windows,
        },
        pullRequest: normalizePullRequest(pullRequestData.value),
        pullRequestError: pullRequestData.error,
        repository: {
            defaultBranch: repositoryData.defaultBranchRef?.name ?? null,
            nameWithOwner,
            url: repositoryData.url,
        },
        recentLimit: limit,
        runs,
        summary: {
            failed: selectedWindow.failed,
            neutral: selectedWindow.neutral,
            running: selectedWindow.active,
            success: selectedWindow.success,
            total: selectedWindow.total,
        },
        workflows: buildWorkflowSummary(runs, now, workflowData),
    };
}
