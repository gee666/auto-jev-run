"""Build the readable capacity report from retained measurements."""
import json, pathlib, statistics

ROOT=pathlib.Path(__file__).resolve().parents[1]
OUT=ROOT/'reports/capacity-2026-09-27'
def read(name): return json.loads((OUT/name).read_text(encoding='utf-8-sig'))
def median(rows): return statistics.median(r['seconds'] for r in rows)
def table(headers,rows):
    return '\n'.join(['| '+' | '.join(headers)+' |','| '+' | '.join(['---']*len(headers))+' |']+['| '+' | '.join(map(str,row))+' |' for row in rows])

public=read('concurrency-slots-1-public.json')
options=read('option-counts.json')
cache=read('cache.json')
direct=[read(f'concurrency-slots-{n}-direct.json') for n in [1,2,4]]
long=read('long-context.json')
lines=['# AutoJev capacity and caching tests, 27 September 2026','',
'The existing project was committed as `fd163e6` before these experiments. Tests used the installed AutoJev-27B Q4_K_M on native Windows 11, a Ryzen 9 7900X, 64 GB RAM, and an RTX 4070 with 12 GB VRAM. CUDA mode still uses CPU and system RAM because all model weights do not fit on the GPU. No new model weights or packages were installed.','',
'## Can identical options share a KV cache?','',
'Not independently across different text inputs with this model and its trained format. The prompt is `State -> Question -> Options`. Each option is processed after the state and can attend to it. Its cached attention values and the hybrid model\'s recurrent state therefore depend on that particular state. Reusing those values for changed text would change the computation and can produce incorrect answers.','',
'The underlying server enables common-prefix caching by default. However, this build creates hybrid/recurrent context checkpoints only for completion tasks, while AutoJev uses a classifier task of type `SERVER_TASK_TYPE_RERANK`. The measurements show no useful speedup even for an exact repeat in this configuration. Recognizing a matching prefix in the log does not mean it avoided evaluating that prefix.','',
table(['Options','Input tokens','Exact repeat, median seconds','Changed text, same options, median seconds','Correct'],[[g['option_count'],g['identical_request'][0]['input_tokens'],f"{median(g['identical_request']):.2f}",f"{median(g['changed_state_same_options']):.2f}",f"{sum(r['correct'] for r in g['identical_request']+g['changed_state_same_options'])}/12"] for g in cache['groups']]),'',
'Each row uses six repeats followed by six changed-state trials. The changed state has a different request ID near its beginning, with otherwise identical filler, problem and option list. These are warm inference timings; model loading is excluded.','',
'Useful next steps, depending on workload:','',
'- For exactly repeated full requests, an application-level result cache or coalescing simultaneous identical requests can skip inference. Its key must include the complete state/questions and model/quantization/runtime identity. This is a proposed optimization, not an installed feature.','- Keeping the model loaded between batches already avoids repeated model loading. The existing five-minute idle timer remains in place.','- Reusing parsed option definitions or tokenization can save small amounts of preparation work, but does not remove the model computation.','- A question/options-first format could make that entire prefix reusable, provided the classifier path also supports the required hybrid checkpoints. This needs runtime work plus model evaluation and potentially fine-tuning, since it changes the trained input order. Simply moving the options is not a validated optimization.','',
'## Option count and quality','',
'The hard limit is **255 choices per question**. The classifier has 255 output dimensions and both the model helper and API enforce that limit. The test with 256 choices returned HTTP 400. All input text, question instructions, option names/descriptions, and formatting must also fit the configured 12,288-token context. Long descriptions can exceed context before reaching 255 choices. Multiple questions in one request are separate classifier tasks, not one free shared evaluation.','']
rows=[]
for n in [2,4,8,16,32,64,128,255]:
    rs=[r for r in options['rows'] if r['option_count']==n]
    rows.append([n,f"{sum(r['correct'] for r in rs)}/{len(rs)}",f'{median(rs):.2f}',f"{min(r['input_tokens'] for r in rs)} to {max(r['input_tokens'] for r in rs)}"])
