"""Bounded branch qualification. Source preparation never starts this runner."""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tarfile
import threading
import time
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
MAX_LOG = 16 * 1024**2
MAX_DISK = 2 * 1024**3


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def save(path, data):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')


def digest(path, algorithm='sha256'):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest().upper()


def inventory(root):
    return [{'path': p.relative_to(root).as_posix(), 'bytes': p.stat().st_size,
             'sha256': digest(p)} for p in sorted(root.rglob('*')) if p.is_file()]


def disk_bytes(root):
    total = 0
    for path in root.rglob('*'):
        try:
            if path.is_file():
                total += path.stat().st_size
        except FileNotFoundError:
            # NuGet replaces transient extraction files while the monitor is reading.
            continue
    return total


def owned_mount_identity(work):
    owner = work.stat()
    if owner.st_uid == 0 or owner.st_gid == 0 or owner.st_uid != os.getuid() or owner.st_gid != os.getgid():
        raise RuntimeError('Owned mount must belong to the non-root qualification controller')
    return owner, str(owner.st_uid) + ':' + str(owner.st_gid)


def download(url, target, expected, algorithm='sha256'):
    target.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url, timeout=30) as source, target.open('wb') as dest:
        shutil.copyfileobj(source, dest)
    if digest(target, algorithm) != expected.upper():
        raise RuntimeError('Vendor archive hash mismatch: ' + target.name)


def unzip(path, dest):
    with zipfile.ZipFile(path) as archive:
        for entry in archive.infolist():
            target = (dest / entry.filename).resolve()
            if not target.is_relative_to(dest.resolve()):
                raise RuntimeError('Archive path escapes owned stage')
        archive.extractall(dest)


def derive_source(stage, archive, receipt):
    """Exact declared Git-blob to previously qualified raw-byte reconstruction."""
    manifest = read(HERE / 'SOURCE-MANIFEST.json')
    rows = []
    status = 'FAILED'
    failure = None
    try:
        with zipfile.ZipFile(archive) as source:
            if source.comment.decode('ascii') != manifest['source']:
                raise RuntimeError('Exported source commit comment differs')
        if len(inventory(stage)) != len(manifest['files']) or len(manifest['files']) != 540:
            raise RuntimeError('Exported source file count differs')
        for record in manifest['files']:
            path = stage / record['path']
            if not path.resolve().is_relative_to(stage.resolve()):
                raise RuntimeError('Source manifest path escapes stage')
            original = path.read_bytes()
            observed = hashlib.sha256(original).hexdigest().upper()
            row = {'path': record['path'], 'gitBlobExpectedSha256': record['gitBlobSha256'],
                   'gitBlobObservedSha256': observed, 'gitBlobObservedBytes': len(original),
                   'transform': record['stagingTransform'], 'qualifiedRawExpectedSha256': record['sha256']}
            rows.append(row)
            if observed != record['gitBlobSha256'] or len(original) != record['gitBlobBytes']:
                raise RuntimeError('Exact Git source bytes differ: ' + record['path'])
            if record['stagingTransform'] == 'identity':
                qualified = original
            elif record['stagingTransform'] == 'lf_to_crlf':
                if b'\r' in original:
                    raise RuntimeError('Unexpected carriage return in canonical Git source')
                qualified = original.replace(b'\n', b'\r\n')
                if qualified.replace(b'\r\n', b'\n') != original:
                    raise RuntimeError('Declared source transform is not exactly invertible')
            else:
                raise RuntimeError('Undeclared source transformation')
            actual = hashlib.sha256(qualified).hexdigest().upper()
            row.update(qualifiedRawObservedSha256=actual, qualifiedRawObservedBytes=len(qualified))
            if actual != record['sha256'] or len(qualified) != record['qualifiedRawBytes']:
                raise RuntimeError('Reconstruction differs from qualified raw source: ' + record['path'])
            if qualified != original:
                path.write_bytes(qualified)
        status = 'PASS_EXACT_SOURCE_BYTES_ONLY_NOT_RUNTIME_QUALIFICATION'
    except Exception as exception:
        failure = str(exception)
        raise
    finally:
        save(receipt, {'status': status, 'source': manifest['source'],
             'sourceArchiveSha256': digest(archive), 'manifestSha256': digest(HERE / 'SOURCE-MANIFEST.json'),
             'rows': rows, 'failure': failure, 'runtimeStarted': False})


