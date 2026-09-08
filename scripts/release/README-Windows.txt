Asuka for Windows — unsigned release package
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

    Unblock-File .\Install-Asuka.ps1, .\install-unsigned.ps1

Verify first
------------
From the extracted folder, run:

    .\Install-Asuka.ps1 -VerifyOnly

This checks the MSIX hash, architecture, manifest identity, and unsigned-package
requirements without installing anything.

Build and release verification on a different CPU architecture may instead use:

    .\Install-Asuka.ps1 -VerifyOnly -SkipHostCompatibility

This checks package integrity and the manifest without comparing its Windows or
CPU requirements with the current machine. The result reports
HostCompatibilityChecked=False and does not assert ReadyForAllowUnsigned.
This option requires -VerifyOnly and can never be used to install a package.
Use the ordinary -VerifyOnly command on the actual target device before installing.

Install
-------
Open Administrator PowerShell, change to the extracted folder, and run:

    .\Install-Asuka.ps1

The wrapper validates SHA256SUMS and then invokes install-unsigned.ps1 with the
exact package and verified hash. It does not change PowerShell execution policy
or request elevation itself.

Windows does not permit MSIX downgrades. If this device already has a newer
Asuka AllowUnsigned development identity installed, the installer will stop
with its version instead of removing it or its data automatically.

Security notice
---------------
This package is completely unsigned. It uses Asuka's dedicated development
identity with Windows' AllowUnsigned installation path. That identity is not the
identity that will be used for a future formally signed Asuka release. Treat
this package as an unsigned development release and install it only when you
trust the GitHub Release and have verified its external ZIP SHA-256 value.

License
-------
Asuka is distributed under GNU AGPL version 3 or any later version
(SPDX: AGPL-3.0-or-later). See LICENSE for the complete terms.