lines += [table(['Choices','Correct','Median seconds','Total input tokens'],rows),'',
'These are eight hand-authored programming scenarios: SQL injection, a counter race, invoice authorization, XSS, HTTP timeouts, credential logging, file-handle cleanup, and numeric sorting. Each has up to two plausible wrong answers; the remaining choices are unrelated engineering remedies. Option order is deterministically shuffled once per scenario/count. All 64 evaluations passed.','',
'This establishes that large option lists work on this quantized model. It does not establish a universal quality-preserving maximum: hundreds of near-duplicate choices, subtle instructions, order effects, and longer real inputs were not exhaustively tested. Use the smallest set that represents the actual decisions and distinguish overlapping options explicitly. There is no evidence here for a magic accuracy cutoff at 16 or 32. For very large taxonomies, a shortlist or hierarchy can reduce cost, but a shortlist can also discard the correct answer and needs its own evaluation.','',
'## Concurrent callers to the installed API','',
'The public API currently runs one inference at a time. It permits one active request and up to 16 queued requests. The following timings include queueing. Every request has 16 options, different state text, and one question. Cases and option descriptions vary across requests. A unique request ID near the start prevents accidental prefix reuse.','']
rows=[]
for g in public['groups']:
    tokens=[r['input_tokens'] for r in g['results']]
    rows.append([f'{min(tokens)} to {max(tokens)}',g['concurrency'],f"{g['p50_seconds']:.2f}",f"{g['p95_seconds']:.2f}",f"{g['throughput_per_second']:.3f}",f"{g['correct']}/{g['requests']}"])
lines += [table(['Input tokens','Concurrent callers','Median seconds','Observed p95 seconds','Requests/sec','Correct'],rows),'',
f"The simultaneous 24-request overload burst returned {sum(r['status']==200 for r in public['overload_24'])} successful responses and {sum(r['status']==429 for r in public['overload_24'])} HTTP 429 responses. All successful burst responses were correct. The queue limit prevents unbounded accumulation. Clients should honor `Retry-After` and use bounded retries with jitter.",'',
'Longer inputs were tested separately through the restored normal API. Each point has only four requests, so the table reports maximum observed latency instead of a percentile.','',
table(['Input tokens','Concurrent callers','Median seconds','Maximum seconds','Requests/sec','Correct'],[[f"{min(r['input_tokens'] for r in g['results'])} to {max(r['input_tokens'] for r in g['results'])}",g['concurrency'],f"{g['p50_seconds']:.2f}",f"{g['max_seconds']:.2f}",f"{g['throughput_per_second']:.3f}",f"{g['correct']}/4"] for g in long['groups']]),'',
'## Experimental backend slots','',
'The normal app was temporarily stopped. Isolated backends on loopback port 18766 tested one, two and four inference slots. Each slot retained a 12,288-token context allowance, so total context allocation was increased with slot count. The model, eight CPU threads, and automatic GPU fit with a 1,536 MiB reserve stayed the same. These tests bypassed the public API\'s one-request gate. They do not imply the shipped API has parallel inference enabled.','',
'Requested batch settings were `-b 2048 -ub 512`. Startup logs show the embedding/classifier mode reduces the effective batch size to 512. These results apply to that installed runtime configuration, not an exhaustive search of all batch sizes, GPU splits, or CPU thread counts.','']
rows=[]
for d in direct:
    for g in d['groups']:
        rows.append([d['slots'],g['concurrency'],f"{g['p50_seconds']:.2f}",f"{g['p95_seconds']:.2f}",f"{g['throughput_per_second']:.3f}",f"{g['correct']}/{g['requests']}"])
