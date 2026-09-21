namespace LibRecomp.Mods;

internal sealed class PatchedFunction
{
    private const string GeneratedOriginals = "Original";

    private static readonly Dictionary<string, PatchedFunction> ByName = [];

    private readonly string _name;
    private readonly bool _isPatchedByProject;
    private RecompFunc _body;
    private Mod? _replacedBy;

    private PatchedFunction(string name, RecompFunc body)
    {
        _name = name;
        _body = body;
        _isPatchedByProject = body.Target is not null || body.Method.Name != name || body.Method.DeclaringType?.Name != GeneratedOriginals;
    }

    public List<RecompFunc> EntryHooks { get; } = [];

    public List<RecompFunc> ReturnHooks { get; } = [];

    public static PatchedFunction For(ref RecompFunc function, string expression)
    {
        string name = expression[(expression.LastIndexOf('.') + 1)..];
        if (!ByName.TryGetValue(name, out var patched))
        {
            patched = new PatchedFunction(name, function);
            ByName.Add(name, patched);
            function = patched.Run;
        }

        return patched;
    }

    public void Replace(Mod mod, RecompFunc replacement)
    {
        if (_replacedBy is not null)
        {
            throw new InvalidDataException($"It replaces {_name}, which {_replacedBy.Manifest.Id} replaces too.");
        }

        if (_isPatchedByProject)
        {
            throw new InvalidDataException($"It replaces {_name}, which the project patches.");
        }

        _replacedBy = mod;
        _body = replacement;
    }

    private void Run(RecompContext ctx)
    {
        foreach (var hook in EntryHooks)
        {
            hook(ctx);
        }

        _body(ctx);

        foreach (var hook in ReturnHooks)
        {
            hook(ctx);
        }
    }
}
