"""Complete build / original807 / derived2 provenance integration."""
import hashlib
import json
import shutil
import zipfile
from pathlib import Path

import derive_tests
import owned_cancellation

HERE = Path(__file__).resolve().parent
METHODS = owned_cancellation.METHODS
TFMS = ('net8.0', 'net10.0')


def digest_inventory(path):
    return {p.relative_to(path).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in path.rglob('*') if p.is_file()}


def split(whole):
    if whole['expectedCaseCount'] != 809 or whole['expectedMethodCount'] != 620:
        raise RuntimeError('Canonical original plan differs')
    commands = [x for x in whole['commands'] if x['exactMethod']['fullyQualifiedName'] not in METHODS]
    selected = [x for x in whole['commands'] if x['exactMethod']['fullyQualifiedName'] in METHODS]
    if len(commands) != 618 or len(selected) != 2 or any(x['expectedCases'] != 1 for x in selected):
        raise RuntimeError('Exact two-method source partition differs')
    return commands


def derived_root(state, tfm):
    return state['work'] / 'derived' / tfm


def build(p, q, gate, state, tfm, checkout, run):
    seals = p.read(HERE / 'CANONICAL-TEST-SOURCE-SEALS.json')
    source = Path(checkout) / 'Source/MQTTnet.Tests'
    if digest_inventory(source) != seals['files'] or seals['canonicalSource'] != p.SOURCE:
        raise RuntimeError('Canonical source file bytes/set differ')
    extension_hashes = p.read(HERE / 'EXACT-FROZEN-EXTENSION-HASHES.json')[tfm]
    frozen = state['stage'] / 'Source/MQTTnet.Tests/bin/Release' / tfm
    for name, expected in extension_hashes.items():
        if p.sha(frozen / name).lower() != expected:
            raise RuntimeError('Frozen original extension binary differs')
    root = derived_root(state, tfm)
    project_dir = root / 'Source/MQTTnet.Tests'
    derivation = derive_tests.derive(checkout, project_dir, extension_hashes)
    for name in ('Directory.Build.props', 'Directory.Build.targets', 'global.json'):
        p.bounded_copy(state['stage'] / name, root / name)
    config = root / 'NuGet.Config'
    config.write_bytes(b'<configuration><packageSources><clear/><add key="sealed-private-offline" value="/job/work/feed"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>\n')
    project = project_dir / 'MQTTnet.Tests.csproj'
    properties = ['-p:TargetFrameworks=' + tfm,
                  '-p:OwnedFrozenExtensionDirectory=' + str(frozen),
                  '-p:UseAppHost=false', '-p:GeneratePackageOnBuild=false',
                  '-p:IncludeSymbols=false', '-p:SourceRevisionId=' + p.SOURCE,
                  '-p:AssemblyVersion=1.0.0.0', '-p:Version=' + p.VERSION]
    run([state['exe'], 'restore', str(project), '--configfile', str(config),
         '--packages', str(state['work'] / 'packages')] + properties, 'derived-restore', 120)
    run([state['exe'], 'build', str(project), '-c', 'Release', '--no-restore',
         '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false',
         '-p:BuildInParallel=false'] + properties, 'derived-build', 180)
    assets_path = project_dir / 'obj/project.assets.json'
    assets = p.read(assets_path)
    folders = list(assets['packageFolders'])
    if len(folders) != 1 or folders[0] not in ('/job/work/packages', '/job/work/packages/'):
        raise RuntimeError('Derived build cache escaped exact private cache')
    allowed = {x['id'].lower() + '/' + x['version'] for x in p.read(q.HERE / 'TEST-PACKAGE-CLOSURE.json')['packages']}
    allowed |= {x['id'].lower() + '/' + x['version'] for x in p.read(q.HERE.parent / 'native-package-phase1/SOURCELINK-PRODUCTION-CLOSURE.json')['archives']}
    allowed |= {x.lower() + '/' + p.VERSION for x in p.FAMILIES}
    libraries = {name.lower() for name, value in assets['libraries'].items() if value.get('type') == 'package'}
    if not libraries.issubset(allowed) or any(value.get('type') == 'project' for value in assets['libraries'].values()):
        raise RuntimeError('Derived restore graph is not the frozen package/binary closure')
    if set(assets['targets']) != {tfm}:
        raise RuntimeError('Derived restore unexpectedly targeted another framework')
    for family in p.FAMILIES:
        if family + '/' + p.VERSION not in assets['targets'][tfm]:
            raise RuntimeError('Derived test exact native package was not restored')
    output = project_dir / 'bin/Release' / tfm
    for record, name in gate.windows_package_records(p.read(gate.HERE / 'EXACT-PRODUCED-PACKAGES.json')):
        package = state['work'] / 'feed' / name
        family = name.split('.' + p.VERSION)[0]
        suffix = '.pdb' if package.suffix == '.snupkg' else '.dll'
        with zipfile.ZipFile(package) as archive:
            raw = archive.read('lib/' + tfm + '/' + family + suffix)
        target = output / (family + suffix)
        # NuGet symbol archives are not restored as package references. Supply only
        # original symbol bytes, explicitly retained as a distinct copy operation.
        if suffix == '.pdb' and not target.exists():
            target.write_bytes(raw)
        if target.read_bytes() != raw:
            raise RuntimeError('Derived build changed original native DLL/PDB')
    for name, expected in extension_hashes.items():
        target = output / name
        if name.endswith('.pdb') and not target.exists():
            p.bounded_copy(frozen / name, target)
        if p.sha(target).lower() != expected:
            raise RuntimeError('Derived output changed frozen extension DLL/PDB')
    q.pin_configs(root)
    test = output / 'MQTTnet.Tests.dll'
    if p.sha(test) == p.sha(frozen / test.name):
        raise RuntimeError('Derived test executable was silently relabeled original')
    probe = state['stage'] / 'consumer/bin/Release' / tfm / 'PackageOnlyConsumer.dll'
    bindings_path = state['receipts'] / (tfm + '-DERIVED-ROW-BINDINGS.json')
    run([state['exe'], str(probe), '--row-bindings', str(test), str(bindings_path)], 'derived-row-bindings', 20)
    bindings = p.read(bindings_path)
    if bindings['testsInvoked'] != 0 or bindings['testDllSha256'] != p.sha(test) or bindings['framework'] != ('8.0.30' if tfm == 'net8.0' else '10.0.11'):
        raise RuntimeError('Derived row binding assembly/runtime differs')
    expected = [row for row in p.read(gate.HERE / 'EXACT-CASE-LEDGER.json') if row['framework'] == tfm]
    observed = {}
    for row in bindings['rows']:
        observed.setdefault(row['method'], []).append(row)
    if len(observed) != 623 or len(bindings['rows']) != 812 or len({key.rsplit('.', 1)[0] for key in observed}) != 88:
        raise RuntimeError('Derived Release623/88/812 inventory differs')
    if set(observed) != {entry['method']['fullyQualifiedName'] for entry in expected}:
        raise RuntimeError('Derived canonical method set differs')
    for entry in expected:
        rows = observed[entry['method']['fullyQualifiedName']]
        if len(rows) != entry['expectedCases'] or [row['staticRow'] for row in rows] != (entry['method']['staticRows'] or [None]):
            raise RuntimeError('Derived original typed static rows differ')
        if len({row['displayName'] for row in rows}) != len(rows):
            raise RuntimeError('Derived ambiguous case display identity')
    loaded = {row['name']: row for row in bindings['loaded']}
    originals = p.read(gate.HERE / (tfm + '-ORIGINAL-METHOD-MAP.json'))
    old_loaded = {row['name']: row for row in originals['loaded']}
    for family in p.FAMILIES:
        if any(loaded[family][key] != old_loaded[family][key] for key in ('sha256', 'mvid', 'version')):
            raise RuntimeError('Derived binding loaded different native package identity')
    p.bounded_copy(assets_path, state['receipts'] / 'original-derived-project.assets.json')
    p.bounded_snapshot(root, state['receipts'] / 'derived-build-evidence')
    result = {'completed': True, 'framework': tfm, 'derivation': derivation,
              'originalTestDllSha256': p.sha(frozen / 'MQTTnet.Tests.dll'),
              'derivedTestDllSha256': p.sha(test), 'derivedTestPdbSha256': p.sha(output / 'MQTTnet.Tests.pdb'),
              'derivedTestIdentity': loaded['MQTTnet.Tests'], 'derivedRows': bindings['rows'],
              'buildInventory': p.bounded_inventory(root), 'rowBindingsSha256': p.sha(bindings_path),
              'nativePackagesRebuilt': False, 'methods': 623, 'testClasses': 88, 'cases': 812}
    p.save(state['work'].parent / ('DERIVED-BUILD-' + tfm + '.json'), result)


