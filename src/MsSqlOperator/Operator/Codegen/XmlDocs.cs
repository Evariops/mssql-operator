using System.Collections.Concurrent;
using System.Reflection;
using System.Xml.Linq;

namespace MsSqlOperator.Operator.Client;

/// <summary>
/// Reads property summaries from the compiler-generated XML documentation file so the CRD
/// schema carries descriptions (<c>kubectl explain</c> was empty).
/// </summary>
internal static class XmlDocs
{
    private static readonly ConcurrentDictionary<Assembly, Dictionary<string, string>> Cache = new();

    public static string? SummaryFor(PropertyInfo property)
    {
        var type = property.DeclaringType;
        if (type is null) return null;

        var docs = Cache.GetOrAdd(type.Assembly, Load);
        return docs.GetValueOrDefault($"P:{type.FullName}.{property.Name}");
    }

    private static Dictionary<string, string> Load(Assembly assembly)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path))
            return docs;

        try
        {
            var xml = XDocument.Load(path);
            foreach (var member in xml.Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                var summary = member.Element("summary")?.Value;
                if (name is null || summary is null) continue;
                var text = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (text.Length > 0)
                    docs[name] = text;
            }
        }
        catch
        {
            // Descriptions are best-effort; a malformed doc file must not break codegen.
        }

        return docs;
    }
}
