"""Source-only proposal: reviewed existing preparation and helper required.
Never call locally. One explicitly assigned chunk or consumer phase per invocation.
External-network chunks are deliberately blocked pending a reviewed route.
"""
import argparse
import importlib.util
import json
import os
import sys
import time
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path, PureWindowsPath
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
FAMILIES = ['MQTTnet', 'MQTTnet.Server', 'MQTTnet.AspNetCore']
VERSION = '5.2.0-local.tlscontext.24208d37'


def load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def check_bindings(p, state, tfm, commands):
    path = state['receipts'] / (tfm + '-ROW-BINDINGS.json')
    data = p.read(path)
    dll = state['stage'] / 'Source/MQTTnet.Tests/bin/Release' / tfm / 'MQTTnet.Tests.dll'
    if data['testsInvoked'] != 0 or data['testDllSha256'] != p.sha(dll):
        raise RuntimeError('Binding does not identify exact original test DLL')
    expected_runtime = '8.0.30' if tfm == 'net8.0' else '10.0.11'
    if data['framework'] != expected_runtime: raise RuntimeError('Binding runtime differs')
    expected = [x for x in p.read(HERE / 'EXACT-CASE-LEDGER.json') if x['framework'] == tfm]
    actual = {}
    for row in data['rows']:
        actual.setdefault(row['method'], []).append(row)
    if set(actual) != {x['method']['fullyQualifiedName'] for x in expected}:
        raise RuntimeError('Binding method set differs')
    for entry in expected:
        method = entry['method']
        rows = actual[method['fullyQualifiedName']]
        if len(rows) != entry['expectedCases'] or any(x['metadataToken'] != method['metadataToken'] for x in rows):
            raise RuntimeError('Binding row count/token differs')
        if len({x['displayName'] for x in rows}) != len(rows):
            raise RuntimeError('Ambiguous display identity; do not claim row coverage')
        if [x['staticRow'] for x in rows] != (method['staticRows'] or [None]):
            raise RuntimeError('Typed static row binding differs from canonical metadata')
    loaded = {x['name']: x for x in data['loaded']}
    for family in FAMILIES:
        if loaded[family]['sha256'] != p.sha(dll.parent / (family + '.dll')):
            raise RuntimeError('Row binding loaded another candidate')
    return {x['exactMethod']['fullyQualifiedName']: actual[x['exactMethod']['fullyQualifiedName']] for x in commands}


def check_trx(p, directory, method, bindings):
    files = list(directory.rglob('*.trx'))
    if len(files) != 1: raise RuntimeError('Missing or duplicate original TRX')
    root = ET.fromstring(p.bounded_read(files[0]))
    counters = root.find('.//{*}Counters')
    results = root.findall('.//{*}UnitTestResult')
    if counters is None: raise RuntimeError('Missing original counters')
    count = len(bindings)
    if int(counters.get('total', '-1')) != count or int(counters.get('passed', '-1')) != count:
        raise RuntimeError('Missing/failed/skipped cases')
    for key in ['failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted', 'disconnected']:
        if int(counters.get(key, '0')) != 0: raise RuntimeError('Nonpass original counter: ' + key)
    definitions = {}
    for unit in root.findall('.//{*}UnitTest'):
        definition = unit.find('{*}TestMethod')
        if definition is not None: definitions[unit.get('id')] = definition
    expected = {x['displayName']: x for x in bindings}
    observed = set()
    for row in results:
        name = row.get('testName')
        definition = definitions.get(row.get('testId'))
        if definition is None or definition.get('className', '').split(',')[0] + '.' + definition.get('name', '') != method:
            raise RuntimeError('TRX method definition differs; no fuzzy name binding')
        if name not in expected or name in observed or row.get('outcome') != 'Passed':
            raise RuntimeError('Missing/duplicate/extra/nonpass exact typed row')
        observed.add(name)
    if len(results) != count or observed != set(expected): raise RuntimeError('Original case set differs')
    return {'method': method, 'originalTrxSha256': p.sha(files[0]), 'rows': bindings, 'passed': count}


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


