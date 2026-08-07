import { execFile } from "node:child_process";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const DAY_MS = 24 * 60 * 60 * 1000;

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

async function tryRunGh(args, workspacePath) {
    try {
        return await runGh(args, workspacePath);
    } catch {
        return null;
    }
}

async function tryRunGhText(args, workspacePath) {
    try {
        return await runGhText(args, workspacePath);
    } catch {
        return null;
    }
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
    const firstPassSuccess = evaluated.filter(
        (run) => run.conclusion === "success" && run.runAttempt === 1,
    ).length;
    const rerunRecoveries = evaluated.filter(
        (run) => run.conclusion === "success" && run.runAttempt > 1,
    ).length;
    const queueTimes = selected
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
        firstPassRate: evaluated.length
            ? Math.round((firstPassSuccess / evaluated.length) * 1000) / 10
            : null,
        queueP50Ms: percentile(queueTimes, 0.5),
        queueP95Ms: percentile(queueTimes, 0.95),
        rerunRecoveries,
        runtimeP50Ms: percentile(runtimes, 0.5),
        runtimeP95Ms: percentile(runtimes, 0.95),
        success,
        successRate: evaluated.length
            ? Math.round((success / evaluated.length) * 1000) / 10
            : null,
        total: selected.length,
    };
}

function buildWorkflowSummary(runs, now, configuredWorkflows) {
    const workflows = new Map(
        configuredWorkflows.map((workflow) => [
            workflow.name,
            {
                latestConclusion: null,
                latestStatus: null,
                name: workflow.name,
                runs: [],
                state: workflow.state,
            },
        ]),
    );

    for (const run of runs) {
        const workflow = workflows.get(run.workflowName) ?? {
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
        workflows.set(run.workflowName, workflow);
    }

    return [...workflows.values()]
        .map((workflow) => ({
            ...buildMetrics(workflow.runs, 90, now),
            latestConclusion: workflow.latestConclusion,
            latestStatus: workflow.latestStatus,
            name: workflow.name,
            state: workflow.state,
        }))
        .sort((left, right) => {
            const leftRate = left.firstPassRate ?? 101;
            const rightRate = right.firstPassRate ?? 101;
            return leftRate - rightRate || left.name.localeCompare(right.name);
        });
}

function buildTrend(runs, days, now) {
    const start = new Date(now.getTime() - (days - 1) * DAY_MS);
    start.setUTCHours(0, 0, 0, 0);
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
        runId,
    };
}

function failureLogExcerpt(text) {
    if (!text) {
        return [];
    }

    const lines = text
        .replace(/\u001b\[[0-9;]*m/g, "")
        .split(/\r?\n/)
        .filter(Boolean);
    const interesting = new Set();
    const pattern =
        /\b(error|failed|failure|exception|fatal|exit code|assert|timed out)\b/i;

    lines.forEach((line, index) => {
        if (!pattern.test(line)) {
            return;
        }
        for (
            let context = Math.max(0, index - 2);
            context <= Math.min(lines.length - 1, index + 3);
            context += 1
        ) {
            interesting.add(context);
        }
    });

    const selected =
        interesting.size > 0
            ? [...interesting].sort((left, right) => left - right).slice(-160)
            : lines.slice(-80).map((_, index) => lines.length - 80 + index);

    return selected
        .filter((index) => index >= 0)
        .map((index) => lines[index])
        .filter(Boolean);
}

export async function loadFailureDiagnostics({
    jobIds,
    repository,
    runId,
    workspacePath,
}) {
    const [logText, artifactData, annotationPages] = await Promise.all([
        tryRunGhText(
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
        tryRunGh(
            [
                "api",
                `repos/${repository}/actions/runs/${runId}/artifacts`,
            ],
            workspacePath,
        ),
        Promise.all(
            jobIds.map((jobId) =>
                tryRunGh(
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

    const annotations = annotationPages
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
    const artifacts = (artifactData?.artifacts ?? []).map((artifact) => ({
        createdAt: artifact.created_at,
        expired: artifact.expired,
        expiresAt: artifact.expires_at,
        name: artifact.name,
        sizeBytes: artifact.size_in_bytes,
    }));

    return {
        annotations,
        artifacts,
        logLines: failureLogExcerpt(logText),
        runId,
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
        workflowName: run.workflowName ?? "Unknown workflow",
    }));
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

    const [pages, workflowData, pullRequestData] = await Promise.all([
        runGh(
            [
                "api",
                "--method",
                "GET",
                `repos/${nameWithOwner}/actions/runs`,
                "--paginate",
                "--slurp",
                "-f",
                "per_page=100",
                "-f",
                `created=>=${since}`,
            ],
            workspacePath,
        ),
        runGh(
            [
                "workflow",
                "list",
                "--all",
                "--repo",
                nameWithOwner,
                "--json",
                "id,name,state",
            ],
            workspacePath,
        ),
        tryRunGh(
            [
                "pr",
                "view",
                "--repo",
                nameWithOwner,
                "--json",
                "number,title,url,headRefOid,mergeStateStatus,statusCheckRollup",
            ],
            workspacePath,
        ),
    ]);

    const workflowNames = new Map(
        workflowData.map((workflow) => [
            String(workflow.id),
            workflow.name,
        ]),
    );
    const runs = pages
        .flatMap((page) => page.workflow_runs ?? [])
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
            windowDays: days,
            windows,
        },
        pullRequest: normalizePullRequest(pullRequestData),
        repository: {
            defaultBranch: repositoryData.defaultBranchRef?.name ?? null,
            nameWithOwner,
            url: repositoryData.url,
        },
        recentLimit: limit,
        runs,
        summary: {
            failed: selectedWindow.failed,
            neutral: 0,
            running: selectedWindow.active,
            success: selectedWindow.success,
            total: selectedWindow.total,
        },
        workflows: buildWorkflowSummary(runs, now, workflowData),
    };
}
