namespace Asuka.App;

/// <summary>
/// A runtime or shipped native component whose license is linked from the About page.
/// The list intentionally follows the resolved package graph and the native files copied
/// into the MSIX output, rather than listing build-only SDK tools.
/// </summary>
public sealed record ComponentLicense(
    string DisplayName,
    string LicenseName,
    Uri LicenseUri);

internal static class ComponentLicenses
{
    internal static IReadOnlyList<ComponentLicense> All { get; } =
    [
        new(
            "Microsoft Windows App SDK 2.4.0",
            "Microsoft Software License Terms",
            new Uri("https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.4.0/license")),
        new(
            ".NET 10 runtime",
            "MIT",
            new Uri("https://github.com/dotnet/runtime/blob/main/LICENSE.TXT")),
        new(
            "ASP.NET Core 10 runtime",
            "MIT",
            new Uri("https://github.com/dotnet/aspnetcore/blob/main/LICENSE.txt")),
        new(
            "Microsoft.Data.Sqlite 10.0.11",
            "MIT",
            new Uri("https://github.com/dotnet/efcore/blob/main/LICENSE.txt")),
        new(
            "SQLitePCLRaw 2.1.12",
            "Apache License 2.0",
            new Uri("https://github.com/ericsink/SQLitePCL.raw/blob/main/LICENSE.TXT")),
        new(
            "SQLite e_sqlite3 native library",
            "Public domain",
            new Uri("https://sqlite.org/copyright.html")),
        new(
            "Skype SILK SDK 1.0.9.6",
            "BSD-style license",
            new Uri("https://github.com/kn007/silk-v3-decoder/blob/507be6bca8ce1fb977a061481f1d79e8c610e309/silk/interface/SKP_Silk_SDK_API.h#L1-L35")),
        new(
            "Microsoft WebView2 SDK (transitive Windows App SDK payload)",
            "BSD 3-Clause license",
            new Uri("https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/license")),
    ];
}
