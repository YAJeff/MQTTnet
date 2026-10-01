using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MQTTnet.Adapter;
using MQTTnet.Formatter;
using MQTTnet.Formatter.V5;

var results = new List<object>();
long checksum = 0;
foreach (var size in new[] { 32, 128, 1024 })
{
    foreach (var unicode in new[] { false, true })
    {
        var text = unicode ? string.Concat(Enumerable.Repeat("温度😀", size / 4)) : new string('a', size);
        var encoded = Encoding.UTF8.GetBytes(text);
        byte[] field = [(byte)(encoded.Length >> 8), (byte)encoded.Length, .. encoded];
        var reader = new MqttBufferReader();
        Measure($"string-{(unicode ? "unicode" : "ascii")}-{size}", encoded.Length, () =>
        {
            reader.SetBuffer(field, 0, field.Length);
            checksum += reader.ReadString().Length;
        });
        byte[] raw = [0, .. field];
        Measure($"property-{(unicode ? "unicode" : "ascii")}-{size}", encoded.Length, () =>
        {
            reader.SetBuffer(raw, 0, raw.Length);
            var properties = new MqttV5PropertiesReader(reader);
            checksum += properties.ReadUserPropertyValueBuffer().Length;
        });
        byte[] body = [0, 1, (byte)'t', 0, .. encoded];
        var packet = new ReceivedMqttPacket(0x30, new ArraySegment<byte>(body), body.Length + 2);
        var decoder = new MqttV5PacketDecoder();
        Measure($"publish-payload-{(unicode ? "unicode" : "ascii")}-{size}", encoded.Length, () =>
        {
            checksum += ((MQTTnet.Packets.MqttPublishPacket)decoder.Decode(packet)).Payload.Length;
        });
        byte[] property = [0x26, 0, 1, (byte)'n', .. field];
        byte[] stringBody = [.. field, .. VariableInteger(property.Length), .. property, (byte)'p'];
        var stringPacket = new ReceivedMqttPacket(0x30, new ArraySegment<byte>(stringBody), stringBody.Length + 5);
        Measure($"publish-strings-{(unicode ? "unicode" : "ascii")}-{size}", 2 * encoded.Length, () =>
        {
            var decoded = (MQTTnet.Packets.MqttPublishPacket)decoder.Decode(stringPacket);
            checksum += decoded.Topic.Length + decoded.UserProperties[0].ValueBuffer.Length;
        });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount,
    libraryVersion = FileVersionInfo.GetVersionInfo(typeof(MqttBufferReader).Assembly.Location).ProductVersion,
    checksum, results, scope = "In-process string/property and MQTT packet decoding; not network, broker or CoreMQ capacity" }));

void Measure(string name, int bytes, Action action)
{
    for (var i = 0; i < 2000; i++) action();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var iterations = Math.Clamp(8 * 1024 * 1024 / bytes, 10000, 100000);
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    var start = Stopwatch.GetTimestamp();
    for (var i = 0; i < iterations; i++) action();
    var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
    var allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
    results.Add(new { name, inputBytes = bytes, iterations, elapsedSeconds = elapsed,
        operationsPerSecond = iterations / elapsed, allocatedBytesPerOperation = (double)allocation / iterations });
}

static byte[] VariableInteger(int value)
{
    var bytes = new List<byte>();
    do { var digit = (byte)(value % 128); value /= 128; if (value > 0) digit |= 128; bytes.Add(digit); } while (value > 0);
    return bytes.ToArray();
}