def run(command, stem, seconds, state, entrypoint=None):
    """Every dotnet command has a private Windows job or bounded Linux container."""
    receipts = state['receipts']
    if os.name == 'nt':
        if entrypoint is not None:
            raise RuntimeError('Linux permissions probe cannot run on Windows')
        import windows_process_support as jobs
        jobs.WHOLE_SECONDS = state['slot']
        result = jobs.run_job(jobs.api(), command, state['stage'], state['env'], seconds,
                              stem, receipts, state['work'], state['start'])
        state['commands'].append(result)
        return
    container = 'mqttnet-context-' + uuid.uuid4().hex
    mount_owner, user = owned_mount_identity(state['work'])
    # Immutable runtime-deps image; mounted SDK/feed staged and verified before this phase.
    mapped = [str(x).replace(str(state['work'].parent), '/job') for x in command]
    args = ['docker', 'create', '--name', container, '--user', user, '--network', 'none',
            '--memory', str(2 * 1024**3), '--memory-swap', str(2 * 1024**3),
            '--pids-limit', '128', '--cpus', '2', '--cap-drop', 'ALL',
            '--security-opt', 'no-new-privileges', '--read-only',
            '--label', 'mqttnet-context-owner=' + state['token'],
            '--tmpfs', '/tmp:rw,nosuid,size=64m',
            '-v', str(state['work'].parent) + ':/job:rw', '-w', '/job/work/source']
    for key, value in state['env'].items():
        if key in state['ownedEnvKeys']:
            args += ['-e', key + '=' + value.replace(str(state['work']), '/job/work')]
    if entrypoint not in (None, '/bin/sh'):
        raise RuntimeError('Undeclared container entrypoint')
    args += ['--entrypoint', entrypoint or '/job/work/toolchain/dotnet', state['image']] + mapped[1:]
    state['ownedContainers'].add(container)
    save(receipts / (stem + '.container.json'), {'name': container, 'argv': mapped,
         'timeoutSeconds': seconds, 'image': state['image'], 'network': 'none',
         'memoryBytes': 2 * 1024**3, 'pidsLimit': 128, 'ownerToken': state['token'],
         'ownedMountUid': mount_owner.st_uid, 'ownedMountGid': mount_owner.st_gid,
         'requestedContainerUser': user})
    subprocess.run(args, check=True, capture_output=True, timeout=20)
    failure = None
    max_disk = 0
    try:
        with (receipts / (stem + '.stdout.log')).open('wb') as stdout, \
             (receipts / (stem + '.stderr.log')).open('wb') as stderr:
            process = subprocess.Popen(['docker', 'start', '-a', container], stdout=stdout, stderr=stderr)
            deadline = time.monotonic() + seconds
            while process.poll() is None:
                log_bytes = max((receipts / (stem + suffix)).stat().st_size
                                for suffix in ['.stdout.log', '.stderr.log'])
                used = disk_bytes(state['work'].parent)
                max_disk = max(max_disk, used)
                if time.monotonic() > deadline or log_bytes > MAX_LOG or used > MAX_DISK:
                    failure = 'Timeout or disk/log abort threshold'
                    subprocess.run(['docker', 'kill', container], capture_output=True, timeout=10)
                    break
                time.sleep(.2)
            process.wait(timeout=10)
        raw = json.loads(subprocess.check_output(['docker', 'inspect', container], timeout=10))[0]
        if raw['Config'].get('User') != user:
            failure = failure or 'Actual container user differs from owned mount identity'
        max_disk = max(max_disk, disk_bytes(state['work'].parent))
        if max_disk > MAX_DISK:
            failure = failure or 'Disk abort threshold exceeded'
        code = raw['State']['ExitCode']
        save(receipts / (stem + '.process.json'), {'exitCode': code, 'failure': failure,
             'containerState': raw['State'], 'actualLimits': raw['HostConfig'],
             'actualContainerUser': raw['Config'].get('User'),
             'maxDiskObservedBytes': max_disk, 'diskAbortBytes': MAX_DISK,
             'diskOvershootBytes': max(0, max_disk - MAX_DISK)})
        state['commands'].append({'stem': stem, 'exitCode': code})
        if failure or code != 0 or raw['State']['Running'] or raw['State']['OOMKilled']:
            raise RuntimeError('Qualification command failed: ' + stem)
    finally:
        subprocess.run(['docker', 'rm', '-f', container], check=True, capture_output=True, timeout=10)
        probe = subprocess.run(['docker', 'inspect', container], capture_output=True, timeout=10)
        if probe.returncode == 0 or 'no such object' not in probe.stderr.decode(errors='replace').lower():
            raise RuntimeError('Owned container absence not proven')
        state['ownedContainers'].discard(container)
        save(receipts / (stem + '.cleanup.json'), {'ownedContainer': container,
             'freshAbsent': True, 'inspectExit': probe.returncode})


