import { randomUUID } from "node:crypto";
import { createServer } from "node:http";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
    CanvasError,
    createCanvas,
    joinSession,
} from "@github/copilot-sdk/extension";
import {
    loadDashboard,
    loadFailureDiagnostics,
    loadRecentRuns,
    loadRunDetails,
    isUnsuccessfulConclusion,
    rerunFailedJobs,
} from "./github-actions.mjs";
import { appJavaScript, dashboardHtml, dashboardStyles } from "./renderer.mjs";

const servers = new Map();
const pendingServers = new Map();
const workspacePath = resolve(
    dirname(fileURLToPath(import.meta.url)),
    "..",
    "..",
    "..",
);

function writeJson(res, statusCode, value) {
    res.writeHead(statusCode, {
        "Cache-Control": "no-store",
        "Content-Type": "application/json; charset=utf-8",
    });
    res.end(JSON.stringify(value));
}

function broadcast(entry) {
    const message = `event: update\ndata: ${JSON.stringify({
        loading: entry.state.loading,
        healthUpdatedAt: entry.state.healthUpdatedAt,
        recentUpdatedAt: entry.state.recentUpdatedAt,
        updatedAt: entry.state.updatedAt,
    })}\n\n`;

    for (const client of entry.clients) {
        if (client.destroyed || client.writableEnded) {
            entry.clients.delete(client);
            continue;
        }

        try {
            client.write(message);
        } catch {
            entry.clients.delete(client);
            client.destroy();
        }
    }
}

async function refreshEntry(entry) {
    if (entry.refreshPromise) {
        return entry.refreshPromise;
    }

    entry.state = {
        ...entry.state,
        error: null,
        loading: true,
    };
    broadcast(entry);

    entry.refreshPromise = (async () => {
        try {
            const data = await loadDashboard({
                days: entry.options.days,
                limit: entry.options.limit,
                repository: entry.options.repository,
                workspacePath,
            });
            entry.state = {
                data,
                error: null,
                healthUpdatedAt: new Date().toISOString(),
                loading: false,
                recentError: null,
                recentUpdatedAt: new Date().toISOString(),
                updatedAt: new Date().toISOString(),
            };
            entry.failureDiagnostics.clear();
            entry.runDetails.clear();
            return data;
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            entry.state = {
                ...entry.state,
                error: message,
                loading: false,
                updatedAt: new Date().toISOString(),
            };
            throw error;
        } finally {
            entry.refreshPromise = undefined;
            broadcast(entry);
        }
    })();

    return entry.refreshPromise;
}

async function refreshRecentRuns(entry) {
    if (entry.refreshPromise) {
        await entry.refreshPromise;
        return entry.state.data?.runs ?? [];
    }
    if (!entry.state.data) {
        const data = await refreshEntry(entry);
        return data.runs;
    }
    if (entry.recentRefreshPromise) {
        return entry.recentRefreshPromise;
    }

    entry.recentRefreshPromise = (async () => {
        try {
            const runs = await loadRecentRuns({
                limit: entry.options.limit,
                repository: entry.state.data.repository.nameWithOwner,
                workspacePath,
            });
            const cutoff = Date.now() - 90 * 24 * 60 * 60 * 1000;
            const mergedRuns = new Map(
                entry.state.data.runs.map((run) => [run.databaseId, run]),
            );
            for (const run of runs) {
                entry.failureDiagnostics.delete(run.databaseId);
                entry.runDetails.delete(run.databaseId);
                mergedRuns.set(run.databaseId, run);
            }
            const history = [...mergedRuns.values()]
                .filter(
                    (run) =>
                        new Date(run.createdAt).getTime() >= cutoff,
                )
                .sort(
                    (left, right) =>
                        new Date(right.createdAt).getTime() -
                        new Date(left.createdAt).getTime(),
                );
            entry.state = {
                ...entry.state,
                data: {
                    ...entry.state.data,
                    runs: history,
                },
                recentError: null,
                recentUpdatedAt: new Date().toISOString(),
            };
            return history;
        } catch (error) {
            entry.state = {
                ...entry.state,
                recentError:
                    error instanceof Error ? error.message : String(error),
            };
            throw error;
        } finally {
            entry.recentRefreshPromise = undefined;
            broadcast(entry);
        }
    })();

    return entry.recentRefreshPromise;
}

