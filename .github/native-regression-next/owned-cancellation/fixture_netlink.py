"""Linux namespace-local nftables fixture. Never invoke on a host namespace.

Only Python's standard library and the kernel netfilter syscall ABI are used.
The controller must attest the container namespace before invoking arm.
"""
import argparse
import hashlib
import ipaddress
import json
import os
import socket
import struct
import time
from pathlib import Path

NESTED = 0x8000


def attr(kind, data):
    raw = struct.pack('=HH', len(data) + 4, kind) + data
    return raw + b'\0' * (-len(raw) % 4)


def nested(kind, data):
    return attr(kind | NESTED, data)


def u32(value):
    return struct.pack('!I', value)


def string(value):
    return value.encode('ascii') + b'\0'


def attrs(data):
    output = []
    offset = 0
    while offset < len(data):
        if len(data) - offset < 4:
            raise RuntimeError('Truncated attribute')
        length, kind = struct.unpack_from('=HH', data, offset)
        if length < 4 or offset + length > len(data):
            raise RuntimeError('Invalid attribute length')
        output.append((kind & 0x3fff, data[offset + 4:offset + length]))
        offset += (length + 3) & ~3
    return output


def one(data, kind):
    values = [value for key, value in attrs(data) if key == kind]
    if len(values) != 1:
        raise RuntimeError('Missing or duplicate attribute ' + str(kind))
    return values[0]


def expr(name, data):
    return nested(1, attr(1, string(name)) + nested(2, data))


def payload(base, offset, size):
    return expr('payload', attr(1, u32(1)) + attr(2, u32(base)) +
                attr(3, u32(offset)) + attr(4, u32(size)))


def equal(value):
    return expr('cmp', attr(1, u32(1)) + attr(2, u32(0)) +
                nested(3, attr(1, value)))


def recipe(source, destination):
    return (payload(1, 12, 4) + equal(socket.inet_aton(source)) +
            payload(1, 16, 4) + equal(socket.inet_aton(destination)) +
            payload(1, 9, 1) + equal(b'\x06') +
            payload(2, 2, 2) + equal(struct.pack('!H', 1883)) +
            payload(2, 13, 1) +
            expr('bitwise', attr(1, u32(1)) + attr(2, u32(1)) +
                 attr(3, u32(1)) + attr(6, u32(0)) + nested(4, attr(1, b'\x12')) +
                 nested(5, attr(1, b'\0'))) + equal(b'\x02') +
            expr('counter', b'') +
            expr('immediate', attr(1, u32(0)) +
                 nested(2, nested(2, attr(1, u32(0))))))


