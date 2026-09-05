Matcha for Windows — unsigned release package
==============================================

Requirements
------------
* Windows 11 version 24H2 (build 26100) or newer.
* Run installation from an elevated (Administrator) PowerShell window.

Before installing
-----------------
1. Download this ZIP only from the intended GitHub Release.
2. Download the release-level SHA256SUMS and verify the SHA-256 entry for the
   downloaded ZIP itself before extracting it. This detects a damaged or replaced
   archive; it does not replace trusting the GitHub repository and release.
3. Extract the entire ZIP to one folder. Do not rename, add, or remove files.
4. Files downloaded through a browser may carry Mark-of-the-Web. Only after the
   outer ZIP checksum matches the release-level SHA256SUMS, unblock exactly the
   two bundled scripts from the extracted folder:

    Unblock-File .\Install-Matcha.ps1, .\install-unsigned.ps1

Verify first
------------
From the extracted folder, run:

    .\Install-Matcha.ps1 -VerifyOnly

This checks the MSIX hash, architecture, manifest identity, and unsigned-package
requirements without installing anything.

Install
-------
Open Administrator PowerShell, change to the extracted folder, and run:

    .\Install-Matcha.ps1

The wrapper validates SHA256SUMS and then invokes install-unsigned.ps1 with the
exact package and verified hash. It does not change PowerShell execution policy
or request elevation itself.

Windows does not permit MSIX downgrades. If this device already has a newer
Matcha AllowUnsigned development identity installed, the installer will stop
with its version instead of removing it or its data automatically.

Security notice
---------------
This package is completely unsigned. It uses Matcha's dedicated development
identity with Windows' AllowUnsigned installation path. That identity is not the
identity that will be used for a future formally signed Matcha release. Treat
this package as an unsigned development release and install it only when you
trust the GitHub Release and have verified its external ZIP SHA-256 value.
