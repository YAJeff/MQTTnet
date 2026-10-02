#nullable enable
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

static class PackageDiscovery
{
    public static void Run(string assemblyPath, string output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        foreach (var family in new[] { "MQTTnet", "MQTTnet.Server", "MQTTnet.AspNetCore" })
            Assembly.LoadFrom(Path.Combine(directory, family + ".dll"));
        var methods = new List<object>();
        foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName))
        {
            if (!type.CustomAttributes.Any(a => a.AttributeType.Name == "TestClassAttribute")) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(m => m.Name))
            {
                var attrs = method.CustomAttributes.ToArray();
                if (!attrs.Any(a => a.AttributeType.Name is "TestMethodAttribute" or "DataTestMethodAttribute")) continue;
                var data = attrs.Where(a => a.AttributeType.Name == "DataRowAttribute").Select(a => new {
                    arguments = a.ConstructorArguments.Select(Value).ToArray(),
                    named = a.NamedArguments.Select(n => new { name = n.MemberName, value = Value(n.TypedValue) }).ToArray()
                }).ToArray();
                methods.Add(new { fullyQualifiedName = type.FullName + "." + method.Name,
                    metadataToken = method.MetadataToken, staticRows = data,
                    dynamicData = attrs.Any(a => a.AttributeType.Name == "DynamicDataAttribute"),
                    ignored = attrs.Any(a => a.AttributeType.Name == "IgnoreAttribute") || type.CustomAttributes.Any(a => a.AttributeType.Name == "IgnoreAttribute") });
            }
        }
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && File.Exists(a.Location))
            .Select(a => new { name = a.GetName().Name, version = a.GetName().Version?.ToString(),
                path = a.Location, mvid = a.ManifestModule.ModuleVersionId,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray();
        var sourceRoot = Path.GetFullPath(Path.Combine(directory, "../../../../../")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var symbols = VerifySymbols(directory, sourceRoot);
        File.WriteAllText(output, JsonSerializer.Serialize(new { discoveryKind = "MetadataOnlyNoTestsOrDataProvidersInvoked",
            runtime = Environment.Version.ToString(), methods, loaded, symbols }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { output, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))), methodCount = methods.Count }));
    }
    static object? Value(CustomAttributeTypedArgument arg) => arg.Value is IEnumerable<CustomAttributeTypedArgument> list
        ? list.Select(Value).ToArray() : new { type = arg.ArgumentType.FullName, value = arg.Value?.ToString() };

    static object[] VerifySymbols(string directory, string sourceRoot)
    {
        const string source = "24208d37d9bb9804f2c78b8d023ec1709d47e0f3";
        const string expectedUrl = "https://raw.githubusercontent.com/YAJeff/MQTTnet/" + source + "/*";
        var result = new List<object>();
        foreach (var family in new[] { "MQTTnet", "MQTTnet.Server", "MQTTnet.AspNetCore" })
        {
            var pdb = Path.Combine(directory, family + ".pdb");
            using var stream = File.OpenRead(pdb);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var reader = provider.GetMetadataReader();
            Dictionary<string, string>? links = null;
            foreach (var handle in reader.CustomDebugInformation)
            {
                var info = reader.GetCustomDebugInformation(handle);
                var bytes = reader.GetBlobBytes(info.Value);
                if (bytes.Length == 0 || bytes[0] != (byte)'{') continue;
                using var json = JsonDocument.Parse(bytes);
                if (!json.RootElement.TryGetProperty("documents", out var documents)) continue;
                if (links is not null) throw new InvalidOperationException("Duplicate PDB SourceLink map");
                links = documents.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            }
            if (links is null || links.Count != 1 || links.Values.Any(value => value != expectedUrl))
                throw new InvalidOperationException("PDB SourceLink fork/commit differs");
            var documentsVerified = new List<object>();
            foreach (var handle in reader.Documents)
            {
                var doc = reader.GetDocument(handle);
                var name = reader.GetString(doc.Name).Replace('\\', '/');
                var match = links.Single(pair => pair.Key.EndsWith('*') && name.StartsWith(pair.Key[..^1], StringComparison.Ordinal));
                var relative = name[match.Key[..^1].Length..];
                var path = Path.GetFullPath(Path.Combine(sourceRoot, relative));
                if (!path.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
                    throw new InvalidOperationException("PDB source document escapes/missing source stage");
                var bytes = File.ReadAllBytes(path);
                var hash = reader.GetBlobBytes(doc.Hash);
                if (hash.Length != 32) throw new InvalidOperationException("Expected SHA256 PDB source checksum");
                var observed = SHA256.HashData(bytes);
                if (!hash.AsSpan().SequenceEqual(observed)) throw new InvalidOperationException("PDB document checksum differs from canonical stage");
                documentsVerified.Add(new { name, relative, hashAlgorithm = reader.GetGuid(doc.HashAlgorithm),
                    checksum = Convert.ToHexString(hash), generated = relative.Split('/').Contains("obj"), bytes = bytes.Length });
            }
            if (documentsVerified.Count == 0) throw new InvalidOperationException("PDB has no source documents");
            result.Add(new { family, pdb, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pdb))),
                sourceLink = links, sourceDocuments = documentsVerified });
        }
        return result.ToArray();
    }
}
