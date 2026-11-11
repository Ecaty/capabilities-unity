using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;
namespace Ecaty.Flecs.Capabilities
{
    /// <summary>
    /// Declares a reflected capability configuration. The public fields of the class, in declaration order,
    /// become the Flecs struct members. Field initializers are the defaults used for missing members.
    /// Keep this class outside an optional plugin's #if so its schema exists whether or not the plugin is installed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CapabilityAttribute : Attribute
    {
        public string Component
        {
            get;
        }
        public string Category
        {
            get;
        }
        public int Order
        {
            get;
        }
        /// <summary>Scripting symbol that compiles the optional adapter instance.</summary>
        public string Gate
        {
            get; set;
        }
        /// <summary>Project-relative file whose presence defines the gate symbol.</summary>
        public string Probe
        {
            get; set;
        }
        public CapabilityAttribute(string component, string category, int order)
        {
            Component = component;
            Category = category;
            Order = order;
        }
    }
    /// <summary>The string field holds an entity reference (Flecs member type entity); reflection serializes it as a path.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class EntityFieldAttribute : Attribute
    {
    }
    /// <summary>The string field holds a key registered in the capability asset catalog.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class AssetKeyAttribute : Attribute
    {
    }
    /// <summary>Typed adapter instance. Derive from this and the adapter is registered automatically.</summary>
    [RequireDerived]
    public abstract class CapabilityInstance<TConfig> : CapabilityInstance where TConfig : class, new()
    {
        protected CapabilityInstance(CapabilityContext context) : base(context)
        {
        }
        public sealed override void Apply(JObject config)
        {
            Apply(CapabilityConfig.Read<TConfig>(config));
        }
        protected abstract void Apply(TConfig config);
    }
    public static class CapabilityConfig
    {
        public static TConfig Read<TConfig>(JObject json) where TConfig : class, new()
        {
            var config = new TConfig();
            if (json == null)
                return config;
            foreach (var field in Members(typeof(TConfig)))
            {
                var token = json[field.Name];
                if (token == null || token.Type == JTokenType.Null)
                    continue;
                field.SetValue(config, Convert(field, token));
            }
            return config;
        }
        public static JObject Write(object config)
        {
            var json = new JObject();
            foreach (var field in Members(config.GetType()))
            {
                var value = field.GetValue(config) ?? "";
                // An empty entity reference is the null entity.
                if (field.IsDefined(typeof(EntityFieldAttribute)) && (string)value == "")
                    value = "#0";
                json[field.Name] = JToken.FromObject(value);
            }
            return json;
        }
        static object Convert(FieldInfo field, JToken token)
        {
            var type = field.FieldType;
            if (type == typeof(float))
            {
                float value = token.Value<float>();
                if (!float.IsFinite(value))
                    throw new ArgumentException("Nonfinite " + field.Name);
                return value;
            }
            if (type == typeof(int))
                return token.Value<int>();
            if (type == typeof(bool))
                return token.Value<bool>();
            if (type == typeof(ulong))
                return token.Value<ulong>();
            if (type == typeof(string))
                return token.Type == JTokenType.String ? (string)token : token.ToString();
            throw new NotSupportedException("Unsupported capability field " + field.DeclaringType.Name + "." + field.Name);
        }
        public static IEnumerable<FieldInfo> Members(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(f => f.MetadataToken);
        public static string MemberType(FieldInfo field)
        {
            var type = field.FieldType;
            if (type == typeof(string))
                return field.IsDefined(typeof(EntityFieldAttribute)) ? "entity" : "string";
            if (type == typeof(float))
                return "f32";
            if (type == typeof(int))
                return "i32";
            if (type == typeof(bool))
                return "bool";
            if (type == typeof(ulong))
                return "u64";
            throw new NotSupportedException("Unsupported capability field " + field.DeclaringType.Name + "." + field.Name);
        }
        /// <summary>Flecs Script struct declaration, e.g. <c>struct CapLight(kind: i32, intensity: f32)</c>.</summary>
        public static string SchemaLine(string component, Type config) => "struct " + component + "(" + string.Join(", ", Members(config).Select(f => f.Name + ": " + MemberType(f))) + ")";
    }
    public sealed class CapabilityDescriptor
    {
        public CapabilityAttribute Info;
        public Type Config;
        /// <summary>Null when the optional plugin that implements this capability is not compiled.</summary>
        public Type Instance;
        public string Schema => CapabilityConfig.SchemaLine(Info.Component, Config);
        public IEnumerable<string> AssetFields => CapabilityConfig.Members(Config).Where(f => f.IsDefined(typeof(AssetKeyAttribute))).Select(f => f.Name);
        public IEnumerable<string> EntityFields => CapabilityConfig.Members(Config).Where(f => f.IsDefined(typeof(EntityFieldAttribute))).Select(f => f.Name);
    }
    /// <summary>Discovers typed capability configurations and their adapter instances in loaded assemblies.</summary>
    public static class CapabilityCatalog
    {
        static CapabilityDescriptor[] all;
        public static IReadOnlyList<CapabilityDescriptor> All => all ??= Discover();
        public static CapabilityDescriptor Find(string component) => All.FirstOrDefault(d => d.Info.Component == component);
        /// <summary>Struct declarations for every known configuration, including ones whose plugin is absent.</summary>
        public static string Schema(IEnumerable<CapabilityDescriptor> descriptors = null)
        {
            var source = new StringBuilder();
            foreach (var d in descriptors ?? All)
                source.Append(d.Schema).Append('\n');
            return source.ToString();
        }
        /// <summary>Package-relative path of the committed export for tools that load schemas without Unity (Python smokes).</summary>
        public const string ExportPath = "Packages/com.runtime.capabilities/Runtime/Capabilities/CapabilityConfigSchema.flecs";
        /// <summary>Contents of <see cref="ExportPath"/>; the runtime itself always generates the schema from the classes.</summary>
        public static string ExportText() => "// Generated from [Capability] configuration classes. Do not edit; recompile in Unity to refresh.\n" + Schema();
        static CapabilityDescriptor[] Discover()
        {
            var configs = new Dictionary<Type, CapabilityDescriptor>();
            var instances = new List<Type>();
            var self = typeof(CapabilityAttribute).Assembly;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly != self && !assembly.GetReferencedAssemblies().Any(a => a.Name == self.GetName().Name))
                    continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException partial)
                {
                    types = partial.Types.Where(t => t != null).ToArray();
                }
                foreach (var type in types)
                {
                    var info = type.GetCustomAttribute<CapabilityAttribute>();
                    if (info != null)
                        configs[type] = new CapabilityDescriptor { Info = info, Config = type };
                    if (!type.IsAbstract && ConfigOf(type) != null)
                        instances.Add(type);
                }
            }
            foreach (var instance in instances)
            {
                var config = ConfigOf(instance);
                if (!configs.TryGetValue(config, out var descriptor))
                    throw new InvalidOperationException(instance.FullName + " uses " + config.Name + " without [Capability].");
                if (descriptor.Instance != null)
                    throw new InvalidOperationException("Two adapters implement " + descriptor.Info.Component + ".");
                descriptor.Instance = instance;
            }
            var result = configs.Values.OrderBy(d => d.Info.Order).ThenBy(d => d.Info.Component, StringComparer.Ordinal).ToArray();
            var duplicate = result.GroupBy(d => d.Info.Component).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException("Duplicate capability component: " + duplicate.Key);
            return result;
        }
        static Type ConfigOf(Type type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(CapabilityInstance<>))
                    return t.GetGenericArguments()[0];
            return null;
        }
        /// <summary>Adapters for every compiled typed capability, in order.</summary>
        public static IEnumerable<ICapabilityAdapter> Adapters() => All.Where(d => d.Instance != null).Select(d => (ICapabilityAdapter)new TypedAdapter(d));
        sealed class TypedAdapter : ICapabilityAdapter
        {
            readonly CapabilityDescriptor descriptor;
            readonly ConstructorInfo constructor;
            public TypedAdapter(CapabilityDescriptor d)
            {
                descriptor = d;
                constructor = d.Instance.GetConstructor(new[] { typeof(CapabilityContext) }) ?? throw new InvalidOperationException(d.Instance.Name + " needs a (CapabilityContext) constructor.");
            }
            public string Component => descriptor.Info.Component;
            public string Category => descriptor.Info.Category;
            public int Order => descriptor.Info.Order;
            public CapabilityDescriptor Descriptor => descriptor;
            public CapabilityInstance Create(CapabilityContext context)
            {
                try
                {
                    return (CapabilityInstance)constructor.Invoke(new object[] { context });
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    throw;
                }
            }
        }
    }
}
