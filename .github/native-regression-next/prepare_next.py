"""Proposed bounded hosted preparation only; no native compiler/runtime invoked.
Uses exact original V6 Linux ZIP and the existing pinned toolchain preparer.
Root must review before assigning. No owner local invocation.
"""
import argparse
import importlib.util
import json
import os
import time
import zipfile
from pathlib import Path, PureWindowsPath
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
ORIGINAL_ZIP_SHA256 = '643D14185BDCCA6A52E2AFA7D2928B7503F48E64A1D4DC8B613EAFC70498B662'


def windows_package_records(records):
    expected = {family + '.5.2.0-local.tlscontext.24208d37.' + extension
        for family in ['MQTTnet', 'MQTTnet.Server', 'MQTTnet.AspNetCore']
        for extension in ['nupkg', 'snupkg']}
    selected = []
    for row in records:
        provenance = PureWindowsPath(row['path'])
        if not provenance.is_absolute(): raise RuntimeError('Declared Windows package provenance must be absolute')
        name = provenance.name
        if name not in expected: raise RuntimeError('Exact original package filename/case differs: ' + name)
        selected.append((row, name))
    if len(selected) != 6 or {name for _, name in selected} != expected:
        raise RuntimeError('Original six package identities differ or duplicate')
    return selected


def prepare(args):
    spec = importlib.util.spec_from_file_location('reviewed_phase1', Path(args.checkout) / '.github/native-package-phase1/phase1.py')
    p = importlib.util.module_from_spec(spec); spec.loader.exec_module(p)
    root = Path(args.job_root).resolve()
    if root.exists(): raise RuntimeError('New owned root required')
    budget = p.PhaseBudget(root)
    p.ACTIVE_BUDGET = budget
    success = False
    failure = None
    try:
        original = Path(args.original_zip).resolve()
        if original.stat().st_size != 258490617 or p.sha(original) != ORIGINAL_ZIP_SHA256:
            raise RuntimeError('Exact original V6 ZIP required')
        q = p.helper(Path(args.checkout).resolve())
        root.mkdir(mode=0o700)
        prepared_root = root / 'prepared'
        scratch = root / 'original-archive'
        if scratch.exists(): raise RuntimeError('New original archive extraction root required')
        # Archive extraction, pinned SDK and all copied inputs share one owned
        # umbrella root under the existing disk monitor and hard clock.
        scratch.mkdir(mode=0o700)
        with zipfile.ZipFile(original) as archive:
            selected = [entry for entry in archive.infolist() if not entry.is_dir() and
                (entry.filename.startswith('transfer/') or entry.filename.startswith('receipts-package-discovery/produced-packages/'))]
            if len(selected) != 673: raise RuntimeError('Original transfer/package member count differs')
            for entry in selected:
                relative = Path(entry.filename)
                if relative.is_absolute() or '..' in relative.parts: raise RuntimeError('Original archive escapes owned scratch')
                target = scratch / relative; target.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(entry) as incoming, target.open('wb') as outgoing:
                    while True:
                        p.checkpoint()
                        chunk = incoming.read(1024**2)
                        if not chunk: break
                        outgoing.write(chunk)
        transfer = scratch / 'transfer'
        q.prepare(SimpleNamespace(job_root=str(prepared_root), transfer=str(transfer), source_zip=None, baseline=None))
        work = prepared_root / 'work'; stage = work / 'source'
        for name in ['tmp', 'local-appdata', 'dotnet-home', 'packages', 'appdata', 'http-cache', 'plugin-cache']:
            (work / name).mkdir(mode=0o700, exist_ok=True)
        feed = work / 'feed'; feed.mkdir(mode=0o700, exist_ok=True)
        closure = p.read(q.HERE / 'TEST-PACKAGE-CLOSURE.json')['packages']
        for row in closure:
            q.download(row['url'], feed / (row['id'].lower() + '.' + row['version'] + '.nupkg'), row['sha256'])
        for row in p.read(Path(args.checkout) / '.github/native-package-phase1/SOURCELINK-PRODUCTION-CLOSURE.json')['archives']:
            q.download(row['officialUrl'], feed / (row['id'].lower() + '.' + row['version'] + '.nupkg'), row['sha256'])
        packages = scratch / 'receipts-package-discovery/produced-packages'
        for row, name in windows_package_records(p.read(HERE / 'EXACT-PRODUCED-PACKAGES.json')):
            source = packages / name
            if source.stat().st_size != row['bytes'] or p.sha(source) != row['sha256']:
                raise RuntimeError('Original package seal differs')
            p.bounded_copy(source, feed / source.name)
        for source in sorted((HERE / 'consumer').rglob('*')):
            if source.is_file(): p.bounded_copy(source, stage / 'consumer' / source.relative_to(HERE / 'consumer'))
        for source in sorted((HERE / 'canonical-build-policy').iterdir()):
            p.bounded_copy(source, stage / source.name)
        source_receipts = prepared_root / 'receipts-source'
        for source in sorted(HERE.rglob('*')):
            if source.is_file(): p.bounded_copy(source, source_receipts / 'source-packet' / source.relative_to(HERE))
        p.save(source_receipts / 'SOURCE-PACKET-SEALS.json', p.bounded_inventory(source_receipts / 'source-packet'))
        # Keep a fixed offline path under the mounted owned work directory.
        (stage / 'consumer/NuGet.Config').write_bytes(b'<configuration><packageSources><clear/><add key="sealed-private-offline" value="../../feed"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>\n')
        p.save(stage / 'global.json', {'sdk': {'version': '10.0.303', 'rollForward': 'disable', 'allowPrerelease': False}})
        if args.consumer_transfer:
            previous = Path(args.consumer_transfer).resolve()
            manifest = p.read(previous / 'CONSUMER-TRANSFER.json')
            if manifest['producedSource'] != '24208d37d9bb9804f2c78b8d023ec1709d47e0f3': raise RuntimeError('Consumer source differs')
            if manifest.get('consumerSourceFiles') != p.bounded_inventory(HERE / 'consumer'):
                raise RuntimeError('Transferred consumer source revision differs from reviewed packet')
            for row in manifest['files']:
                relative = Path(row['path'])
                if relative.is_absolute() or '..' in relative.parts or relative.parts[:2] != ('consumer', 'bin'):
                    raise RuntimeError('Unowned consumer transfer path')
                source = previous / relative
                if source.stat().st_size != row['bytes'] or p.sha(source) != row['sha256']:
                    raise RuntimeError('Consumer transfer differs')
                p.bounded_copy(source, stage / relative)
        p.save(prepared_root / 'NEXT-PREPARATION.json', {'originalArtifactId': 11211612223, 'originalZipSha256': ORIGINAL_ZIP_SHA256,
            'toolchainFiles': p.bounded_inventory(work / 'toolchain'), 'feedFiles': p.bounded_inventory(feed),
            'stagedSourceFiles': p.bounded_inventory(stage), 'helperFiles': p.bounded_inventory(q.HERE),
            'nextGateFiles': p.bounded_inventory(HERE), 'nativeStarted': False})
        success = True
    except BaseException as exc:
        failure = exc
    finally:
        try:
            budget.final_measure(); budget.close()
        except BaseException as exc:
            failure = failure or exc
        if root.exists():
            p.save_unchecked(root / 'NEXT-PREPARATION-RESULT.json', {'completed': success and failure is None and budget.failure is None,
                'failure': repr(failure) if failure else None, 'hostMonitorFailure': budget.failure,
                'elapsedSeconds': time.monotonic() - budget.start, 'nativeStarted': False, 'originalArchivePreserved': True})
        budget.timer.cancel(); budget.stop.set(); p.ACTIVE_BUDGET = None
    if failure: raise failure
    if budget.failure: raise RuntimeError(budget.failure)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--checkout', required=True)
    parser.add_argument('--job-root', required=True)
    parser.add_argument('--original-zip', required=True)
    parser.add_argument('--consumer-transfer')
    prepare(parser.parse_args())
