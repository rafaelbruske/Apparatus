# Third-party notices

Apparatus itself is released under the [BSD Zero Clause License](LICENSE) (0BSD), which asks for no credit.
The program that gets installed (`Setup.exe`) also contains the libraries below, each under its **own** license.
Several of those licenses require the copyright notice and the license text to travel with the program, and this file
is how that is done: `build\release.ps1` copies it into the installer, next to a `licenses\` folder with the
full texts and notices of the .NET runtime and the Windows App SDK, so the installed folder carries them.

The list was written from the packages in the published folder of version 0.8.0 and from the license each package
declares (25 November 2026). The same list is shown inside the program, in Settings → Sobre → Licenças de código aberto, from
`src/Apparatus.Core/Resources/third-party-licenses.json`. When you add or update a dependency, update this file and that
one; a unit test checks that both list the same packages.

## Packages and licenses

| Package | Version | License | Copyright notice declared by the package |
|---|---|---|---|
| Microsoft.Data.Sqlite.Core | 10.0.0 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.AI.Abstractions | 11.0.1 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.AI.OpenAI | 11.0.1 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Options | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Primitives | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| System.ClientModel | 1.16.0 | MIT | © Microsoft Corporation. All rights reserved. |
| System.CodeDom | 10.0.0 | MIT | © Microsoft Corporation. All rights reserved. |
| System.Management | 10.0.0 | MIT | © Microsoft Corporation. All rights reserved. |
| System.Memory.Data | 10.0.10 | MIT | © Microsoft Corporation. All rights reserved. |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT | © Microsoft Corporation. All rights reserved. |
| OpenAI | 2.15.0 | MIT | Copyright (c) 2026 OpenAI (https://openai.com) |
| Velopack | 1.2.161 | MIT | Copyright © Velopack Ltd. All rights reserved. |
| SQLitePCLRaw.core | 2.1.11 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.11 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.11 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| SQLitePCLRaw.lib.e_sqlite3 (the SQLite engine) | 3.53.3 | Public domain | SQLite is in the public domain (https://sqlite.org/copyright.html) |
| Microsoft.Web.WebView2 | 1.0.3719.77 | BSD 3-Clause | Copyright (C) Microsoft Corporation. All rights reserved. |
| Microsoft.WindowsAppSDK (Foundation, InteractiveExperiences, WinUI and the packages they pull in) | 2.7.0 | Microsoft Software License Terms | © Microsoft Corporation. All rights reserved. |
| .NET runtime (the app is published self-contained) | 10.0.x | MIT | .NET Foundation and Contributors |

The SQLitePCLRaw packages and the SQLite engine bundled with them are by Eric Sink (SourceGear, LLC).

## .NET runtime and the Microsoft libraries

The .NET runtime and the Microsoft libraries above are MIT licensed. The `licenses\` folder installed with Apparatus has
the runtime's own `dotnet-runtime-LICENSE.TXT` and `dotnet-runtime-THIRD-PARTY-NOTICES.TXT`; the latter is the list of
notices for the other open source code that the runtime and the Microsoft libraries include (the same file ships inside
`Microsoft.Extensions.*` and `System.*` packages).

## Windows App SDK

The Windows App SDK is **not** under an open source license. It comes with the *Microsoft Software License Terms* (full
text in `licenses\WindowsAppSDK-license.txt`). Those terms let you redistribute the files the package places next to the
application, including in self-contained apps, but they ask the distributor of an application that contains them to:

- add significant primary functionality of its own;
- require distributors and end users to agree to terms that protect the Windows App SDK code and Microsoft at least as much
  as those terms do;
- indemnify and hold Microsoft harmless from claims about the distribution or use of the application, except where the
  claim is based solely on the unmodified redistributed code;
- not use Microsoft's trademarks in a way that suggests the application is from or endorsed by Microsoft.

The Windows App SDK also incorporates open source components from third parties. Their notices are shipped by Microsoft
with the SDK and are copied, unchanged, into the installed `licenses\` folder: `WindowsAppSDK-NOTICE.txt` (the runtime and
base packages) and `WindowsAppSDK-WinUI-NOTICE.txt` (WinUI). Microsoft's own text in them also says where the source code
of those components can be obtained.

This file does not replace those end user terms. See the Microsoft text above for the exact wording.

## MIT License (applies to every package marked MIT, each with the copyright notice in the table above)

```
Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## BSD 3-Clause License (Microsoft.Web.WebView2)

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Apache License 2.0 (SQLitePCLRaw.core, .provider.e_sqlite3 and .bundle_e_sqlite3)