class Netfilter:
    def __init__(self):
        self.sock = socket.socket(socket.AF_NETLINK, socket.SOCK_RAW, 12)
        self.sock.bind((0, 0))
        self.sock.settimeout(5)
        self.port = self.sock.getsockname()[0]
        self.sequence = 0

    def message(self, kind, flags, body, family=socket.AF_INET, resource=0):
        self.sequence += 1
        data = struct.pack('!BBH', family, 0, resource) + body
        return self.sequence, struct.pack('=IHHII', 16 + len(data), kind,
                                          flags, self.sequence, self.port) + data

    def receive(self, sequences, dump=False):
        pending = set(sequences)
        output = []
        deadline = time.monotonic() + 8
        while pending:
            if time.monotonic() >= deadline:
                raise RuntimeError('Netlink acknowledgement deadline')
            raw, address = self.sock.recvfrom(1024 * 1024)
            if address[0] != 0:
                raise RuntimeError('Netlink sender is not kernel')
            offset = 0
            while offset < len(raw):
                length, kind, flags, sequence, port = struct.unpack_from('=IHHII', raw, offset)
                if length < 16 or offset + length > len(raw):
                    raise RuntimeError('Invalid netlink message')
                body = raw[offset + 16:offset + length]
                offset += (length + 3) & ~3
                if sequence not in sequences:
                    raise RuntimeError('Unexpected netlink sequence')
                if kind == 2:
                    error = struct.unpack_from('=i', body)[0]
                    if error:
                        raise OSError(-error, os.strerror(-error))
                    if not dump:
                        pending.discard(sequence)
                elif kind == 3:
                    if len(body) >= 4 and struct.unpack_from('=i', body)[0]:
                        raise RuntimeError('Interrupted netlink dump')
                    pending.discard(sequence)
                else:
                    if flags & 0x10:
                        raise RuntimeError('Interrupted netlink dump')
                    output.append((kind, body))
                if len(output) > 64:
                    raise RuntimeError('Unexpected namespace rule volume')
        return output

    def transaction(self, operations):
        _, begin = self.message(16, 1, b'', socket.AF_UNSPEC, 10)
        messages = []
        sequences = []
        for command, flags, body in operations:
            sequence, message = self.message((10 << 8) | command, flags | 5, body)
            messages.append(message)
            sequences.append(sequence)
        _, end = self.message(17, 1, b'', socket.AF_UNSPEC, 10)
        self.sock.sendto(begin + b''.join(messages) + end, (0, 0))
        self.receive(sequences)

    def dump(self, command):
        sequence, message = self.message((10 << 8) | command, 1 | 0x300, b'', socket.AF_UNSPEC)
        self.sock.sendto(message, (0, 0))
        rows = self.receive([sequence], True)
        if any(kind != (10 << 8) | (command - 1) for kind, body in rows):
            raise RuntimeError('Unexpected dump response type')
        return [body[4:] for kind, body in rows]


def exact_fields(data, schema):
    fields = attrs(data)
    if len(fields) != len(schema) or {key for key, value in fields} != set(schema):
        raise RuntimeError('Exact expression attribute schema differs')
    result = {}
    for key, value in fields:
        if key in result:
            raise RuntimeError('Duplicate expression attribute')
        result[key] = exact_fields(value, schema[key]) if schema[key] is not None else value
    return result


def expression_signature(data):
    # Kernel dumps intentionally omit NLA_F_NESTED on legacy nft attributes.
    # Compare exact typed attribute trees, never raw nesting flags/order.
    top = attrs(data)
    if len(top) != 2 or {key for key, value in top} != {1, 2}:
        raise RuntimeError('Unexpected expression envelope')
    name = one(data, 1)
    schemas = {
        string('payload'): {1: None, 2: None, 3: None, 4: None},
        string('cmp'): {1: None, 2: None, 3: {1: None}},
        string('bitwise'): {1: None, 2: None, 3: None, 6: None, 4: {1: None}, 5: {1: None}},
        string('immediate'): {1: None, 2: {2: {1: None}}},
    }
    if name not in schemas:
        raise RuntimeError('Unexpected expression type')
    return name, exact_fields(one(data, 2), schemas[name])


