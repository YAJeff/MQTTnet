using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace MQTTnet.ApiSurfaceInspection;

internal static class Program
{
    static readonly string[] ExpectedLibraryNames = ["MQTTnet", "MQTTnet.AspNetCore", "MQTTnet.Server"];
    static readonly string[] InspectionOrder = ["MQTTnet", "MQTTnet.Server", "MQTTnet.AspNetCore"];
    static int Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Input identity manifest and new output file required");
        using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
        var input = new Input(document.RootElement.GetProperty("role").GetString(), document.RootElement.GetProperty("libraries").EnumerateArray().Select(l => new Library(l.GetProperty("name").GetString(), l.GetProperty("path").GetString(), l.GetProperty("sha256").GetString())).ToArray());
        if (input == null || input.Libraries.Length != 3 || input.Libraries.Select(l => l.Name).Order().SequenceEqual(ExpectedLibraryNames) == false)
            throw new InvalidOperationException("Exactly the sealed native triple is required");
        if (File.Exists(args[1])) throw new InvalidOperationException("Original surface output must not be overwritten");
        foreach (var library in input.Libraries) Verify(library);
        var context = new PinnedContext(input.Libraries);
        var api = new SortedDictionary<string, string[]>();
        var rows = new SortedDictionary<string, int>();
        var identities = new List<object>();
        foreach (var name in InspectionOrder)
        {
            var expected = input.Libraries.Single(l => l.Name == name);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(expected.Path));
            if (!ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), context) || !string.Equals(Path.GetFullPath(assembly.Location), Path.GetFullPath(expected.Path), StringComparison.OrdinalIgnoreCase) || !string.Equals(Hash(assembly.Location), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Loaded assembly identity differs from explicit input");
            rows[name] = assembly.GetTypes().Max(t => t.MetadataToken & 0x00FFFFFF);
            identities.Add(new { name, requestedPath = expected.Path, loadedPath = assembly.Location, sha256 = Hash(assembly.Location), fullName = assembly.FullName, mvid = assembly.ManifestModule.ModuleVersionId });
            var records = new List<string>();
            foreach (var type in assembly.GetExportedTypes())
            {
                var prefix = type.FullName + "|";
                records.Add(prefix + "TYPE|" + type.Attributes + "|BASE:" + type.BaseType + "|INTERFACES:" + string.Join(",", type.GetInterfaces().Select(t => t.ToString()).Order()) + "|GENERICS:" + Generics(type.GetGenericArguments()));
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var details = member switch
                    {
                        MethodInfo method => MethodDetails(method) + "|RETURN:" + method.ReturnType + ":" + ParameterDetails(method.ReturnParameter),
                        ConstructorInfo ctor => MethodDetails(ctor),
                        PropertyInfo property => "GET:" + property.GetMethod?.Attributes + "|SET:" + property.SetMethod?.Attributes + "|INDEX:" + string.Join(";", property.GetIndexParameters().Select(ParameterDetails)),
                        FieldInfo field => "FLAGS:" + field.Attributes + "|CONST:" + (field.IsLiteral ? Value(field.GetRawConstantValue()) : "") + "|REQUIRED:" + string.Join(",", field.GetRequiredCustomModifiers().Select(t => t.ToString())),
                        EventInfo evt => "ADD:" + evt.AddMethod?.Attributes + "|REMOVE:" + evt.RemoveMethod?.Attributes,
                        _ => ""
                    };
                    records.Add(prefix + member.MemberType + "|" + member + "|" + details);
                }
            }
            api[name] = records.Order().ToArray();
        }
        var output = JsonSerializer.Serialize(new { api, typeDefRowsFromMetadataTokens = rows, loadedAssemblyIdentities = identities, role = input.Role, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription });
        File.WriteAllText(args[1], output);
        context.Unload();
        Console.WriteLine(JsonSerializer.Serialize(new { outputPath = Path.GetFullPath(args[1]), sha256 = Hash(args[1]), role = input.Role, assemblyCount = 3 }));
        return 0;
    }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void Verify(Library library) { if (!string.Equals(Hash(library.Path), library.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Input DLL hash mismatch"); }
    static string MethodDetails(MethodBase method) => "FLAGS:" + method.Attributes + "|PARAMETERS:" + string.Join(";", method.GetParameters().Select(ParameterDetails)) + "|GENERICS:" + Generics(method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes);
    static string ParameterDetails(ParameterInfo p) => p.ParameterType + ":" + p.Attributes + ":" + p.IsOptional + ":" + p.IsOut + ":DEFAULT:" + Value(p.RawDefaultValue) + ":REQUIRED:" + string.Join(",", p.GetRequiredCustomModifiers().Select(t => t.ToString())) + ":OPTIONAL:" + string.Join(",", p.GetOptionalCustomModifiers().Select(t => t.ToString()));
    static string Generics(Type[] types) => string.Join(";", types.Where(t => t.IsGenericParameter).Select(t => t.Name + ":" + t.GenericParameterAttributes + ":" + string.Join(",", t.GetGenericParameterConstraints().Select(c => c.ToString()).Order())));
    static string Value(object value) => value == null ? "null" : value.GetType().FullName + ":" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    sealed record Input(string Role, Library[] Libraries);
    sealed record Library(string Name, string Path, string Sha256);
    sealed class PinnedContext(Library[] libraries) : AssemblyLoadContext("SealedNativeSurface", isCollectible: true)
    {
        protected override Assembly Load(AssemblyName assemblyName)
        {
            var library = libraries.SingleOrDefault(l => l.Name == assemblyName.Name);
            if (library == null) return null;
            Verify(library);
            return LoadFromAssemblyPath(Path.GetFullPath(library.Path));
        }
    }
}