lines += [table(['Backend slots','Concurrent callers','Median seconds','Observed p95 seconds','Requests/sec','Correct'],rows),'',
'The direct tests use approximately 1,300 input tokens per request and eight requests at each point. The eight-request batches and single repetitions make p95 only an observed sample statistic, not a production latency guarantee. Each configuration loads a fresh backend, then receives an excluded warm-up call. Windows file cache may already contain the model weights.','',
'## Resource usage','']
rows=[]
for label,groups in [('Installed API, shorter inputs',public['groups']),('Installed API, 8K inputs',long['groups'])]+[(f"Direct, {d['slots']} slots",d['groups']) for d in direct]:
    rs=[g['resources'] for g in groups]
    rows.append([label,f"{max(r['peak_backend_rss_mib'] for r in rs)/1024:.2f}",f"{max(r['peak_backend_private_mib'] for r in rs)/1024:.2f}",f"{max(r['peak_total_gpu_mib'] for r in rs)/1024:.2f}",f"{min(r['mean_cpu_cores'] for r in rs):.1f} to {max(r['mean_cpu_cores'] for r in rs):.1f}",f"{min(r['mean_gpu_util_percent'] for r in rs):.0f} to {max(r['mean_gpu_util_percent'] for r in rs):.0f}%"])
lines += [table(['Configuration','Peak backend RAM, GiB','Peak private commit, GiB','Peak whole-GPU VRAM, GiB','Mean CPU core equivalents','Mean GPU utilization'],rows),'',
'RAM is the backend process working set; private commit is a different Windows memory measure, so do not add those columns. VRAM/utilization include the desktop and other applications. CPU core equivalents are process CPU-seconds divided by wall time; divide by 24 for an approximate fraction of this machine\'s logical CPU capacity. Measurements sample about twice per second and can miss short peaks. Raw data include sample counts and peak GPU utilization. Startup allocation peaks were not sampled.','',
'## Recommendation','',
'Keep **one inference slot** for this machine and runtime. Two and four slots consumed more system RAM and reduced throughput in these tests. The shipped configuration remains unchanged. For latency-sensitive clients, start with one outstanding request per producer, or two if needed to keep a producer pipeline busy. Two to four outstanding requests are a practical small burst, but queueing raises response time. Sixteen waiting requests are an overload buffer, not a speed setting.','',
'For sustained traffic, keep the arrival rate below the measured service rate with headroom for variation: about 0.7 requests/sec at 600 tokens or 0.4 at 1,300 tokens in these workloads. A practical initial budget is about 0.5 and 0.3 requests/sec respectively. This headroom is a recommendation, not another measured limit. Long-context work needs a much lower rate. Concurrent callers from multiple applications share the same capacity.','',
'The measurements are for CUDA with partial CPU offload on this Windows machine. They are not CPU-only, WSL, macOS, or AWS concurrency results. Prior CPU-only latency measurements remain in [the model comparison](../all-tested-models.md).','',
'## Source checks and reproduction','',
'Source commit `aa885f0a86efe36079d70b522f927cd2fc38683b`:','',
'- [Prompt format and 255-option validation](https://github.com/thomasgauthier/jev.cpp/blob/aa885f0a86efe36079d70b522f927cd2fc38683b/tools/autojev/autojev-common.cpp), lines 19, 146, 200 and 215 onward.','- [Default prompt caching](https://github.com/thomasgauthier/jev.cpp/blob/aa885f0a86efe36079d70b522f927cd2fc38683b/tools/server/server-task.h), line 53.','- [Server implementation](https://github.com/thomasgauthier/jev.cpp/blob/aa885f0a86efe36079d70b522f927cd2fc38683b/tools/server/server-context.cpp), common-prefix handling at 3256, hybrid checkpoint restoration at 3390, completion-only checkpoints at 3499, and AutoJev classifier tasks at 5469. These findings came from the installed source checkout.','- [Archived original AutoJev Python format](../research-2026-09-26/autojev-model.py).','',
'Run from the project root with Python 3.12 on Windows. The benchmark code uses only the standard library and the installed NVIDIA utility.','',
'```powershell\npy -3.12 tests/capacity.py public\npy -3.12 tests/capacity.py options\npy -3.12 tests/capacity.py cache\npy -3.12 tests/parallel_capacity.py\npy -3.12 tests/capacity.py long\npy -3.12 tests/capacity_report.py\n```','',
'Run the commands sequentially. `parallel_capacity.py` temporarily stops and then restores the normal app; do not use it while other applications need inference. Raw JSON, launch metadata, backend logs and environment details are retained alongside this report. No inference defaults or startup registration were changed.','']
(OUT/'report.md').write_text('\n'.join(lines),encoding='utf-8')
