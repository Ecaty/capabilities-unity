using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Runtime.Flecs;
namespace Runtime.Capabilities
{
    /// <summary>
    /// For plugins that create standalone meshes, materials, textures or ScriptableObjects without destroying them.
    /// Records what a synchronous operation created so the adapter can destroy exactly those objects later.
    /// Scene objects are not tracked: they are destroyed with the binding host. Not for per-frame use.
    /// </summary>
    public sealed class CreatedObjects
    {
        readonly List<Object> owned = new();
        public int Count => owned.Count;
        static readonly Type[] Tracked = { typeof(Mesh), typeof(Material), typeof(Texture), typeof(ScriptableObject) };
        static HashSet<Object> Snapshot()
        {
            var set = new HashSet<Object>();
            foreach (var type in Tracked)
                foreach (var o in Resources.FindObjectsOfTypeAll(type))
                    set.Add(o);
            return set;
        }
        /// <summary>
        /// Runs <paramref name="operation"/> and returns the objects it created. If it throws, those objects are
        /// destroyed before the exception propagates. Main thread only.
        /// </summary>
        public static CreatedObjects During(Action operation)
        {
            var before = Snapshot();
            var created = new CreatedObjects();
            try
            {
                operation();
            }
            catch
            {
                created.Collect(before);
                created.Destroy();
                throw;
            }
            created.Collect(before);
            return created;
        }
        void Collect(HashSet<Object> before)
        {
            var seen = new HashSet<Object>(owned);
            foreach (var type in Tracked)
                foreach (var o in Resources.FindObjectsOfTypeAll(type))
                    if (o && !before.Contains(o) && seen.Add(o))
                        owned.Add(o);
        }
        public void Destroy()
        {
            foreach (var o in owned)
                if (o)
                    Object.Destroy(o);
            owned.Clear();
        }
    }
}