Copyright 2014-2024 SourceGear, LLC. Licensed under the Apache License, Version 2.0 (the "License"); you may not use these
files except in compliance with the License. You may obtain a copy of the License at
http://www.apache.org/licenses/LICENSE-2.0.

The terms of the License:

```
Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/
TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION
1. Definitions.
"License" shall mean the terms and conditions for use, reproduction, and distribution as defined by Sections 1 through 9 of this document.
"Licensor" shall mean the copyright owner or entity authorized by the copyright owner that is granting the License.
"Legal Entity" shall mean the union of the acting entity and all other entities that control, are controlled by, or are under common control with that entity. For the purposes of this definition, "control" means (i) the power, direct or indirect, to cause the direction or management of such entity, whether by contract or otherwise, or (ii) ownership of fifty percent (50%) or more of the outstanding shares, or (iii) beneficial ownership of such entity.
"You" (or "Your") shall mean an individual or Legal Entity exercising permissions granted by this License.
"Source" form shall mean the preferred form for making modifications, including but not limited to software source code, documentation source, and configuration files.
"Object" form shall mean any form resulting from mechanical transformation or translation of a Source form, including but not limited to compiled object code, generated documentation, and conversions to other media types.
"Work" shall mean the work of authorship, whether in Source or Object form, made available under the License, as indicated by a copyright notice that is included in or attached to the work (an example is provided in the Appendix below).
"Derivative Works" shall mean any work, whether in Source or Object form, that is based on (or derived from) the Work and for which the editorial revisions, annotations, elaborations, or other modifications represent, as a whole, an original work of authorship. For the purposes of this License, Derivative Works shall not include works that remain separable from, or merely link (or bind by name) to the interfaces of, the Work and Derivative Works thereof.
"Contribution" shall mean any work of authorship, including the original version of the Work and any modifications or additions to that Work or Derivative Works thereof, that is intentionally submitted to Licensor for inclusion in the Work by the copyright owner or by an individual or Legal Entity authorized to submit on behalf of the copyright owner. For the purposes of this definition, "submitted" means any form of electronic, verbal, or written communication sent to the Licensor or its representatives, including but not limited to communication on electronic mailing lists, source code control systems, and issue tracking systems that are managed by, or on behalf of, the Licensor for the purpose of discussing and improving the Work, but excluding communication that is conspicuously marked or otherwise designated in writing by the copyright owner as "Not a Contribution."
"Contributor" shall mean Licensor and any individual or Legal Entity on behalf of whom a Contribution has been received by Licensor and subsequently incorporated within the Work.
2. Grant of Copyright License. Subject to the terms and conditions of this License, each Contributor hereby grants to You a perpetual, worldwide, non-exclusive, no-charge, royalty-free, irrevocable copyright license to reproduce, prepare Derivative Works of, publicly display, publicly perform, sublicense, and distribute the Work and such Derivative Works in Source or Object form.
3. Grant of Patent License. Subject to the terms and conditions of this License, each Contributor hereby grants to You a perpetual, worldwide, non-exclusive, no-charge, royalty-free, irrevocable (except as stated in this section) patent license to make, have made, use, offer to sell, sell, import, and otherwise transfer the Work, where such license applies only to those patent claims licensable by such Contributor that are necessarily infringed by their Contribution(s) alone or by combination of their Contribution(s) with the Work to which such Contribution(s) was submitted. If You institute patent litigation against any entity (including a cross-claim or counterclaim in a lawsuit) alleging that the Work or a Contribution incorporated within the Work constitutes direct or contributory patent infringement, then any patent licenses granted to You under this License for that Work shall terminate as of the date such litigation is filed.
4. Redistribution. You may reproduce and distribute copies of the Work or Derivative Works thereof in any medium, with or without modifications, and in Source or Object form, provided that You meet the following conditions:
You must give any other recipients of the Work or Derivative Works a copy of this License; and
You must cause any modified files to carry prominent notices stating that You changed the files; and
You must retain, in the Source form of any Derivative Works that You distribute, all copyright, patent, trademark, and attribution notices from the Source form of the Work, excluding those notices that do not pertain to any part of the Derivative Works; and
If the Work includes a "NOTICE" text file as part of its distribution, then any Derivative Works that You distribute must include a readable copy of the attribution notices contained within such NOTICE file, excluding those notices that do not pertain to any part of the Derivative Works, in at least one of the following places: within a NOTICE text file distributed as part of the Derivative Works; within the Source form or documentation, if provided along with the Derivative Works; or, within a display generated by the Derivative Works, if and wherever such third-party notices normally appear. The contents of the NOTICE file are for informational purposes only and do not modify the License. You may add Your own attribution notices within Derivative Works that You distribute, alongside or as an addendum to the NOTICE text from the Work, provided that such additional attribution notices cannot be construed as modifying the License.
You may add Your own copyright statement to Your modifications and may provide additional or different license terms and conditions for use, reproduction, or distribution of Your modifications, or for any such Derivative Works as a whole, provided Your use, reproduction, and distribution of the Work otherwise complies with the conditions stated in this License.
5. Submission of Contributions. Unless You explicitly state otherwise, any Contribution intentionally submitted for inclusion in the Work by You to the Licensor shall be under the terms and conditions of this License, without any additional terms or conditions. Notwithstanding the above, nothing herein shall supersede or modify the terms of any separate license agreement you may have executed with Licensor regarding such Contributions.
6. Trademarks. This License does not grant permission to use the trade names, trademarks, service marks, or product names of the Licensor, except as required for reasonable and customary use in describing the origin of the Work and reproducing the content of the NOTICE file.
7. Disclaimer of Warranty. Unless required by applicable law or agreed to in writing, Licensor provides the Work (and each Contributor provides its Contributions) on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied, including, without limitation, any warranties or conditions of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A PARTICULAR PURPOSE. You are solely responsible for determining the appropriateness of using or redistributing the Work and assume any risks associated with Your exercise of permissions under this License.
8. Limitation of Liability. In no event and under no legal theory, whether in tort (including negligence), contract, or otherwise, unless required by applicable law (such as deliberate and grossly negligent acts) or agreed to in writing, shall any Contributor be liable to You for damages, including any direct, indirect, special, incidental, or consequential damages of any character arising as a result of this License or out of the use or inability to use the Work (including but not limited to damages for loss of goodwill, work stoppage, computer failure or malfunction, or any and all other commercial damages or losses), even if such Contributor has been advised of the possibility of such damages.
9. Accepting Warranty or Additional Liability. While redistributing the Work or Derivative Works thereof, You may choose to offer, and charge a fee for, acceptance of support, warranty, indemnity, or other liability obligations and/or rights consistent with this License. However, in accepting such obligations, You may act only on Your own behalf and on Your sole responsibility, not on behalf of any other Contributor, and only if You agree to indemnify, defend, and hold each Contributor harmless for any liability incurred by, or claims asserted against, such Contributor by reason of your accepting any such warranty or additional liability.
END OF TERMS AND CONDITIONS
```

## SQLite

SQLite is in the public domain and needs no notice. It is credited here because `e_sqlite3` is part of the installed
program: https://sqlite.org/copyright.html.
