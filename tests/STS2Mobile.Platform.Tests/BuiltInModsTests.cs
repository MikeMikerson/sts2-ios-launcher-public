namespace STS2Mobile.Platform.Tests;

public class BuiltInModsTests
{
    [Fact]
    public void EmptyCatalogHasNoMods() => Assert.Empty(BuiltInMods.ParseCatalog("{\"mods\": []}"));
}