async function getFailureDiagnostics(entry, runId) {
    const details = await getRunDetails(entry, runId);
    const failedJobIds = details.jobs
        .filter((job) => isUnsuccessfulConclusion(job.conclusion))
        .map((job) => job.databaseId);
    if (failedJobIds.length === 0) {
        throw new CanvasError(
            "actions_run_has_no_failures",
            "The requested run has no failed jobs.",
        );
    }

    let diagnostics = entry.failureDiagnostics.get(runId);
    if (!diagnostics) {
        diagnostics = loadFailureDiagnostics({
            jobIds: failedJobIds,
            repository: entry.state.data.repository.nameWithOwner,
            runId,
            workspacePath,
        });
        entry.failureDiagnostics.set(runId, diagnostics);
    }

    try {
        return await diagnostics;
    } catch (error) {
        entry.failureDiagnostics.delete(runId);
        throw error;
    }
}

async function getRunDetails(entry, runId) {
    const run = entry.state.data?.runs.find(
        (candidate) => candidate.databaseId === runId,
    );
    if (!run) {
        throw new CanvasError(
            "actions_run_not_loaded",
            "The requested run is not present in the loaded dashboard.",
        );
    }

    let details = entry.runDetails.get(runId);
    if (!details) {
        details = loadRunDetails({
            repository: entry.state.data.repository.nameWithOwner,
            runId,
            workspacePath,
        });
        entry.runDetails.set(runId, details);
    }

    try {
        return await details;
    } catch (error) {
        entry.runDetails.delete(runId);
        throw error;
    }
}

