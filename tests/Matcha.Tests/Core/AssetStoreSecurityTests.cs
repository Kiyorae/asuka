using System.Net;
using Matcha.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Matcha.Tests.Core;

[TestClass]
public sealed class AssetStoreSecurityTests
{
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.0.0.1")]
    [DataRow("100.64.0.1")]
    [DataRow("169.254.1.1")]
    [DataRow("172.16.0.1")]
    [DataRow("192.168.0.1")]
    [DataRow("192.0.2.1")]
    [DataRow("198.18.0.1")]
    [DataRow("198.51.100.1")]
    [DataRow("203.0.113.1")]
    [DataRow("224.0.0.1")]
    [DataRow("::1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("fc00::1")]
    [DataRow("fe80::1")]
    [DataRow("ff02::1")]
    [DataRow("2001:db8::1")]
    public void PublicAddressFilterRejectsPrivateAndReservedAddresses(string value)
    {
        Assert.IsFalse(AssetStore.IsPublicInternetAddress(IPAddress.Parse(value)), value);
    }

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("1.1.1.1")]
    [DataRow("2606:4700:4700::1111")]
    public void PublicAddressFilterAllowsPublicAddresses(string value)
    {
        Assert.IsTrue(AssetStore.IsPublicInternetAddress(IPAddress.Parse(value)), value);
    }
}