def cancel(p, gate, state, tfm):
    root = derived_root(state, tfm)
    admission = p.read(state['work'].parent / ('DERIVED-BUILD-' + tfm + '.json'))
    row_path = state['work'].parent / ('receipts-next-derived-build-' + tfm) / (tfm + '-DERIVED-ROW-BINDINGS.json')
    if p.sha(row_path) != admission['rowBindingsSha256'] or p.read(row_path)['rows'] != admission['derivedRows']:
        raise RuntimeError('Derived typed-row receipt provenance differs')
    if not admission['completed'] or admission['buildInventory'] != p.bounded_inventory(root):
        raise RuntimeError('Derived build inventory not independently retained')
    test = root / 'Source/MQTTnet.Tests/bin/Release' / tfm / 'MQTTnet.Tests.dll'
    if admission['derivedTestDllSha256'] != p.sha(test):
        raise RuntimeError('Derived test bytes differ from build receipt')
    record = owned_cancellation.run_pair(state, tfm, test, digest_inventory(state['work']))
    p.save(state['receipts'] / 'DERIVED-TWO-CASE-RESULT.json',
           {'completed': record['completed'], 'framework': tfm, 'cases': 2,
            'methods': METHODS, 'derivedBuildReceiptSha256': p.sha(state['work'].parent / ('DERIVED-BUILD-' + tfm + '.json')),
            'originalTestDllSha256': admission['originalTestDllSha256'],
            'derivedTestDllSha256': admission['derivedTestDllSha256'],
            'nativePackagesRebuilt': False, 'whole809SingleBinaryPassed': False})


