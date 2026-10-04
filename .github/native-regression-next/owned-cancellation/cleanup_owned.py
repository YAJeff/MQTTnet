"""Always-run cleanup for exact journaled resources, including interrupted builds."""
import argparse
import json
import re
import subprocess
import time
import uuid
from pathlib import Path

LABEL = 'mqttnet-context-owner'
CLEANUP_DEADLINE = None


def call(args, missing=False):
    remaining = 10 if CLEANUP_DEADLINE is None else min(10, CLEANUP_DEADLINE - time.monotonic())
    if remaining <= 0:
        raise TimeoutError('Cleanup hard command envelope')
    result = subprocess.run(['docker'] + args, capture_output=True, timeout=remaining)
    if len(result.stdout) + len(result.stderr) > 16 * 1024**2:
        raise RuntimeError('Cleanup receipt too large')
    if result.returncode:
        message = result.stderr.decode(errors='replace').lower()
        if missing and any(x in message for x in ('no such object', 'no such image', 'no such network', 'not found')):
            return None
        raise RuntimeError('Docker cleanup failure: ' + message)
    return result.stdout.decode()


def validate_journal(root, path, marker):
    row = json.loads(path.read_text())
    token = row['token']
    if not re.fullmatch('[0-9a-f]{32}', token) or row['parentOwnerToken'] != marker['token']:
        raise RuntimeError('Journal ownership token differs')
    prefix = 'mqttnet-cancel-' + token
    if (row['network'] != prefix or row['fixture'] != prefix + '-fixture' or
            row['imageTag'] != prefix + ':fixture' or row['builder'] != prefix + '-image-builder' or
            row['workers'] != [prefix + '-worker-0', prefix + '-worker-1']):
        raise RuntimeError('Journal resource names differ')
    if row['jobRoot'] != str(root) or path.parent.parent != root:
        raise RuntimeError('Journal escaped exact owned root')
    return row


def cleanup(root, only_journal=None):
    root = Path(root).resolve()
    marker = json.loads((root / 'OWNERSHIP.json').read_text())
    if marker['jobRoot'] != str(root) or marker['work'] != str(root / 'work'):
        raise RuntimeError('Exact ownership marker required')
    paths = [Path(only_journal)] if only_journal else sorted(root.glob('receipts-*/OWNED-FIXTURE-JOURNAL.json'))
    results, errors = [], []
    global CLEANUP_DEADLINE
    deadline = time.monotonic() + 120
    CLEANUP_DEADLINE = deadline
    for path in paths:
        row = validate_journal(root, path, marker)
        token = row['token']
        selector = LABEL + '=' + token
        proof = {'journal': str(path), 'token': token, 'objects': []}
        results.append(proof)
        try:
            # Discover the labeled materializer and committed image even if a
            # create/copy/commit/tag response was lost. No unowned build stages.
            listed = call(['ps', '-aq', '--filter', 'label=' + selector]).split()
            for name in list(dict.fromkeys(row['workers'] + [row['fixture'], row['builder']] + listed)):
                if time.monotonic() >= deadline:
                    raise TimeoutError('Cleanup resource deadline')
                raw = call(['inspect', name], missing=True)
                if raw is not None:
                    actual = json.loads(raw)[0]
                    if actual['Config']['Labels'].get(LABEL) != token:
                        raise RuntimeError('Container actual ownership differs')
                    call(['rm', '-f', name])
                if call(['inspect', name], missing=True) is not None:
                    raise RuntimeError('Fresh container absence failed')
                proof['objects'].append({'container': name, 'freshAbsent': True})
            raw = call(['network', 'inspect', row['network']], missing=True)
            if raw is not None:
                actual = json.loads(raw)[0]
                if actual['Labels'].get(LABEL) != token or actual['Containers']:
                    raise RuntimeError('Network ownership or dependencies differ')
                call(['network', 'rm', row['network']])
            if call(['network', 'inspect', row['network']], missing=True) is not None:
                raise RuntimeError('Fresh network absence failed')
            proof['objects'].append({'network': row['network'], 'freshAbsent': True})
            image_ids = call(['image', 'ls', '-q', '--filter', 'label=' + selector]).split()
            raw_tag = call(['image', 'inspect', row['imageTag']], missing=True)
            if raw_tag is not None:
                tagged = json.loads(raw_tag)[0]
                if tagged['Config']['Labels'].get(LABEL) != token:
                    raise RuntimeError('Interrupted build tag ownership differs')
                image_ids.append(tagged['Id'])
            # Descendants first; retry ordering only for image dependencies, never
            # native qualification. No force deletion, shared base removal or prune.
            pending = list(dict.fromkeys(image_ids))
            while pending:
                progress = False
                for image in list(pending):
                    if time.monotonic() >= deadline:
                        raise TimeoutError('Image cleanup deadline')
                    raw = call(['image', 'inspect', image], missing=True)
                    if raw is None:
                        pending.remove(image)
                        progress = True
                        continue
                    actual = json.loads(raw)[0]
                    if actual['Config']['Labels'].get(LABEL) != token:
                        raise RuntimeError('Image actual ownership differs')
                    tags = actual.get('RepoTags') or []
                    if any(tag != row['imageTag'] for tag in tags):
                        raise RuntimeError('Owned image unexpectedly has shared tags')
                    result = subprocess.run(['docker', 'image', 'rm'] + (tags or [image]),
                                            capture_output=True, timeout=max(0.001, min(10, deadline - time.monotonic())))
                    if result.returncode == 0:
                        if call(['image', 'inspect', image], missing=True) is not None:
                            raise RuntimeError('Fresh image ID absence failed')
                        proof['objects'].append({'image': image, 'freshAbsent': True})
                        pending.remove(image)
                        progress = True
                    elif b'dependent child images' not in result.stderr.lower():
                        raise RuntimeError('Owned image removal failed: ' + result.stderr.decode(errors='replace'))
                if not progress:
                    raise RuntimeError('Owned image dependencies could not be resolved')
            if call(['image', 'inspect', row['imageTag']], missing=True) is not None:
                raise RuntimeError('Fresh image tag absence failed')
            if (call(['ps', '-aq', '--filter', 'label=' + selector]).strip() or
                    call(['network', 'ls', '-q', '--filter', 'label=' + selector]).strip() or
                    call(['image', 'ls', '-q', '--filter', 'label=' + selector]).strip()):
                raise RuntimeError('Fresh labeled resource discovery found leftovers')
            proof['freshLabeledAbsence'] = True
        except BaseException as exc:
            errors.append({'journal': str(path), 'failure': repr(exc)})
    output = {'completed': not errors, 'journals': results, 'failures': errors,
              'sharedBaseRemoved': False, 'globalPrune': False}
    target = root / ('OWNED-FIXTURE-CLEANUP-' + uuid.uuid4().hex + '.json')
    target.write_text(json.dumps(output, indent=2) + '\n')
    if errors:
        raise RuntimeError('Owned fixture cleanup failed; original failures preserved')
    return output


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--job-root', required=True)
    cleanup(parser.parse_args().job_root)
