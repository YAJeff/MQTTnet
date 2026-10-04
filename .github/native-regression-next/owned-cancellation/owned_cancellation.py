"""SOURCE REVIEW ONLY. Root must admit this module and exact derived inputs.

No CLI, implicit discovery, retries, publication or host firewall use.
Caller provides its existing verified phase state and exact derived DLL path.
"""
import hashlib
import ipaddress
import json
import os
import subprocess
import time
import uuid
import cleanup_owned
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE = 'docker.io/library/python@sha256:781449467ffb6f04218f09b1ecdcdc7d22b289ee5da9ec498b024e24ad7a6db7'
WORKER_IMAGE = 'mcr.microsoft.com/dotnet/runtime-deps@sha256:3b8db93b36e94c7de4524d5b3da230ab9ef5e336c469c7d5c44e0cdbab49d297'
PREFIX = 'MQTTnet.Tests.Clients.MqttClient.MqttClient_Connection_Tests.'
METHODS = [PREFIX + 'Connect_To_Invalid_Server_Wrong_IP', PREFIX + 'No_Unobserved_Exception']
LABEL = 'mqttnet-context-owner'
ACTIVE_BUDGET = None


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def docker(argv, timeout=10):
    if ACTIVE_BUDGET is not None:
        ACTIVE_BUDGET.check()
        timeout = min(timeout, int(ACTIVE_BUDGET.deadline - time.monotonic() - 90))
        if timeout < 1:
            raise TimeoutError('No fixture cleanup headroom')
    env = dict(os.environ)
    result = subprocess.run(['docker'] + argv, capture_output=True, timeout=timeout, env=env)
    if len(result.stdout) + len(result.stderr) > 16 * 1024 * 1024:
        raise RuntimeError('Docker receipt exceeds bound')
    if result.returncode:
        raise RuntimeError('Docker operation failed: ' + argv[0] + ': ' + result.stderr.decode(errors='replace'))
    return result.stdout.decode()


def inspect(kind, name):
    return json.loads(docker(([kind, 'inspect'] if kind == 'network' else ['inspect']) + [name]))[0]


def owned(row, token):
    labels = row.get('Labels') if 'Config' not in row else row['Config'].get('Labels')
    if not labels or labels.get(LABEL) != token:
        raise RuntimeError('Actual Docker ownership label differs')


def fixture_command(name, action, *arguments):
    return json.loads(docker(['exec', name, 'python', '-B', '/fixture_netlink.py', action] + list(arguments)))


