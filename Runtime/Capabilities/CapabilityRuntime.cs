using System;
using System.Collections.Generic;
using System.Linq;
using Ecaty.Flecs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
namespace Ecaty.Flecs.Capabilities
{
    public interface ICapabilityAdapter
    {
        string Component
        {
            get;
        }
        string Category
        {
            get;
        }
        int Order
        {
            get;
        }
        CapabilityInstance Create(CapabilityContext context);
    }
    public abstract class CapabilityInstance : IDisposable
    {
        protected readonly CapabilityContext Context;
        protected Transform Target => Context.Target;
        protected GameObject Host => Context.Host;
        protected CapabilityInstance(CapabilityContext context)
        {
            Context = context;
        }
        public abstract void Apply(JObject config);
        public virtual void Tick(float delta)
        {
        }
        public virtual void FixedTick(float delta)
        {
        }
        public virtual void LateTick(float delta)
        {
        }
        public virtual JObject CaptureState() => null;
        public virtual void RestoreState(JObject state)
        {
        }
        public virtual void Dispose()
        {
        }
    }
    public sealed class CapabilityContext
    {
        public CapabilityRuntime Runtime; internal FlecsSession SessionOwner; public FlecsSession Session => SessionOwner;
        public ulong Entity, TargetEntity; public string Path; public Transform Target; public GameObject Host;
        public T Asset<T>(string key) where T : UnityEngine.Object => Runtime.Assets != null ? Runtime.Assets.Resolve<T>(key) : null;
        public Transform Resolve(ulong id) => Runtime.Resolve(id);
        /// <summary>Resolves an entity field (path, numeric id, or empty for none).</summary>
        public ulong Entity(string reference)
        {
            if (string.IsNullOrEmpty(reference) || reference == "0" || reference == "#0")
                return 0;
            if (ulong.TryParse(reference.TrimStart('#'), out ulong id))
                return id;
            return Session.Lookup(reference);
        }
        public Transform Resolve(string reference) => Runtime.Resolve(Entity(reference));
        public JObject Read(string type)
        {
            var value = Session.ComponentJson(Entity, type);
            return string.IsNullOrEmpty(value) ? null : JObject.Parse(value);
        }
        public void Publish(string type, object value) => Session.SetComponentJson(Path, type, JsonConvert.SerializeObject(value));
        public void OwnPose()
        {
            if (!Runtime.World.AcquirePose(TargetEntity, this))
                throw new InvalidOperationException("Target already has a movement owner.");
        }
        public void ReleasePose() => Runtime.World.ReleasePose(TargetEntity, this);
    }
    // The ECS is the source of truth. These dictionaries are disposable presentation bindings only.
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Ecaty.Flecs.Capabilities", "Ecaty.Flecs", "CapabilityRuntime")]
    [DefaultExecutionOrder(150)]
    public sealed class CapabilityRuntime : MonoBehaviour
    {
        public FlecsWorld World
        {
            get; private set;
        }
        public CapabilityAssets Assets;
        public Camera ViewCamera;
        /// <summary>The host supplies licensed audio data; no application paths belong in this package.</summary>
        public Func<byte[]> AudioBankProvider;
        byte[] audioBank;
        internal byte[] GetAudioBank()
        {
            if (audioBank != null)
                return audioBank;
            var data = AudioBankProvider?.Invoke();
            if (data == null || data.Length == 0)
                throw new InvalidOperationException("The host must provide a nonempty SoundFont for native audio.");
            return audioBank = data;
        }
        readonly Dictionary<string, JObject> pendingRestore = new();
        public JObject CaptureState() => CaptureState(null);
        /// <summary>Selected adapter state of bindings whose entity path is within <paramref name="scope"/> (all when null).</summary>
        public JObject CaptureState(string scope)
        {
            var result = new JObject();
            foreach (var p in bindings)
            {
                string path = p.Value.Context.Path;
                if (!string.IsNullOrEmpty(scope) && path != scope && !path.StartsWith(scope + ".", StringComparison.Ordinal))
                    continue;
                var state = p.Value.Instance?.CaptureState();
                if (state != null)
                    result[p.Key] = state;
            }
            return result;
        }
        public void QueueRestore(JObject values) => QueueRestore(values, false);
        /// <summary>Queues state for the next successful apply of each "path|Component" binding; merge keeps other queued state.</summary>
        public void QueueRestore(JObject values, bool merge)
        {
            EnsureSession();
            if (!merge)
                pendingRestore.Clear();
            if (values != null)
            foreach (var p in values.Properties())
                if (p.Value is JObject state)
                    pendingRestore[p.Name] = (JObject)state.DeepClone();
        }
        public Func<ulong, Transform> TargetResolver;
        public IReadOnlyList<ICapabilityAdapter> Adapters => adapters;
        public int BindingCount => bindings.Count;
        readonly List<ICapabilityAdapter> adapters = new();
        readonly Dictionary<string, Binding> bindings = new();
        sealed class Binding
        {
            public CapabilityContext Context;
            public CapabilityInstance Instance;
            public string Component;
            public string Signature;
            public JObject LastValid;
            public bool Retained;
            public int Generation;
            public int Phase;
            public int Revision;
            public bool Ticks => Phase == 1 || Retained;
        }
        FlecsSession session;
        public void Initialize(FlecsWorld world)
        {
            World = world;
            EnsureSession();
        }
        void EnsureSession()
        {
            if (!World || World.Session == null || World.Session.IsDisposed)
                return;
            if (session == World.Session)
                return;
            Clear();
            session = World.Session;
            InstallSchema(session);
            foreach (var adapter in adapters)
                PublishType(adapter);
            if (Assets)
            {
                var source = new System.Text.StringBuilder("module CapabilityAssetIndex\n");
                int i = 0;
                foreach (var item in Assets.Entries)
                if (item.Asset)
                    source.Append("asset").Append(i++).Append(" { CapAsset: { key: ").Append(JsonConvert.ToString(item.Key)).Append(", type: ").Append(JsonConvert.ToString(item.Asset.GetType().FullName)).Append(" } }\n");
                session.SetManagedScript("CapabilityAssetIndex", source.ToString());
            }
        }
        /// <summary>
        /// Shared contracts come from CapabilitySchema.flecs; configuration structs are generated from [Capability] classes.
        /// A restored session keeps its saved schema source. Configurations it lacks are added in a separate module.
        /// </summary>
        public static void InstallSchema(FlecsSession target)
        {
            if (target.ManagedScriptSource(SchemaModule) == null)
            {
                target.SetManagedScript(SchemaModule, Resources.Load<TextAsset>(SchemaModule).text + "
" + CapabilityCatalog.Schema());
                return;
            }
            // One module per added struct: never re-evaluate a module that already defines live component types.
            foreach (var d in CapabilityCatalog.All)
                if (target.Lookup(d.Info.Component) == 0)
                    target.SetManagedScript(SchemaModule + "Additions." + d.Info.Component, d.Schema);
        }
        public const string SchemaModule = "CapabilitySchema";
        void PublishType(ICapabilityAdapter a)
        {
            string notes = "Runtime create/update/remove. One capability per binding entity; compose by shared targets and dependencies. Assets must be registered.";
            var descriptor = CapabilityCatalog.Find(a.Component);
            if (descriptor != null && descriptor.AssetFields.Any())
                notes += " Asset key fields: " + string.Join(", ", descriptor.AssetFields) + ".";
            string code = "module CapabilityTypes." + a.Component + "\ndefinition { CapType: {component:" + JsonConvert.ToString(a.Component) + ",category:" + JsonConvert.ToString(a.Category) + ",description:" + JsonConvert.ToString(notes) + "}";
            if (session.Lookup("EngineCapability") != 0)
                code += "; BoundCapability; EngineCapability: {category:" + JsonConvert.ToString(a.Category) + ",command:" + JsonConvert.ToString(a.Component + " configuration with CapBinding target/enabled/revision") + ",result:" + JsonConvert.ToString("CapState and capability-specific observed outputs") + ",notes:" + JsonConvert.ToString(notes) + "}";
            session.SetManagedScript("CapabilityTypes." + a.Component, code + " }");
        }
        /// <summary>Registers every compiled typed adapter that is not registered yet.</summary>
        public void RegisterDiscovered()
        {
            foreach (var adapter in CapabilityCatalog.Adapters())
                if (adapters.All(a => a.Component != adapter.Component))
                    Register(adapter);
        }
        public void Register(ICapabilityAdapter adapter)
        {
            if (adapters.Any(a => a.Component == adapter.Component))
                throw new ArgumentException("Duplicate capability adapter: " + adapter.Component);
            adapters.Add(adapter);
            adapters.Sort((a, b) => a.Order.CompareTo(b.Order));
            if (session != null)
                PublishType(adapter);
        }
        public Transform Resolve(ulong entity)
        {
            var custom = TargetResolver?.Invoke(entity);
            return custom ? custom : World.FindView(entity)?.transform;
        }
        public void Reconcile()
        {
            EnsureSession();
            if (session == null || session.IsDisposed)
                return;
            var seen = new HashSet<string>();
            foreach (var adapter in adapters)
            {
                if (session.Lookup(adapter.Component) == 0)
                    continue;
                foreach (var row in JObject.Parse(session.Query("CapBinding, " + adapter.Component))["results"])
                {
                    var values = row["fields"]?["values"];
                    if (values == null)
                        continue;
                    string path = row["parent"] == null ? (string)row["name"] : (string)row["parent"] + "." + (string)row["name"];
                    var input = (JObject)values[0];
                    var config = (JObject)values[1];
                    string key = path + "|" + adapter.Component;
                    if (!(bool)input["enabled"])
                        continue;
                    seen.Add(key);
                    var targetId = session.Lookup((string)input["target"]); // Reflection serializes entity references as paths.
                    if (input["target"]?.Type == JTokenType.Integer)
                        targetId = (ulong)input["target"];
                    if (targetId == 0)
                        targetId = session.Lookup(path);
                    string signature = input.ToString(Formatting.None) + config.ToString(Formatting.None);
                    if (!bindings.TryGetValue(key, out var binding))
                    {
                        if (bindings.Count >= 256)
                            throw new InvalidOperationException("Capability binding budget exceeded.");
                        var target = Resolve(targetId);
                        if (!target)
                        {
                            WriteState(path, 0, (int)input["revision"], 0, "Waiting for target view");
                            continue;
                        }
                        var host = new GameObject(adapter.Component + " binding");
                        host.transform.SetParent(target, false);
                        binding = new Binding { Component = adapter.Component, Context = new CapabilityContext { Runtime = this, SessionOwner = session, Entity = session.Lookup(path), Path = path, TargetEntity = targetId, Target = target, Host = host } };
                        bindings.Add(key, binding);
                    }
                    binding.Revision = (int)input["revision"];
                    try
                    {
                        if (!binding.Context.Target || binding.Context.TargetEntity != targetId)
                        {
                            Remove(key);
                            seen.Remove(key);
                            continue;
                        }
                        int ready = CapabilityNative.fu_cap_ready(session.NativeContext, binding.Context.Entity);
                        if (ready != 1)
                        {
                            // Keep the last valid output while a prerequisite regenerates or has failed; apply again once it is ready.
                            if (binding.Phase == 1)
                                binding.Phase = 0;
                            string waiting = ready < 0 ? "Dependency failed or cycle" : "Waiting for dependency";
                            if (binding.Generation > 0)
                                waiting += "; keeping last valid result";
                            WriteState(path, ready < 0 ? -1 : 0, (int)input["revision"], binding.Generation, waiting);
                            continue;
                        }
                        bool dirty = CapabilityNative.fu_cap_has(session.NativeContext, binding.Context.Entity, "CapDirty") != 0;
                        // A failed configuration is retried only after it changes or a prerequisite result changes.
                        if (binding.Signature == "!" + signature && !dirty)
                            continue;
                        if (binding.Signature != signature || dirty || binding.Phase != 1)
                        {
                            ApplyBinding(adapter, binding, config);
                            if (pendingRestore.Remove(key, out var restore))
                                binding.Instance.RestoreState(restore);
                            CapabilityNative.fu_cap_ack(session.NativeContext, binding.Context.Entity);
                            binding.Signature = signature;
                            binding.LastValid = (JObject)config.DeepClone();
                            binding.Retained = false;
                            binding.Phase = 1;
                            ++binding.Generation;
                            WriteState(path, 1, (int)input["revision"], binding.Generation, "");
                        }
                    }
                    catch (Exception e)
                    {
                        CapabilityNative.fu_cap_ack(session.NativeContext, binding.Context.Entity);
                        Fail(adapter, binding, signature, (int)input["revision"], e);
                    }
                }
            }
            foreach (string key in bindings.Keys.Where(k => !seen.Contains(k)).ToArray())
                Remove(key);
        }
        void ApplyBinding(ICapabilityAdapter adapter, Binding binding, JObject config)
        {
            if (!binding.Context.Host)
            {
                binding.Context.Host = new GameObject(adapter.Component + " binding");
                binding.Context.Host.transform.SetParent(binding.Context.Target, false);
            }
            binding.Instance ??= adapter.Create(binding.Context);
            binding.Context.Host.SetActive(true);
            binding.Instance.Apply(config);
        }
        /// <summary>
        /// Reports the failure and, when an earlier configuration succeeded, rebuilds a fresh instance from it so the
        /// last valid result stays in the world. Dependents see phase -1 and keep their own last results.
        /// </summary>
        void Fail(ICapabilityAdapter adapter, Binding binding, string signature, int revision, Exception error, bool keepLastValid = true)
        {
            DisposeBinding(binding);
            binding.Signature = "!" + signature;
            binding.Phase = -1;
            binding.Retained = false;
            string message = Failure(error);
            if (keepLastValid && binding.LastValid != null && adapter != null)
            {
                try
                {
                    ApplyBinding(adapter, binding, binding.LastValid);
                    binding.Retained = true;
                    message += " Keeping last valid configuration.";
                }
                catch (Exception fallback)
                {
                    DisposeBinding(binding);
                    message += " Last valid configuration also failed: " + fallback.Message;
                }
            }
            WriteState(binding.Context.Path, -1, revision, binding.Generation, message);
        }
        // A tick failure would repeat with the same inputs, so it disposes the instance until an edit or dependency change.
        void TickFailed(Binding binding, Exception error) => Fail(null, binding, binding.Signature.TrimStart('!'), binding.Revision, error, false);
        static string Failure(Exception e) => (Application.isEditor || Debug.isDebugBuild) ? e.ToString() : e.Message;
        void WriteState(string path, int phase, int revision, int generation, string message) => session.SetComponentJson(path, "CapState", JsonConvert.SerializeObject(new { phase, appliedRevision = revision, generation, message }));
        float nextReconcile;
        void Update()
        {
            EnsureSession();
            if (!World || session == null || session.IsDisposed)
                return;
            if (Time.unscaledTime >= nextReconcile)
            {
                nextReconcile = Time.unscaledTime + .05f;
                Reconcile();
            }
            foreach (var b in bindings.Values.ToArray())
            if (b.Ticks)
                try
                {
                    b.Instance?.Tick(Time.unscaledDeltaTime);
                }
                catch (Exception e)
                {
                    TickFailed(b, e);
                }
        }
        void FixedUpdate()
        {
            if (session == null || session.IsDisposed)
                return;
            foreach (var b in bindings.Values.ToArray())
            if (b.Ticks)
                try
                {
                    b.Instance?.FixedTick(Time.fixedDeltaTime);
                }
                catch (Exception e)
                {
                    TickFailed(b, e);
                }
        }
        void LateUpdate()
        {
            if (session == null || session.IsDisposed)
                return;
            foreach (var b in bindings.Values.ToArray())
            if (b.Ticks)
                try
                {
                    b.Instance?.LateTick(Time.deltaTime);
                }
                catch (Exception e)
                {
                    TickFailed(b, e);
                }
        }
        void Remove(string key)
        {
            if (!bindings.Remove(key, out var b))
                return;
            DisposeBinding(b);
            if (session != null && !session.IsDisposed && session.Lookup(b.Context.Path) != 0 && CapabilityNative.fu_cap_has(session.NativeContext, b.Context.Entity, "CapBinding") != 0)
                WriteState(b.Context.Path, 0, 0, b.Generation, "Disabled or removed");
        }
        static void DisposeBinding(Binding b)
        {
            try
            {
                b.Instance?.Dispose();
            }
            finally { b.Instance = null; b.Context.ReleasePose(); if (b.Context.Host) { b.Context.Host.SetActive(false); Destroy(b.Context.Host); b.Context.Host = null; } }
        }
        public void ResetBindings()
        {
            Clear();
        }
        void Clear()
        {
            foreach (string key in bindings.Keys.ToArray())
                Remove(key);
        }
        void OnDestroy()
        {
            Clear();
            session = null;
            audioBank = null;
            AudioBankProvider = null;
        }
    }
    public sealed class Adapter<T> : ICapabilityAdapter where T : CapabilityInstance
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
        readonly Func<CapabilityContext, T> create;
        public Adapter(string component, string category, int order, Func<CapabilityContext, T> factory)
        {
            Component = component;
            Category = category;
            Order = order;
            create = factory;
        }
        public CapabilityInstance Create(CapabilityContext context) => create(context);
    }
    public static class Fields
    {
        public static JArray Points(string json, int minimum = 0)
        {
            if (string.IsNullOrEmpty(json))
                json = "[]";
            if (json.Length > 65536)
                throw new ArgumentException("Point data is too large.");
            var points = JArray.Parse(json);
            if (points.Count < minimum || points.Count > 256)
                throw new ArgumentException("Expected " + minimum + "..256 points.");
            foreach (var p in points)
            {
                if (p is not JArray a || a.Count != 3)
                    throw new ArgumentException("Points must be [x,y,z].");
                foreach (var item in a)
                {
                    if (item.Type != JTokenType.Float && item.Type != JTokenType.Integer)
                        throw new ArgumentException("Point coordinates must be numeric.");
                    float value = (float)item;
                    if (!float.IsFinite(value) || Mathf.Abs(value) > 1000000)
                        throw new ArgumentException("Point coordinates are outside the finite supported range.");
                }
            }
            return points;
        }
        public static float F(this JObject j, string key, float fallback = 0)
        {
            float f = (float?)j[key] ?? fallback;
            if (!float.IsFinite(f))
                throw new ArgumentException("Nonfinite " + key);
            return f;
        }
        public static int I(this JObject j, string key, int fallback = 0) => (int?)j[key] ?? fallback;
        public static bool B(this JObject j, string key) => (bool?)j[key] ?? false;
        public static string S(this JObject j, string key) => (string)j[key] ?? "";
        public static Vector3 XYZ(this JObject j) => new(j.F("x"), j.F("y"), j.F("z"));
        public static ulong Entity(this JObject j, string key, FlecsSession s) => j[key]?.Type == JTokenType.Integer ? (ulong)j[key] : s.Lookup(j.S(key));
    }
}
