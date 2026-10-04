using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

static class RowBindings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(string assemblyPath, string output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        foreach (var family in new[] { "MQTTnet", "MQTTnet.Server", "MQTTnet.AspNetCore" })
            Assembly.LoadFrom(Path.Combine(directory, family + ".dll"));
        var rows = new List<object>();
        foreach (var type in assembly.GetTypes().OrderBy(t => t.FullName))
        {
            if (!type.CustomAttributes.Any(a => a.AttributeType.Name == "TestClassAttribute")) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(m => m.Name))
            {
                var attrs = method.CustomAttributes.ToArray();
                if (!attrs.Any(a => a.AttributeType.Name is "TestMethodAttribute" or "DataTestMethodAttribute")) continue;
                if (attrs.Any(a => a.AttributeType.Name is "DynamicDataAttribute" or "IgnoreAttribute") || type.CustomAttributes.Any(a => a.AttributeType.Name == "IgnoreAttribute"))
                    throw new InvalidOperationException("Unexpected dynamic/ignored method");
                var dataRows = attrs.Where(a => a.AttributeType.FullName == "Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute").ToArray();
                if (dataRows.Length == 0)
                    rows.Add(new { method = type.FullName + "." + method.Name, metadataToken = method.MetadataToken, displayName = method.Name, staticRow = (object)null });
                foreach (var row in dataRows)
                {
                    // Only the sealed standard DataRow attribute is instantiated; no test,
                    // test initializer, dynamic data provider or custom attribute is invoked.
                    var attribute = row.Constructor.Invoke(row.ConstructorArguments.Select(Argument).ToArray());
                    foreach (var named in row.NamedArguments)
                    {
                        if (named.MemberInfo is PropertyInfo property) property.SetValue(attribute, Argument(named.TypedValue));
                        else if (named.MemberInfo is FieldInfo field) field.SetValue(attribute, Argument(named.TypedValue));
                        else throw new InvalidOperationException("Unexpected named argument");
                    }
                    var data = row.AttributeType.GetProperty("Data").GetValue(attribute);
                    var display = (string)row.AttributeType.GetMethod("GetDisplayName", new[] { typeof(MethodInfo), typeof(object[]) }).Invoke(attribute, new[] { (object)method, data });
                    if (string.IsNullOrEmpty(display)) throw new InvalidOperationException("Empty row display identity");
                    rows.Add(new { method = type.FullName + "." + method.Name, metadataToken = method.MetadataToken, displayName = display,
                        staticRow = (object)new { arguments = row.ConstructorArguments.Select(Value).ToArray(), named = row.NamedArguments.Select(n => new { name = n.MemberName, value = Value(n.TypedValue) }).ToArray() } });
                }
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { testsInvoked = 0, framework = Environment.Version.ToString(),
            testDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))), rows,
            loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && File.Exists(a.Location)).Select(a => new {
                name = a.GetName().Name, version = a.GetName().Version.ToString(), mvid = a.ManifestModule.ModuleVersionId,
                path = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray() }, Options));
    }

    static object Argument(CustomAttributeTypedArgument arg)
    {
        if (arg.Value is IEnumerable<CustomAttributeTypedArgument> list)
        {
            var items = list.ToArray();
            var array = Array.CreateInstance(arg.ArgumentType.GetElementType(), items.Length);
            for (var i = 0; i < items.Length; i++) array.SetValue(Argument(items[i]), i);
            return array;
        }
        return arg.ArgumentType.IsEnum && arg.Value != null ? Enum.ToObject(arg.ArgumentType, arg.Value) : arg.Value;
    }
    static object Value(CustomAttributeTypedArgument arg) => arg.Value is IEnumerable<CustomAttributeTypedArgument> list
        ? list.Select(Value).ToArray() : new { type = arg.ArgumentType.FullName, value = arg.Value?.ToString() };
}
