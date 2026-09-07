namespace STS2SkinChanger.WorkshopBootstrapFixture;

// A real compiled resource-pack bootstrap, inspected as bytes without loading the assembly.
public static class Bootstrap
{
    public static void Initialize() => System.Console.WriteLine("Cosmetic resources ready.");
}
