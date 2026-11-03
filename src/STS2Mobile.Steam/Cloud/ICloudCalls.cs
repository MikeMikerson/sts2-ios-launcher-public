using ProtoBuf;
using STS2Mobile.Steam.Connection;

namespace STS2Mobile.Steam.Cloud;

/// <summary>The Steam Cloud service calls <see cref="CloudClient"/> makes; tests stand in for Steam here.</summary>
internal interface ICloudCalls
{
    Task<TResult> SendAsync<TRequest, TResult>(string method, TRequest request)
        where TRequest : IExtensible, new()
        where TResult : IExtensible, new();
}

/// <summary>The signed-in connection, which sends one request at a time.</summary>
internal sealed class ConnectionCloudCalls(SteamConnection connection) : ICloudCalls
{
    public Task<TResult> SendAsync<TRequest, TResult>(string method, TRequest request)
        where TRequest : IExtensible, new()
        where TResult : IExtensible, new() => connection.SendCloudAsync<TRequest, TResult>(method, request);
}