async function handleRequest(entry, req, res) {
    const url = new URL(req.url ?? "/", "http://127.0.0.1");
    if (req.headers.host !== entry.host) {
        writeJson(res, 421, { error: "Unexpected Host header." });
        return;
    }
    if (
        req.method !== "GET" &&
        req.headers.origin !== entry.origin
    ) {
        writeJson(res, 403, { error: "Cross-origin request rejected." });
        return;
    }

    if (req.method === "GET" && url.pathname === "/") {
        res.writeHead(200, {
            "Cache-Control": "no-store",
            "Content-Security-Policy":
                "default-src 'none'; connect-src 'self'; script-src 'self'; style-src 'self'; img-src 'none'; base-uri 'none'",
            "Content-Type": "text/html; charset=utf-8",
        });
        res.end(dashboardHtml);
        return;
    }

    if (req.method === "GET" && url.pathname === "/styles.css") {
        res.writeHead(200, {
            "Cache-Control": "no-store",
            "Content-Type": "text/css; charset=utf-8",
        });
        res.end(dashboardStyles);
        return;
    }

    if (req.method === "GET" && url.pathname === "/app.js") {
        res.writeHead(200, {
            "Cache-Control": "no-store",
            "Content-Type": "text/javascript; charset=utf-8",
        });
        res.end(appJavaScript);
        return;
    }

    if (req.method === "GET" && url.pathname === "/api/state") {
        writeJson(res, 200, {
            ...entry.state,
            mutationToken: entry.mutationToken,
        });
        return;
    }

    if (req.method === "GET" && url.pathname === "/events") {
        res.writeHead(200, {
            "Cache-Control": "no-cache",
            Connection: "keep-alive",
            "Content-Type": "text/event-stream",
        });
        res.write(": connected\n\n");
        entry.clients.add(res);
        req.on("close", () => entry.clients.delete(res));
        return;
    }

    if (req.method === "POST" && url.pathname === "/api/refresh") {
        try {
            const data = await refreshEntry(entry);
            writeJson(res, 200, {
                repository: data.repository.nameWithOwner,
                runCount: data.runs.length,
                summary: data.summary,
            });
        } catch {
            writeJson(res, 502, { error: entry.state.error });
        }
        return;
    }

    if (req.method === "POST" && url.pathname === "/api/refresh/recent") {
        try {
            const runs = await refreshRecentRuns(entry);
            writeJson(res, 200, {
                recentUpdatedAt: entry.state.recentUpdatedAt,
                runCount: runs.length,
            });
        } catch {
            writeJson(res, 502, { error: entry.state.recentError });
        }
        return;
    }

    const runDetailsMatch = url.pathname.match(/^\/api\/runs\/(\d+)$/);
    if (req.method === "GET" && runDetailsMatch) {
        try {
            const details = await getRunDetails(
                entry,
                Number(runDetailsMatch[1]),
            );
            writeJson(res, 200, details);
        } catch (error) {
            writeJson(res, 404, {
                error:
                    error instanceof Error
                        ? error.message
                        : "Unable to load run details.",
            });
        }
        return;
    }

    const diagnosticsMatch = url.pathname.match(
        /^\/api\/runs\/(\d+)\/failure-diagnostics$/,
    );
    if (req.method === "GET" && diagnosticsMatch) {
        try {
            writeJson(
                res,
                200,
                await getFailureDiagnostics(
                    entry,
                    Number(diagnosticsMatch[1]),
                ),
            );
        } catch (error) {
            writeJson(res, 404, {
                error:
                    error instanceof Error
                        ? error.message
                        : "Unable to load failure diagnostics.",
            });
        }
        return;
    }

    const rerunMatch = url.pathname.match(
        /^\/api\/runs\/(\d+)\/rerun-failed$/,
    );
    if (req.method === "POST" && rerunMatch) {
        if (
            req.headers["x-canvas-confirmation"] !== entry.mutationToken
        ) {
            writeJson(res, 403, { error: "Confirmation token is invalid." });
            return;
        }

        const runId = Number(rerunMatch[1]);
        const run = entry.state.data?.runs.find(
            (candidate) => candidate.databaseId === runId,
        );
        if (
            !run ||
            run.status !== "completed" ||
            !isUnsuccessfulConclusion(run.conclusion)
        ) {
            writeJson(res, 409, {
                error: "Only a loaded failed run can be rerun.",
            });
            return;
        }

        try {
            await rerunFailedJobs({
                repository: entry.state.data.repository.nameWithOwner,
                runId,
                workspacePath,
            });
            entry.failureDiagnostics.delete(runId);
            entry.runDetails.delete(runId);
            await refreshRecentRuns(entry);
            writeJson(res, 202, { runId, status: "rerun_requested" });
        } catch (error) {
            writeJson(res, 502, {
                error:
                    error instanceof Error
                        ? error.message
                        : "Unable to rerun failed jobs.",
            });
        }
        return;
    }

    writeJson(res, 404, { error: "Not found" });
}

async function startServer(options) {
    const entry = {
        clients: new Set(),
        failureDiagnostics: new Map(),
        mutationToken: randomUUID(),
        options,
        recentRefreshPromise: undefined,
        refreshPromise: undefined,
        runDetails: new Map(),
        server: undefined,
        state: {
            data: null,
            error: null,
            healthUpdatedAt: null,
            loading: true,
            recentError: null,
            recentUpdatedAt: null,
            updatedAt: null,
        },
        url: undefined,
    };

    const server = createServer((req, res) => {
        handleRequest(entry, req, res).catch((error) => {
            if (!res.headersSent) {
                writeJson(res, 500, {
                    error: error instanceof Error ? error.message : String(error),
                });
            } else {
                res.end();
            }
        });
    });

    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    entry.server = server;
    entry.host = `127.0.0.1:${port}`;
    entry.origin = `http://${entry.host}`;
    entry.url = `http://127.0.0.1:${port}/`;
    return entry;
}

