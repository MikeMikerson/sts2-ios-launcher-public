namespace STS2Mobile.Platform;

public static class GameHttp
{
    public static HttpClient CreateClient() => new(new GameOnlyHandler());
}