def prepare(args):
    root = Path(args.job_root).resolve()
    if root.exists():
        raise RuntimeError('Job root must be new')
    root.mkdir(mode=0o700)
    work = root / 'work'
    work.mkdir(mode=0o700)
    save(root / 'OWNERSHIP.json', {'jobRoot': str(root), 'work': str(work), 'token': uuid.uuid4().hex,
         'controllerUid': os.getuid() if os.name != 'nt' else None,
         'controllerGid': os.getgid() if os.name != 'nt' else None,
         'ownedRootCreationMode': '0700', 'ownedWorkCreationMode': '0700'})
    toolchain = work / 'toolchain'
    toolchain.mkdir()
    rid = 'win-x64' if os.name == 'nt' else 'linux-x64'
    distributions = read(HERE / 'TOOLCHAIN-DISTRIBUTIONS.json')
    archive_records = []
    for archive in distributions['distributions']:
        if archive['rid'] != rid:
            continue
        dest = work / 'downloads' / (archive['version'] + '-' + archive['component'] + '-' + archive['name'])
        download(archive['url'], dest, archive['hash'], 'sha512')
        archive_records.append({'name': dest.name, 'sha512': digest(dest, 'sha512'), 'bytes': dest.stat().st_size})
        if archive['component'] == 'reference-pack-source':
            prefixes = ('packs/Microsoft.NETCore.App.Ref/8.0.30/', 'packs/Microsoft.AspNetCore.App.Ref/8.0.30/')
            if dest.suffix == '.zip':
                with zipfile.ZipFile(dest) as source:
                    for entry in source.infolist():
                        if entry.filename.lstrip('./').startswith(prefixes):
                            source.extract(entry, toolchain)
            else:
                with tarfile.open(dest) as source:
                    members = [entry for entry in source.getmembers() if entry.name.lstrip('./').startswith(prefixes)]
                    source.extractall(toolchain, members=members, filter='data')
        elif dest.suffix == '.zip':
            unzip(dest, toolchain)
        else:
            with tarfile.open(dest) as source:
                source.extractall(toolchain, filter='data')
        if dest.parent.resolve() != (work / 'downloads').resolve() or digest(dest, 'sha512') != archive['hash'].upper():
            raise RuntimeError('Owned archive deletion path or hash mismatch')
        dest.unlink()
    for family in ['Microsoft.NETCore.App.Ref', 'Microsoft.AspNetCore.App.Ref']:
        if not (toolchain / 'packs' / family / '8.0.30').is_dir():
            raise RuntimeError('Exact net8 reference pack missing')
    for family in ['Microsoft.NETCore.App', 'Microsoft.AspNetCore.App']:
        for version in ['8.0.30', '10.0.11']:
            if not (toolchain / 'shared' / family / version).is_dir():
                raise RuntimeError('Exact runtime missing')
    if os.name != 'nt':
        subprocess.run(['docker', 'pull', distributions['linuxContainerImage']], check=True, timeout=120)
    if args.transfer:
        transfer = Path(args.transfer).resolve()
        seal = read(transfer / 'TRANSFER.json')
        for record in seal['files']:
            source = transfer / record['path']
            if digest(source) != record['sha256']:
                raise RuntimeError('Transferred binary changed')
            dest = work / 'source' / record['path']
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, dest)
    else:
        stage = work / 'source'
        stage.mkdir()
        unzip(Path(args.source_zip), stage)
        derive_source(stage, Path(args.source_zip), root / 'SOURCE-DERIVATION.json')
        global_json = read(stage / 'global.json')
        global_json['sdk'] = {'version': '10.0.303', 'rollForward': 'disable', 'allowPrerelease': False}
        save(stage / 'global.json', global_json)
        for folder in ['probe', 'inspector']:
            shutil.copytree(HERE / folder, stage / ('qualification-' + folder))
        baseline = read(HERE / 'BASELINE-TRIPLE-TFM-SEAL.json')
        for record in baseline['members']:
            matches = list(Path(args.baseline).rglob(record['archiveName']))
            if len(matches) != 1 or digest(matches[0]) != record['archiveSha256'].upper():
                raise RuntimeError('Original abfa package changed or missing')
            with zipfile.ZipFile(matches[0]) as archive:
                dest = stage / '.github/legacy-abfa/test-binaries' / record['framework'] / (record['package'] + '.dll')
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(archive.read(record['member']))
            if digest(dest) != record['dllSha256'].upper():
                raise RuntimeError('Original baseline DLL mismatch')
        for package in read(HERE / 'TEST-PACKAGE-CLOSURE.json')['packages']:
            filename = package['id'].lower() + '.' + package['version'] + '.nupkg'
            download(package['url'], work / 'feed' / filename, package['sha256'])
    save(root / 'PREPARATION.json', {'vendorArchiveRecords': archive_records, 'toolchainFiles': inventory(toolchain),
         'feedFiles': inventory(work / 'feed') if (work / 'feed').exists() else [],
         'stagedSourceFiles': inventory(work / 'source'),
         'helperFiles': inventory(HERE), 'runtimeStarted': False})


