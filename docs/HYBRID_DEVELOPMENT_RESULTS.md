# Hybrid Development Results

Results from running the same task through Armada's FullPipeline with frontier models doing the thinking
(Claude Code, Codex) and a local model on Mux doing the hands-on work. The goal is to learn how capable the
local model has to be before the hybrid split pays off. Add a column to the comparison table and a section under
[Per-model notes](#per-model-notes) for every model tried.

## Test setup

| Item | Value |
|---|---|
| Admiral | view.homedns.org, server build 222d6ae7 (no post-stage build check; see [Caveats](#caveats)) |
| Harbor | Developer MacBook Pro, split mode; Mux v1.4.0 |
| Local inference | Ollama on the GB10 behind the view.homedns.org:8900 proxy (the proxy times out requests after 60 s) |
| Vessel | jchristn/Timestamps, reset to 4db8e40 content before every run |
| Pipeline | FullPipeline: Product Manager, Architect, Worker, Usability Engineer, Test Engineer, Linter, Judge, Recorder; no review gates |
| Persona routing | PM and Architect: Claude Code, then Codex. Judge and Recorder: Codex, then Claude Code. Worker, Usability Engineer, Test Engineer, Linter: Mux (one captain) |
| Recovery | Resume in place up to 3 times per mission; Judge rework up to 2 rounds on the Worker, then 1 round escalated to the planner's captain (Claude) |
| Mux endpoint settings | Ollama adapter, temperature 0.1, 131072 context, 300 s request timeout, 50 agent iterations per run |
| Task | Add `Stop()` and `Elapsed` to `Timestamp`, add tests to the existing suite under `src/Test.Shared`, document both in README.md; all tests must pass |

The task prompt, used verbatim for every run:

> In src/Timestamps/Timestamp.cs, add two backward-compatible members to the Timestamp class. (1) A public method
> Stop() that sets End to DateTime.UtcNow if End is not already set, and does nothing if End is already set, so
> calling it twice keeps the first end time. (2) A public read-only property Elapsed of type TimeSpan that returns
> End - Start when End is set, and DateTime.UtcNow - Start otherwise. Give both members XML documentation comments
> in the same style as the existing members, and follow the repository's coding conventions (no var, explicit
> types). Do not change or remove any existing member. Add tests for both members in the existing test suite under
> src/Test.Shared, following how the existing tests are written: Stop sets End, a second Stop keeps the first End,
> Elapsed is non-negative and grows before Stop, and Elapsed is fixed after Stop. Document both members in
> README.md. All tests must pass with dotnet run --project src/Test.Automated --framework net10.0.

## Comparison

| Metric | gpt-oss:20b | gpt-oss:120b | qwen3-coder:30b | qwen3.8:27b |
|---|---|---|---|---|
| Date | 2026-10-15 | 2026-10-15 | 2026-10-16 | 2026-10-16 |
| Voyage | vyg_mva04dcz_W8l0yGzpWUR | vyg_mva59krh_3QNTq2EeOpY | vyg_mva85itu_NtRRAtlpBGi | vyg_mvaa171g_LDAFLhao2sJ |
| Mux version | 1.4.0 | 1.4.0 | 1.4.0 | 1.4.0 for slice 1 Worker to Linter; 1.4.1 after |
| Outcome | Landed (c5dd6fe); Claude rewrote the code | Stopped by hand at 80 min; nothing landed | Landed (f2ed270, b50685e); code is the local model's | **Landed (befbb8e, 39382b2); every commit is the local model's** |
| Wall time | 49.2 min | 80+ min (not finished) | 51.5 min | 83.8 min |
| Architect slices | 1 | 2 (code and tests; README) | 2 (code and tests; README) | 2 (code and tests; README) |
| First Worker pass builds and passes tests | No | No (logic right; test registration broken, test method missing) | **Yes, 36/36** | **Yes, 36/36** |
| First Worker pass time / launches | 20.2 min / 4 | 24.4 min / 3 | **4.5 min / 1** | 8.2 min / 1 |
| Committed its own work | Never | Never | Yes (first pass and README) | **Yes, every stage** |
| Kept BOM and CRLF | No | No | No | **Yes** (worked around the Mux bug) |
| Malformed tool calls (Ollama 500) | 4 | 3 | 0 | **0** |
| Repetition loops (Ollama 500) | 1 | 0 | 0 | **0** |
| Proxy 502 (60 s timeout) | 0 | 0 in run (cold load failed probes) | 1 | **0** |
| 50-iteration limit hits | 0 | 4 | 1 | **0** |
| Resumes in place, all Mux stages | 5 | 7 | 2 | **0** |
| Judge verdicts | NEEDS_REVISION x3, PASS x1 | NEEDS_REVISION x4 | NEEDS_REVISION x5, PASS x2 | **PASS x2, NEEDS_REVISION x1** |
| What the Judge rejected | Broken build, wrong edits | Broken build, missing test method, BOM, generated XML | Only BOM, CRLF, generated XML, a stray backup file, 4 missing awaits | One missing README sentence (use UTC for Start and End) |
| Rework rounds | 2 local + 1 Claude | 3 local (cancelled) | 4 local + 1 Claude | **1 local** |
| Claude takeover | Yes; rewrote the logic and tests | Not reached | Slice 1 only; format-only fix (CRLF, generated XML) | **None** |
| Tests on main after landing | 36 | n/a | 36 | 37 (Test Engineer added a case) |
| Mux missions / tokens in / out | 6 / 3.98M / 31.3k | 10 / 3.11M / 22.8k | 12 / 15.19M / 23.5k | 9 / 5.76M / 77.1k |
| Claude missions / tokens in / out | 3 / 934k / 21.0k | 2 / 327k / 11.9k | 4 / 1.02M / 24.8k (one Judge routed to Claude) | **2 / 315k / 14.3k** (planning only) |
| Codex missions / tokens in / out | 5 / 1.57M / 8.0k | 4 / 472k / 3.2k | 8 / 2.23M / 11.1k | 5 / 1.54M / 7.7k |
| Active Mux minutes | 40.6 | 75.5 | 39.4 | 75.2 |

Token and minute figures come from Armada's token-usage records and mission events (`captain.launched` to
`mission.work_produced`). They count only stages that ran. The 120b totals stop at cancellation.

## Per-model notes

### gpt-oss:20b

- **Result:** landed, but every line that mattered came from Claude's escalated rework. Without the takeover it would
  have failed.
- **Tool calls:** the weakest of the models tried. Four Ollama 500s from malformed tool-call JSON and one "token
  repeat limit reached" loop. Each one killed the Mux run, and Armada resumed it in place.
- **Editing:** it misplaced edits in the existing files. It mixed up the line numbers that `read_file` shows with the
  file's content, broke the class structure, and never got the branch to build. Two local rework rounds still left
  the build broken.
- **Commits:** it never committed its own work; Armada's leftover-work auto-commit picked it up every time.
- **Speed:** fast per call. Usability, Test Engineer and Linter each took a few minutes, but they worked on a broken
  build.
- **Takeaway:** too weak to make multi-part edits to existing C# files without help.

### gpt-oss:120b

- **Result:** stopped by hand after about 80 minutes, when the user switched to the next model. Nothing landed. Both
  slices had NEEDS_REVISION verdicts, and slice 1 was on its last local rework round before a Claude takeover.
- **Tool calls:** better than 20b, but not clean. Three malformed tool-call 500s; one example was
  `{"file_path":"src/Test.Shared/TimestampsSuites.cs","limit":15","offset":"?"]}`. No repetition loops.
- **Editing:** the `Stop()` and `Elapsed` code was correct on the first pass, with good XML docs. The test part
  failed:
  - it added `StopElapsedSuite()` to the suite list twice, with a missing comma;
  - it never wrote the method itself.
  
  Rework 1 fixed the syntax but still left the method out, so the branch never built.
- **Speed:** slower, and prone to wandering. It hit the 50-iteration limit four times. The slice 1 Linter spent
  32 minutes and all three resumes trying to repair the build, and failed.
- **Cold start:** a cold load of the 65 GB model took more than 60 s, so the first probes got 502 errors from the
  proxy. A warm-up request with `keep_alive` fixed that.
- **Takeaway:** good enough for the logic. Not reliable at a coordinated two-part edit (write a method and register
  it) without feedback from a failing build.

### qwen3-coder:30b

- **Result:** landed, and the code is qwen's. Ignoring whitespace and line endings, the final source equals qwen's
  first Worker pass plus 8 lines it added in its own rework. Claude's escalated round on slice 1 only restored CRLF
  line endings and reverted the generated `Timestamps.xml`. The README slice passed without any Claude help.
- **First pass:** 4.5 minutes, one launch, committed with a proper message. The branch built, and all 36 tests passed,
  including 7 new `StopElapsedSuite` cases written in the house style. This is the only model so far whose first pass
  worked.
- **Tool calls:** no malformed tool calls and no repetition loops. One 50-iteration limit hit and one proxy 502 (60 s
  timeout), both during slice 1 rework 1, both recovered by resume in place.
- **Why it still went around the rework loop five times:** every rejection was about encoding, scope, or style, none
  about behavior:
  - the BOM was lost, which is a Mux bug (see [Tooling issues](#tooling-issues-found));
  - when it restored the BOM by rewriting the file, it turned CRLF into LF;
  - the build regenerated the tracked `Timestamps.xml`;
  - it left a `README.md.backup` that Armada's auto-commit picked up;
  - four test cases were missing the `await Task.CompletedTask;` the house style wants.
- **Tokens:** by far the most local input tokens (15.2M), from many short tool-heavy iterations that each resend the
  context. That is free on local hardware, but it costs throughput.
- **Takeaway:** strong enough to be the Worker for this kind of task. What remains is tooling and repo hygiene, not
  model capability.

### qwen3.8:27b

- **Result:** landed with no Claude involvement after planning. Every commit on main (eight, across both slices) was
  written by the local model. Codex reviewed three times: slice 1 passed on the first review, the first first-review
  pass by any model, and slice 2 passed after one rework.
- **First pass:** 8.2 minutes, one launch, committed with a proper message. The branch built and all 36 tests
  passed. Then the downstream stages added real value:
  - Usability Engineer clarified the `Elapsed` XML doc to say negative values are allowed when End is before Start;
  - Test Engineer added a 37th test (Elapsed goes live again when End is cleared);
  - Usability Engineer added a matching README note.
- **Reliability:** zero failures across 10 Mux runs. No malformed tool calls, no repetition loops, no proxy 502s, no
  iteration-limit hits, no resumes. No other model came close.
- **Encoding and scope:** it noticed that Mux's `edit_file` would drop the BOM. It restored `Timestamp.cs` with
  `git checkout` and edited it with a byte-level Python script, which kept the BOM and CRLF. It used `edit_file`
  only on the BOM-less test file. It put the regenerated `Timestamps.xml` back before committing, and left no stray
  files. It ran the build and the tests itself before committing.
- **The one rejection:** the README left out the UTC guidance the Architect's brief asked for ("Use UTC values for
  Start and End"). That is a real content gap, not tooling, and one rework fixed it.
- **Speed:** the slowest model that finished. It used 75 active Mux minutes, against 39 for qwen3-coder, and 83.8 min
  wall time. It writes a lot: 77k output tokens, three times qwen3-coder's. The review stages are slow even on
  README-only work (10 to 15 minutes each).
- **Mux version:** the slice 1 Worker, Usability Engineer, Test Engineer and Linter ran on Mux 1.4.0, and everything
  after ran on 1.4.1 (which fixes the BOM bug). The model had already worked around the bug, so the upgrade did not
  change its result.
- **Takeaway:** the first local model that did the whole job to a frontier reviewer's standard. It trades about 30
  minutes of wall time for no rework churn and no frontier-model coding.

## Patterns across models

- **The hard step:** changing an existing test file in two coordinated places (write the suite method and register
  it). Both gpt-oss models failed it, and both qwen models got it right first time. The new member code is easy for
  every model.
- **Model family and tuning matter more than size.** qwen3-coder:30b and qwen3.8:27b both beat gpt-oss:120b, a model
  about four times their size, on correctness, tool-call reliability and speed.
- **Two kinds of good local model:**
  - qwen3-coder is fast but careless with details (encoding, stray files, house-style awaits), so it relies on
    the rework loop;
  - qwen3.8 is slow but careful: it checks its own work, protects encodings and scope, and passes review.
  
  With the tooling fixes below, qwen3-coder's main weakness mostly goes away.
- **Claude's role shrinks as the local model improves.** Claude's input tokens: 934k (20b, rewrote the code), then
  1.02M (qwen3-coder, a format-only takeover plus one Judge), then 315k (qwen3.8, planning only).
- **Broken builds flow downstream.** On the 222d6ae7 build, nothing checks the build after the Worker, so Usability,
  Test Engineer and Linter run on code that does not compile. Codex's Judge catches it, but only after three wasted
  stages per round.
- **The Codex Judge is consistent and specific.** Its findings name the file, the line and the fix. But it never
  completed a build or test run itself (see [Tooling issues](#tooling-issues-found), item 5).

## Tooling issues found

These hit every model equally and inflate rework rounds. Fixing them should help any local model.

1. **Mux dropped the UTF-8 BOM on every edit (fixed in Mux 1.4.1).** `EditFileTool`, `MultiEditTool` and
   `WriteFileTool` read files with `File.ReadAllTextAsync` and wrote them with `File.WriteAllTextAsync(path, string)`,
   which writes UTF-8 without a BOM. A direct reproduction showed all three dropping the BOM on 1.4.0 and keeping it
   on 1.4.1. Line endings were always kept. Details are in `archive/BUGFIXES_FROM_ARMADA.md` in the Mux repository.
2. **Mux ended the run on Ollama's tool-call parse 500s (fixed in Mux 1.4.1).** `enableMalformedToolCallRecovery` was
   never read, and `LlmClient` treated any HTTP status as final. So `error parsing tool call` and `token repeat limit
   reached` ended the run. This was the main failure mode for gpt-oss. The fix still needs to be confirmed in a live
   gpt-oss run.
3. **Timestamps tracks a generated file.** `src/Timestamps/Timestamps.xml` is regenerated by every build, so any stage
   that builds changes it, and the Judge flags it as out of scope. Fix it in the repo (stop tracking it, or have the
   Judge ignore build outputs).
4. **Armada's leftover-work auto-commit takes everything.** It committed qwen3-coder's `README.md.backup`. It should
   skip obvious scratch files (`*.bak`, `*.backup`, `*.orig`, `*.rej`), or the Worker prompt should say to delete them.
5. **The Codex Judge cannot run `dotnet` on the Harbor.** Codex reported that `dotnet build` and `dotnet run`
   "stalled after a CSSM diagnostic" and that "sandbox restrictions denied process inspection". Codex's macOS sandbox
   blocks keychain/CSSM access, which hangs `dotnet`. So every Judge verdict so far came from reading the source plus
   earlier stages' test reports, never from a test run of its own. Fix it in the Codex captain's sandbox settings on the
   Harbor.
6. **The proxy's 60 s timeout.** It cuts off cold model loads and some long prompt evaluations. Raise it, or keep the
   Worker model warm with `keep_alive`.

## Caveats

- **Build check:** all runs used build 222d6ae7. Build 8d31441e adds a stage check that runs the vessel's build and
  test commands after every Mux stage and resumes the model with the failure output. It is expected to help every
  model, and the weaker ones most, and it changes the results. Record which build each run used.
- **Slicing:** the Architect (Claude) decides how many slices to make, so runs on the same prompt can differ (1 slice
  for 20b, 2 for the others). Extra slices add serial Mux stages, because there is one Mux captain.
- **Sample size:** one run per model. Treat differences of a few minutes or a couple of errors as noise.

## Raw stats

Per-mission stats from `vstats.py` (scratch tooling), in pipeline order. Minutes are active time from first launch
to work produced.

### gpt-oss:20b

```
persona            runtime     active launches resumes verdict            in_tok out_tok  notes
Product Manager    ClaudeCode     1.3        1       0                    437688    9391  claude-opus-5-5
Architect          ClaudeCode     0.7        1       0                    145843    4488  claude-opus-5-5
Worker             Mux           20.2        4       3                    268591    1569  gpt-oss:20b
Usability Engineer Mux            9.5        2       1                    774336    8618  gpt-oss:20b
Test Engineer      Mux            1.7        1       0                    697004    3671  gpt-oss:20b
Linter             Mux            3.5        2       1                    930273    4920  gpt-oss:20b
Judge              Codex          0.7        1       0 NEEDS_REVISION     183514    1066  Codex
Recorder           Codex          1.1        1       0                    539128    2875  Codex
Worker             Mux            2.6        1       0                    696503    5846  gpt-oss:20b rework
Judge              Codex          0.9        1       0 NEEDS_REVISION     205199    1273  Codex
Worker             Mux            3.1        1       0                    614485    6696  gpt-oss:20b rework
Judge              Codex          0.8        1       0 NEEDS_REVISION     218145    1129  Codex
Worker             ClaudeCode     1.1        1       0                    350485    7112  claude-opus-5-5 rework escalated
Judge              Codex          1.5        1       0 PASS               422393    1650  Codex
```

### gpt-oss:120b (cancelled)

```
persona            runtime     active launches resumes verdict            in_tok out_tok  notes
Product Manager    ClaudeCode     1.1        1       0                    216793    6534  claude-opus-5-5
Architect          ClaudeCode     0.8        1       0                    110300    5378  claude-opus-5-5
Worker             Mux           24.4        3       2                    764641    5754  gpt-oss:120b
Usability Engineer Mux            1.1        1       0                     36546    1419  gpt-oss:120b
Test Engineer      Mux            0.5        1       0                     35881     194  gpt-oss:120b
Linter             Mux           32.4        4       3                    409753    4652  gpt-oss:120b
Judge              Codex          0.6        1       0 NEEDS_REVISION     160459    1052  Codex
Worker             Mux            2.1        1       0                         0       0  slice 2
Usability Engineer Mux            0.6        1       0                     34861     415  gpt-oss:120b
Test Engineer      Mux            1.1        1       0                    393552    1298  gpt-oss:120b
Linter             Mux            0.9        1       0                     70559    1030  gpt-oss:120b
Judge              Codex          0.4        1       0 NEEDS_REVISION      92178     789  Codex
Worker             Mux            8.8        2       1                   1036366    5856  gpt-oss:120b rework slice 1
Judge              Codex          0.9        1       0 NEEDS_REVISION     219802    1355  Codex
Worker             Mux            3.6        2       1                    326708    2231  gpt-oss:120b rework slice 2
Judge              Codex          -          1       0 NEEDS_REVISION                     slice 2, finished after cancel
Worker             Mux            -          1       0 (cancelled)                        rework 2 slice 1
```

### qwen3-coder:30b

```
persona            runtime     active launches resumes verdict            in_tok out_tok  notes
Product Manager    ClaudeCode     1.0        1       0                    162268    6808  claude-opus-5-5
Architect          ClaudeCode     1.1        1       0                    201022    7901  claude-opus-5-5
Worker             Mux            4.5        1       0                   1855653    4480  qwen3-coder:30b
Usability Engineer Mux            4.0        1       0                   2118064    2794  qwen3-coder:30b
Test Engineer      Mux            2.0        1       0                    845368     967  qwen3-coder:30b
Linter             Mux            1.8        1       0                    926826    1091  qwen3-coder:30b
Judge              Codex          1.4        1       0 NEEDS_REVISION     305948    1626  Codex
Recorder           Codex          1.0        1       0                    472868    2020  Codex
Worker             Mux            1.8        1       0                   1146615    1542  qwen3-coder:30b slice 2
Usability Engineer Mux            2.1        1       0                    902549    1960  qwen3-coder:30b
Test Engineer      Mux            1.2        1       0                    412187     777  qwen3-coder:30b
Linter             Mux            1.2        1       0                    525663     794  qwen3-coder:30b
Judge              Codex          0.5        1       0 NEEDS_REVISION      95195     884  Codex
Recorder           Codex          1.3        1       0                    624358    2324  Codex
Worker             Mux           13.7        3       2                   2602028    3647  qwen3-coder:30b rework 1 slice 1
Judge              Codex          1.1        1       0 NEEDS_REVISION     292976    1249  Codex
Worker             Mux            1.8        1       0                    984470    1571  qwen3-coder:30b rework 1 slice 2
Judge              Codex          0.5        1       0 NEEDS_REVISION     100896     907  Codex
Worker             Mux            3.4        1       0                   1982989    2525  qwen3-coder:30b rework 2 slice 1
Judge              Codex          1.0        1       0 NEEDS_REVISION     247462    1293  Codex
Worker             Mux            1.9        1       0                    888317    1395  qwen3-coder:30b rework 2 slice 2
Judge              Codex          0.4        1       0 PASS                93895     771  Codex
Worker             ClaudeCode     1.1        1       0                    493137    7424  claude-opus-5-5 rework 3 (escalated) slice 1
Judge              ClaudeCode     0.5        1       0 PASS               166563    2716  claude-opus-5-5
```

### qwen3.8:27b

```
persona            runtime     active launches resumes verdict            in_tok out_tok  notes
Product Manager    ClaudeCode     1.3        1       0                    203765    8242  claude-opus-5-5
Architect          ClaudeCode     1.0        1       0                    111003    6068  claude-opus-5-5
Worker             Mux            8.2        1       0                    984445    9976  qwen3.8:27b
Usability Engineer Mux           11.7        1       0                    968763   11167  qwen3.8:27b
Test Engineer      Mux            8.8        1       0                   1072645    8697  qwen3.8:27b
Linter             Mux            8.2        1       0                    633458    6944  qwen3.8:27b
Judge              Codex          2.1        1       0 PASS               467423    1715  Codex
Recorder           Codex          1.4        1       0                    478722    2683  Codex
Worker             Mux            3.2        1       0                    400810    2715  qwen3.8:27b slice 2
Usability Engineer Mux           10.2        1       0                    374014   11676  qwen3.8:27b
Test Engineer      Mux           14.7        1       0                    667354   16777  qwen3.8:27b
Linter             Mux            7.0        1       0                    185552    6941  qwen3.8:27b
Judge              Codex          0.6        1       0 NEEDS_REVISION     103608     850  Codex
Recorder           Codex          1.1        1       0                    394263    1784  Codex
Worker             Mux            3.2        1       0                    468983    2215  qwen3.8:27b rework 1 slice 2
Judge              Codex          0.5        1       0 PASS                99454     692  Codex
```