def namespace_baseline(net):
    tables, chains, rules = net.dump(1), net.dump(4), net.dump(7)
    if not tables and not chains and not rules:
        return {'tables': [], 'chains': [], 'rules': []}
    # Docker's embedded DNS may install its own ip/nat table in this fresh
    # container namespace. Preserve and seal only that exact loopback baseline.
    if len(tables) != 1 or one(tables[0], 1) != string('nat') or len(chains) != 6 or len(rules) != 6:
        raise RuntimeError('Unexpected preexisting fixture namespace resources')
    allowed = {'PREROUTING', 'INPUT', 'OUTPUT', 'POSTROUTING', 'DOCKER_OUTPUT', 'DOCKER_POSTROUTING'}
    if {one(chain, 3).rstrip(b'\0').decode('ascii') for chain in chains} != allowed:
        raise RuntimeError('Unexpected Docker DNS baseline chains')
    hooks = {'PREROUTING': (0, -100), 'INPUT': (1, 100), 'OUTPUT': (3, -100), 'POSTROUTING': (4, 100)}
    for chain in chains:
        name = one(chain, 3).rstrip(b'\0').decode('ascii')
        if one(chain, 1) != string('nat'):
            raise RuntimeError('Docker DNS baseline table differs')
        if name in hooks:
            hook, priority = hooks[name]
            if (one(chain, 7) != string('nat') or one(chain, 5) != u32(1) or
                    one(one(chain, 4), 1) != u32(hook) or
                    one(one(chain, 4), 2) != struct.pack('!i', priority)):
                raise RuntimeError('Docker DNS baseline base-chain policy differs')
    for rule in rules:
        if (one(rule, 1) != string('nat') or
                one(rule, 2) not in {string(name) for name in allowed - {'PREROUTING', 'INPUT'}}):
            raise RuntimeError('Docker DNS baseline affects incoming private packets')
        expressions = [value for key, value in attrs(one(rule, 4)) if key == 1]
        loopback_match = False
        for index, expression in enumerate(expressions[:-1]):
            if one(expression, 1) != string('payload'):
                continue
            data = one(expression, 2)
            if (one(data, 2) == u32(1) and one(data, 3) in (u32(12), u32(16)) and one(data, 4) == u32(4)):
                following = expressions[index + 1]
                if one(following, 1) == string('bitwise'):
                    signature = expression_signature(following)[1]
                    if (signature != {1: one(data, 1), 2: one(data, 1), 3: u32(4), 6: u32(0),
                                      4: {1: b'\xff' * 4}, 5: {1: b'\0' * 4}} or index + 2 >= len(expressions)):
                        continue
                    following = expressions[index + 2]
                if (one(following, 1) == string('cmp') and one(one(following, 2), 1) == one(data, 1) and one(one(following, 2), 2) == u32(0) and
                        one(one(one(following, 2), 3), 1) == socket.inet_aton('127.0.0.11')):
                    loopback_match = True
        if not loopback_match:
            raise RuntimeError('Docker DNS baseline is not confined to resolver loopback')
    return {'tables': sorted(row.hex() for row in tables), 'chains': sorted(row.hex() for row in chains),
            'rules': sorted(row.hex() for row in rules)}


def state_path():
    return Path('/tmp/mqttnet-fixture.json')


