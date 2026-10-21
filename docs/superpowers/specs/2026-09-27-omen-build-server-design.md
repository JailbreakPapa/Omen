# Omen build server — design

Date: 2026-09-27. Status: awaiting review.

## 1. Why

NightFox builds are slow before the first compile starts, and several builds cannot run at once.

Measured on `F:\engine` (2026-09-26):

- Visual Studio "Build Solution" starts one `omen build` per project. All of them wait on the single
  `Intermediate/<config>/build.lock` (`BuildOrchestrator.cs`, `TryAcquireBuildLock`) and run one after another:
  with 16 targets the last waits for the other 15. Agent sessions building different targets block each other the
  same way.
- Every `omen build` process starts cold: rule compilation 3–23 s, rule instantiation and closure 3–21 s, then the
  action graph (static include scan of every translation unit) and an up-to-date pass that stats every input.

Goal: the wait before compiling is gone for any build after the first, and concurrent builds share the machine
instead of queueing.

Related work already landed: unity builds on by default (`e79c2d4`), and a rebuild reason on every action plus a
ranked summary per build (`ddd9e8a`). Over-building fixes are decided from that data separately; they are not part of
this spec.

### Decisions (user, 2026-09-27)

1. The server is a headless background `omen` process, started on demand, exiting when idle — not the GUI, not a
   Windows service.
2. Concurrent requests share one machine-wide pool with priority: interactive builds ahead of background (agent)
   builds.

## 2. Architecture and lifecycle

### 2.1 Pieces

- **`omen build …` is a thin client.** It connects to a named pipe keyed by engine root and configuration
  (`omen-<sanitized root>-<config>`). If no server answers, it starts `omen server` detached (a spawn lock file stops
  two clients starting two servers), then connects. It sends the request — target, configuration, platform,
  architecture, `-j`, `--background`, `--dry-run`, `--clean`, dimensions, coordinator — and streams back messages,
  progress and the result. Console output and exit code are what `omen build` prints and returns today, so Visual
  Studio makefile projects, scripts and agents need no change beyond agents passing `--background`.
- **`omen server`** (hidden command) is the headless process. It owns the warm state (section 3), the scheduler
  (section 4) and the `Intermediate/<config>` directory: it holds `build.lock` for its lifetime, so no second server
  or `--no-server` build can write into the same directory at the same time.
- **`--no-server`** (or environment `OMEN_NO_SERVER=1`) runs today's in-process path unchanged. It stays as the
  fallback, and the server reuses the same orchestrator code.

### 2.2 Lifecycle

- **Idle exit** after 30 minutes without a request (`omen server --idle-minutes <n>`).
- **Version handshake.** The client sends its informational version (`1.0.0+<commit>`). On a mismatch the server
  finishes in-flight requests, refuses new ones with "restarting", and exits; the client then starts a server of its
  own version. `scripts/update-omen-tool.ps1` first runs `omen server --stop` for every running server, then copies.
- **Control.** `omen server --stop` asks servers to finish and exit. `omen server --status` lists warm targets,
  queued and running requests, and pool usage per request.
- **Server crash.** If the pipe drops mid-build the client prints "Omen server died" and reruns that build with
  `--no-server`, so Visual Studio never hangs on a dead server.

### 2.3 Code layout

- New project `src/Omen.Server`: pipe protocol (length-prefixed JSON messages), request queue, warm-state cache,
  scheduler.
- `src/Omen.CLI`: the `server` command, and the client path in `BuildCommand`.
- `src/Omen.Executors`: `BuildOrchestrator` is split so "build this target" can run against caller-supplied warm
  state and a caller-supplied scheduler instead of always compiling rules and owning a `ParallelExecutor`.

## 3. Warm state

### 3.1 What is kept, and what throws it away

Per engine root and configuration:

| State | Produced by | Thrown away when |
|---|---|---|
| Compiled rule assembly and discovered rule-file list | `RuleCompiler` | any `*.target.cs`, `*.module.cs`, `*.plugin.cs`, `*.omenproject.cs` or `plugin.json` is added, changed, deleted or renamed |
| Instantiated rules and module closure, per target/platform/architecture | orchestrator | the rules above are thrown away, or a `.cpp`/`.c`/`.h`/`.hpp`/`.inl` is added, deleted or renamed under a module that globs its `SourceDirectory` |
| Action graph, per target | `ActionGraphBuilder` | its closure is thrown away, or the same add/delete/rename |
| Include-scan result, per file | `IncludeScanner` | that file's content changes (only its entry) |
| File timestamps | the up-to-date pass | the watcher reports a change to that path |
| Toolchain and SDK detection | platform factory | never within one server's life; `omen server --stop` resets |