def package_bytes(p, state):
    records = p.read(HERE / 'EXACT-PRODUCED-PACKAGES.json')
    for record, name in windows_package_records(records):
        archive_path = state['work'] / 'feed' / name
        if archive_path.stat().st_size != record['bytes'] or p.sha(archive_path) != record['sha256']:
            raise RuntimeError('Original produced package changed')
        family = archive_path.name.split('.' + VERSION)[0]
        extension = '.pdb' if archive_path.suffix == '.snupkg' else '.dll'
        with zipfile.ZipFile(archive_path) as archive:
            for tfm in ['net8.0', 'net10.0']:
                member = archive.read('lib/' + tfm + '/' + family + extension)
                import hashlib
                original_sha = hashlib.sha256(member).hexdigest().upper()
                test_dll = state['stage'] / 'Source/MQTTnet.Tests/bin/Release' / tfm / (family + extension)
                if p.sha(test_dll) != original_sha: raise RuntimeError('Tests do not use produced package DLL')
                consumer_dll = state['stage'] / 'consumer/bin/Release' / tfm / (family + '.dll')
                if extension == '.dll' and consumer_dll.exists() and p.sha(consumer_dll) != original_sha:
                    raise RuntimeError('Consumer does not use produced package DLL')


def check_whole_trx(p, directory, bindings):
    files = list(directory.rglob('*.trx'))
    if len(files) != 1: raise RuntimeError('Missing/duplicate original full suite TRX')
    root = ET.fromstring(p.bounded_read(files[0]))
    counters = root.find('.//{*}Counters')
    expected = {(method, row['displayName']): row for method, rows in bindings.items() for row in rows}
    if len(expected) != 809 or len(bindings) != 620:
        raise RuntimeError('Exact Linux method/static case set differs')
    if counters is None or int(counters.get('total', '-1')) != 809 or int(counters.get('passed', '-1')) != 809:
        raise RuntimeError('Original full suite missing/failed/skipped cases')
    for key in ['failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted', 'disconnected']:
        if int(counters.get(key, '0')) != 0: raise RuntimeError('Original nonpass counter: ' + key)
    definitions = {}
    for unit in root.findall('.//{*}UnitTest'):
        definition = unit.find('{*}TestMethod')
        if definition is not None:
            if unit.get('id') in definitions: raise RuntimeError('Duplicate original test definition')
            defined_method = definition.get('className', '').split(',')[0] + '.' + definition.get('name', '')
            if defined_method not in bindings: raise RuntimeError('Unexpected original test definition')
            definitions[unit.get('id')] = definition
    observed = set()
    rows = root.findall('.//{*}UnitTestResult')
    for row in rows:
        definition = definitions.get(row.get('testId'))
        if definition is None: raise RuntimeError('Original result has no test definition')
        method = definition.get('className', '').split(',')[0] + '.' + definition.get('name', '')
        identity = (method, row.get('testName'))
        if identity not in expected or identity in observed or row.get('outcome') != 'Passed':
            raise RuntimeError('Unexpected/duplicate/failed/skipped original typed case')
        observed.add(identity)
    if len(rows) != 809 or observed != set(expected): raise RuntimeError('Exact original typed case set differs')
    return {'originalTrxSha256': p.sha(files[0]), 'methods': 620, 'passedCases': 809,
        'caseBindings': [{'method': method, 'row': row} for method, values in bindings.items() for row in values],
        'fullCanonicalCoverage': False, 'excludedCaseCount': 3}


