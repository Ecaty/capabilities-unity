using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Ecaty.Flecs.Capabilities
{
    /// <summary>
    /// Saves and reconstructs capability bindings, their CapDependsOn graph, native behavior trees, selected adapter
    /// state and optionally owned managed sources. Flecs remains the source of truth; this is a serialized view of it.
    /// <para>Document: { version, sources: [{path, code}], entities: [{path, components, dependsOn}], state: {"path|Component": {...}} }</para>
    /// </summary>
    public static class CapabilityGraph
    {
        public const int Version = 1;
        const int MaxEntities = 256;
        const int MaxSources = 128;
        static readonly Regex Segment = new(@"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z");
        /// <summary>Components saved for a graph entity besides registered capability configurations.</summary>
        static readonly string[] Contracts = { "CapBinding", "NativeTree" };
        static IEnumerable<string> Saved() => Contracts.Concat(CapabilityCatalog.All.Select(d => d.Info.Component));
        static bool InScope(string path, string scope) => string.IsNullOrEmpty(scope) || path == scope || path.StartsWith(scope + ".", StringComparison.Ordinal);
        static string RowPath(JToken row) => row["parent"] == null ? (string)row["name"] : (string)row["parent"] + "." + (string)row["name"];
        /// <summary>
        /// Captures every binding/tree entity within <paramref name="scope"/> (all when null) and the managed sources whose
        /// path starts with <paramref name="sourcePrefix"/> (none when null).
        /// </summary>
        public static JObject Capture(FlecsSession session, CapabilityRuntime runtime, string scope, string sourcePrefix = null)
        {
            var saved = new HashSet<string>(Saved(), StringComparer.Ordinal);
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string type in Contracts)
                if (session.Lookup(type) != 0)
                    foreach (var row in JObject.Parse(session.Query(type))["results"])
                    {
                        string path = RowPath(row);
                        if (InScope(path, scope))
                            paths.Add(path);
                    }
            var entities = new JArray();
            foreach (string path in paths)
            {
                var entity = JObject.Parse(session.EntityJson(path));
                var components = new JObject();
                if (entity["components"] is JObject all)
                    foreach (var value in all.Properties())
                        if (saved.Contains(value.Name))
                            components[value.Name] = value.Value.DeepClone();
                var dependsOn = new JArray();
                var pairs = entity["pairs"]?["CapDependsOn"];
                if (pairs is JArray many)
                    foreach (var target in many)
                        dependsOn.Add((string)target);
                else if (pairs != null)
                    dependsOn.Add((string)pairs);
                entities.Add(new JObject { ["path"] = path, ["components"] = components, ["dependsOn"] = dependsOn });
            }
            var sources = new JArray();
            if (!string.IsNullOrEmpty(sourcePrefix))
                foreach (string path in session.ManagedPaths.Where(p => p.StartsWith(sourcePrefix, StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string code = session.ManagedScriptSource(path);
                    if (!string.IsNullOrEmpty(code))
                        sources.Add(new JObject { ["path"] = path, ["code"] = code });
                }
            return new JObject
            {
                ["version"] = Version,
                ["sources"] = sources,
                ["entities"] = entities,
                ["state"] = runtime != null ? runtime.CaptureState(scope) : new JObject()
            };
        }
        /// <summary>
        /// Validates a document without touching the session. Returns null when it is acceptable.
        /// </summary>
        public static string Validate(JObject graph, string scope, string sourcePrefix = null)
        {
            if (graph == null)
                return null;
            if ((int?)graph["version"] != Version)
                return "Unsupported capability graph version.";
            if (graph["entities"] is not JArray entities || entities.Count > MaxEntities)
                return "Capability graph entities are missing or exceed " + MaxEntities + ".";
            if (graph["sources"] is JArray sources)
            {
                if (sources.Count > MaxSources)
                    return "Capability graph has too many sources.";
                foreach (var source in sources)
                {
                    string path = (string)source["path"];
                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sourcePrefix) || !path.StartsWith(sourcePrefix, StringComparison.Ordinal) || source["code"]?.Type != JTokenType.String)
                        return "Capability graph source is outside its owner: " + path;
                }
            }
            var saved = new HashSet<string>(Saved(), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                string path = (string)entity["path"];
                if (!ValidPath(path) || !InScope(path, scope) || !seen.Add(path))
                    return "Invalid or duplicate capability entity: " + path;
                if (entity["components"] is not JObject components)
                    return "Capability entity without components: " + path;
                foreach (var component in components.Properties())
                    if (!saved.Contains(component.Name) || component.Value.Type != JTokenType.Object)
                        return "Unsupported capability component: " + path + ":" + component.Name;
                if (entity["dependsOn"] is JArray dependsOn)
                    foreach (var target in dependsOn)
                        if (!ValidPath((string)target))
                            return "Invalid dependency of " + path;
            }
            return null;
        }
        static bool ValidPath(string path) => !string.IsNullOrEmpty(path) && path.Length <= 512 && path.Split('.').All(Segment.IsMatch);
        /// <summary>
        /// Reconstructs a captured graph. Owned sources are installed first; entities they do not define are recreated
        /// in <paramref name="module"/> together with their dependency relations; configurations are then written and
        /// adapter state is queued for the first successful apply. Problems are returned, not thrown, once validation passed.
        /// </summary>
        public static List<string> Restore(FlecsSession session, CapabilityRuntime runtime, JObject graph, string scope, string module, string sourcePrefix = null)
        {
            var issues = new List<string>();
            if (graph == null)
                return issues;
            string invalid = Validate(graph, scope, sourcePrefix);
            if (invalid != null)
                throw new ArgumentException(invalid);
            if (graph["sources"] is JArray sources)
                RestoreSources(session, sources, issues);
            var entities = ((JArray)graph["entities"]).Cast<JObject>().ToList();
            string script = Recreate(session, entities, issues);
            if (script.Length > 0)
            {
                try
                {
                    session.SetManagedScript(module, script);
                }
                catch (Exception error)
                {
                    issues.Add("Could not recreate capability entities: " + error.Message);
                    try
                    {
                        session.SetManagedScript(module, "");
                    }
                    catch (Exception)
                    {
                        // The module stays empty; the remaining entities are still configured below.
                    }
                }
            }
            // Bindings first so targets are known before configurations change; then the rest in saved order.
            foreach (var entity in entities)
            {
                string path = (string)entity["path"];
                if (session.Lookup(path) == 0)
                {
                    issues.Add("Capability entity was not reconstructed: " + path);
                    continue;
                }
                var components = (JObject)entity["components"];
                foreach (var component in components.Properties().OrderBy(p => p.Name == "CapBinding" ? 0 : 1))
                {
                    try
                    {
                        session.SetComponentJson(path, component.Name, component.Value.ToString(Formatting.None));
                    }
                    catch (Exception error)
                    {
                        issues.Add(error.Message);
                    }
                }
            }
            runtime?.QueueRestore(graph["state"] as JObject, true);
            return issues;
        }
        static void RestoreSources(FlecsSession session, JArray sources, List<string> issues)
        {
            // Sources may reference each other; retry unresolved ones until no further progress is made.
            var remaining = sources.Cast<JObject>().ToList();
            while (remaining.Count > 0)
            {
                int before = remaining.Count;
                string last = null;
                foreach (var source in remaining.ToArray())
                {
                    try
                    {
                        session.SetManagedScript((string)source["path"], (string)source["code"]);
                        remaining.Remove(source);
                    }
                    catch (Exception error)
                    {
                        last = (string)source["path"] + ": " + error.Message;
                        session.SetManagedScript((string)source["path"], "");
                    }
                }
                if (remaining.Count == before)
                {
                    issues.Add("Owned source could not be restored: " + last);
                    return;
                }
            }
        }
        /// <summary>Flecs Script that creates missing entities and their dependency pairs, prerequisites first.</summary>
        static string Recreate(FlecsSession session, List<JObject> entities, List<string> issues)
        {
            var missing = entities.Where(e => session.Lookup((string)e["path"]) == 0).ToList();
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            var script = new StringBuilder();
            bool Available(string path) => emitted.Contains(path) || session.Lookup(path) != 0;
            while (missing.Count > 0)
            {
                var next = missing.FirstOrDefault(e => Targets(e).All(t => Available(t) || missing.All(m => (string)m["path"] != t)));
                bool cycle = next == null;
                next ??= missing[0];
                string path = (string)next["path"];
                var pairs = new List<string>();
                foreach (string target in Targets(next))
                {
                    if (Available(target))
                        pairs.Add(target);
                    else
                        issues.Add((cycle ? "Dependency cycle dropped: " : "Missing prerequisite dropped: ") + path + " -> " + target);
                }
                Emit(script, path, pairs);
                emitted.Add(path);
                missing.Remove(next);
            }
            return script.ToString();
        }
        static IEnumerable<string> Targets(JObject entity) => entity["dependsOn"] is JArray a ? a.Select(t => (string)t) : Enumerable.Empty<string>();
        static void Emit(StringBuilder script, string path, List<string> dependsOn)
        {
            var segments = path.Split('.');
            for (int i = 0; i < segments.Length; i++)
                script.Append(' ', i * 4).Append(segments[i]).Append(" {\n");
            foreach (string target in dependsOn)
                script.Append(' ', segments.Length * 4).Append("(CapDependsOn, ").Append(target).Append(")\n");
            for (int i = segments.Length - 1; i >= 0; i--)
                script.Append(' ', i * 4).Append("}\n");
        }
    }
}