def build(state):
    work, stage, receipts = state['work'], state['stage'], state['receipts']
    from xml.sax.saxutils import escape
    config = work / 'NuGet.Config'
    feed = str(work / 'feed') if os.name == 'nt' else '/job/work/feed'
    config.write_text('<configuration><packageSources><clear/><add key="sealed-local" value="' +
                     escape(feed) + '"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders>' +
                     '<auditSources><clear/></auditSources></configuration>', encoding='utf-8')
    user_config = work / 'appdata/NuGet/NuGet.Config'
    user_config.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(config, user_config)
    if os.name != 'nt':
        prove_owned_permissions(state)
    flags = ['-c', 'Release', '-m:1', '-nodeReuse:false', '-p:BuildInParallel=false',
             '-p:UseSharedCompilation=false', '-p:GeneratePackageOnBuild=false', '-p:IncludeSymbols=false',
             '-p:UseAppHost=false', '-p:AssemblyVersion=1.0.0.0',
             '-p:SourceRevisionId=2a6cb80a7a0625967d0f06e83e15e4be03d9c503',
             '-p:Version=5.2.0-local.tlscontext.2a6cb80a', '-p:RestoreConfigFile=' + str(config),
             '-p:RestorePackagesPath=' + str(work / 'packages'), '-p:RestoreSources=' + str(work / 'feed')]
    commands = [('Source/MQTTnet.Tests/MQTTnet.Tests.csproj', 180, 'candidate-test-build', []),
                ('.github/legacy-certificate-provider/LegacyCertificateProviderFixture.csproj', 90, 'old-provider-build', []),
                ('qualification-probe/CompileApiProbe.csproj', 90, 'probe-build', ['-p:CandidateRoot=' + str(stage)]),
                ('qualification-inspector/ApiSurfaceInspector.csproj', 90, 'inspector-build', [])]
    for project, seconds, stem, extra in commands:
        try:
            run([state['exe'], 'build', project] + flags + extra, stem, seconds, state)
        finally:
            snapshot(stage, receipts / 'build-evidence' / stem)
    expected = {p['id'].lower() + '/' + p['version'] for p in read(HERE / 'TEST-PACKAGE-CLOSURE.json')['packages']}
    observed = set()
    for path in stage.rglob('project.assets.json'):
        assets = read(path)
        actual = {name.lower() for name, value in assets['libraries'].items() if value.get('type') == 'package'}
        if not actual.issubset(expected):
            raise RuntimeError('Restored graph differs from sealed closure: ' + str(actual - expected))
        allowed = str(work / 'packages') if os.name == 'nt' else '/job/work/packages'
        if any(Path(folder).as_posix().rstrip('/') != Path(allowed).as_posix() for folder in assets['packageFolders']):
            raise RuntimeError('Restore used an unowned cache')
        observed |= actual
    if observed != expected:
        raise RuntimeError('Restored package closure not exact: ' + str(expected - observed))
    if list(stage.rglob('*.nupkg')):
        raise RuntimeError('Unexpected native package emission')
    save(receipts / 'PACKAGE-CLOSURE-RESULT.json', {'exact': True, 'packages': sorted(observed)})
    run([state['exe'], '--info'], 'sdk-info', 5, state)
    pin_configs(stage)