def receipt(net, state):
    tables, chains, rules = net.dump(1), net.dump(4), net.dump(7)
    foreign = {'tables': sorted(row.hex() for row in tables if one(row, 1) != string(state['table'])),
               'chains': sorted(row.hex() for row in chains if one(row, 1) != string(state['table'])),
               'rules': sorted(row.hex() for row in rules if one(row, 1) != string(state['table']))}
    if foreign != state['baseline']:
        raise RuntimeError('Sealed Docker DNS baseline changed')
    tables = [row for row in tables if one(row, 1) == string(state['table'])]
    chains = [row for row in chains if one(row, 1) == string(state['table'])]
    rules = [row for row in rules if one(row, 1) == string(state['table'])]
    if len(tables) != 1 or len(chains) != 1 or len(rules) != 1:
        raise RuntimeError('Namespace must contain exactly one table, chain and rule')
    if one(tables[0], 1) != string(state['table']):
        raise RuntimeError('Table ownership mismatch')
    chain = chains[0]
    if one(chain, 1) != string(state['table']) or one(chain, 3) != string('input'):
        raise RuntimeError('Chain ownership mismatch')
    if one(chain, 7) != string('filter') or one(chain, 5) != u32(1):
        raise RuntimeError('Unexpected chain type or policy')
    hook = one(chain, 4)
    if one(hook, 1) != u32(1) or one(hook, 2) != u32(0):
        raise RuntimeError('Unexpected chain hook')
    rule = rules[0]
    if one(rule, 1) != string(state['table']) or one(rule, 2) != string('input'):
        raise RuntimeError('Rule ownership mismatch')
    if one(rule, 7) != state['token'].encode('ascii'):
        raise RuntimeError('Rule userdata ownership mismatch')
    expected = attrs(recipe(state['source'], state['destination']))
    actual = attrs(one(rule, 4))
    if len(actual) != len(expected):
        raise RuntimeError('Expression count mismatch')
    counters = None
    for (ak, av), (ek, ev) in zip(actual, expected):
        if ak != ek or one(av, 1) != one(ev, 1):
            raise RuntimeError('Expression identity mismatch')
        if one(av, 1) == string('counter'):
            values = one(av, 2)
            counter_fields = attrs(values)
            if len({key for key, value in counter_fields}) != len(counter_fields) or not {key for key, value in counter_fields}.issubset({1, 2, 3}):
                raise RuntimeError('Counter schema differs')
            if any(value for key, value in counter_fields if key == 3):
                raise RuntimeError('Nonempty counter padding')
            counters = {'bytes': struct.unpack('!Q', one(values, 1))[0],
                        'packets': struct.unpack('!Q', one(values, 2))[0]}
        elif expression_signature(av) != expression_signature(ev):
            raise RuntimeError('Exact rule expression mismatch')
    sockets = []
    for row in Path('/proc/net/tcp').read_text().splitlines()[1:]:
        fields = row.split()
        if int(fields[1].split(':')[1], 16) == 1883:
            sockets.append(fields[3])
    if sockets:
        raise RuntimeError('Fixture unexpectedly owns a TCP1883 socket')
    return dict(state, counters=counters, netns=os.readlink('/proc/self/ns/net'),
                ruleSha256=hashlib.sha256(rule).hexdigest(),
                ruleHex=rule.hex(), tcp1883Sockets=sockets, kernelRelease=os.uname().release)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['serve', 'arm', 'status', 'disarm'])
    parser.add_argument('--token')
    parser.add_argument('--source')
    parser.add_argument('--destination')
    args = parser.parse_args()
    if args.action == 'serve':
        time.sleep(590)
        return
    net = Netfilter()
    if args.action == 'arm':
        if not args.token or len(args.token) != 32 or any(c not in '0123456789abcdef' for c in args.token):
            raise RuntimeError('Invalid ownership token')
        for value in (args.source, args.destination):
            ip = ipaddress.IPv4Address(value)
            if not (ip in ipaddress.ip_network('10.0.0.0/8') or
                    ip in ipaddress.ip_network('172.16.0.0/12') or
                    ip in ipaddress.ip_network('192.168.0.0/16')):
                raise RuntimeError('RFC1918 address required')
        if args.source == args.destination or state_path().exists():
            raise RuntimeError('Duplicate fixture or address')
        baseline = namespace_baseline(net)
        state = {'token': args.token, 'table': 'mq_' + args.token,
                 'source': args.source, 'destination': args.destination, 'baseline': baseline}
        table = attr(1, string(state['table']))
        chain = attr(1, string(state['table'])) + attr(3, string('input'))
        hook = nested(4, attr(1, u32(1)) + attr(2, u32(0)))
        net.transaction([(0, 0x600, table),
                         (3, 0x600, chain + hook + attr(5, u32(1)) + attr(7, string('filter'))),
                         (6, 0xe00, attr(1, string(state['table'])) + attr(2, string('input')) +
                          nested(4, recipe(args.source, args.destination)) +
                          attr(7, args.token.encode('ascii')))])
        state_path().write_text(json.dumps(state))
    else:
        state = json.loads(state_path().read_text())
    result = receipt(net, state)
    if args.action == 'disarm':
        net.transaction([(2, 0, attr(1, string(state['table'])))])
        if namespace_baseline(net) != state['baseline']:
            raise RuntimeError('Fresh owned-rule absence / baseline preservation not proven')
        state_path().unlink()
        result['freshRuleAbsence'] = True
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
