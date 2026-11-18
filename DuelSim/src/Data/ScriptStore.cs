namespace DuelSim.Data;

/// <summary>
/// Finds card scripts (ProjectIgnis CardScripts layout) by file name. The engine asks for "c&lt;id&gt;.lua" and the
/// helper scripts ask for files like "proc_synchro.lua"; both are looked up by name across the script folders.
/// </summary>
public sealed class ScriptStore
{
    // Earlier folders win when two hold the same file name.
    private static readonly string[] Folders = { "", "official", "pre-release", "pre-errata", "goat", "skill", "rush", "unofficial" };

    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public string Root { get; }

    /// <param name="root">The CardScripts checkout (vendor/scripts).</param>
    /// <param name="overrides">Folders whose scripts replace same-named ones in root (DuelSim/scripts: pre-errata cards).</param>
    public ScriptStore(string root, params string[] overrides)
    {
        Root = root;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Card scripts not found at {root}. Run setup.ps1 first.");
        foreach (var dir in overrides.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*.lua", SearchOption.AllDirectories))
                _paths[Path.GetFileName(file)] = file;
        foreach (var folder in Folders)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.lua"))
                _paths.TryAdd(Path.GetFileName(file), file);
        }
    }

    /// <summary>Adds a generated script (e.g. an alias stub) under a file name.</summary>
    public void AddVirtual(string fileName, string content)
    {
        lock (_sync)
            _cache[fileName] = System.Text.Encoding.UTF8.GetBytes(content);
    }

    public byte[]? Read(string name)
    {
        var fileName = Path.GetFileName(name.Replace('\\', '/'));
        lock (_sync)
        {
            if (_cache.TryGetValue(fileName, out var cached))
                return cached;
            if (!_paths.TryGetValue(fileName, out var path))
                return null;
            var bytes = File.ReadAllBytes(path);
            _cache[fileName] = bytes;
            return bytes;
        }
    }
}