def execute(args):
    # Import the already reviewed module without invoking its command entry point.
    p = load(Path(args.checkout) / '.github/native-package-phase1/phase1.py', 'reviewed_phase1')
    root = Path(args.job_root).resolve()
    if root.name != 'prepared': raise RuntimeError('Exact owned preparation umbrella required')
    budget = p.PhaseBudget(root.parent)
    p.ACTIVE_BUDGET = budget
    state = None
    q = None
    receipts = root / ('receipts-next-' + args.assignment)
    primary = None
    secondary = []
    completed = False
    try:
        marker = p.read(root / 'OWNERSHIP.json')
        work = root / 'work'
        if marker['jobRoot'] != str(root) or marker['work'] != str(work): raise RuntimeError('Wrong owned root')
        receipts.mkdir(exist_ok=False)
        budget.receipts = receipts
        p.save(receipts / 'PHASE-START.json', {'phaseStartMonotonic': budget.start, 'boundSeconds': 600, 'hardWatchdogExit': 124})
        q = p.helper(Path(args.checkout).resolve())
        sys.path.insert(0, str(q.HERE))
        original_run = q.run
        q.read, q.save, q.inventory = p.read, p.save, p.bounded_inventory
        prepared = p.read(root / 'NEXT-PREPARATION.json')
        if args.assignment != 'consumer':
            consumer_result = p.read(root / 'receipts-next-consumer/RESULT.json')
            if not consumer_result['completed']: raise RuntimeError('Successful original consumer phase required')
            revised = p.read(root / 'NEXT-PREPARATION-CONSUMER.json')
            if revised['originalPreparationSha256'] != p.sha(root / 'NEXT-PREPARATION.json'):
                raise RuntimeError('Consumer transition lost original preparation binding')
            prepared = revised['prepared']
        for directory, key in [(work / 'toolchain', 'toolchainFiles'), (work / 'feed', 'feedFiles'), (work / 'source', 'stagedSourceFiles'), (q.HERE, 'helperFiles'), (HERE, 'nextGateFiles')]:
            if p.bounded_inventory(directory) != prepared[key]: raise RuntimeError('Prepared input set changed: ' + key)
        owned = {name: str(work / folder) for name, folder in [('APPDATA', 'appdata'), ('LOCALAPPDATA', 'local-appdata'), ('DOTNET_CLI_HOME', 'dotnet-home'), ('DOTNET_ROOT', 'toolchain'), ('NUGET_PACKAGES', 'packages'), ('NUGET_HTTP_CACHE_PATH', 'http-cache'), ('NUGET_PLUGINS_CACHE_PATH', 'plugin-cache'), ('TEMP', 'tmp'), ('TMP', 'tmp')]}
        owned.update(DOTNET_GENERATE_ASPNET_CERTIFICATE='false', DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false', DOTNET_CLI_USE_MSBUILD_SERVER='0', MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_MULTILEVEL_LOOKUP='0', DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='1', MSBuildEnableWorkloadResolver='false', NUGET_CERT_REVOCATION_MODE='offline')
        env = dict(os.environ); env.update(owned)
        state = {'work': work, 'stage': work / 'source', 'receipts': receipts, 'env': env, 'ownedEnvKeys': set(owned), 'commands': [], 'start': budget.start, 'slot': 600, 'exe': str(work / 'toolchain' / ('dotnet.exe' if os.name == 'nt' else 'dotnet')), 'image': p.read(q.HERE / 'TOOLCHAIN-DISTRIBUTIONS.json')['linuxContainerImage'], 'ownedContainers': budget.containers, 'token': marker['token'], 'mode': 'next-gate'}
        if args.assignment.startswith('linux-whole-'):
            selected = [c for c in p.read(HERE / 'LINUX-WHOLE-SUITE-PLAN.json') if c['id'] == args.assignment]
            if len(selected) != 1 or os.name == 'nt': raise RuntimeError('Exact Linux whole suite assignment required')
            whole = selected[0]
            if whole['expectedMethodCount'] != 620 or whole['expectedCaseCount'] != 809:
                raise RuntimeError('Whole suite source inventory differs')
        elif args.assignment != 'consumer':
            selected = [c for c in p.read(HERE / 'BOUNDED-SERIAL-COMMANDS.json') if c['id'] == args.assignment]
            if len(selected) != 1: raise RuntimeError('Exact single chunk required before native execution')
            chunk = selected[0]
            if chunk['route'] == 'external-network-review-required': raise RuntimeError('Network route not authorized/implemented')
            if (chunk['route'] == 'windows-guard') != (os.name == 'nt'): raise RuntimeError('Wrong platform assignment')
        def run(command, stem, seconds):
            budget.check()
            allowed = min(seconds, int(budget.deadline - time.monotonic() - 15))
            if allowed < 1: raise TimeoutError('No command/cleanup headroom')
            original_run(command, stem, allowed, state)
            budget.check()
        package_bytes(p, state)
        if os.name != 'nt':
            q.prove_owned_permissions(state)
            budget.check()
        if args.assignment == 'consumer':
            if os.name == 'nt': raise RuntimeError('Package consumer initial phase assigned Linux only')
            project = 'consumer/PackageOnlyConsumer.csproj'
            run([state['exe'], 'restore', project, '--configfile', 'consumer/NuGet.Config', '--packages', str(work / 'packages')], 'consumer-restore', 120)
            run([state['exe'], 'build', project, '-c', 'Release', '--no-restore', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:BuildInParallel=false'], 'consumer-build', 120)
            q.pin_configs(state['stage'] / 'consumer')
            asset_path = state['stage'] / 'consumer/obj/project.assets.json'
            assets = p.read(asset_path)
            p.bounded_copy(asset_path, receipts / 'original-project.assets.json')
            expected_folder = '/job/work/packages'
            actual_folders = list(assets['packageFolders'])
            owned = p.read(receipts / 'OWNED-PERMISSIONS.json')
            owned_rows = [row for row in owned['rows'] if row['path'] == expected_folder]
            accepted = (len(actual_folders) == 1 and actual_folders[0] in
                [expected_folder, expected_folder + '/'] and len(owned_rows) == 1 and
                owned_rows[0]['uid'] == '1001' and owned_rows[0]['gid'] == '1001' and
                owned_rows[0]['mode'] == '700' and owned_rows[0]['directoryAndFileWriteReadRemove'])
            p.save(receipts / 'PACKAGE-FOLDER-OWNERSHIP.json', {'originalAssetsSha256': p.sha(asset_path),
                'expectedContainerFolder': expected_folder, 'observedPackageFolders': actual_folders,
                'originalOwnedPermissionRows': owned_rows, 'accepted': accepted})
            if not accepted:
                raise RuntimeError('Consumer restored from an unowned cache: ' + repr(actual_folders))
            allowed = {x['id'].lower() + '/' + x['version'] for x in p.read(q.HERE / 'TEST-PACKAGE-CLOSURE.json')['packages']}
            allowed |= {x['id'].lower() + '/' + x['version'] for x in p.read(Path(args.checkout) / '.github/native-package-phase1/SOURCELINK-PRODUCTION-CLOSURE.json')['archives']}
            allowed |= {x.lower() + '/' + VERSION for x in FAMILIES}
            libraries = {name.lower() for name, value in assets['libraries'].items() if value.get('type') == 'package'}
            if not libraries.issubset(allowed) or any(value.get('type') == 'project' for value in assets['libraries'].values()):
                raise RuntimeError('Consumer package-only graph differs from sealed closure')
            for tfm in ['net8.0', 'net10.0']:
                target = assets['targets'][tfm]
                for family in FAMILIES:
                    if family + '/' + VERSION not in target: raise RuntimeError('Exact package family not resolved')
                run([state['exe'], str(state['stage'] / 'consumer/bin/Release' / tfm / 'PackageOnlyConsumer.dll'), str(state['stage'] / '.github/legacy-certificate-provider/bin/Release' / tfm / 'LegacyCertificateProviderFixture.dll')], tfm + '-consumer', 20)
                lines = p.bounded_read(receipts / (tfm + '-consumer.stdout.log')).decode().splitlines()
                if len(lines) != 5 or sum(x.startswith('PASS ') for x in lines) != 4:
                    raise RuntimeError('Original four named consumer checks missing')
                result = json.loads(lines[-1])
                if any(result[key] != value for key, value in [('passed', 4), ('failed', 0), ('skipped', 0), ('contextCreated', 0), ('socketsOpened', 0)]):
                    raise RuntimeError('Original consumer counts differ')
                expected_runtime = '8.0.30' if tfm == 'net8.0' else '10.0.11'
                if result['runtimeVersion'] != expected_runtime: raise RuntimeError('Consumer runtime differs')
                originals = p.read(HERE / (tfm + '-ORIGINAL-METHOD-MAP.json'))
                expected_loaded = {x['name']: x for x in originals['loaded'] if x['name'] in FAMILIES}
                if len(result['loaded']) != 3: raise RuntimeError('Consumer loaded assembly set differs')
                for row in result['loaded']:
                    original = expected_loaded[row['name']]
                    if any(row[key] != original[key] for key in ['version', 'mvid', 'sha256']):
                        raise RuntimeError('Actual loaded consumer package identity differs')
                    if row['sha256'] != p.sha(state['stage'] / 'consumer/bin/Release' / tfm / (row['name'] + '.dll')):
                        raise RuntimeError('Consumer loaded path differs from retained binary')
            package_bytes(p, state)
            p.bounded_snapshot(state['stage'] / 'consumer', receipts / 'consumer-build-evidence')
            transfer = root / 'consumer-transfer'
            transfer.mkdir(exist_ok=False)
            for path in sorted((state['stage'] / 'consumer/bin').rglob('*')):
                if path.is_file(): p.bounded_copy(path, transfer / path.relative_to(state['stage']))
            p.save(transfer / 'CONSUMER-TRANSFER.json', {'producedSource': '24208d37d9bb9804f2c78b8d023ec1709d47e0f3',
                'consumerSourceFiles': p.bounded_inventory(HERE / 'consumer'), 'files': p.bounded_inventory(transfer)})
            updated = p.bounded_inventory(state['stage'])
            unchanged_before = [r for r in prepared['stagedSourceFiles'] if not r['path'].startswith('consumer/')]
            unchanged_after = [r for r in updated if not r['path'].startswith('consumer/')]
            if unchanged_before != unchanged_after: raise RuntimeError('Consumer build altered original transferred inputs')
            for directory, key in [(work / 'toolchain', 'toolchainFiles'), (work / 'feed', 'feedFiles'), (q.HERE, 'helperFiles'), (HERE, 'nextGateFiles')]:
                if p.bounded_inventory(directory) != prepared[key]: raise RuntimeError('Consumer build altered sealed inputs: ' + key)
            revised = dict(prepared)
            revised['stagedSourceFiles'] = updated
            p.save(root / 'NEXT-PREPARATION-CONSUMER.json', {'originalPreparationSha256': p.sha(root / 'NEXT-PREPARATION.json'),
                'consumerSourceFiles': p.bounded_inventory(HERE / 'consumer'), 'prepared': revised})
        elif args.assignment.startswith('linux-whole-'):
            tfm = whole['framework']
            dll = state['stage'] / 'Source/MQTTnet.Tests/bin/Release' / tfm / 'MQTTnet.Tests.dll'
            probe = state['stage'] / 'consumer/bin/Release' / tfm / 'PackageOnlyConsumer.dll'
            run([state['exe'], str(probe), '--row-bindings', str(dll), str(receipts / (tfm + '-ROW-BINDINGS.json'))], 'row-bindings', 20)
            bindings = check_bindings(p, state, tfm, whole['commands'])
            state['env']['MQTTNET_LEGACY_CERTIFICATE_PROVIDER_DLL'] = str(state['stage'] / '.github/legacy-certificate-provider/bin/Release' / tfm / 'LegacyCertificateProviderFixture.dll')
            state['ownedEnvKeys'].add('MQTTNET_LEGACY_CERTIFICATE_PROVIDER_DLL')
            directory = receipts / 'trx' / 'whole-suite'; directory.mkdir(parents=True)
            # Pass the exact conjunction as one argv element: no shell expansion,
            # substring exclusions, class-wide exclusions, retry or failure masking.
            run([state['exe'], str(dll), '--filter', whole['filter'], '--report-trx', '--report-trx-filename', 'result.trx', '--results-directory', str(directory)], 'whole-suite', 480)
            result = check_whole_trx(p, directory, bindings)
            package_bytes(p, state)
            p.save(receipts / 'LINUX-WHOLE-SUITE-RESULT.json', {'assignment': args.assignment, 'result': result, 'excluded': whole['excluded'], 'retryCount': 0, 'wholeCanonicalRegressionQualified': False})
        else:
            selected = [c for c in p.read(HERE / 'BOUNDED-SERIAL-COMMANDS.json') if c['id'] == args.assignment]
            if len(selected) != 1: raise RuntimeError('Exact single chunk required')
            chunk = selected[0]
            if chunk['route'] == 'external-network-review-required': raise RuntimeError('Network route not authorized/implemented')
            if (chunk['route'] == 'windows-guard') != (os.name == 'nt'): raise RuntimeError('Wrong platform assignment')
            tfm = args.assignment.split('-')[0]
            dll = state['stage'] / 'Source/MQTTnet.Tests/bin/Release' / tfm / 'MQTTnet.Tests.dll'
            probe = state['stage'] / 'consumer/bin/Release' / tfm / 'PackageOnlyConsumer.dll'
            run([state['exe'], str(probe), '--row-bindings', str(dll), str(receipts / (tfm + '-ROW-BINDINGS.json'))], 'row-bindings', 20)
            bindings = check_bindings(p, state, tfm, chunk['commands'])
            state['env']['MQTTNET_LEGACY_CERTIFICATE_PROVIDER_DLL'] = str(state['stage'] / '.github/legacy-certificate-provider/bin/Release' / tfm / 'LegacyCertificateProviderFixture.dll')
            state['ownedEnvKeys'].add('MQTTNET_LEGACY_CERTIFICATE_PROVIDER_DLL')
            results = []
            for item in chunk['commands']:
                method = item['exactMethod']['fullyQualifiedName']
                directory = receipts / 'trx' / item['id']; directory.mkdir(parents=True)
                run([state['exe'], str(dll), '--filter', 'FullyQualifiedName=' + method, '--report-trx', '--report-trx-filename', 'result.trx', '--results-directory', str(directory)], item['id'], 120)
                results.append(check_trx(p, directory, method, bindings[method]))
            package_bytes(p, state)
            p.save(receipts / 'EXACT-CHUNK-RESULT.json', {'assignment': args.assignment, 'rows': results, 'wholeRegressionQualified': False})
        completed = True
    except BaseException as exc:
        primary = exc
    finally:
        if q is not None:
            try:
                budget.cleanup_mode = True
                q.cleanup(SimpleNamespace(job_root=str(root)))
            except BaseException as exc: secondary.append({'cleanup': repr(exc)})
            finally: budget.cleanup_mode = False
        try:
            observation = budget.final_measure(); budget.close()
        except BaseException as exc:
            observation = None; secondary.append({'finalMeasure': repr(exc)})
        if receipts.exists():
            p.save_unchecked(receipts / 'RESULT.json', {'completed': completed and primary is None and not secondary and budget.failure is None, 'originalFailure': repr(primary) if primary else None, 'secondaryFailures': secondary, 'hostMonitorFailure': budget.failure, 'commands': state['commands'] if state else [], 'phaseElapsedSeconds': time.monotonic() - budget.start, 'observation': observation, 'wholeRegressionQualified': False, 'windowsPositiveTlsQualified': False, 'packagePublished': False, 'brokerPinChanged': False})
        budget.timer.cancel(); budget.stop.set(); p.ACTIVE_BUDGET = None
    if primary: raise primary
    if secondary or budget.failure: raise RuntimeError('Original failures retained; gate failed')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--checkout', required=True)
    parser.add_argument('--job-root', required=True)
    parser.add_argument('--assignment', required=True)
    execute(parser.parse_args())