def combined(p, gate, root):
    root = Path(root).resolve()
    results = []
    for tfm in TFMS:
        original_dir = root / ('receipts-next-linux-whole-' + tfm)
        derived_dir = root / ('receipts-next-owned-cancel-' + tfm)
        original = p.read(original_dir / 'LINUX-WHOLE-SUITE-RESULT.json')
        derived = p.read(derived_dir / 'DERIVED-TWO-CASE-RESULT.json')
        if (not p.read(original_dir / 'RESULT.json')['completed'] or
                not p.read(derived_dir / 'RESULT.json')['completed'] or not derived['completed'] or
                original['result']['passedCases'] != 807 or original['result']['methods'] != 618 or derived['cases'] != 2):
            raise RuntimeError('Both exact provenance partitions must pass')
        expected = next(row for row in p.read(gate.HERE / 'LINUX-WHOLE-SUITE-PLAN.json') if row['framework'] == tfm)
        if original['result']['originalTestDllSha256'] != derived['originalTestDllSha256']:
            raise RuntimeError('Combined original binary provenance differs')
        original_methods = {row['method'] for row in original['result']['caseBindings']}
        if (original_methods & set(METHODS) or original_methods | set(METHODS) !=
                {row['exactMethod']['fullyQualifiedName'] for row in expected['commands']}):
            raise RuntimeError('Combined method identity coverage differs')
        results.append({'framework': tfm, 'originalPassedCases': 807, 'originalPassedMethods': 618,
                        'derivedPassedCases': 2, 'derivedPassedMethods': 2, 'combinedPassedCases': 809,
                        'combinedMethods': 620, 'originalReceiptSha256': p.sha(original_dir / 'LINUX-WHOLE-SUITE-RESULT.json'),
                        'derivedReceiptSha256': p.sha(derived_dir / 'DERIVED-TWO-CASE-RESULT.json'),
                        'originalTestDllSha256': derived['originalTestDllSha256'],
                        'derivedTestDllSha256': derived['derivedTestDllSha256']})
    p.save(root / 'COMBINED-REGRESSION-RESULT.json', {'completed': True, 'frameworks': results,
        'coverage': '807 original network-none cases + 2 explicitly derived owned-SYN-drop cases per TFM',
        'all809OriginalBinaryPassed': False, 'all809DerivedBinaryPassed': False,
        'fullCanonical812CasesQualified': False, 'remainingCasesPerTfm': 3,
        'windowsPositiveTlsQualified': False, 'packagePublished': False})
