using System.Text.Json;
using STS2SkinChanger.Catalog;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CatalogInspect <game.pck> <mod-root> [<mod-root> ...]");
    return;
}

var descriptors = new List<SkinModDescriptor>();
foreach (var root in args.Skip(1))
{
    foreach (var manifestPath in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath).TrimStart('\uFEFF'));
            var json = document.RootElement;
            if (!json.TryGetProperty("has_pck", out var hasPck) || !hasPck.GetBoolean())
            {
                continue;
            }

            var id = json.GetProperty("id").GetString()!;
            var name = json.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? id : id;
            var pckName = json.TryGetProperty("pck_name", out var pckNameValue) ? pckNameValue.GetString() ?? id : id;
            var pckPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(manifestPath)!, pckName + ".pck");
            var affectsGameplay = !json.TryGetProperty("affects_gameplay", out var gameplayValue) || gameplayValue.GetBoolean();
            descriptors.Add(new SkinModDescriptor(id, name, pckPath, affectsGameplay));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"skip {manifestPath}: {exception.Message}");
        }
    }
}

using var catalog = SkinCatalog.Build(args[0], descriptors);
foreach (var group in catalog.Groups)
{
    Console.WriteLine($"{group.Id}\t{group.DisplayName}");
    foreach (var option in group.Options)
    {
        Console.WriteLine($"  {option.Id}\t{option.Name}\t{option.Assets.Count} assets");
    }
}