def prove_owned_permissions(state):
    """Before any dotnet command, observe the actual isolated UID/GID and private writes."""
    import shlex
    paths = [state['work'] / name for name in
             ['', 'dotnet-home', 'tmp', 'packages', 'appdata', 'local-appdata', 'http-cache', 'plugin-cache']]
    owner = str(os.getuid()) + ':' + str(os.getgid())
    for path in paths:
        info = path.stat()
        if info.st_uid != os.getuid() or info.st_gid != os.getgid() or info.st_mode & 0o777 != 0o700:
            raise RuntimeError('Private directory ownership/mode differs from declared controller identity')
    token = '.mqttnet-permission-' + uuid.uuid4().hex
    script = 'set -eu; id -u; id -g; for p in ' + ' '.join(shlex.quote(str(p)) for p in paths) + '; do '
    script += 'd="$p/' + token + '"; test ! -e "$d"; test -w "$p"; test -x "$p"; '
    script += '(umask 077; mkdir "$d"; printf owned > "$d/probe"); '
    script += 'test "$(cat "$d/probe")" = owned; rm "$d/probe"; rmdir "$d"; '
    script += 'printf "%s|" "$p"; stat -c "%u|%g|%a" "$p"; done'
    run(['/bin/sh', '-c', script], 'owned-permissions', 5, state, entrypoint='/bin/sh')
    lines = (state['receipts'] / 'owned-permissions.stdout.log').read_text().splitlines()
    if len(lines) != len(paths) + 2 or lines[:2] != [str(os.getuid()), str(os.getgid())]:
        raise RuntimeError('Isolated permissions probe identity/count mismatch')
    rows = []
    for path, line in zip(paths, lines[2:]):
        observed_path, uid, gid, mode = line.split('|')
        expected_path = str(path).replace(str(state['work'].parent), '/job')
        if observed_path != expected_path or uid + ':' + gid != owner or mode != '700':
            raise RuntimeError('Isolated private path readback differs')
        rows.append({'path': observed_path, 'uid': uid, 'gid': gid, 'mode': mode, 'directoryAndFileWriteReadRemove': True})
    save(state['receipts'] / 'OWNED-PERMISSIONS.json', {'containerUser': owner, 'rows': rows,
         'dotnetStartedBeforeProbe': False, 'worldPermissionsAdded': False,
         'globalPathsTouched': False, 'sourceRuntimeQualified': False})


def snapshot(stage, destination):
    for path in sorted(stage.rglob('*')):
        if path.is_file() and any(part in ['bin', 'obj'] for part in path.relative_to(stage).parts):
            if path.suffix in ['.dll', '.json', '.pdb'] or path.name.endswith(('.g.props', '.g.targets')):
                dest = destination / path.relative_to(stage)
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(path, dest)
    save(destination.parent / (destination.name + '-inventory.json'), inventory(destination))


def pin_configs(stage):
    for path in stage.rglob('*.runtimeconfig.json'):
        version = '8.0.30' if 'net8.0' in path.parts else '10.0.11'
        data = read(path)
        data['runtimeOptions']['rollForward'] = 'Disable'
        for framework in data['runtimeOptions'].get('frameworks', []):
            framework['version'] = version
        if 'framework' in data['runtimeOptions']:
            data['runtimeOptions']['framework']['version'] = version
        save(path, data)


