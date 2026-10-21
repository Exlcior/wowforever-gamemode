using Sandbox;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WoWForever;

/// <summary>One pending load per mounted path. Asking the engine again for a path whose load is still running
/// blocks the main thread on the loader's lock while that load needs the main thread to finish.</summary>
public static class MountLoads
{
    private static readonly Dictionary<string, Task<Model>> models = new();
    private static readonly Dictionary<string, Task<PrefabFile>> prefabs = new();

    public static Task<Model> Model(string path) => Get(models, path);
    public static Task<PrefabFile> Prefab(string path) => Get(prefabs, path);

    private static Task<T> Get<T>(Dictionary<string, Task<T>> pending, string path) where T : Resource
    {
        if (pending.TryGetValue(path, out var task) && !task.IsCompleted) return task;
        task = ResourceLibrary.LoadAsync<T>(path);
        pending[path] = task;
        if (pending.Count > 512)
        {
            var done = new List<string>();
            foreach (var (key, value) in pending) if (value.IsCompleted) done.Add(key);
            foreach (var key in done) pending.Remove(key);
        }
        return task;
    }
}
