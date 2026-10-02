"""SOURCE-ONLY prepared executable; Root must review/assign before invocation.
Reuses original qualification containment, watchdogs, source export and offline feeds.
No test methods invoked. No feed publication. Linux pack+metadata gate only.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import threading
import time
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
SOURCE = '24208d37d9bb9804f2c78b8d023ec1709d47e0f3'
VERSION = '5.2.0-local.tlscontext.24208d37'
FAMILIES = ['MQTTnet', 'MQTTnet.Server', 'MQTTnet.AspNetCore']
TFMS = ['net8.0', 'net10.0']
ACTIVE_BUDGET = None
MAX_FILE = 16 * 1024**2


class PhaseBudget:
    """One clock includes input sealing, native commands, evidence and cleanup."""
    def __init__(self, root):
        self.start = time.monotonic()
        self.deadline = self.start + 600
        self.root = root
        self.receipts = None
        self.containers = set()
        self.failure = None
        self.stop = threading.Event()
        self.peak_disk = 0
        self.peak_log = 0
        self.timer = threading.Timer(600, self.abort)
        self.timer.start()
        self.monitor = threading.Thread(target=self.watch, daemon=True)
        self.monitor.start()

    def abort(self):
        # External always-cleanup still observes the original timeout after this
        # hard stop. No cleanup result can turn the timed-out gate into success.
        # No filesystem/subprocess call on the hard-deadline path can delay exit.
        # PHASE-START plus the original exit124/logs are retained by always-upload.
        os._exit(124)

    def watch(self):
        while not self.stop.wait(.2):
            try:
                disk, largest = 0, 0
                for path in self.root.rglob('*'):
                    if self.stop.is_set(): return
                    try:
                        if path.is_file():
                            size = path.stat().st_size
                            disk += size
                            if path.suffix == '.log': largest = max(largest, size)
                    except FileNotFoundError:
                        continue
                self.peak_disk = max(self.peak_disk, disk)
                self.peak_log = max(self.peak_log, largest)
                if disk > 2 * 1024**3 or largest > MAX_FILE:
                    self.failure = self.failure or 'Host/native disk or log abort threshold'
                    # A bounded docker kill request interrupts active native work;
                    # the same timer also bounds this observer and host-only work.
                    for name in list(self.containers):
                        subprocess.run(['docker', 'kill', name], capture_output=True, timeout=5)
            except BaseException as exc:
                self.failure = self.failure or ('Host monitor failed: ' + repr(exc))

    def check(self, cleanup=False):
        if time.monotonic() >= self.deadline: raise TimeoutError('600s phase deadline')
        if self.failure and not cleanup: raise RuntimeError(self.failure)

    def close(self):
        self.check(cleanup=True)
        self.stop.set()
        self.monitor.join(timeout=min(1, max(0, self.deadline - time.monotonic())))
        if self.monitor.is_alive(): raise RuntimeError('Host bound monitor did not stop')

    def final_measure(self):
        disk, largest = 0, 0
        for path in self.root.rglob('*'):
            self.check(cleanup=True)
            try:
                if path.is_file():
                    size = path.stat().st_size
                    disk += size
                    if path.suffix == '.log': largest = max(largest, size)
            except FileNotFoundError:
                continue
        self.peak_disk = max(self.peak_disk, disk)
        self.peak_log = max(self.peak_log, largest)
        if disk > 2 * 1024**3 or largest > MAX_FILE:
            self.failure = self.failure or 'Final retained host disk or log threshold exceeded'
        if disk + MAX_FILE > 2 * 1024**3:
            self.failure = self.failure or 'Insufficient bounded final-receipt headroom inside 2GiB threshold'
        return {'diskBytes': disk, 'largestLogBytes': largest}



def checkpoint():
    if ACTIVE_BUDGET: ACTIVE_BUDGET.check(cleanup=getattr(ACTIVE_BUDGET, 'cleanup_mode', False))


def save_unchecked(path, value):
    encoded = (json.dumps(value, indent=2) + '\n').encode()
    if len(encoded) > MAX_FILE: raise RuntimeError('Host receipt exceeds 16MiB output ceiling')
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_bytes(encoded)


def bounded_read(path):
    checkpoint()
    if Path(path).stat().st_size > MAX_FILE: raise RuntimeError('Host input exceeds 16MiB ceiling')
    with Path(path).open('rb') as stream:
        value = stream.read(MAX_FILE + 1)
    if len(value) > MAX_FILE: raise RuntimeError('Host input exceeds 16MiB ceiling')
    checkpoint()
    return value


def bounded_inventory(root):
    rows = []
    for path in sorted(root.rglob('*')):
        checkpoint()
        if path.is_file(): rows.append({'path': path.relative_to(root).as_posix(),
            'bytes': path.stat().st_size, 'sha256': sha(path)})
    checkpoint()
    return rows


def bounded_copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    with source.open('rb') as incoming, target.open('wb') as outgoing:
        while True:
            checkpoint()
            chunk = incoming.read(1024**2)
            if not chunk: break
            outgoing.write(chunk)
    checkpoint()


def bounded_snapshot(stage, destination):
    for path in sorted(stage.rglob('*')):
        checkpoint()
        if path.is_file() and any(part in ['bin', 'obj'] for part in path.relative_to(stage).parts):
            if path.suffix in ['.dll', '.json', '.pdb'] or path.name.endswith(('.g.props', '.g.targets')):
                bounded_copy(path, destination / path.relative_to(stage))
    save(destination.parent / (destination.name + '-inventory.json'), bounded_inventory(destination))


def bounded_transfer(state):
    dest = state['work'].parent / 'transfer'
    dest.mkdir(exist_ok=False)
    for path in state['stage'].rglob('*'):
        checkpoint()
        if path.is_file() and ('bin' in path.relative_to(state['stage']).parts or 'test-binaries' in path.parts):
            bounded_copy(path, dest / path.relative_to(state['stage']))
    save(dest / 'TRANSFER.json', {'source': SOURCE, 'files': bounded_inventory(dest)})


def read(path): return json.loads(bounded_read(path))
def save(path, value):
    checkpoint()
    save_unchecked(path, value)
    checkpoint()
def sha(path):
    value = hashlib.sha256()
    with Path(path).open('rb') as stream:
        while True:
            checkpoint()
            chunk = stream.read(1024**2)
            if not chunk: break
            value.update(chunk)
    checkpoint()
    return value.hexdigest().upper()


def helper(checkout):
    path = checkout / '.github/native-context/qualify.py'
    spec = importlib.util.spec_from_file_location('sealed_qualification', path)
    q = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(q)
    return q


def acquire(q):
    target = HERE / 'official-sourcelink-acquisition'
    target.mkdir(exist_ok=False)
    for row in read(HERE / 'SOURCELINK-PRODUCTION-CLOSURE.json')['archives']:
        path = target / (row['id'].lower() + '.' + row['version'] + '.nupkg')
        q.download(row['officialUrl'], path, row['sha256'])
        if path.stat().st_size != row['bytes']: raise RuntimeError('SourceLink archive size differs')


def prepare(args, q):
    q.prepare(args)
    root = Path(args.job_root).resolve()
    work = root / 'work'
    stage = work / 'source'
    original = read(root / 'PREPARATION.json')
    # Restore canonical LF bytes for SourceLink checksum/provenance. All declared
    # original qualification transformations are first verified by q.prepare.
    manifest = read(q.HERE / 'SOURCE-MANIFEST.json')
    canonical_rows = []
    with zipfile.ZipFile(args.source_zip) as exported:
        for row in manifest['files']:
            path = stage / row['path']
            value = exported.read(row['path'])
            if hashlib.sha256(value).hexdigest().upper() != row['gitBlobSha256']:
                raise RuntimeError('Canonical production source mismatch: ' + row['path'])
            path.write_bytes(value)
            canonical_rows.append({'path': row['path'], 'sha256': sha(path), 'bytes': len(value)})
    global_json = read(stage / 'global.json')
    global_json['sdk'] = {'version': '10.0.303', 'rollForward': 'disable', 'allowPrerelease': False}
    save(stage / 'global.json', global_json)
    acquisition = read(HERE / 'SOURCELINK-PRODUCTION-CLOSURE.json')
    additions = []
    for row in acquisition['archives']:
        archive = HERE / 'official-sourcelink-acquisition' / (row['id'].lower() + '.' + row['version'] + '.nupkg')
        if sha(archive) != row['sha256']: raise RuntimeError('SourceLink archive differs')
        shutil.copyfile(archive, work / 'feed' / archive.name)
        additions.append({'id': row['id'], 'version': row['version']})
    # Clone ONLY local checkout objects; no network or shared object alternates.
    # Canonical source files already byte verified. Metadata is retained separately.
    metadata = root / 'git-metadata'
    subprocess.run(['git', 'clone', '--no-checkout', '--no-hardlinks', str(args.checkout), str(metadata)], check=True, timeout=30, capture_output=True)
    subprocess.run(['git', '-C', str(metadata), 'remote', 'set-url', 'origin', 'https://github.com/YAJeff/MQTTnet.git'], check=True, timeout=5)
    shutil.move(str(metadata / '.git'), str(stage / '.git'))
    subprocess.run(['git', '-C', str(stage), 'reset', '--mixed', SOURCE], check=True, timeout=10, capture_output=True)
    observed = subprocess.check_output(['git', '-C', str(stage), 'rev-parse', 'HEAD'], timeout=5).decode().strip()
    if observed != SOURCE: raise RuntimeError('SourceLink metadata HEAD differs')
    probe = stage / 'qualification-probe/Program.cs'
    value = probe.read_bytes()
    needle = b'RunChecks(args);'
    if value.count(needle) != 1: raise RuntimeError('Probe entrypoint anchor differs')
    probe.write_bytes(value.replace(needle, b'if (args.Length == 3 && args[0] == "--discover") PackageDiscovery.Run(args[1], args[2]); else RunChecks(args);'))
    shutil.copyfile(HERE / 'Discover.cs', probe.parent / 'Discover.cs')
    save(root / 'PHASE1-SOURCE-PREPARATION.json', {'canonicalSource': SOURCE, 'canonicalFiles': canonical_rows,
         'originalPreparation': original, 'additionalArchives': additions,
         'gitHead': observed, 'helperOverlay': q.inventory(probe.parent), 'productionSourceLF': True,
         'noRuntimeStarted': True})
    original['stagedSourceFiles'] = q.inventory(stage)
    original['feedFiles'] = q.inventory(work / 'feed')
    save(root / 'PREPARATION.json', original)


def tag(node): return node.tag.split('}')[-1]


def package_contract(xml, family):
    metadata = next(n for n in xml if tag(n) == 'metadata')
    def groups(name):
        parents = [n for n in metadata if tag(n) == name]
        if len(parents) > 1: raise RuntimeError('Duplicate nuspec contract block')
        result = []
        for parent in parents:
            for group in parent:
                if tag(group) != 'group': raise RuntimeError('Unexpected non-group nuspec dependency')
                children = [dict(n.attrib) for n in group]
                result.append({'tfm': group.attrib.get('targetFramework'),
                    'children': sorted(children, key=lambda n: n.get('id', n.get('name', '')))})
        return sorted(result, key=lambda n: n['tfm'])
    dependencies, frameworks = groups('dependencies'), groups('frameworkReferences')
    if [g['tfm'] for g in dependencies] != sorted(TFMS): raise RuntimeError('Exact dependency TFM set differs')
    expected_ids = {'MQTTnet': set(), 'MQTTnet.Server': {'MQTTnet'}, 'MQTTnet.AspNetCore': {'MQTTnet', 'MQTTnet.Server'}}[family]
    for group in dependencies:
        if len(group['children']) != len(expected_ids) or {d['id'] for d in group['children']} != expected_ids:
            raise RuntimeError('Exact family dependency set differs')
    baseline_rows = read(HERE / 'ORIGINAL-ABFA-NUSPEC-CONTRACTS.json')
    baseline = next(r for r in baseline_rows if '<id>' + family + '</id>' in r['nuspecXml'])
    old = ET.fromstring(baseline['nuspecXml'])
    old_metadata = next(n for n in old if tag(n) == 'metadata')
    old_version = next(n.text for n in old_metadata if tag(n) == 'version')
    def original_groups(name):
        answer = []
        for parent in old_metadata:
            if tag(parent) != name: continue
            for group in parent:
                children = []
                for child in group:
                    item = dict(child.attrib)
                    if tag(child) == 'dependency':
                        # Original bare version is NuGet's inclusive minimum.
                        # Only its exact version token changes; range syntax and
                        # exclude flags remain byte-exact, not substring accepted.
                        if item['version'] != old_version: raise RuntimeError('Unexpected original dependency range')
                        item['version'] = VERSION
                    children.append(item)
                answer.append({'tfm': group.attrib['targetFramework'],
                    'children': sorted(children, key=lambda n: n.get('id', n.get('name', '')))})
        return sorted(answer, key=lambda n: n['tfm'])
    if dependencies != original_groups('dependencies') or frameworks != original_groups('frameworkReferences'):
        raise RuntimeError('Exact original nuspec ranges/excludes/framework refs changed')
    expected_frameworks = ([{'tfm': tfm, 'children': [{'name': 'Microsoft.AspNetCore.App'}]} for tfm in sorted(TFMS)]
        if family == 'MQTTnet.AspNetCore' else [])
    if frameworks != expected_frameworks: raise RuntimeError('Framework-reference identity differs')
    if any(n.text != VERSION for n in metadata if tag(n) == 'version'):
        raise RuntimeError('Exact package version differs')
    if next(n.text for n in metadata if tag(n) == 'id') != family: raise RuntimeError('Exact family identity differs')
    for original in old_metadata:
        if tag(original) in ['id', 'version', 'repository', 'dependencies', 'frameworkReferences']: continue
        actual = [n for n in metadata if tag(n) == tag(original)]
        if len(actual) != 1 or actual[0].attrib != original.attrib or (actual[0].text or '').replace('\r', '') != (original.text or '').replace('\r', ''):
            raise RuntimeError('Original package metadata changed: ' + tag(original))
    repository = [n for n in metadata if tag(n) == 'repository']
    if len(repository) != 1 or repository[0].attrib.get('type') != 'git' or repository[0].attrib.get('url') != 'https://github.com/dotnet/MQTTnet.git' or repository[0].attrib.get('commit') != SOURCE:
        raise RuntimeError('Package repository identity/provenance differs')
    return {'dependencies': dependencies, 'frameworkReferences': frameworks, 'baselineArchiveSha256': baseline['sha256']}


def package_members(stage, receipts):
    rows = []
    for family in FAMILIES:
        checkpoint()
        project = stage / 'Source' / ('MQTTnet.AspnetCore' if family == 'MQTTnet.AspNetCore' else family)
        for extension in ['nupkg', 'snupkg']:
            path = project / 'bin/Release' / (family + '.' + VERSION + '.' + extension)
            if not path.is_file() or path.stat().st_size > MAX_FILE: raise RuntimeError('Missing/oversized exact package')
            with zipfile.ZipFile(path) as archive:
                names = archive.namelist()
                if len(names) != len(set(names)): raise RuntimeError('Duplicate ZIP members')
                def member(name):
                    checkpoint()
                    item = archive.getinfo(name)
                    if item.file_size > MAX_FILE or Path(name).is_absolute() or '..' in Path(name).parts:
                        raise RuntimeError('Oversized/escaping archive member')
                    value = archive.read(item)
                    checkpoint()
                    return value
                members = [{'path': n.filename, 'bytes': n.file_size, 'sha256': hashlib.sha256(member(n.filename)).hexdigest().upper()}
                    for n in archive.infolist() if not n.is_dir()]
                names = {m['path'] for m in members}
                if {n.split('/')[1] for n in names if n.startswith('lib/')} != set(TFMS): raise RuntimeError('Extra/dropped package TFM')
                suffix = '.dll' if extension == 'nupkg' else '.pdb'
                if {n for n in names if n.endswith(suffix)} != {'lib/' + tfm + '/' + family + suffix for tfm in TFMS}:
                    raise RuntimeError('Extra/dropped family assembly or symbol')
                for tfm in TFMS:
                    name = 'lib/' + tfm + '/' + family + suffix
                    if member(name) != bounded_read(project / 'bin/Release' / tfm / Path(name).name): raise RuntimeError('Package binary differs')
                    if extension == 'nupkg':
                        name = 'lib/' + tfm + '/' + family + '.xml'
                        if name not in names or member(name) != bounded_read(project / 'bin/Release' / tfm / (family + '.xml')):
                            raise RuntimeError('XML documentation missing/different')
                nuspecs = [n for n in names if n.endswith('.nuspec')]
                if len(nuspecs) != 1: raise RuntimeError('Exact nuspec count differs')
                raw = member(nuspecs[0])
                xml = ET.fromstring(raw)
                metadata = next(n for n in xml if tag(n) == 'metadata')
                for name, expected_value in [('id', family), ('version', VERSION)]:
                    elements = [n for n in metadata if tag(n) == name]
                    if len(elements) != 1 or elements[0].text != expected_value:
                        raise RuntimeError('Exact primary/symbol package identity differs')
                repositories = [n for n in metadata if tag(n) == 'repository']
                if len(repositories) != 1 or repositories[0].attrib.get('commit') != SOURCE or repositories[0].attrib.get('url') != 'https://github.com/dotnet/MQTTnet.git':
                    raise RuntimeError('Exact primary/symbol repository provenance differs')
                contract = package_contract(xml, family) if extension == 'nupkg' else None
                if extension == 'nupkg':
                    for name, original in [('LICENSE', stage / 'LICENSE'), ('nuget.png', stage / 'Images/nuget.png')]:
                        if name not in names or member(name) != bounded_read(original): raise RuntimeError('License/icon missing/different')
                    if (project / 'README.md').is_file():
                        if 'README.md' not in names or member('README.md') != bounded_read(project / 'README.md'):
                            raise RuntimeError('Canonical README missing/different')
                rows.append({'package': family, 'extension': extension, 'path': str(path), 'sha256': sha(path),
                    'members': members, 'exactNuspecContract': contract, 'nuspecXml': raw.decode('utf-8-sig')})
    save(receipts / 'PACKAGES.json', rows)
    bindings = []
    for tfm in TFMS:
        for family in FAMILIES:
            project = 'MQTTnet.AspnetCore' if family == 'MQTTnet.AspNetCore' else family
            expected = stage / 'Source' / project / 'bin/Release' / tfm / (family + '.dll')
            for folder in [stage / 'Source/MQTTnet.Tests/bin/Release' / tfm,
                           stage / 'qualification-probe/bin/Release' / tfm,
                           stage / 'Source/MQTTnet.AspnetCore/bin/Release' / tfm]:
                target = folder / (family + '.dll')
                if sha(target) != sha(expected): raise RuntimeError('Test/probe DLL differs from package producer')
                pdb = target.with_suffix('.pdb')
                if sha(pdb) != sha(expected.with_suffix('.pdb')): raise RuntimeError('Test/probe PDB differs from symbol package producer')
                bindings.append({'tfm': tfm, 'family': family, 'path': str(target), 'sha256': sha(target),
                    'pdbPath': str(pdb), 'pdbSha256': sha(pdb)})
    save(receipts / 'PACKAGE-TEST-DLL-BINDINGS.json', bindings)


def gate(args):
    global ACTIVE_BUDGET
    # The first gate action starts the one600s clock, before any metadata/hash I/O.
    budget = PhaseBudget(Path(args.job_root).absolute())
    ACTIVE_BUDGET = budget
    primary = None
    secondary = []
    q = None
    state = None
    receipts = None
    success = False
    final_observation = None
    try:
        if os.name == 'nt': raise RuntimeError('Initial package gate is Linux-only')
        root = Path(args.job_root).resolve()
        budget.root = root
        work = root / 'work'
        marker = read(root / 'OWNERSHIP.json')
        if marker['jobRoot'] != str(root) or marker['work'] != str(work): raise RuntimeError('Wrong owned root')
        receipts = root / 'receipts-package-discovery'
        receipts.mkdir(exist_ok=False)
        budget.receipts = receipts
        save(receipts / 'PHASE-START.json', {'phaseStartMonotonic': budget.start, 'phaseBoundSeconds': 600,
            'completed': False, 'nativeStarted': False, 'hardWatchdogExitCode': 124,
            'clockStartedBeforeInputChecks': True})
        validate_packet()
        args.checkout = args.checkout.resolve()
        q = helper(args.checkout)
        original_read, original_run = q.read, q.run
        q.inventory, q.snapshot, q.save = bounded_inventory, bounded_snapshot, save
        def gate_read(path):
            data = read(path)
            if Path(path).name == 'TEST-PACKAGE-CLOSURE.json':
                data['packages'] += [{'id': x['id'], 'version': x['version']} for x in read(HERE / 'SOURCELINK-PRODUCTION-CLOSURE.json')['archives']]
            return data
        q.read = gate_read
        prepared = read(root / 'PREPARATION.json')
        for directory, key in [(work / 'toolchain', 'toolchainFiles'), (work / 'feed', 'feedFiles'), (work / 'source', 'stagedSourceFiles'), (q.HERE, 'helperFiles')]:
            if bounded_inventory(directory) != prepared[key]: raise RuntimeError('Prepared inputs changed: ' + key)
        env = dict(os.environ)
        owned = {'APPDATA': str(work / 'appdata'), 'LOCALAPPDATA': str(work / 'local-appdata'),
            'DOTNET_CLI_HOME': str(work / 'dotnet-home'), 'DOTNET_ROOT': str(work / 'toolchain'),
            'DOTNET_GENERATE_ASPNET_CERTIFICATE': 'false', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH': 'false',
            'DOTNET_CLI_USE_MSBUILD_SERVER': '0', 'MSBUILDDISABLENODEREUSE': '1',
            'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
            'DOTNET_MULTILEVEL_LOOKUP': '0', 'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': '1',
            'MSBuildEnableWorkloadResolver': 'false', 'NUGET_PACKAGES': str(work / 'packages'),
            'NUGET_HTTP_CACHE_PATH': str(work / 'http-cache'), 'NUGET_PLUGINS_CACHE_PATH': str(work / 'plugin-cache'),
            'NUGET_CERT_REVOCATION_MODE': 'offline', 'TEMP': str(work / 'tmp'), 'TMP': str(work / 'tmp')}
        env.update(owned)
        for name in ['tmp', 'local-appdata', 'dotnet-home', 'packages', 'appdata', 'http-cache', 'plugin-cache']:
            (work / name).mkdir(mode=0o700, exist_ok=True)
        state = {'work': work, 'stage': work / 'source', 'receipts': receipts, 'env': env, 'ownedEnvKeys': set(owned),
            'commands': [], 'start': budget.start, 'slot': 600, 'exe': str(work / 'toolchain/dotnet'),
            'image': read(q.HERE / 'TOOLCHAIN-DISTRIBUTIONS.json')['linuxContainerImage'], 'ownedContainers': budget.containers,
            'token': marker['token'], 'mode': 'package-discovery'}
        def gate_run(command, stem, seconds, st, entrypoint=None):
            budget.check()
            # Reserve final audit/cleanup time; never increase an original command.
            allowed = min(seconds, int(budget.deadline - time.monotonic() - 15))
            if allowed < 1: raise TimeoutError('No remaining phase time for owned command and cleanup')
            if 'build' in command:
                command = [x.replace('-p:IncludeSymbols=false', '-p:IncludeSymbols=true') for x in command]
                command += ['-p:PublishRepositoryUrl=true', '-p:EmbedUntrackedSources=true', '-p:RepositoryCommit=' + SOURCE]
            result = original_run(command, stem, allowed, st, entrypoint)
            budget.check()
            return result
        q.run = gate_run
        q.build(state)
        for family in FAMILIES:
            directory = 'MQTTnet.AspnetCore' if family == 'MQTTnet.AspNetCore' else family
            q.run([state['exe'], 'pack', 'Source/' + directory + '/' + family + '.csproj', '--no-build', '--no-restore',
                '-c', 'Release', '-m:1', '-nodeReuse:false', '-p:BuildInParallel=false', '-p:GeneratePackageOnBuild=true',
                '-p:IncludeSymbols=true', '-p:Version=' + VERSION, '-p:SourceRevisionId=' + SOURCE,
                '-p:AssemblyVersion=1.0.0.0', '-p:RepositoryCommit=' + SOURCE], 'pack-' + family, 25, state)
        package_members(state['stage'], receipts)
        source_links = []
        for family in FAMILIES:
            folder = 'MQTTnet.AspnetCore' if family == 'MQTTnet.AspNetCore' else family
            for tfm in TFMS:
                path = state['stage'] / 'Source' / folder / 'obj/Release' / tfm / (family + '.sourcelink.json')
                document = read(path)
                if len(document.get('documents', {})) != 1 or any(v != 'https://raw.githubusercontent.com/YAJeff/MQTTnet/' + SOURCE + '/*' for v in document['documents'].values()):
                    raise RuntimeError('SourceLink does not bind actual canonical fork commit')
                source_links.append({'path': str(path), 'sha256': sha(path), 'documents': document['documents']})
        save(receipts / 'SOURCELINK-IDENTITIES.json', source_links)
        q.pin_configs(state['stage'])
        q.api_checks(state)
        source_inventory = read(HERE / 'CANONICAL-REGRESSION-INVENTORY.json')
        expected = {m['fullyQualifiedName']: m for c in source_inventory['classes'] for m in c['methods']}
        for tfm in TFMS:
            output = receipts / (tfm + '-discovery.json')
            q.run([state['exe'], str(state['stage'] / 'qualification-probe/bin/Release' / tfm / 'CompileApiProbe.dll'),
                '--discover', str(state['stage'] / 'Source/MQTTnet.Tests/bin/Release' / tfm / 'MQTTnet.Tests.dll'),
                str(output)], tfm + '-discovery', 10, state)
            discovered = read(output)
            actual = {m['fullyQualifiedName']: m for m in discovered['methods']}
            if len(actual) != 629 or set(actual) != set(expected): raise RuntimeError('Dropped/added method mapping')
            if len({name.rsplit('.', 1)[0] for name in actual}) != 89: raise RuntimeError('Test class mapping differs')
            for name, method in actual.items():
                if method['dynamicData'] or method['ignored'] or len(method['staticRows']) != expected[name]['staticDataRows']:
                    raise RuntimeError('Row/ignore mapping differs: ' + name)
            loaded = {x['name']: x for x in discovered['loaded']}
            for family in FAMILIES:
                expected_path = state['stage'] / 'qualification-probe/bin/Release' / tfm / (family + '.dll')
                if loaded[family]['sha256'] != sha(expected_path): raise RuntimeError('Actual loaded native DLL differs')
            save(receipts / (tfm + '-METHOD-MAP.json'), {'metadataDiscoveryOnly': True, 'methods': list(actual.values()),
                'methodCount': 629, 'classCount': 89, 'caseCount': sum(max(1, len(m['staticRows'])) for m in actual.values()),
                'testsInvoked': 0, 'loaded': discovered['loaded']})
        bounded_transfer(state)
        success = True
    except BaseException as exc:
        primary = exc
    finally:
        # Evidence and owned cleanup remain INSIDE the original clock/watchdog.
        # Snapshot/cleanup exceptions are secondary; preserve the original cause.
        if q is not None and state is not None:
            try:
                bounded_snapshot(state['stage'], receipts / 'final-build-evidence')
            except BaseException as exc:
                secondary.append({'phase': 'snapshot', 'failure': repr(exc)})
        if q is not None and receipts is not None:
            try:
                budget.check(cleanup=True)
                # Cleanup may run after a threshold failure but cannot clear it.
                budget.cleanup_mode = True
                q.cleanup(SimpleNamespace(job_root=str(budget.root)))
                budget.check(cleanup=True)
            except BaseException as exc:
                secondary.append({'phase': 'cleanup', 'failure': repr(exc)})
            finally:
                budget.cleanup_mode = False
        try:
            budget.check(cleanup=True)
            final_observation = budget.final_measure()
            budget.close()
        except BaseException as exc:
            secondary.append({'phase': 'hostMonitorJoin', 'failure': repr(exc)})
        if receipts is not None:
            save_unchecked(receipts / 'RESULT.json', {'boundedGateCompleted': success and primary is None and not secondary and budget.failure is None,
                'originalFailure': repr(primary) if primary else None, 'secondaryFailures': secondary,
                'hostMonitorFailure': budget.failure, 'wholeRegressionPassed': False, 'source': SOURCE,
                'commands': state['commands'] if state else [], 'testsInvoked': 0, 'packPublished': False,
                'brokerPinChanged': False, 'rootIndependentAuditPending': True,
                'phaseStartMonotonic': budget.start, 'phaseElapsedSeconds': time.monotonic() - budget.start,
                'phaseBoundSeconds': 600, 'peakDiskObservedBytes': budget.peak_disk, 'diskAbortBytes': 2 * 1024**3,
                'peakLogObservedBytes': budget.peak_log, 'perLogAbortBytes': MAX_FILE,
                'finalRetainedObservationBeforeResultBytes': final_observation,
                'hostOutputCeilingBytes': MAX_FILE, 'evidenceAndCleanupInsideWatchdog': True})
        budget.check(cleanup=True)
        budget.timer.cancel()
        ACTIVE_BUDGET = None
    if primary is not None: raise primary
    if secondary or budget.failure: raise RuntimeError('Gate failed; original and secondary receipts retained')


def validate_packet():
    for record in read(HERE / 'PHASE1-INPUTS.json')['files']:
        path = HERE / record['path']
        if not path.resolve().is_relative_to(HERE) or sha(path) != record['sha256']:
            raise RuntimeError('Phase1 source packet changed')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--mode', choices=['acquire', 'prepare', 'package-discovery', 'cleanup'], required=True)
    parser.add_argument('--checkout', type=Path, required=True)
    parser.add_argument('--job-root', required=True)
    parser.add_argument('--source-zip')
    parser.add_argument('--baseline')
    parser.add_argument('--transfer')
    args = parser.parse_args()
    if args.mode == 'package-discovery':
        gate(args)
    else:
        args.checkout = args.checkout.resolve()
        validate_packet()
        q = helper(args.checkout)
        if args.mode == 'acquire': acquire(q)
        elif args.mode == 'prepare': prepare(args, q)
        else: q.cleanup(args)