def api_checks(state):
    stage, receipts = state['stage'], state['receipts']
    allowed = ('MQTTnet.Certificates.ICertificateContextProvider|', 'MQTTnet.Certificates.ICertificateContextLease|')
    for tfm in ['net8.0', 'net10.0']:
        surfaces = {}
        for role in ['baseline', 'candidate']:
            directory = stage / ('.github/legacy-abfa/test-binaries/' + tfm if role == 'baseline'
                                 else 'Source/MQTTnet.AspnetCore/bin/Release/' + tfm)
            libraries = [{'name': name, 'path': str(directory / (name + '.dll')),
                          'sha256': digest(directory / (name + '.dll'))}
                         for name in ['MQTTnet', 'MQTTnet.Server', 'MQTTnet.AspNetCore']]
            if role == 'baseline':
                expected = {row['package']: row['dllSha256'].upper() for row in
                            read(HERE / 'BASELINE-TRIPLE-TFM-SEAL.json')['members'] if row['framework'] == tfm}
                if any(row['sha256'] != expected[row['name']] for row in libraries):
                    raise RuntimeError('Original baseline bytes changed')
            if role == 'candidate' and state['mode'] == 'build-api':
                retained = receipts / 'build-evidence/candidate-test-build/Source/MQTTnet.AspnetCore/bin/Release' / tfm
                for row in libraries:
                    if digest(retained / (row['name'] + '.dll')) != row['sha256']:
                        raise RuntimeError('Candidate differs from immediate compiler snapshot')
                    row['path'] = str(retained / (row['name'] + '.dll'))
            manifest = receipts / (tfm + '-' + role + '-identity.json')
            # Linux manifest paths are consumed inside the same network-disabled container.
            save(manifest, {'role': role, 'libraries': libraries})
            if os.name != 'nt':
                save(manifest, json.loads(json.dumps(read(manifest)).replace(str(state['work'].parent), '/job')))
            output = receipts / (tfm + '-' + role + '-surface.json')
            run([state['exe'], str(stage / 'qualification-inspector/bin/Release' / tfm / 'ApiSurfaceInspector.dll'),
                 str(manifest), str(output)], tfm + '-' + role + '-surface', 5, state)
            result = read(output)
            receipt = read(receipts / (tfm + '-' + role + '-surface.stdout.log'))
            if receipt['sha256'] != digest(output) or receipt['assemblyCount'] != 3 or receipt['role'] != role:
                raise RuntimeError('Separate API file digest receipt mismatch')
            if result['framework'] != '.NET ' + ('8.0.30' if tfm == 'net8.0' else '10.0.11'):
                raise RuntimeError('Inspector runtime version differs')
            if result['role'] != role or len(result['loadedAssemblyIdentities']) != 3:
                raise RuntimeError('Inspector identity receipt invalid')
            for actual, expected in zip(result['loadedAssemblyIdentities'], libraries):
                expected_path = expected['path'].replace(str(state['work'].parent), '/job') if os.name != 'nt' else expected['path']
                if actual['name'] != expected['name'] or actual['loadedPath'] != expected_path or actual['sha256'] != expected['sha256']:
                    raise RuntimeError('Loaded DLL identity mismatch')
            if role == 'baseline':
                expected_rows = {'MQTTnet': 262 if tfm == 'net8.0' else 263, 'MQTTnet.Server': 235, 'MQTTnet.AspNetCore': 42}
                if result['typeDefRowsFromMetadataTokens'] != expected_rows:
                    raise RuntimeError('Baseline metadata count mismatch')
                if any(line.startswith(allowed) for line in result['api']['MQTTnet']):
                    raise RuntimeError('Baseline bound candidate interfaces')
            surfaces[role] = result['api']
        diffs = {}
        for name in surfaces['baseline']:
            removed = sorted(set(surfaces['baseline'][name]) - set(surfaces['candidate'][name]))
            added = sorted(set(surfaces['candidate'][name]) - set(surfaces['baseline'][name]))
            diffs[name] = {'removed': removed, 'added': added}
            if removed or any(name != 'MQTTnet' or not line.startswith(allowed) for line in added):
                save(receipts / (tfm + '-api-diff.json'), diffs)
                raise RuntimeError('Strict API comparison failed')
        additions = diffs['MQTTnet']['added']
        if sum('|TYPE|' in line for line in additions) != 2 or not all(any(line.startswith(prefix) for line in additions) for prefix in allowed):
            raise RuntimeError('Two approved interface families required')
        save(receipts / (tfm + '-api-diff.json'), diffs)
        probe = stage / 'qualification-probe/bin/Release' / tfm / 'CompileApiProbe.dll'
        fixture = stage / '.github/legacy-certificate-provider/bin/Release' / tfm / 'LegacyCertificateProviderFixture.dll'
        run([state['exe'], str(probe), str(fixture)], tfm + '-compatibility', 5, state)
        lines = (receipts / (tfm + '-compatibility.stdout.log')).read_text().splitlines()
        if len(lines) != 5 or sum(line.startswith('PASS ') for line in lines) != 4:
            raise RuntimeError('Compatibility named checks missing')
        result = json.loads(lines[-1])
        if result['passed'] != 4 or result['failed'] != 0 or result['skipped'] != 0 or result['contextCreated'] != 0 or result['socketsOpened'] != 0 or result['runtimeVersion'] != ('8.0.30' if tfm == 'net8.0' else '10.0.11'):
            raise RuntimeError('Compatibility check counts differ')


