"""Source-only exact derivation; no compiler, restore or native invocation."""
import hashlib
import json
from pathlib import Path


def derive(canonical_source, destination, extension_hashes):
    canonical_source = Path(canonical_source).resolve()
    destination = Path(destination).resolve()
    if destination.exists():
        raise RuntimeError('Derivation destination must be new')
    originals = canonical_source / 'Source/MQTTnet.Tests'
    destination.mkdir(parents=True)
    files = {}
    for path in originals.rglob('*'):
        if path.is_file() and not any(p in ('bin', 'obj') for p in path.relative_to(originals).parts):
            target = destination / path.relative_to(originals)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(path.read_bytes())
            files[path.relative_to(originals).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    connection = destination / 'Clients/MqttClient/MqttClient_Connection_Tests.cs'
    raw = connection.read_bytes()
    old = b'.WithTcpServer("1.2.3.4")'
    if raw.count(old) != 2:
        raise RuntimeError('Canonical two-address precondition differs')
    derived = raw.replace(old, b'.WithTcpServer(OwnedCancellationFixture.Address)')
    connection.write_bytes(derived)
    (connection.parent / 'OwnedCancellationFixture.cs').write_bytes(
        (Path(__file__).parent / 'OwnedCancellationFixture.cs').read_bytes())
    project = destination / 'MQTTnet.Tests.csproj'
    raw_project = project.read_bytes()
    import re
    refs = list(re.finditer(rb'<ProjectReference Include="([^"]+)"\s*/>', raw_project))
    if len(refs) != 5:
        raise RuntimeError('Canonical five project references differ')
    replacements = {}
    native = {'MQTTnet', 'MQTTnet.Server', 'MQTTnet.AspNetCore'}
    extensions = {'MQTTnet.Extensions.Rpc', 'MQTTnet.Extensions.TopicTemplate'}
    seen = set()
    for match in refs:
        name = match.group(1).decode().replace('\\', '/').split('/')[-1][:-7]
        if name.lower() == 'mqttnet.aspnetcore':
            name = 'MQTTnet.AspNetCore'
        if name in seen:
            raise RuntimeError('Duplicate project reference')
        seen.add(name)
        if name in native:
            replacement = '<PackageReference Include="' + name + '" Version="[5.2.0-local.tlscontext.24208d37]" />'
        elif name in extensions:
            replacement = '<Reference Include="' + name + '"><HintPath>$(OwnedFrozenExtensionDirectory)/' + name + '.dll</HintPath><Private>true</Private></Reference>'
        else:
            raise RuntimeError('Unexpected project reference')
        replacements[match.group(0)] = replacement.encode()
    if seen != native | extensions or set(extension_hashes) != {name + suffix for name in extensions for suffix in ('.dll', '.pdb')}:
        raise RuntimeError('Exact frozen extension closure differs')
    for old_ref, new_ref in replacements.items():
        raw_project = raw_project.replace(old_ref, new_ref)
    project.write_bytes(raw_project)
    receipt = {'canonicalSourceFiles': files,
               'derivedSourceFiles': {p.relative_to(destination).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                                      for p in destination.rglob('*') if p.is_file()},
               'originalConnectionSha256': hashlib.sha256(raw).hexdigest(),
               'derivedConnectionSha256': hashlib.sha256(derived).hexdigest(),
               'addressChanges': 2, 'nativeProjectReferences': 0,
               'exactFrozenExtensionHashes': extension_hashes,
               'nativeExecutionStarted': False, 'derivedTestBinaryProduced': False}
    (destination / 'DERIVATION-SOURCE.json').write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt
