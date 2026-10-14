#!/usr/bin/env python3
"""Generate varied historical screenshot usage; preview by default, never invoke inference or dispatch work."""
import argparse
import datetime
import importlib.util
import json
import math
from pathlib import Path
import random
import secrets
import uuid

DIRECTORY = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location('prompt_deployment', DIRECTORY / 'apply-prompt-improvements.py')
AUTH = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUTH)


def generate(days=42, seed=None, now=None):
    if not 31 <= days <= 365:
        raise ValueError('Choose 31-365 days')
    seed = secrets.randbits(64) if seed is None else seed
    rng = random.Random(seed)
    now = now or datetime.datetime.now(datetime.timezone.utc)
    records = []
    models = [('gpt-6.1-sol', 'codex'), ('claude-sonnet-4', 'claudecode'), ('gpt-oss:120b', 'mux')]
    # Workday peaks in late UTC hours, with occasional night work, bursts and idle dates.
    weights = [0.08 + math.exp(-0.5 * ((hour - 19) / 4) ** 2) for hour in range(24)]
    for offset in range(days):
        day = now.date() - datetime.timedelta(days=days - offset - 1)
        weekend = day.weekday() >= 5
        activity = rng.lognormvariate(-0.12, 0.55)
        count = max(1, round((16 if weekend else 48) * activity))
        if rng.random() < (0.20 if weekend else 0.06) and offset not in (0, days - 1):
            count = 0
        if rng.random() < 0.13:
            count = round(count * rng.uniform(1.5, 2.8))
        if day == now.date():
            count = max(1, round(count * (now.hour + now.minute / 60) / 24))
        for _ in range(count):
            timestamp = None
            for attempt in range(32):
                hour = rng.choices(range(24), weights=weights)[0]
                timestamp = datetime.datetime.combine(day, datetime.time(hour, rng.randrange(60), rng.randrange(60)), datetime.timezone.utc)
                if timestamp <= now:
                    break
            if timestamp > now:
                midnight = datetime.datetime.combine(day, datetime.time(), datetime.timezone.utc)
                timestamp = midnight + datetime.timedelta(seconds=rng.uniform(0, (now - midnight).total_seconds()))
            model, runtime = rng.choices(models, weights=[0.44, 0.34, 0.22])[0]
            context = min(500000, max(300, int(rng.lognormvariate(math.log(9000), 0.95))))
            cached = int(context * rng.uniform(0.25, 0.9)) if rng.random() < 0.70 else 0
            output = max(80, int(context * rng.uniform(0.025, 0.22)))
            records.append({'Model': model, 'Runtime': runtime, 'CreatedUtc': timestamp.isoformat().replace('+00:00', 'Z'),
                            'InputTokens': context, 'OutputTokens': output, 'CachedTokens': cached})
    records.sort(key=lambda row: row['CreatedUtc'])
    return {'seed': seed, 'days': days, 'records': records}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--tenant', default='default')
    parser.add_argument('--session-file')
    parser.add_argument('--days', type=int, default=42)
    parser.add_argument('--seed', type=int)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--manifest', required=True, help='New external mode-600 file recording exact data, batches and receipts')
    parser.add_argument('--resume', help='Replay an existing manifest idempotently, rather than generate another random batch')
    args = parser.parse_args()
    if args.resume:
        manifest = json.loads(Path(args.resume).read_text())
        if manifest['base'] != args.base:
            raise ValueError('Manifest target differs from --base')
    else:
        generated = generate(args.days, args.seed)
        batch = 'screenshot-' + uuid.uuid4().hex[:16]
        manifest = {'base': args.base, 'synthetic': True, 'seed': generated['seed'], 'days': generated['days'],
                    'batches': [{'BatchId': batch + '-' + str(i // 250), 'Records': generated['records'][i:i + 250]}
                                for i in range(0, len(generated['records']), 250)], 'receipts': []}
    AUTH.external_write(args.manifest, manifest)
    count = sum(len(batch['Records']) for batch in manifest['batches'])
    print(json.dumps({'mode': 'apply' if args.apply else 'preview', 'days': manifest['days'], 'records': count,
                      'batches': len(manifest['batches']), 'manifest': args.manifest}))
    if not args.apply:
        return
    token = AUTH.session(args)
    receipts = []
    for batch in manifest['batches']:
        result = AUTH.request(args.base, token, 'POST', '/api/v1/token-usage/synthetic', batch, timeout=120)
        receipts.append(result)
        print(json.dumps(result), flush=True)
        manifest['receipts'] = receipts
        # Manifest was created restricted above; append progress without changing its permissions.
        Path(args.manifest).write_text(json.dumps(manifest, indent=2) + '\n')
    print('Synthetic screenshot usage imported; no inference or mission load generated.')


if __name__ == '__main__':
    main()