def lifetime(state, windows=False):
    plan = read(HERE / 'PLAN.json')
    section = plan['windows' if windows else 'linux']
    if windows != (os.name == 'nt'):
        raise RuntimeError('Wrong operating system for exact lifetime case selection')
    results = []
    for tfm in ['net8.0', 'net10.0']:
        state['env']['MQTTNET_LEGACY_CERTIFICATE_PROVIDER_DLL'] = str(state['stage'] / '.github/legacy-certificate-provider/bin/Release' / tfm / 'LegacyCertificateProviderFixture.dll')
        state['ownedEnvKeys'].add('MQTTNET_LEGACY_CERTIFICATE_PROVIDER_DLL')
        for group in section['exactMethodGroups']:
            method = group['method']
            stem = tfm + '-' + method
            result_dir = state['receipts'] / 'trx' / stem
            result_dir.mkdir(parents=True)
            dll = state['stage'] / 'Source/MQTTnet.Tests/bin/Release' / tfm / 'MQTTnet.Tests.dll'
            run([state['exe'], str(dll), '--filter', 'FullyQualifiedName=MQTTnet.Tests.Server.CertificateContextLease_Tests.' + method,
                 '--report-trx', '--report-trx-filename', 'result.trx', '--results-directory', str(result_dir)], stem, 10, state)
            files = list(result_dir.rglob('*.trx'))
            if len(files) != 1:
                raise RuntimeError('Missing or duplicate TRX')
            root = ET.parse(files[0]).getroot()
            counters = root.find('.//{*}Counters')
            rows = root.findall('.//{*}UnitTestResult')
            if counters is None or int(counters.get('total')) != group['cases'] or int(counters.get('passed')) != group['cases'] or int(counters.get('failed')) != 0:
                raise RuntimeError('Exact lifetime counts failed')
            if len(rows) != group['cases'] or any(row.get('outcome') != 'Passed' or method not in row.get('testName', '') for row in rows):
                raise RuntimeError('Missing, skipped or unexpected lifetime row')
            results.append({'framework': tfm, 'method': method, 'passed': len(rows), 'trxSha256': digest(files[0])})
    save(state['receipts'] / 'LIFETIME-RESULT.json', {'rows': results,
         'scope': 'Windows creation guard only' if windows else 'Linux actual TLS/context lifetime only',
         'windowsActualTlsQualified': False, 'brokerPinChanged': False})


def transfer(state):
    dest = state['work'].parent / 'transfer'
    for path in state['stage'].rglob('*'):
        if path.is_file() and ('bin' in path.relative_to(state['stage']).parts or 'test-binaries' in path.parts):
            target = dest / path.relative_to(state['stage'])
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, target)
    save(dest / 'TRANSFER.json', {'source': '2a6cb80a7a0625967d0f06e83e15e4be03d9c503', 'files': inventory(dest)})


def execute(args):
    phase_start = time.monotonic()
    owned_containers = set()
    slot = 600 if args.mode == 'build-api' else 300 if args.mode == 'linux-lifetime' else 120
    def watchdog():
        if os.name != 'nt':
            for name in list(owned_containers):
                subprocess.run(['docker', 'rm', '-f', name], capture_output=True, timeout=10)
        os._exit(124)
    timer = threading.Timer(slot, watchdog)
    timer.daemon = True
    timer.start()
    root = Path(args.job_root).resolve()
    marker = read(root / 'OWNERSHIP.json')
    if marker['jobRoot'] != str(root) or marker['work'] != str(root / 'work'):
        raise RuntimeError('Owned root marker mismatch')
    work = root / 'work'
    receipts = root / ('receipts-' + args.mode)
    if receipts.exists():
        raise RuntimeError('No receipt overwrite')
    receipts.mkdir()
    prepared = read(root / 'PREPARATION.json')
    for row in prepared['toolchainFiles']:
        if digest(work / 'toolchain' / row['path']) != row['sha256']:
            raise RuntimeError('Prepared toolchain changed')
    if inventory(work / 'toolchain') != prepared['toolchainFiles']:
        raise RuntimeError('Prepared toolchain file set changed')
    if args.mode == 'linux-lifetime':
        for row in read(root / 'transfer/TRANSFER.json')['files']:
            if digest(work / 'source' / row['path']) != row['sha256']:
                raise RuntimeError('Lifetime binary differs from qualified transfer')
    if args.mode == 'windows-api-guard' and inventory(work / 'source') != prepared['stagedSourceFiles']:
        raise RuntimeError('Transferred binary stage changed')
    if args.mode == 'build-api':
        if inventory(work / 'source') != prepared['stagedSourceFiles'] or inventory(work / 'feed') != prepared['feedFiles']:
            raise RuntimeError('Prepared source or offline feed changed')
    if inventory(HERE) != prepared['helperFiles']:
        raise RuntimeError('Prepared qualification helper changed')
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
    state = {'work': work, 'stage': work / 'source', 'receipts': receipts, 'env': env,
             'ownedEnvKeys': set(owned), 'commands': [], 'start': phase_start,
             'slot': slot,
             'exe': str(work / 'toolchain' / ('dotnet.exe' if os.name == 'nt' else 'dotnet')),
             'image': read(HERE / 'TOOLCHAIN-DISTRIBUTIONS.json')['linuxContainerImage'], 'ownedContainers': owned_containers, 'token': marker['token'], 'mode': args.mode}
    success = False
    try:
        if args.mode == 'build-api':
            build(state)
            api_checks(state)
            transfer(state)
        elif args.mode == 'linux-lifetime':
            lifetime(state)
        else:
            api_checks(state)
            lifetime(state, windows=True)
        success = True
    finally:
        if os.name != 'nt':
            for name in list(state['ownedContainers']):
                subprocess.run(['docker', 'rm', '-f', name], check=True, capture_output=True, timeout=10)
        snapshot(state['stage'], receipts / 'final-build-evidence')
        save(receipts / 'OWNED-WORK-INVENTORY.json', inventory(work))
        observed = disk_bytes(root)
        save(receipts / 'RESULT.json', {'checksPassed': success and observed <= MAX_DISK, 'allPassed': None if success and observed <= MAX_DISK else False, 'independentRootAuditPending': True,
             'commands': state['commands'], 'ownerRuntimeStarted': False, 'workHeld': True,
             'source': '2a6cb80a7a0625967d0f06e83e15e4be03d9c503', 'mode': args.mode,
             'slotSeconds': state['slot'], 'brokerPinChanged': False, 'packagePublication': False,
             'ownedDiskObservedBytes': observed, 'diskAbortBytes': MAX_DISK,
             'diskOvershootBytes': max(0, observed - MAX_DISK)})
        timer.cancel()
        if observed > MAX_DISK:
            raise RuntimeError('Retained evidence exceeded owned disk abort threshold')