async function getOrStartServer(instanceId, options) {
    const existing = servers.get(instanceId);
    if (existing) {
        return { entry: existing, isNew: false };
    }

    let pending = pendingServers.get(instanceId);
    if (!pending) {
        pending = startServer(options).then((entry) => {
            servers.set(instanceId, entry);
            return entry;
        });
        pendingServers.set(instanceId, pending);
    }

    try {
        return { entry: await pending, isNew: true };
    } finally {
        if (pendingServers.get(instanceId) === pending) {
            pendingServers.delete(instanceId);
        }
    }
}

function summarize(data) {
    return {
        health: data.health.selectedWindow,
        repository: data.repository.nameWithOwner,
        runCount: data.runs.length,
        summary: data.summary,
        updatedAt: new Date().toISOString(),
    };
}

await joinSession({
    canvases: [
        createCanvas({
            id: "actions-dashboard",
            displayName: "GitHub Actions Dashboard",
            description:
                "Shows recent GitHub Actions runs, health metrics, and status filters for a repository.",
            inputSchema: {
                type: "object",
                additionalProperties: false,
                properties: {
                    limit: {
                        type: "integer",
                        minimum: 1,
                        maximum: 100,
                        default: 30,
                    },
                    days: {
                        type: "integer",
                        minimum: 7,
                        maximum: 90,
                        default: 90,
                        description:
                            "Health analysis window in days. Defaults to 90.",
                    },
                    repository: {
                        type: "string",
                        pattern: "^[^/\\s]+/[^/\\s]+$",
                        description:
                            "Optional owner/repository. Defaults to the current workspace repository.",
                    },
                },
            },
            actions: [
                {
                    name: "refresh",
                    description:
                        "Refresh the dashboard with the latest GitHub Actions runs.",
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) {
                            throw new CanvasError(
                                "actions_dashboard_not_open",
                                "The dashboard instance is not open.",
                            );
                        }

                        try {
                            return summarize(await refreshEntry(entry));
                        } catch (error) {
                            throw new CanvasError(
                                "actions_refresh_failed",
                                error instanceof Error
                                    ? error.message
                                    : "Unable to refresh GitHub Actions runs.",
                            );
                        }
                    },
                },
                {
                    name: "get_run_details",
                    description:
                        "Load jobs and steps for one run currently shown in the dashboard.",
                    inputSchema: {
                        type: "object",
                        additionalProperties: false,
                        required: ["runId"],
                        properties: {
                            runId: {
                                type: "integer",
                                minimum: 1,
                            },
                        },
                    },
                    handler: async (ctx) => {
                        const entry = servers.get(ctx.instanceId);
                        if (!entry) {
                            throw new CanvasError(
                                "actions_dashboard_not_open",
                                "The dashboard instance is not open.",
                            );
                        }

                        try {
                            return await getRunDetails(
                                entry,
                                ctx.input.runId,
                            );
                        } catch (error) {
                            if (error instanceof CanvasError) {
                                throw error;
                            }
                            throw new CanvasError(
                                "actions_run_details_failed",
                                error instanceof Error
                                    ? error.message
                                    : "Unable to load run details.",
                            );
                        }
                    },
                },
            ],
            open: async (ctx) => {
                const { entry, isNew } = await getOrStartServer(
                    ctx.instanceId,
                    {
                        days: ctx.input?.days ?? 90,
                        limit: ctx.input?.limit ?? 30,
                        repository: ctx.input?.repository,
                    },
                );
                if (isNew) {
                    await refreshEntry(entry).catch(() => undefined);
                }

                const repository =
                    entry.state.data?.repository.nameWithOwner ??
                    entry.options.repository ??
                    "Current repository";
                return {
                    status: entry.state.error ? "Refresh failed" : "Live",
                    title: `${repository} Actions`,
                    url: entry.url,
                };
            },
            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (!entry) {
                    return;
                }

                servers.delete(ctx.instanceId);
                for (const client of entry.clients) {
                    client.end();
                }
                await new Promise((resolve) =>
                    entry.server.close(() => resolve()),
                );
            },
        }),
    ],
});