def run_pair(state, tfm, derived_dll, admitted_manifest):
    """Run exactly two reviewed methods; never qualifies the whole suite.

    admitted_manifest: exact sha256 map of ALL work input files relative to work,
    including toolchain, test closure, derived DLL/PDB, immutable native DLL/PDB,
    six original archives, original + derived row bindings, and derivation receipt.
    Caller MUST validate native package provenance and complete typed identities
    before this invocation; this function cannot authorize a derivation itself.
    """
    if os.name == 'nt' or tfm not in ('net8.0', 'net10.0'):
        raise RuntimeError('Only admitted Linux qualification is supported')
    work = Path(state['work']).resolve()
    receipts = Path(state['receipts']).resolve()
    dll = Path(derived_dll).resolve()
    dll.relative_to(work)
    if receipts == work or work in receipts.parents or receipts in work.parents:
        raise RuntimeError('Receipts and sealed read-only inputs must be disjoint')
    actual = {p.relative_to(work).as_posix(): sha(p) for p in work.rglob('*') if p.is_file()}
    if actual != admitted_manifest or not admitted_manifest:
        raise RuntimeError('Exact admitted input inventory differs')
    if os.getuid() != 1001 or os.getgid() != 1001:
        raise RuntimeError('Existing owner identity must be 1001:1001')
    if state['image'] != WORKER_IMAGE:
        raise RuntimeError('Worker image differs from frozen image')
    global ACTIVE_BUDGET
    ACTIVE_BUDGET = state['budget']
    ACTIVE_BUDGET.check()
    token = uuid.uuid4().hex
    network = 'mqttnet-cancel-' + token
    fixture = network + '-fixture'
    image_tag = network + ':fixture'
    builder = network + '-image-builder'
    containers = []
    attachments = []
    cleanup_errors = []
    primary = None
    image_id = None
    network_created = False
    armed = False
    start = time.monotonic()
    record = {'ownerToken': token, 'tfm': tfm, 'methods': [], 'completed': False,
              'inputInventory': actual, 'fixtureBase': BASE, 'wholeSuiteQualified': False,
              'fixtureSources': {'fixture_netlink.py': sha(HERE / 'fixture_netlink.py')}}
    journal = receipts / 'OWNED-FIXTURE-JOURNAL.json'
    save(journal, {'token': token, 'parentOwnerToken': state['token'],
                   'jobRoot': str(work.parent), 'network': network, 'fixture': fixture,
                   'imageTag': image_tag, 'builder': builder, 'workers': [network + '-worker-' + str(i) for i in range(2)]})
    state['ownedContainers'].update([fixture, builder] + [network + '-worker-' + str(i) for i in range(2)])
    try:
        # Build is deferred until explicit Root admission; no apt/pip/firewall tools.
        record['pullOutput'] = docker(['pull', '--platform', 'linux/amd64', BASE], 120)
        base_image = json.loads(docker(['image', 'inspect', BASE]))[0]
        if BASE.split('@')[1] not in {value.split('@')[-1] for value in base_image.get('RepoDigests', [])}:
            raise RuntimeError('Pinned tooling base digest differs')
        record['baseImage'] = base_image
        # No Dockerfile build intermediates: a never-started, already labeled
        # container receives exactly one sealed file, then a bounded commit.
        # Every created container/image is owned from its first observable state.
        containers.append(builder)
        docker(['create', '--name', builder, '--network', 'none', '--user', '0:0',
                '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
                '--memory', '67108864', '--memory-swap', '67108864', '--cpus', '0.25',
                '--pids-limit', '16', '--label', LABEL + '=' + token,
                '--label', 'mqttnet-context-fixture-source=' + sha(HERE / 'fixture_netlink.py'),
                base_image['Id']])
        builder_row = inspect('container', builder)
        owned(builder_row, token)
        if (builder_row['State']['Running'] or builder_row['State']['Pid'] != 0 or
                builder_row['HostConfig']['NetworkMode'] != 'none' or builder_row['Mounts'] or
                builder_row['HostConfig']['CapDrop'] != ['ALL'] or builder_row['HostConfig']['CapAdd']):
            raise RuntimeError('Image materializer unexpectedly executes or has access')
        record['imageMaterializerInspect'] = builder_row
        docker(['cp', str(HERE / 'fixture_netlink.py'), builder + ':/fixture_netlink.py'], 20)
        record['commitOutput'] = docker(['commit', builder, image_tag], 120)
        image = json.loads(docker(['image', 'inspect', image_tag]))[0]
        if image['Config']['Labels'].get(LABEL) != token:
            raise RuntimeError('Built tooling image ownership differs')
        if image['RootFS']['Layers'][:-1] != base_image['RootFS']['Layers'] or image['Config']['Labels'].get('mqttnet-context-fixture-source') != sha(HERE / 'fixture_netlink.py'):
            raise RuntimeError('Tooling image differs from pinned base plus one sealed file layer')
        image_id = image['Id']
        record['fixtureImage'] = image
        network_created = True
        docker(['network', 'create', '--internal', '--driver', 'bridge',
                '--label', LABEL + '=' + token, network])
        network_created = True
        net = inspect('network', network)
        owned(net, token)
        if not net['Internal'] or net['Driver'] != 'bridge' or net.get('Containers'):
            raise RuntimeError('Expected one empty internal bridge network')
        record['networkBefore'] = net
        containers.append(fixture)
        docker(['create', '--name', fixture, '--network', network,
                '--label', LABEL + '=' + token, '--cap-drop', 'ALL', '--cap-add', 'NET_ADMIN',
                '--security-opt', 'no-new-privileges', '--read-only', '--user', '0:0',
                '--memory', '67108864', '--memory-swap', '67108864', '--cpus', '0.25',
                '--pids-limit', '16', '--tmpfs', '/tmp:rw,nosuid,noexec,size=1m',
                '--entrypoint', 'python', image_id, '-B', '/fixture_netlink.py', 'serve'])
        docker(['start', fixture])
        fixture_row = inspect('container', fixture)
        owned(fixture_row, token)
        host = fixture_row['HostConfig']
        if (host['NetworkMode'] != network or host['Privileged'] or host['CapAdd'] != ['NET_ADMIN'] or
                host['CapDrop'] != ['ALL'] or not host['ReadonlyRootfs'] or
                fixture_row['Config']['User'] != '0:0' or
                any(mount['Type'] != 'tmpfs' or mount['Destination'] != '/tmp' or mount.get('Source')
                    for mount in fixture_row.get('Mounts', []))):
            raise RuntimeError('Actual fixture isolation differs')
        copied_sha = docker(['exec', fixture, 'python', '-B', '-c', 'import hashlib; print(hashlib.sha256(open("/fixture_netlink.py","rb").read()).hexdigest())']).strip()
        if copied_sha != sha(HERE / 'fixture_netlink.py'):
            raise RuntimeError('Actual fixture script bytes differ')
        fixture_ns = docker(['exec', fixture, 'python', '-B', '-c',
                             'import os; print(os.readlink("/proc/self/ns/net"))']).strip()
        if fixture_ns == os.readlink('/proc/self/ns/net'):
            raise RuntimeError('Fixture unexpectedly shares controller network namespace')
        capability = json.loads(docker(['exec', fixture, 'python', '-B', '-c',
            'import os,json; s=dict(x.split(":",1) for x in open("/proc/self/status") if ":" in x); print(json.dumps({"uid":os.getuid(),"capEff":int(s["CapEff"].strip(),16)}))']))
        if capability != {'uid': 0, 'capEff': 1 << 12}:
            raise RuntimeError('Actual fixture capability differs from NET_ADMIN only')
        record['fixtureActualIdentity'] = capability
        destination = fixture_row['NetworkSettings']['Networks'][network]['IPAddress']
        record['fixtureInspect'] = fixture_row
        for number, method in enumerate(METHODS):
            if time.monotonic() >= ACTIVE_BUDGET.deadline - 90:
                raise RuntimeError('Fixture phase deadline')
            worker = network + '-worker-' + str(number)
            result_dir = receipts / (tfm + '-' + method.rsplit('.', 1)[1])
            result_dir.mkdir(mode=0o700)
            args = ['create', '--name', worker, '--network', network, '--user', '1001:1001',
                    '--label', LABEL + '=' + token, '--cap-drop', 'ALL',
                    '--security-opt', 'no-new-privileges', '--read-only',
                    '--memory', '2147483648', '--memory-swap', '2147483648',
                    '--cpus', '2', '--pids-limit', '128', '--log-driver', 'none', '--tmpfs', '/tmp:rw,nosuid,size=64m',
                    '--mount', 'type=bind,src=' + str(work) + ',dst=/job,readonly',
                    '--mount', 'type=bind,src=' + str(result_dir) + ',dst=/results',
                    '--env', 'MQTTNET_OWNED_CANCELLATION_IPV4=' + destination,
                    '--env', 'DOTNET_ROOT=/job/toolchain', '--env', 'DOTNET_MULTILEVEL_LOOKUP=0',
                    '--env', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', '--env', 'DOTNET_CLI_HOME=/tmp',
                    '--entrypoint', '/bin/sh', WORKER_IMAGE, '-c',
                    'command -v sleep >/dev/null || exit 126; while [ ! -f /results/START ]; do sleep 0.025; done; exec "$@"',
                    'owned-start-gate', '/job/toolchain/dotnet', '/job/' + dll.relative_to(work).as_posix(), '--filter',
                    'FullyQualifiedName=' + method, '--report-trx', '--report-trx-filename',
                    'result.trx', '--results-directory', '/results']
            containers.append(worker)
            docker(args)
            stdout = (result_dir / 'stdout.log').open('wb')
            stderr = (result_dir / 'stderr.log').open('wb')
            attachment = subprocess.Popen(['docker', 'start', '-a', worker], stdout=stdout, stderr=stderr)
            attachments.append((attachment, stdout, stderr))
            startup_deadline = time.monotonic() + 10
            while True:
                worker_row = inspect('container', worker)
                if worker_row['State']['Running']:
                    break
                if attachment.poll() is not None or time.monotonic() >= startup_deadline:
                    raise RuntimeError('Owned worker startup gate failed')
                time.sleep(0.025)
            owned(worker_row, token)
            wh = worker_row['HostConfig']
            if (worker_row['Config']['User'] != '1001:1001' or wh['NetworkMode'] != network or
                    wh['CapDrop'] != ['ALL'] or wh['CapAdd'] or wh['Privileged'] or
                    not wh['ReadonlyRootfs'] or wh['Memory'] != 2147483648 or
                    wh['MemorySwap'] != 2147483648 or wh['NanoCpus'] != 2000000000 or wh['PidsLimit'] != 128 or
                    set(worker_row['NetworkSettings']['Networks']) != {network}):
                raise RuntimeError('Actual worker isolation differs')
            tools = docker(['exec', worker, '/bin/sh', '-c',
                'set -eu; command -v readlink; command -v cat; command -v sleep; readlink /proc/self/ns/net; cat /proc/self/status']).splitlines()
            if len(tools) < 5 or any(not value.startswith('/') for value in tools[:3]):
                raise RuntimeError('Required in-worker tooling was not observed')
            worker_ns = tools[3]
            status = dict(line.split(':', 1) for line in tools[4:] if ':' in line)
            if (status['Uid'].split() != ['1001'] * 4 or status['Gid'].split() != ['1001'] * 4 or
                    int(status['CapEff'].strip(), 16) != 0 or status['NoNewPrivs'].strip() != '1'):
                raise RuntimeError('Actual in-worker identity/isolation differs')
            record.setdefault('workerActualToolsAndIdentity', []).append(tools)
            if worker_ns in (fixture_ns, os.readlink('/proc/self/ns/net')):
                raise RuntimeError('Worker network namespace isolation differs')
            record.setdefault('workerNamespaces', []).append(worker_ns)
            membership = inspect('network', network)
            owned(membership, token)
            if not membership['Internal'] or set(membership['Containers']) != {fixture_row['Id'], worker_row['Id']}:
                raise RuntimeError('Only owned fixture and worker may join the internal network')
            actual_binds = {(mount['Type'], mount['Source'], mount['Destination'], mount['RW'])
                            for mount in worker_row['Mounts'] if mount['Type'] != 'tmpfs'}
            if actual_binds != {('bind', str(work), '/job', False), ('bind', str(result_dir), '/results', True)}:
                raise RuntimeError('Actual worker owned mounts differ')
            if any(mount['Destination'] != '/tmp' or mount.get('Source') for mount in worker_row['Mounts'] if mount['Type'] == 'tmpfs'):
                raise RuntimeError('Unexpected worker temporary mount')
            if worker_row['Config']['Env'].count('MQTTNET_OWNED_CANCELLATION_IPV4=' + destination) != 1:
                raise RuntimeError('Actual owned-address environment differs')
            source = worker_row['NetworkSettings']['Networks'][network]['IPAddress']
            for address in (source, destination):
                ip = ipaddress.IPv4Address(address)
                if not any(ip in ipaddress.ip_network(c) for c in ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')):
                    raise RuntimeError('Docker assigned non-RFC1918 address')
            record.setdefault('armRequests', []).append({'method': method, 'workerInspect': worker_row, 'source': source, 'destination': destination})
            before = fixture_command(fixture, 'arm', '--token', token,
                                     '--source', source, '--destination', destination)
            armed = True
            if before['counters'] != {'bytes': 0, 'packets': 0} or before['netns'] != fixture_ns:
                raise RuntimeError('Fresh exact drop rule prerequisite differs')
            entry = {'method': method, 'before': before, 'workerInspect': worker_row,
                     'pendingSynObserved': False}
            record['methods'].append(entry)
            (result_dir / 'START').write_bytes(b'owned-controller-arm-complete\n')
            deadline = min(ACTIVE_BUDGET.deadline - 90, time.monotonic() + 120)
            while inspect('container', worker)['State']['Running']:
                ACTIVE_BUDGET.check()
                if time.monotonic() >= deadline or sum(p.stat().st_size for p in result_dir.rglob('*') if p.is_file()) > 16 * 1024 * 1024:
                    docker(['kill', worker])
                    raise RuntimeError('Native cancellation process deadline/log bound')
                probe = subprocess.run(['docker', 'exec', worker, '/bin/sh', '-c', 'cat /proc/net/tcp'],
                                       capture_output=True, timeout=5)
                if probe.returncode == 0:
                    for line in probe.stdout.decode().splitlines()[1:]:
                        fields = line.split()
                        target = fields[2].split(':')
                        expected_ip = ''.join(reversed([format(v, '02X') for v in ipaddress.IPv4Address(destination).packed]))
                        if target == [expected_ip, '075B'] and fields[3] == '02':
                            entry['pendingSynObserved'] = True
                            entry['pendingTcpRow'] = line
                time.sleep(0.025)
            attachment.wait(timeout=10)
            stdout.close()
            stderr.close()
            if attachment.returncode != 0:
                raise RuntimeError('Original Docker attach process failed')
            entry['attachExitCode'] = attachment.returncode
            final = inspect('container', worker)
            entry['finalWorkerInspect'] = final
            after = fixture_command(fixture, 'status')
            entry['after'] = after
            if (final['State']['ExitCode'] != 0 or final['State']['OOMKilled'] or
                    final['State']['Running'] or not entry['pendingSynObserved'] or
                    after['counters']['packets'] <= before['counters']['packets']):
                raise RuntimeError('Original cancellation test or owned pending-SYN prerequisite failed')
            trx_files = list(result_dir.glob('*.trx'))
            if len(trx_files) != 1:
                raise RuntimeError('Missing or duplicate cancellation TRX')
            trx = ET.fromstring(trx_files[0].read_bytes())
            counts = trx.find('.//{*}Counters')
            rows = trx.findall('.//{*}UnitTestResult')
            definitions = trx.findall('.//{*}UnitTest')
            if counts is None or len(rows) != 1 or len(definitions) != 1:
                raise RuntimeError('Exact cancellation case count differs')
            for key, value in counts.attrib.items():
                if int(value) != (1 if key in ('total', 'executed', 'passed') else 0):
                    raise RuntimeError('Original cancellation TRX has a nonpass counter')
            if any(counts.get(key) != '1' for key in ('total', 'executed', 'passed')):
                raise RuntimeError('Required exact counters missing')
            tm = definitions[0].find('{*}TestMethod')
            if (tm is None or tm.get('className', '').split(',')[0] + '.' + tm.get('name', '') != method or
                    rows[0].get('testId') != definitions[0].get('id') or rows[0].get('outcome') != 'Passed' or
                    rows[0].get('testName') != method.rsplit('.', 1)[1]):
                raise RuntimeError('Exact original cancellation identity/outcome differs')
            entry['originalTrxSha256'] = sha(trx_files[0])
            entry['disarm'] = fixture_command(fixture, 'disarm')
            armed = False
            docker(['rm', worker])
            proof = subprocess.run(['docker', 'inspect', worker], capture_output=True, timeout=10)
            if proof.returncode == 0 or b'no such object' not in proof.stderr.lower():
                raise RuntimeError('Fresh worker absence not proven')
            containers.remove(worker)
            entry['freshWorkerAbsence'] = True
        if actual != {p.relative_to(work).as_posix(): sha(p) for p in work.rglob('*') if p.is_file()}:
            raise RuntimeError('Sealed input bytes changed')
        record['completed'] = True
    except BaseException as exc:
        primary = exc
        record['failure'] = repr(exc)
    finally:
        # Independent always-run journal cleanup also handles hard outer exit124,
        # interrupted image builds and lost Docker command responses.
        if armed:
            try:
                record['failureDisarm'] = fixture_command(fixture, 'disarm')
            except BaseException as exc:
                record['failureDisarmError'] = repr(exc)
        try:
            record['cleanup'] = cleanup_owned.cleanup(work.parent, journal)
            for name in [fixture, builder] + [network + '-worker-' + str(i) for i in range(2)]:
                state['ownedContainers'].discard(name)
        except BaseException as exc:
            cleanup_errors.append(repr(exc))
        for attachment, stdout, stderr in attachments:
            try:
                if attachment.poll() is None:
                    attachment.terminate()
                    try:
                        attachment.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        attachment.kill()
                        attachment.wait(timeout=5)
                stdout.close()
                stderr.close()
            except BaseException as exc:
                cleanup_errors.append('attach process: ' + repr(exc))
        record['cleanupErrors'] = cleanup_errors
        record['elapsedSeconds'] = time.monotonic() - start
        record['completed'] = record['completed'] and not cleanup_errors
        save(receipts / (tfm + '-OWNED-CANCELLATION.json'), record)
        ACTIVE_BUDGET = None
    if primary:
        raise primary
    if cleanup_errors:
        raise RuntimeError('Owned cleanup failed: ' + repr(cleanup_errors))
    return record