def cleanup(args):
    """Observe owned isolation objects after a phase, including an abrupt watchdog exit."""
    root = Path(args.job_root).resolve()
    marker = read(root / 'OWNERSHIP.json')
    if marker['jobRoot'] != str(root):
        raise RuntimeError('Wrong ownership marker')
    rows = []
    if os.name == 'nt':
        import ctypes as C
        from ctypes import wintypes as W
        import windows_process_support as jobs
        kernel = jobs.api()
        kernel.OpenJobObjectW.restype = W.HANDLE
        kernel.OpenJobObjectW.argtypes = [W.DWORD, W.BOOL, W.LPCWSTR]
        for path in root.glob('receipts-*/*.job.json'):
            name = read(path)['jobName']
            if not name.startswith('Local\\MQTTnet-Context-API-'):
                raise RuntimeError('Unexpected job namespace')
            handle = kernel.OpenJobObjectW(4, False, name)
            if handle:
                accounting = jobs.BASIC_ACCOUNTING()
                try:
                    if not kernel.QueryInformationJobObject(handle, 1, C.byref(accounting), C.sizeof(accounting), None):
                        raise C.WinError(C.get_last_error())
                    if accounting.ActiveProcesses != 0:
                        raise RuntimeError('Owned job still active')
                    rows.append({'job': name, 'activeProcesses': 0, 'exists': True})
                finally:
                    kernel.CloseHandle(handle)
            else:
                error = C.get_last_error()
                if error != 2:
                    raise C.WinError(error)
                rows.append({'job': name, 'freshAbsent': True})
    else:
        for path in root.glob('receipts-*/*.container.json'):
            record = read(path)
            name = record['name']
            if record['ownerToken'] != marker['token'] or not name.startswith('mqttnet-context-'):
                raise RuntimeError('Wrong container ownership')
            result = subprocess.run(['docker', 'inspect', name], capture_output=True, timeout=10)
            if result.returncode == 0:
                actual = json.loads(result.stdout)[0]
                if actual['Config']['Labels'].get('mqttnet-context-owner') != marker['token']:
                    raise RuntimeError('Container label ownership mismatch')
                subprocess.run(['docker', 'rm', '-f', name], check=True, capture_output=True, timeout=10)
            proof = subprocess.run(['docker', 'inspect', name], capture_output=True, timeout=10)
            if proof.returncode == 0 or 'no such object' not in proof.stderr.decode(errors='replace').lower():
                raise RuntimeError('Owned container absence not proven')
            rows.append({'container': name, 'freshAbsent': True})
    save(root / 'ISOLATION-POSTFLIGHT.json', {'objects': rows, 'workDeleted': False,
         'independentRootAuditPending': True, 'ephemeralRunnerRetirementOwnedByGitHub': True})


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--job-root', required=True)
    parser.add_argument('--mode', choices=['prepare', 'build-api', 'linux-lifetime', 'windows-api-guard', 'cleanup'], required=True)
    parser.add_argument('--source-zip')
    parser.add_argument('--baseline')
    parser.add_argument('--transfer')
    arguments = parser.parse_args()
    if arguments.mode == 'prepare':
        prepare(arguments)
    elif arguments.mode == 'cleanup':
        cleanup(arguments)
    else:
        execute(arguments)
