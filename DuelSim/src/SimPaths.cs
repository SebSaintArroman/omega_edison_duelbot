namespace DuelSim;

/// <summary>Locates the simulator folder (DuelSim/) and its vendor data from wherever the exe runs.</summary>
public sealed record SimPaths(string Root)
{
    public string Vendor => Path.Combine(Root, "vendor");
    public string Scripts => Path.Combine(Vendor, "scripts");
    /// <summary>Hand-written scripts (pre-errata cards) that replace same-named vendor scripts.</summary>
    public string CustomScripts => Path.Combine(Root, "scripts");
    public string CardDb => Path.Combine(Vendor, "cdb", "cards.cdb");
    public string Output => Path.Combine(Root, "out");
    /// <summary>The position dataset (positions.jsonl, card-names.json): outside out/, so superseding runs never moves it.</summary>
    public string Dataset => Path.Combine(Root, "dataset");
    /// <summary>Tuned bot variants (&lt;name&gt;.json) and the evolve history.</summary>
    public string Tuning => Path.Combine(Root, "tuning");

    public static SimPaths Discover()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DuelSim.csproj")))
                return new SimPaths(dir.FullName);
            dir = dir.Parent;
        }
        return new SimPaths(Environment.CurrentDirectory);
    }
}
