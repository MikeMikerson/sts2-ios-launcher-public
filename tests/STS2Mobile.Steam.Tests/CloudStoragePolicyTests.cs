using STS2Mobile.Steam.Network;

namespace STS2Mobile.Steam.Tests;

public class CloudStoragePolicyTests
{
    public static TheoryData<string> CloudStorageAddresses =>
    [
        "https://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/2868840/abc?X-Amz-Signature=secret",
        "https://steamcloud-tokyo.storage.googleapis.com/2868840/a?sig=secret",
        "https://steamcloud-us-west1.storage.googleapis.com/a",
        "https://steamcloud-hk.storage.googleapis.com/a",
        "https://steamcloudquincy.blob.core.windows.net/a",
        "https://steamcloudlrstyo.blob.core.windows.net/a",
    ];

    [Theory]
    [MemberData(nameof(CloudStorageAddresses))]
    public void RecognizesSteamCloudStorage(string address) =>
        Assert.True(SteamNetworkPolicy.IsCloudStorage(new Uri(address)));

    [Theory]
    [MemberData(nameof(CloudStorageAddresses))]
    public void GeneralPolicyStillRefusesSteamCloudStorage(string address) =>
        Assert.False(SteamNetworkPolicy.IsAllowed(new Uri(address)));

    [Theory]
    [InlineData("http://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("https://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com:8443/a")]
    [InlineData("https://user:pw@steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("https://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/a#fragment")]
    [InlineData("https://attacker.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("https://steamcloud-us-west3.s3.us-west-2.amazonaws.com/a")]
    [InlineData("https://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com.attacker.test/a")]
    [InlineData("https://x.steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("https://steamcloud-.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("wss://steamcloud-us-west3.s3.dualstack.us-west-2.amazonaws.com/a")]
    [InlineData("https://storage.googleapis.com/steamcloud-tokyo/a")]
    [InlineData("https://attacker.storage.googleapis.com/a")]
    [InlineData("https://steamcloud-tokyo.storage.googleapis.com.attacker.test/a")]
    [InlineData("http://steamcloud-tokyo.storage.googleapis.com/a")]
    [InlineData("https://steamcloud.blob.core.windows.net/a")]
    [InlineData("https://attacker.blob.core.windows.net/a")]
    [InlineData("https://steamcloudquincy.table.core.windows.net/a")]
    [InlineData("https://steamcloudquincy.blob.core.windows.net:8443/a")]
    [InlineData("https://steamcloudaccountnamewaytoolong.blob.core.windows.net/a")]
    public void RefusesLookalikeStorage(string address) =>
        Assert.False(SteamNetworkPolicy.IsCloudStorage(new Uri(address)));
}