### 3.2 Change detection

One recursive `FileSystemWatcher` on the engine root. Events are queued and applied at the start of each request, so
state never changes under a running build.

### 3.3 Correctness rule

A warm build makes the same decisions as a cold one; where the server cannot be sure, it goes cold:

- A watcher buffer overflow or watcher error drops all warm state; the next request builds cold and the watcher is
  re-armed.
- Files outside the watched root (Houdini SDK, MSVC and Windows SDK headers, `3rdParty` junctions to other drives)
  are never served from the timestamp cache; they are stat'ed per build as today.
- The up-to-date check (timestamps and digests) still runs on every request. Warm state removes the cost of working
  out what to check, never the check.

### 3.4 Not in scope

Persisting warm state across server restarts (a restart costs one cold build; the on-disk rule cache already covers
the largest piece). Watching drives outside the engine root.

## 4. Scheduling

### 4.1 Pool

The server has N build slots, default logical cores minus 2 (`omen server --jobs <n>`). A request's `-j` caps that
request's own concurrent actions; it does not create a separate pool.

### 4.2 Sharing actions

Every action is keyed by its primary output path (absolute, case-insensitive).

- Request B needs an action that request A has queued or running, **with the same digest**: B waits on that job; it
  runs once.
- **Same output path, different digest**: B's action is queued behind A's for that output path and then runs itself.
  (Two builds writing one file is the corruption the directory lock prevents today; this prevents it per file.)

### 4.3 Priority

- Ready actions are ordered by class — interactive (default) before background (`--background`) — then by today's
  critical-path priority.
- No preemption: a running action is never killed to make room.
- Anti-starvation: with 4 or more slots, background work always keeps at least one slot.
- A shared action takes the highest class of any request waiting on it.

### 4.4 Cancellation

Ctrl+C, Visual Studio Cancel or a client disconnect cancels that request: actions only it needs leave the queue, and
its running ones are killed as a `--no-server` cancel does today. Actions another request also needs keep running.

### 4.5 Failures

A failed action fails every request waiting on it; each client receives the error output. Other requests' independent
work continues. A request succeeds only if all its actions succeeded; exit codes are unchanged.

### 4.6 Per-request work

Pre- and post-build steps, runtime-DLL staging (copies are actions, so they share by output path too) and digest
recording run inside the request. The digest store is one shared, thread-safe instance per intermediate directory.

### 4.7 Observability

Each request writes its own `BuildEvents/*.jsonl` exactly as today, including rebuild reasons and the summary. A shared
action appears in both logs; in the log of the request that did not start it, its `action-started` carries
`"sharedWith": "<request id>"`. `omen server --status` reports pool usage per request.

### 4.8 Unchanged

Distributed builds (`--coordinator`) pass through: the server calls the orchestrator with that option.

## 5. Testing

- **Scheduler unit tests** (fake executor): same-digest sharing runs once; different-digest same-output serializes;
  interactive before background; background keeps one slot at 4+; a shared action inherits the higher class; cancel
  drops only unshared work; a failure reaches every waiting request.
- **Warm-state equivalence test**: a fixture project built cold (`--no-server`) and through a warm server after each
  edit in §3.1's table (touch a header, add a `.cpp`, rename a source, edit a rule file, edit `plugin.json`) yields
  identical action sets and identical skip/run decisions.
- **Watcher failure test**: a forced overflow makes the next request cold.
- **Client tests**: no server → client starts one; version mismatch → old server drains and exits, new one serves;
  server killed mid-build → client falls back to `--no-server` and returns that build's exit code.
- **Two-client integration test**: overlapping targets on the fixture; each shared compile runs once; both succeed.
- **Real check on `F:\engine`**: four parallel `omen build` invocations (as Visual Studio issues them) finish sooner
  than the same four with `--no-server`, and no action runs twice.

## 6. Order

1. Split `BuildOrchestrator` so a build runs against supplied warm state and a supplied scheduler; `--no-server`
   behaviour unchanged (existing suites stay green).
2. Warm-state cache and watcher, with the equivalence and overflow tests.
3. Scheduler: pool, sharing, priority, cancellation, failures.
4. Pipe protocol, `omen server`, client path, version handshake, `--status`/`--stop`, crash fallback.
5. `update-omen-tool.ps1` stops servers before copying; `--background` documented for agent sessions.
6. Real check on `F:\engine`.

